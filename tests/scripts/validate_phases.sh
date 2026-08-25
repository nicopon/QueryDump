#!/bin/bash
set -e

# validate_phases.sh
# P1-8 — execution-stack phases. Happy paths across 3 canonical shapes still succeed;
# failures are attributed to a named phase ([Preflight]/[Schema]/[Execution]) in DEBUG logs.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/phases"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Phase Decomposition Validation"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

A="$ARTIFACTS_DIR"
DEBUG=1 "$DTPIPE" -i generate:5 -o "$A/probe.csv" --no-stats > /dev/null 2>&1 || true
grep -q "\[Preflight\]" "$A/probe.csv" 2>/dev/null && true

cleanup() { rm -f "$A"/*.csv "$A"/*.log; }
trap cleanup EXIT

run_ok() { # label, args...
    local label="$1"; shift
    if "$DTPIPE" "$@" > /dev/null 2>&1; then
        pass "happy path: $label"
    else
        fail "happy path failed: $label"
    fi
}

# ── Happy paths ───────────────────────────────────────────────────────────
run_ok "linear"        -i generate:20 -o "$A/p1.csv" --no-stats

cat > "$A/src.csv" <<EOF
Id,Val
1,a
2,b
3,c
EOF
run_ok "sql-dag" \
    -i "csv:$A/src.csv" --column-types "Id:int32" --alias s \
    --from s --sql "SELECT Id FROM s WHERE Id >= 2" \
    -o "$A/p2.csv" --no-stats

cat > "$A/in.csv" <<'EOF'
a,b
1,x
2,y
EOF
run_ok "csv-pipe-csv" \
    -i "csv:$A/in.csv" \
    --project "a,b" \
    -o "$A/p3.csv" --no-stats

# ── Failure attribution ───────────────────────────────────────────────────
set +e
DEBUG=1 "$DTPIPE" -i "csv:$A/missing.csv" -o "$A/x.csv" --no-stats 2> "$A/err.log"
EXIT=$?
set -e
[ "$EXIT" -ne 0 ] || fail "expected failure for missing input"
if grep -qE "\[Preflight\]|\[Schema\]|\[Execution\]" "$A/err.log"; then
    phase=$(grep -oE "\[(Preflight|Schema|Execution)\]" "$A/err.log" | head -1)
    pass "failure attributed to a named phase ($phase)"
else
    fail "no phase attribution in error output"
fi

# ── Phase ordering sanity (Preflight precedes Execution) ──────────────────
DEBUG=1 "$DTPIPE" -i generate:5 -o "$A/order.csv" --no-stats 2> "$A/order.log" || fail "ordered run failed"
first=$(grep -oE "\[(Preflight|Schema|Execution)\]" "$A/order.log" | head -1)
[ "$first" = "[Preflight]" ] && pass "phases start with [Preflight]" || fail "unexpected first phase: $first"

echo ""
echo "All phase checks passed."
