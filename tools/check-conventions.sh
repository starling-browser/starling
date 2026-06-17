#!/usr/bin/env bash
# Diff-scoped convention gate. Looks only at lines ADDED on this branch (vs a
# base ref), so it never trips on the existing codebase — only on new code.
#
# Enforces two project rules the build/analyzers do not yet catch repo-wide:
#   1. No null-forgiving operator (the postfix `!`).
#   2. Always use braces on single-line control statements.
# And warns (does not fail) when src/ changes without a matching test change.
#
# Usage: tools/check-conventions.sh [base-ref]      # base-ref defaults to origin/main
#
# Why diff-scoped: the engine still has ~334 null-forgiving uses and many
# brace-less statements. A repo-wide gate would require a large (and, for the
# `!` operator, semantically risky) cleanup first, and the build cannot enforce
# braces today because one project (Starling.Telemetry.Daemon) does not compile.
# Gating only added lines stops the bleed without that churn; flip these to
# build-enforced (IDE0011 = error + a null-forgiving analyzer) once the repo is
# clean and that project builds.
set -uo pipefail

base="${1:-origin/main}"
range="${base}...HEAD"
status=0

# Added .cs lines under src/, as "path:line:text" records.
added_src() {
  git diff --unified=0 "$range" -- src \
    | awk '
        /^\+\+\+ / { f = $2; sub(/^b\//, "", f); next }
        f !~ /\.cs$/ { next }
        /^@@ / { match($0, /\+[0-9]+/); ln = substr($0, RSTART + 1, RLENGTH - 1) - 1; next }
        /^\+/  { ln++; print f ":" ln ":" substr($0, 2) }
      '
}

echo "== convention gate (added lines vs ${base}) =="

# --- 1. null-forgiving operator -----------------------------------------------
# A word char / ) / ] immediately followed by `!` (the postfix null-forgiving
# operator), excluding the `!=` operator. `]` is placed first in the bracket
# expression so it is treated as a literal. Strip // comments first so prose
# never matches.
nf=$(added_src | sed 's://.*$::' \
      | grep -E '[]A-Za-z0-9_)]\!' \
      | grep -vE '\!=' || true)
if [ -n "$nf" ]; then
  echo "✗ null-forgiving operator (!) is banned — handle null explicitly:"
  printf '%s\n' "$nf" | sed 's/^/    /'
  status=1
fi

# --- 2. brace-less single-line control statements -----------------------------
# Conservative: only the `if/else if/for/foreach/while (...) <stmt>` and
# `else <stmt>` one-liners, which are unambiguous. Multi-line bodies are left
# to the analyzer follow-up.
braces=$(added_src | sed 's://.*$::' \
      | grep -E ':[[:space:]]*(if|else if|for|foreach|while)[[:space:]]*\(.*\)[[:space:]]*[A-Za-z_"]' || true)
elses=$(added_src | sed 's://.*$::' \
      | grep -E ':[[:space:]]*else[[:space:]]+[A-Za-z_"]' \
      | grep -vE '[[:space:]]else[[:space:]]+if' || true)
all_braces="${braces}"$'\n'"${elses}"
if printf '%s' "$all_braces" | grep -qE '[A-Za-z]'; then
  echo "✗ control statement without braces — always use braces:"
  printf '%s\n' "$all_braces" | sed '/^[[:space:]]*$/d; s/^/    /'
  status=1
fi

# --- 3. test-presence (warn only) ---------------------------------------------
src_changed=$(git diff --name-only "$range" -- src | grep -cE '\.cs$' || true)
test_changed=$(git diff --name-only "$range" -- tests | grep -cE '\.cs$' || true)
if [ "$src_changed" -gt 0 ] && [ "$test_changed" -eq 0 ]; then
  echo "::warning::src/ changed but no test project changed — every fix should ship with a test (AGENTS.md)."
fi

if [ "$status" -eq 0 ]; then
  echo "✓ no new null-forgiving operators or brace-less statements"
fi
exit "$status"
