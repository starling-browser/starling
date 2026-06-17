// SPDX-License-Identifier: Apache-2.0

namespace Starling.RegExp;

/// <summary>AST → flat Pike-VM instruction array via Thompson NFA construction.</summary>
public sealed class RegexCompiler
{
    private readonly List<RegexInst> _code = new();
    private readonly List<RegexCharClass> _klasses = new();
    private readonly List<RegexProgram> _subs = new();
    private readonly RegexFlags _flags;
    private readonly bool _ignoreCase;
    private readonly bool _captureGroups;
    private readonly int _captureCount;
    private readonly IReadOnlyDictionary<string, int> _namedCaptures;

    public RegexCompiler(RegexFlags flags, int captureCount, IReadOnlyDictionary<string, int> named, bool captureGroups = true)
    {
        _flags = flags;
        _ignoreCase = (flags & RegexFlags.IgnoreCase) != 0;
        _captureGroups = captureGroups;
        _captureCount = captureCount;
        _namedCaptures = named;
    }

    public RegexProgram Compile(RegexNode root)
    {
        // Capture group 0 = entire match. We emit SaveStart(0)/SaveEnd(0) around the root.
        Emit(RegexOp.SaveStart, 0, 0);
        Walk(root);
        Emit(RegexOp.SaveEnd, 0, 0);
        Emit(RegexOp.Match, 0, 0);
        return new RegexProgram(_code, _klasses, _subs, _captureCount, _namedCaptures);
    }

    private int Emit(RegexOp op, int a, int b)
    {
        var pc = _code.Count;
        _code.Add(new RegexInst(op, a, b));
        return pc;
    }

    private void Patch(int pc, RegexOp op, int a, int b)
    {
        _code[pc] = new RegexInst(op, a, b);
    }

    private int Here => _code.Count;

    private int AddKlass(RegexCharClass k)
    {
        _klasses.Add(k);
        return _klasses.Count - 1;
    }

    private int AddSub(RegexProgram p)
    {
        _subs.Add(p);
        return _subs.Count - 1;
    }

    private void Walk(RegexNode node)
    {
        switch (node)
        {
            case EmptyNode:
                return;
            case LiteralNode lit:
                Emit(_ignoreCase ? RegexOp.CharIgnoreCase : RegexOp.Char, lit.CodePoint, 0);
                return;
            case AnyNode an:
                Emit(an.DotAll ? RegexOp.Any : RegexOp.AnyExceptNewline, 0, 0);
                return;
            case CharClassNode klass:
                Emit(RegexOp.CharClass, AddKlass(klass.Klass), 0);
                return;
            case AnchorNode anchor:
                Emit(anchor.Kind switch
                {
                    AnchorKind.StartOfInput => RegexOp.AssertStart,
                    AnchorKind.EndOfInput => RegexOp.AssertEnd,
                    AnchorKind.WordBoundary => RegexOp.AssertWordBoundary,
                    _ => RegexOp.AssertNonWordBoundary,
                }, 0, 0);
                return;
            case SequenceNode seq:
                foreach (var i in seq.Items)
                {
                    Walk(i);
                }

                return;
            case AlternationNode alt:
                WalkAlternation(alt);
                return;
            case QuantifierNode q:
                WalkQuantifier(q);
                return;
            case GroupNode g:
                {
                    if (g.CaptureIndex is int idx && _captureGroups)
                    {
                        Emit(RegexOp.SaveStart, idx, 0);
                        Walk(g.Child);
                        Emit(RegexOp.SaveEnd, idx, 0);
                    }
                    else
                    {
                        Walk(g.Child);
                    }
                    return;
                }
            case BackrefNode bref:
                Emit(RegexOp.Backref, bref.Group, 0);
                return;
            case NamedBackrefNode nbref:
                if (!_namedCaptures.TryGetValue(nbref.Name, out var ni))
                {
                    throw new RegexSyntaxException($"Invalid named backreference: {nbref.Name}");
                }

                Emit(RegexOp.Backref, ni, 0);
                return;
            case LookaroundNode la:
                {
                    // Compile sub-program with the same flags/captures inherited.
                    var subCompiler = new RegexCompiler(_flags, _captureCount, _namedCaptures, captureGroups: false);
                    var subProg = subCompiler.Compile(la.Child);
                    var subIdx = AddSub(subProg);
                    int arg2 = (la.Negative ? 1 : 0) | (la.Behind ? 2 : 0);
                    Emit(RegexOp.Lookaround, subIdx, arg2);
                    return;
                }
        }
    }

    private void WalkAlternation(AlternationNode alt)
    {
        // For N alternatives, emit a chain of Splits.
        // Pattern (Russ Cox): split L1, L2; L1: code(a); jmp End; L2: code(b); End:
        var ends = new List<int>();
        for (var i = 0; i < alt.Alternatives.Count; i++)
        {
            var isLast = i == alt.Alternatives.Count - 1;
            int splitPc = -1;
            if (!isLast)
            {
                splitPc = Emit(RegexOp.Split, 0, 0); // patched
            }
            var branchStart = Here;
            Walk(alt.Alternatives[i]);
            if (!isLast)
            {
                ends.Add(Emit(RegexOp.Jmp, 0, 0));
                var nextStart = Here;
                Patch(splitPc, RegexOp.Split, branchStart, nextStart);
            }
        }
        foreach (var jpc in ends)
        {
            Patch(jpc, RegexOp.Jmp, Here, 0);
        }
    }

    private void WalkQuantifier(QuantifierNode q)
    {
        // Compile minimum repetitions in series.
        for (var i = 0; i < q.Min; i++)
        {
            Walk(q.Child);
        }

        if (q.Max == -1)
        {
            // {min,} — kleene star tail.
            // L: split body, end  (or end, body for lazy)
            // body: ...; jmp L
            // end:
            var loopStart = Emit(RegexOp.Split, 0, 0);
            var bodyStart = Here;
            Walk(q.Child);
            Emit(RegexOp.Jmp, loopStart, 0);
            var end = Here;
            if (q.Greedy)
            {
                Patch(loopStart, RegexOp.Split, bodyStart, end);
            }
            else
            {
                Patch(loopStart, RegexOp.Split, end, bodyStart);
            }
        }
        else
        {
            // {min,max} — emit (max-min) optional copies.
            var remaining = q.Max - q.Min;
            var splitPcs = new List<int>();
            var bodyStarts = new List<int>();
            for (var i = 0; i < remaining; i++)
            {
                splitPcs.Add(Emit(RegexOp.Split, 0, 0));
                bodyStarts.Add(Here);
                Walk(q.Child);
            }
            var end = Here;
            for (var i = 0; i < remaining; i++)
            {
                if (q.Greedy)
                {
                    Patch(splitPcs[i], RegexOp.Split, bodyStarts[i], end);
                }
                else
                {
                    Patch(splitPcs[i], RegexOp.Split, end, bodyStarts[i]);
                }
            }
        }
    }
}
