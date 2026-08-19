#!/usr/bin/env bash
# F7 — smoke test for the CI gate. Constructs trace fixtures under a temporary directory
# and asserts that analyze-traces.sh --gate fails on (a) unhandled MCP errors, (b) variance
# above threshold and (c) a failed mission, and passes when everything is clean. No LLM or
# Docker required, so it runs in any environment.
set -uo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ANALYZE="$SCRIPT_DIR/analyze-traces.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "=================================================="
echo "F7 Smoke Test: analyze-traces.sh --gate"
echo "=================================================="
echo

FAILS=0

# Each scenario gets an isolated traces/variance dir to avoid cross-case contamination.
run_gate() {
    local desc="$1"; local expect="$2"; local traces_dir="$3"; local variance_file="$4"; shift 4
    TRACES_DIR="$traces_dir" VARIANCE_FILE="$variance_file" bash "$ANALYZE" --gate "$@" >/dev/null 2>&1
    check "$desc" "$expect" "$?"
}

check() {
    local desc="$1"; local expect="$2"; local actual="$3"
    if [ "$expect" = "$actual" ]; then
         echo "    ✅ $desc (exit=$actual)"
    else
         echo "    ❌ $desc (expected exit=$expect, got $actual)"
         FAILS=$((FAILS + 1))
    fi
}

new_dir() {
    local d="$TMP/case_$RANDOM"
    mkdir -p "$d/traces/gpt-oss_20b"
     : > "$d/variance_results.jsonl"
    echo "$d"
}

# ---- Case 1: clean run passes the gate -------------------------------------
d=$(new_dir)
cat > "$d/traces/gpt-oss_20b/Clean_Mission.md" <<'EOF'
# Agentic Mission Trace: Clean_Mission
## Final Summary
- **Status**: 🟢 **SUCCESS**
- **Iterations**: 3
EOF
run_gate "clean run passes gate" "0" "$d/traces" "$d/variance_results.jsonl" --threshold=0

# ---- Case 2: unhandled MCP error fails the gate -----------------------------
d=$(new_dir)
cat > "$d/traces/gpt-oss_20b/Bad_Mcp_Error.md" <<'EOF'
# Agentic Mission Trace: Bad_Mcp_Error
## Trajectory Log
⚠️ MCP tool returned an error: invalid yaml
## Final Summary
- **Status**: 🟢 **SUCCESS**
EOF
run_gate "unhandled MCP error fails gate" "1" "$d/traces" "$d/variance_results.jsonl" --threshold=0

# ---- Case 3: variance above threshold fails the gate ------------------------
d=$(new_dir)
cat > "$d/traces/gpt-oss_20b/Clean_Mission.md" <<'EOF'
# Agentic Mission Trace: Clean_Mission
## Final Summary
- **Status**: 🟢 **SUCCESS**
EOF
cat >> "$d/variance_results.jsonl" <<'EOF'
{"model":"gpt-oss_20b","mission":"Clean_Mission","repetitions":3,"variance":2}
EOF
run_gate "variance above threshold fails gate" "1" "$d/traces" "$d/variance_results.jsonl" --threshold=0

# ---- Case 4: mission failure fails the gate ---------------------------------
d=$(new_dir)
cat > "$d/traces/gpt-oss_20b/Failed_Mission.md" <<'EOF'
# Agentic Mission Trace: Failed_Mission
## Final Summary
- **Status**: ❌ **FAILURE (Validation Failed)**
EOF
run_gate "mission failure fails gate" "1" "$d/traces" "$d/variance_results.jsonl"

# ---- Case 5: clean run with variance within threshold passes the gate --------
d=$(new_dir)
cat > "$d/traces/gpt-oss_20b/Clean_Mission.md" <<'EOF'
# Agentic Mission Trace: Clean_Mission
## Final Summary
- **Status**: 🟢 **SUCCESS**
EOF
cat >> "$d/variance_results.jsonl" <<'EOF'
{"model":"gpt-oss_20b","mission":"Clean_Mission","repetitions":3,"variance":0}
EOF
run_gate "variance within threshold passes gate" "0" "$d/traces" "$d/variance_results.jsonl" --threshold=0

echo
if [ "$FAILS" -eq 0 ]; then
    echo "🎉 F7 gate smoke test: ALL PASSED"
    exit 0
else
    echo "❌ F7 gate smoke test: $FAILS FAILED"
    exit 1
fi
