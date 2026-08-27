#!/bin/bash
set -e

# validate_channel_endpoints.sh
# F5 — typed channel endpoints end-to-end. Runs the 8 canonical DAG topologies and
# asserts correct row counts; plus a source guard ensuring the fan-out prefix literal
# exists exactly once (in IChannelNaming).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/channel_endpoints"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1
export DEBUG=1   # verbose branch logging so wiring problems surface in stderr

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Channel Endpoints Validation"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

A="$ARTIFACTS_DIR"

cleanup() { rm -f "$A"/*.csv "$A"/*.parquet "$A"/*.log; }
trap cleanup EXIT

csv_rows() { tail -n +2 "$1" | wc -l | tr -d ' '; }

expect_rows() { # file, expected, label
    local got
    got=$(csv_rows "$1")
    [ "$got" = "$2" ] || fail "$3: expected $2 rows, got $got"
    pass "$3 ($got rows)"
}

# Source-guard: the "__fan_" literal must appear in code only via IChannelNaming
# (doc-comment mentions elsewhere are fine).
fan_hits=$(grep -rn '__fan_' "$PROJECT_ROOT/src/DtPipe.Core" --include="*.cs" 2>/dev/null \
    | grep -v 'IChannelNaming.cs' \
    | grep -vE '^[^:]+:[0-9]+:\s*//' \
    | wc -l | tr -d ' ')
if [ "$fan_hits" != "0" ]; then
    fail "fan-out prefix literal found outside IChannelNaming ($fan_hits code occurrences)"
fi
pass "fan-out prefix has a single definition (IChannelNaming)"

cat > "$A/src.csv" <<EOF
Id,Val
1,a
2,b
3,c
4,d
EOF

# [1] Linear
"$DTPIPE" -i generate:7 -o "$A/t1.csv" --no-stats > /dev/null 2>&1 || fail "topology 1 (linear) failed"
expect_rows "$A/t1.csv" 7 "topology 1: linear"

# [2] SQL single
"$DTPIPE" -i "csv:$A/src.csv" --column-types "Id:int32" --alias s \
  --from s --sql "SELECT Id FROM s WHERE Id >= 2" \
  -o "$A/t2.csv" --no-stats > /dev/null 2>&1 || fail "topology 2 failed"
expect_rows "$A/t2.csv" 3 "topology 2: sql-single"

# [3] JOIN (--ref)
"$DTPIPE" -i "csv:$A/src.csv" --column-types "Id:int32" --alias m \
  -i "csv:$A/src.csv" --column-types "Id:int32" --alias r \
  --from m --ref r --sql "SELECT m.Id FROM m JOIN r ON m.Id = r.Id WHERE r.Id <= 3" \
  -o "$A/t3.csv" --no-stats > /dev/null 2>&1 || fail "topology 3 failed"
expect_rows "$A/t3.csv" 3 "topology 3: join"

# [4] Merge
"$DTPIPE" -i generate:4 --alias a -i generate:5 --alias b \
  --from a,b --merge -o "$A/t4.csv" --no-stats > /dev/null 2>&1 || fail "topology 4 failed"
expect_rows "$A/t4.csv" 9 "topology 4: merge"

# [5] Fan-out (tee)
# --no-stats is a global flag (GlobalOptions.NoStats), so it is spelled once for the whole
# run: repeating it per branch is rejected since the duplicate-flag policy landed.
"$DTPIPE" -i generate:6 --alias s \
  --from s -o "$A/t5a.csv" \
  --from s -o "$A/t5b.csv" --no-stats > /dev/null 2>&1 || fail "topology 5 failed"
expect_rows "$A/t5a.csv" 6 "topology 5a: fan-out consumer A"
expect_rows "$A/t5b.csv" 6 "topology 5b: fan-out consumer B"

# [6] Diamond: source → two sql branches → merged output
"$DTPIPE" -i "csv:$A/src.csv" --column-types "Id:int32" --alias src \
  --from src --sql "SELECT Id FROM src WHERE Id <= 3" --alias hi \
  --from src --sql "SELECT Id FROM src WHERE Id >= 2" --alias lo \
  --from hi,lo --merge \
  -o "$A/t6.csv" --no-stats > /dev/null 2>&1 || fail "topology 6 failed"
expect_rows "$A/t6.csv" 6 "topology 6: diamond"

# [7] Join feeding fan-out
"$DTPIPE" -i "csv:$A/src.csv" --column-types "Id:int32" --alias m \
  -i "csv:$A/src.csv" --column-types "Id:int32" --alias r \
  --from m --ref r --sql "SELECT m.Id FROM m JOIN r ON m.Id = r.Id" --alias joined \
  --from joined -o "$A/t7a.csv" \
  --from joined -o "$A/t7b.csv" --no-stats > /dev/null 2>&1 || fail "topology 7 failed"
expect_rows "$A/t7a.csv" 4 "topology 7a: join→fan-out A"
expect_rows "$A/t7b.csv" 4 "topology 7b: join→fan-out B"

# [8] Nested: generate → filter → alias chain → sql sink
"$DTPIPE" -i generate:10 --filter "row.GenerateIndex % 2 == 0" --alias even \
  --from even --sql "SELECT COUNT(*) AS cnt FROM even" \
  -o "$A/t8.csv" --no-stats > /dev/null 2>&1 || fail "topology 8 failed"
expect_rows "$A/t8.csv" 1 "topology 8: nested"

echo ""
echo "All channel endpoint checks passed."
