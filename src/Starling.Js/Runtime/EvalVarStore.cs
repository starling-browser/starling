namespace Starling.Js.Runtime;

/// <summary>
/// wp:M3-73 — a function frame's "eval-introduced var store": the dynamic
/// var-environment slots created by a NON-strict direct <c>eval(...)</c> for its
/// own top-level <c>var</c>/function declarations (§19.2.1.3
/// EvalDeclarationInstantiation, the non-global branch). The VM uses fixed
/// slot-based locals, so a binding the compiler never saw cannot land in a
/// static slot; instead it lives here, keyed by name, in a shared
/// <see cref="Cell"/> so the live-binding semantics match.
/// </summary>
/// <remarks>
/// <para>
/// One store is owned by the caller's running frame and is created lazily the
/// first time that frame performs a direct eval that needs to inject. The same
/// store instance is also threaded into the eval'd code's frame so the eval body
/// resolves the very bindings it just created. After eval returns, the caller's
/// own already-compiled code resolves a free identifier through this store
/// before the global object (spec order: local slot -&gt; upvalue -&gt; this
/// store -&gt; global), which is how a <c>var</c>/function the eval introduced
/// becomes visible to the caller.
/// </para>
/// <para>
/// Bindings here are deletable (configurable): an eval-introduced binding may be
/// removed with <c>delete</c> (§19.2.1.3 leaves them configurable), unlike an
/// ordinary <c>var</c>. <see cref="Delete"/> removes the entry.
/// </para>
/// <para>This is never reachable from user JS; only the eval-aware and
/// global-fallback opcodes consult it.</para>
/// </remarks>
internal sealed class EvalVarStore
{
    private readonly Dictionary<string, Cell> _byName = new(StringComparer.Ordinal);

    /// <summary>wp:M3-73 — an enclosing function's eval-introduced var store
    /// (captured by this frame's function via
    /// <see cref="JsFunction.CapturedEvalVarStore"/>). A read that misses this
    /// store walks the parent chain (spec scope chain) before reaching the global
    /// object. Declarations and sets always target THIS level (the running
    /// function's own variable environment). Null at the outermost level.</summary>
    public EvalVarStore? Parent { get; init; }

    /// <summary>True if a binding of this name exists at this level (own only).</summary>
    public bool Has(string name) => _byName.ContainsKey(name);

    /// <summary>Resolve a binding through this store, then its parent chain.</summary>
    public bool TryGet(string name, out Cell cell)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (s._byName.TryGetValue(name, out cell!))
            {
                return true;
            }
        }

        cell = null!;
        return false;
    }

    /// <summary>§19.2.1.3 — idempotent var/function pre-declaration: create the
    /// binding (initialized to <c>undefined</c>) if it does not already exist;
    /// a re-declaration of an existing binding has no effect. Returns the live
    /// cell either way.</summary>
    public Cell Declare(string name)
    {
        if (_byName.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var cell = new Cell(JsValue.Undefined);
        _byName[name] = cell;
        return cell;
    }

    /// <summary>Set an existing binding's value (the binding must have been
    /// declared first). Used for a var initializer and for a function
    /// declaration's hoisted function object.</summary>
    public void Set(string name, JsValue value)
    {
        if (_byName.TryGetValue(name, out var cell))
        {
            cell.Value = value;
        }
        else
        {
            _byName[name] = new Cell(value);
        }
    }

    /// <summary>Remove the binding (eval-introduced bindings are
    /// configurable). Returns true if one was present.</summary>
    public bool Delete(string name) => _byName.Remove(name);
}
