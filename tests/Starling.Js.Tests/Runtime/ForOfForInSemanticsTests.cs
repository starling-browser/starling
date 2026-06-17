using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Js.Tests.Runtime;

/// <summary>
/// wp:M3-78 — for-of / for-in iteration semantics: per-iteration lexical
/// bindings (CreatePerIterationEnvironment, §14.7.5.13), IteratorClose on
/// abrupt completion (§7.4.8 / §14.7.5.6), the iterator protocol's
/// close-on-abrupt-next rule (§8.5.3), and basic for-in enumeration.
/// </summary>
[TestClass]
public class ForOfForInSemanticsTests
{
    // ----- per-iteration lexical binding (closures capture distinct cells) -----

    [TestMethod]
    public void ForOf_let_binding_is_per_iteration_for_closures()
    {
        var v = Eval(@"
            var fns = [];
            for (let x of [1, 2, 3]) { fns.push(function () { return x; }); }
            fns[0]() + ',' + fns[1]() + ',' + fns[2]();
        ");
        v.AsString.Should().Be("1,2,3");
    }

    [TestMethod]
    public void ForIn_let_binding_is_per_iteration_for_closures()
    {
        var v = Eval(@"
            var fns = [];
            for (let k in { a: 1, b: 1, c: 1 }) { fns.push(function () { return k; }); }
            fns[0]() + ',' + fns[1]() + ',' + fns[2]();
        ");
        v.AsString.Should().Be("a,b,c");
    }

    // ----- IteratorClose on abrupt completion -----

    [TestMethod]
    public void ForOf_break_calls_iterator_return_once()
    {
        var v = Eval(ClosingIterableHeader + @"
            for (var x of iterable) { break; }
            returnCount;
        ");
        v.AsNumber.Should().Be(1);
    }

    [TestMethod]
    public void ForOf_labeled_continue_to_outer_loop_calls_iterator_return()
    {
        var v = Eval(ClosingIterableHeader + @"
            L: do {
                for (var x of iterable) { continue L; }
            } while (false);
            returnCount;
        ");
        v.AsNumber.Should().Be(1);
    }

    [TestMethod]
    public void ForOf_return_from_function_calls_iterator_return()
    {
        var v = Eval(ClosingIterableHeader + @"
            (function () {
                for (var x of iterable) { return; }
            })();
            returnCount;
        ");
        v.AsNumber.Should().Be(1);
    }

    [TestMethod]
    public void ForOf_throw_calls_iterator_return()
    {
        var v = Eval(ClosingIterableHeader + @"
            try { for (var x of iterable) { throw 0; } } catch (e) {}
            returnCount;
        ");
        v.AsNumber.Should().Be(1);
    }

    [TestMethod]
    public void ForOf_plain_continue_does_not_close_iterator_until_exhausted()
    {
        // A continue to THIS loop must NOT call return() — the iterator simply
        // re-steps. With a finite iterable, normal exhaustion also does not
        // call return() (the record is already done).
        var v = Eval(@"
            var returnCount = 0;
            var iterable = {};
            iterable[Symbol.iterator] = function () {
                var i = 0;
                return {
                    next: function () {
                        return i < 3
                            ? { value: i++, done: false }
                            : { value: undefined, done: true };
                    },
                    return: function () { returnCount += 1; return {}; }
                };
            };
            var seen = 0;
            for (var x of iterable) { seen += 1; continue; }
            seen + '|' + returnCount;
        ");
        v.AsString.Should().Be("3|0");
    }

    [TestMethod]
    public void ForOf_iterator_result_not_object_throws_TypeError()
    {
        var threw = EvalThrows(@"
            var iterable = {};
            iterable[Symbol.iterator] = function () {
                return { next: function () { return 42; } };
            };
            for (var x of iterable) {}
        ");
        threw.Should().Be("TypeError");
    }

    [TestMethod]
    public void ForOf_value_getter_error_does_not_close_iterator()
    {
        // §7.4.8 — an error reading the result's `.value` does NOT trigger
        // return() (the iterator is treated as already closed).
        var v = Eval(@"
            var returnCount = 0;
            var iterable = {};
            iterable[Symbol.iterator] = function () {
                return {
                    next: function () {
                        return { done: false, get value() { throw new Error('boom'); } };
                    },
                    return: function () { returnCount += 1; return {}; }
                };
            };
            try { for (var x of iterable) {} } catch (e) {}
            returnCount;
        ");
        v.AsNumber.Should().Be(0);
    }

    // ----- destructuring close-on-abrupt-next (§8.5.3) -----

    [TestMethod]
    public void Destructuring_elision_next_error_does_not_close_iterator()
    {
        // for ([ , ] of [iterable]) — the elision steps the inner iterator's
        // next(), which throws; return() must NOT be called.
        var v = Eval(@"
            var nextCount = 0, returnCount = 0;
            var iterator = {
                next: function () { nextCount += 1; throw new Error('boom'); },
                return: function () { returnCount += 1; }
            };
            var iterable = {};
            iterable[Symbol.iterator] = function () { return iterator; };
            try { for ([ , ] of [iterable]) {} } catch (e) {}
            nextCount + '|' + returnCount;
        ");
        v.AsString.Should().Be("1|0");
    }

    // ----- for-in basics -----

    [TestMethod]
    public void ForIn_basic_enumeration_over_undefined_is_a_no_op()
    {
        var v = Eval(@"
            var ran = 0;
            for (var k in undefined) { ran += 1; }
            ran;
        ");
        v.AsNumber.Should().Be(0);
    }

    [TestMethod]
    public void ForIn_body_var_is_hoisted_even_when_loop_never_runs()
    {
        var v = Eval(@"
            for (__key in undefined) { var key = __key; }
            typeof key;
        ");
        v.AsString.Should().Be("undefined");
    }

    [TestMethod]
    public void ForIn_key_deleted_mid_loop_is_skipped()
    {
        // A property deleted before it is reached is not visited (the key set
        // is a live snapshot but a deleted own key is filtered).
        var v = Eval(@"
            var o = { a: 1, b: 2, c: 3 };
            var seen = [];
            for (var k in o) {
                seen.push(k);
                if (k === 'a') delete o.b;
            }
            seen.join(',');
        ");
        v.AsString.Should().Be("a,c");
    }

    [TestMethod]
    public void ForIn_integer_keys_enumerate_first_in_ascending_order()
    {
        var v = Eval(@"
            var o = {};
            o.x = 1; o[2] = 1; o.y = 1; o[0] = 1; o[1] = 1;
            var keys = [];
            for (var k in o) keys.push(k);
            keys.join(',');
        ");
        v.AsString.Should().Be("0,1,2,x,y");
    }

    // ----- helpers -----

    private const string ClosingIterableHeader = @"
        var returnCount = 0;
        var iterable = {};
        iterable[Symbol.iterator] = function () {
            return {
                next: function () { return { value: 0, done: false }; },
                return: function () { returnCount += 1; return {}; }
            };
        };
    ";

    private static JsValue Eval(string src)
    {
        var program = new JsParser(src).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }

    private static string EvalThrows(string src)
    {
        try
        {
            Eval(src);
            return "(no throw)";
        }
        catch (JsThrow t)
        {
            // The thrown value is a JS error object; read its constructor name.
            if (t.Value.IsObject)
            {
                var ctor = AbstractOperations.Get(null, t.Value.AsObject, "constructor");
                if (ctor.IsObject)
                {
                    var nm = AbstractOperations.Get(null, ctor.AsObject, "name");
                    if (nm.IsString)
                    {
                        return nm.AsString;
                    }
                }
                var proto = t.Value.AsObject.Prototype;
                if (proto is not null)
                {
                    var pc = AbstractOperations.Get(null, proto, "constructor");
                    if (pc.IsObject)
                    {
                        var nm2 = AbstractOperations.Get(null, pc.AsObject, "name");
                        if (nm2.IsString)
                        {
                            return nm2.AsString;
                        }
                    }
                }
            }
            return t.Value.ToString() ?? "(error)";
        }
    }
}
