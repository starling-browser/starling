// SPDX-License-Identifier: Apache-2.0

namespace Starling.RegExp;

/// <summary>
/// Pike-VM regex matcher per Russ Cox's "Regular Expression Matching: the
/// Virtual Machine Approach". Linear-time matching for the common subset.
/// Backreferences and lookbehind fall back to a per-thread recursive matcher.
/// </summary>
public sealed class RegexPikeVm
{
    private readonly RegexProgram _prog;
    private readonly RegexFlags _flags;

    // Reusable buffers for the Pike VM (per-compiled-regex). Matches on the same
    // Regex are assumed to be sequential (not concurrent); this eliminates per-Exec
    // allocations for the thread lists and visited set after the first use.
    private List<Thread> _curr = new();
    private List<Thread> _next = new();
    private int[] _visitGen = Array.Empty<int>();
    private int _currentGen = 1;

    public RegexPikeVm(RegexProgram program, RegexFlags flags)
    {
        _prog = program;
        _flags = flags;
    }

    /// <summary>Run the program starting at <paramref name="start"/> against
    /// <paramref name="input"/>. Returns null if no match is found at or after
    /// the start position (or only at the exact start position when
    /// <c>sticky</c> is set).</summary>
    public RegexMatch? Exec(string input, int start)
    {
        bool sticky = (_flags & RegexFlags.Sticky) != 0;
        // Whether the pattern contains backref or lookaround — if so use the
        // slow recursive matcher (handles backreferences correctly).
        bool needSlow = HasBackrefOrLookaround(_prog);

        int maxStart = sticky ? start : input.Length;
        var pos = start;
        while (pos <= maxStart)
        {
            RegexMatch? m;
            if (needSlow)
            {
                m = ExecSlow(input, pos);
            }
            else
            {
                m = ExecPike(input, pos);
            }

            if (m is not null)
            {
                return m;
            }

            if (sticky)
            {
                return null;
            }

            pos = AdvanceInputIndex(input, pos);
        }
        return null;
    }

    private int AdvanceInputIndex(string input, int index)
    {
        if (((_flags & (RegexFlags.Unicode | RegexFlags.UnicodeSets)) == 0)
            || index + 1 >= input.Length
            || !char.IsHighSurrogate(input[index])
            || !char.IsLowSurrogate(input[index + 1]))
        {
            return index + 1;
        }

        return index + 2;
    }

    private int CodePointAt(ReadOnlySpan<char> input, int pos, out int charLen)
    {
        if (pos >= input.Length)
        {
            charLen = 0;
            return -1;
        }

        int cp = input[pos];
        charLen = 1;
        if (((_flags & (RegexFlags.Unicode | RegexFlags.UnicodeSets)) != 0)
            && char.IsHighSurrogate(input[pos])
            && pos + 1 < input.Length
            && char.IsLowSurrogate(input[pos + 1]))
        {
            cp = char.ConvertToUtf32(input[pos], input[pos + 1]);
            charLen = 2;
        }
        return cp;
    }

    private static bool HasBackrefOrLookaround(RegexProgram p)
    {
        foreach (var i in p.Code)
        {
            if (i.Op == RegexOp.Backref || i.Op == RegexOp.Lookaround)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Mark a PC as visited for the current generation (avoids HashSet allocation
    /// and hashing for the small # of PCs in a typical program). Falls back to
    /// clearing the gen array on (extremely rare) wraparound.
    /// </summary>
    private bool TryMarkPc(int pc, int programSize)
    {
        if (_visitGen.Length < programSize)
        {
            Array.Resize(ref _visitGen, Math.Max(programSize, 16));
        }

        if (_visitGen[pc] == _currentGen)
        {
            return false;
        }

        _visitGen[pc] = _currentGen;
        return true;
    }

    // ------------------------------------------------------------------
    //                    Pike VM (linear time)
    // ------------------------------------------------------------------
    private RegexMatch? ExecPike(string input, int start) =>
        ExecPike(input.AsSpan(), start, input);

    private RegexMatch? ExecPike(ReadOnlySpan<char> input, int start, string? originalInput = null)
    {
        var code = _prog.Code;
        int programSize = code.Count;
        int slotCount = (_prog.CaptureCount + 1) * 2;

        // Bump generation for visited (zero-alloc "seen" after init)
        if (++_currentGen == 0)
        {
            Array.Clear(_visitGen, 0, _visitGen.Length);
            _currentGen = 1;
        }

        _curr.Clear();
        _next.Clear();
        if (++_currentGen == 0)
        {
            Array.Clear(_visitGen, 0, _visitGen.Length);
            _currentGen = 1;
        }

        // §22.2.2.1 RegExpBuiltinExec initializes every capture to undefined; a
        // capture group that doesn't participate in the match must read back as
        // undefined, not the empty string. The slot array uses -1 to mean
        // "not captured", so it must start all -1 (default int[] is all-zero,
        // which would mis-report non-participating groups as the empty span 0,0).
        var initialSlots = new int[slotCount];
        for (var i = 0; i < slotCount; i++)
        {
            initialSlots[i] = -1;
        }

        AddThread(_curr, input, start, initialSlots);
        // Threads queued into _next represent the next input position, so their
        // epsilon-closure dedup must not share the generation used to build
        // _curr. Sharing it suppresses loop-back states for greedy quantifiers
        // like /a*/ after the first character.
        if (++_currentGen == 0)
        {
            Array.Clear(_visitGen, 0, _visitGen.Length);
            _currentGen = 1;
        }
        RegexMatch? best = null;
        int pos = start;
        while (true)
        {
            int cp = pos < input.Length ? input[pos] : -1;
            int charLen = 1;
            if (cp >= 0 && char.IsHighSurrogate((char)cp) && pos + 1 < input.Length
                && char.IsLowSurrogate(input[pos + 1])
                && (_flags & (RegexFlags.Unicode | RegexFlags.UnicodeSets)) != 0)
            {
                cp = char.ConvertToUtf32((char)cp, input[pos + 1]);
                charLen = 2;
            }

            for (var t = 0; t < _curr.Count; t++)
            {
                var th = _curr[t];
                var ins = code[th.Pc];
                bool consumed = false;
                switch (ins.Op)
                {
                    case RegexOp.Match:
                        // Record longest leftmost match; lower-priority threads
                        // at this position are discarded, but next-list keeps
                        // higher-priority continuations queued from earlier in
                        // curr-processing.
                        var matchInput = originalInput ?? input.ToString();
                        best = MakeMatchFromSlots(matchInput, start, pos, th.Slots);
                        goto AfterCurr;
                    case RegexOp.Char:
                        if (cp >= 0 && cp == ins.Arg1)
                        {
                            AddThread(_next, input, pos + charLen, th.Slots, th.Pc + 1);
                            consumed = true;
                        }
                        break;
                    case RegexOp.CharIgnoreCase:
                        if (cp >= 0 && CaseFoldEquals(cp, ins.Arg1))
                        {
                            AddThread(_next, input, pos + charLen, th.Slots, th.Pc + 1);
                            consumed = true;
                        }
                        break;
                    case RegexOp.CharClass:
                        if (cp >= 0 && _prog.Klasses[ins.Arg1].Contains(cp))
                        {
                            AddThread(_next, input, pos + charLen, th.Slots, th.Pc + 1);
                            consumed = true;
                        }
                        break;
                    case RegexOp.Any:
                        if (cp >= 0)
                        {
                            AddThread(_next, input, pos + charLen, th.Slots, th.Pc + 1);
                            consumed = true;
                        }
                        break;
                    case RegexOp.AnyExceptNewline:
                        if (cp >= 0 && !RegexCharClass.IsLineTerminator(cp))
                        {
                            AddThread(_next, input, pos + charLen, th.Slots, th.Pc + 1);
                            consumed = true;
                        }
                        break;
                }
                if (!consumed) { /* discarded — non-consuming ops were already expanded by AddThread */ }
            }
        AfterCurr:
            // If we already matched at this position and there are no further
            // higher-priority continuations queued in next, stop here.
            if (pos >= input.Length)
            {
                break;
            }

            pos += charLen;
            (_curr, _next) = (_next, _curr);
            _next.Clear();
            if (_curr.Count == 0)
            {
                break;
            }
            // New wave of threads arrived at the advanced position: fresh gen for
            // its epsilon-closure dedup (mimics original per-pos seen.Clear()).
            if (++_currentGen == 0)
            {
                Array.Clear(_visitGen, 0, _visitGen.Length);
                _currentGen = 1;
            }
        }
        return best;
    }

    private bool CaseFoldEquals(int a, int b)
    {
        if (a == b)
        {
            return true;
        }

        if (a > 0xFFFF || b > 0xFFFF)
        {
            return false;
        }

        var ca = (char)a; var cb = (char)b;
        return char.ToLowerInvariant(ca) == char.ToLowerInvariant(cb)
            || char.ToUpperInvariant(ca) == char.ToUpperInvariant(cb);
    }

    private struct Thread
    {
        public int Pc;
        public int[] Slots;
        public Thread(int pc, int[] slots) { Pc = pc; Slots = slots; }
    }

    private void AddThread(List<Thread> list, ReadOnlySpan<char> input, int pos, int[] slots, int startPc = -1, Func<int[], int[]>? cloneProvider = null)
    {
        // Split's lower-priority branch is parked on a heap-allocated stack
        // instead of via native recursion. A deeply chained regex (e.g. a
        // long run of `(?:|x)` from minified web code) would otherwise blow
        // the native stack — once per epsilon Split — well before any
        // caller-side recursion guard could fire.
        Stack<Thread>? deferred = null;
        var t = startPc < 0 ? new Thread(0, slots) : new Thread(startPc, slots);
        while (true)
        {
            if (TryMarkPc(t.Pc, _prog.Code.Count))
            {
                var ins = _prog.Code[t.Pc];
                switch (ins.Op)
                {
                    case RegexOp.Jmp:
                        t = new Thread(ins.Arg1, t.Slots);
                        continue;
                    case RegexOp.Split:
                        // Defer Arg2 (lower priority) and continue inline on
                        // Arg1 — preserving leftmost-first / greedy ordering.
                        // The deferred thread gets a slot clone so SaveStart/
                        // SaveEnd mutations on the Arg1 chain don't leak into
                        // it when it's later popped.
                        var splitClone = cloneProvider != null ? cloneProvider(t.Slots) : (int[])t.Slots.Clone();
                        (deferred ??= new Stack<Thread>())
                            .Push(new Thread(ins.Arg2, splitClone));
                        t = new Thread(ins.Arg1, t.Slots);
                        continue;
                    case RegexOp.SaveStart:
                        {
                            var slotsCopy = cloneProvider != null ? cloneProvider(t.Slots) : (int[])t.Slots.Clone();
                            slotsCopy[ins.Arg1 * 2] = pos;
                            t = new Thread(t.Pc + 1, slotsCopy);
                            continue;
                        }
                    case RegexOp.SaveEnd:
                        {
                            var slotsCopy = cloneProvider != null ? cloneProvider(t.Slots) : (int[])t.Slots.Clone();
                            slotsCopy[ins.Arg1 * 2 + 1] = pos;
                            t = new Thread(t.Pc + 1, slotsCopy);
                            continue;
                        }
                    case RegexOp.AssertStart:
                        if (pos == 0 || ((_flags & RegexFlags.Multiline) != 0 && pos > 0
                            && RegexCharClass.IsLineTerminator(input[pos - 1])))
                        {
                            t = new Thread(t.Pc + 1, t.Slots);
                            continue;
                        }
                        break;
                    case RegexOp.AssertEnd:
                        if (pos == input.Length || ((_flags & RegexFlags.Multiline) != 0 && pos < input.Length
                            && RegexCharClass.IsLineTerminator(input[pos])))
                        {
                            t = new Thread(t.Pc + 1, t.Slots);
                            continue;
                        }
                        break;
                    case RegexOp.AssertWordBoundary:
                        if (IsWordBoundary(input, pos))
                        {
                            t = new Thread(t.Pc + 1, t.Slots);
                            continue;
                        }
                        break;
                    case RegexOp.AssertNonWordBoundary:
                        if (!IsWordBoundary(input, pos))
                        {
                            t = new Thread(t.Pc + 1, t.Slots);
                            continue;
                        }
                        break;
                    default:
                        list.Add(t);
                        break;
                }
            }
            // Current branch terminated (seen-dedup, assert failure, or
            // list.Add). Pop the next deferred branch; done when empty.
            if (deferred is null || deferred.Count == 0)
            {
                return;
            }

            t = deferred.Pop();
        }
    }

    private static bool IsWordBoundary(ReadOnlySpan<char> s, int pos)
    {
        bool before = pos > 0 && RegexCharClass.IsWordChar(s[pos - 1]);
        bool after = pos < s.Length && RegexCharClass.IsWordChar(s[pos]);
        return before != after;
    }

    private static RegexMatch MakeMatchFromSlots(string input, int matchStart, int matchEnd, int[] slots)
    {
        // slots[0]/slots[1] hold group 0 (full match). If unset (rare), fall
        // back to (matchStart, matchEnd).
        var captures = new int[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            captures[i] = slots[i];
        }

        if (captures.Length >= 2 && (captures[0] < 0 || captures[1] < 0))
        {
            captures[0] = matchStart;
            captures[1] = matchEnd;
        }
        var start = captures.Length >= 2 ? captures[0] : matchStart;
        var end = captures.Length >= 2 ? captures[1] : matchEnd;
        return new RegexMatch(input, start, end, captures);
    }

    // ------------------------------------------------------------------
    //          Slow backtracking matcher (backrefs + lookarounds)
    // ------------------------------------------------------------------
    /// <summary>Backtracking matcher. Exponential worst-case but supports
    /// backreferences and lookarounds. Used only when the pattern contains
    /// those constructs.</summary>
    private RegexMatch? ExecSlow(string input, int start)
    {
        int slotCount = (_prog.CaptureCount + 1) * 2;
        var slots = new int[slotCount];
        for (var i = 0; i < slotCount; i++)
        {
            slots[i] = -1;
        }

        if (TryMatch(_prog, input, start, 0, slots, out var endPos, out var finalSlots))
        {
            return MakeMatchFromSlots(input, start, endPos, finalSlots);
        }
        return null;
    }

    // Split's lower-priority branch is parked on a heap-allocated backtrack
    // stack instead of via native recursion. A regex with deeply nested
    // alternation or quantifiers (e.g. Google Closure's URL parser against a
    // long uniform payload) backs up one character per failed branch — pre-fix
    // that ran one native frame per char and blew the thread stack well before
    // any caller-side guard could fire. Lookaround still recurses for the
    // sub-program, but each sub-call has its own backtrack stack, so depth is
    // bounded by static lookaround nesting in the pattern.
    private bool TryMatch(RegexProgram prog, string input, int pos, int pc, int[] slots,
        out int endPos, out int[] outSlots)
    {
        endPos = -1;
        outSlots = slots;
        var code = prog.Code;
        Stack<(int Pos, int Pc, int[] Slots)>? backtrack = null;

        while (true)
        {
            while (pc < code.Count)
            {
                var ins = code[pc];
                switch (ins.Op)
                {
                    case RegexOp.Match:
                        endPos = pos;
                        outSlots = slots;
                        return true;
                    case RegexOp.Jmp:
                        pc = ins.Arg1;
                        continue;
                    case RegexOp.Split:
                        {
                            // Park Arg2 with a snapshot of slots; pursue Arg1 in place.
                            backtrack ??= new Stack<(int, int, int[])>();
                            backtrack.Push((pos, ins.Arg2, (int[])slots.Clone()));
                            pc = ins.Arg1;
                            continue;
                        }
                    case RegexOp.SaveStart:
                        slots[ins.Arg1 * 2] = pos;
                        pc++;
                        continue;
                    case RegexOp.SaveEnd:
                        slots[ins.Arg1 * 2 + 1] = pos;
                        pc++;
                        continue;
                    case RegexOp.AssertStart:
                        if (pos == 0 || ((_flags & RegexFlags.Multiline) != 0 && pos > 0
                            && RegexCharClass.IsLineTerminator(input[pos - 1])))
                        {
                            pc++;
                            continue;
                        }
                        goto Fail;
                    case RegexOp.AssertEnd:
                        if (pos == input.Length || ((_flags & RegexFlags.Multiline) != 0 && pos < input.Length
                            && RegexCharClass.IsLineTerminator(input[pos])))
                        {
                            pc++;
                            continue;
                        }
                        goto Fail;
                    case RegexOp.AssertWordBoundary:
                        if (IsWordBoundary(input, pos)) { pc++; continue; }
                        goto Fail;
                    case RegexOp.AssertNonWordBoundary:
                        if (!IsWordBoundary(input, pos)) { pc++; continue; }
                        goto Fail;
                    case RegexOp.Char:
                        {
                            var cp = CodePointAt(input, pos, out var charLen);
                            if (cp >= 0 && cp == ins.Arg1) { pos += charLen; pc++; continue; }
                            goto Fail;
                        }
                    case RegexOp.CharIgnoreCase:
                        {
                            var cp = CodePointAt(input, pos, out var charLen);
                            if (cp >= 0 && CaseFoldEquals(cp, ins.Arg1)) { pos += charLen; pc++; continue; }
                            goto Fail;
                        }
                    case RegexOp.CharClass:
                        {
                            var cp = CodePointAt(input, pos, out var charLen);
                            if (cp >= 0 && prog.Klasses[ins.Arg1].Contains(cp))
                            {
                                pos += charLen; pc++; continue;
                            }
                            goto Fail;
                        }
                    case RegexOp.Any:
                        {
                            var cp = CodePointAt(input, pos, out var charLen);
                            if (cp >= 0) { pos += charLen; pc++; continue; }
                            goto Fail;
                        }
                    case RegexOp.AnyExceptNewline:
                        {
                            var cp = CodePointAt(input, pos, out var charLen);
                            if (cp >= 0 && !RegexCharClass.IsLineTerminator(cp))
                            {
                                pos += charLen; pc++; continue;
                            }
                            goto Fail;
                        }
                    case RegexOp.Backref:
                        {
                            var idx = ins.Arg1;
                            if (idx * 2 + 1 >= slots.Length)
                            {
                                goto Fail;
                            }

                            var s = slots[idx * 2];
                            var e = slots[idx * 2 + 1];
                            if (s < 0 || e < 0)
                            {
                                // Matches empty string when not yet captured.
                                pc++;
                                continue;
                            }
                            var len = e - s;
                            if (pos + len > input.Length)
                            {
                                goto Fail;
                            }

                            var brMatched = true;
                            for (var k = 0; k < len; k++)
                            {
                                var a = input[s + k];
                                var b = input[pos + k];
                                if ((_flags & RegexFlags.IgnoreCase) != 0)
                                {
                                    if (!CaseFoldEquals(a, b)) { brMatched = false; break; }
                                }
                                else if (a != b) { brMatched = false; break; }
                            }
                            if (!brMatched)
                            {
                                goto Fail;
                            }

                            pos += len;
                            pc++;
                            continue;
                        }
                    case RegexOp.Lookaround:
                        {
                            var sub = prog.Subs[ins.Arg1];
                            bool negative = (ins.Arg2 & 1) != 0;
                            bool behind = (ins.Arg2 & 2) != 0;
                            bool matched;
                            if (behind)
                            {
                                // Try matches that end at pos. Scan starting positions
                                // from 0 to pos. Allocate subSlots *once* outside the
                                // loop to avoid O(pos) allocations on long inputs.
                                var subSlotCount = (sub.CaptureCount + 1) * 2;
                                var subSlots = new int[subSlotCount];
                                matched = false;
                                for (var i = 0; i <= pos; i++)
                                {
                                    for (var k = 0; k < subSlotCount; k++)
                                    {
                                        subSlots[k] = -1;
                                    }

                                    if (TryMatch(sub, input, i, 0, subSlots, out var endP, out _) && endP == pos)
                                    {
                                        matched = true; break;
                                    }
                                }
                            }
                            else
                            {
                                var subSlots = new int[(sub.CaptureCount + 1) * 2];
                                for (var k = 0; k < subSlots.Length; k++)
                                {
                                    subSlots[k] = -1;
                                }

                                matched = TryMatch(sub, input, pos, 0, subSlots, out _, out _);
                            }
                            if (matched != negative) { pc++; continue; }
                            goto Fail;
                        }
                    default:
                        goto Fail;
                }
            }
            // pc ran off the end without a Match instruction — treat as failure.
        Fail:
            if (backtrack is null || backtrack.Count == 0)
            {
                outSlots = slots;
                return false;
            }
            var bt = backtrack.Pop();
            pos = bt.Pos;
            pc = bt.Pc;
            slots = bt.Slots;
        }
    }
}
