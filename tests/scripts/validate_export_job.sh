#!/bin/bash
set -e

# validate_export_job.sh
# F3 — --export-job round-trip invariant.
# For three pipelines (fake+filter linear, SQL DAG, incremental cursor):
#   1. export the CLI pipeline to YAML (--export-job)
#   2. run the CLI pipeline directly (--metrics-path cli.json)
#   3. run the exported YAML (--metrics-path yaml.json)
#   4. assert identical ReadCount/WriteCount (wall-clock/memory fields ignored)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/export_job"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Export-Job Round-Trip"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

A="$ARTIFACTS_DIR"

cleanup() { rm -rf "$A"; }
trap cleanup EXIT

metric_value() { # file, key
    grep "\"$2\"" "$1" | head -1 | sed -E 's/[^0-9]*([0-9]+).*/\1/'
}

assert_same_metrics() { # name, cli.json, yaml.json
    local name="$1"
    for key in ReadCount WriteCount; do
        local cli_val yaml_val
        cli_val=$(metric_value "$2" "$key")
        yaml_val=$(metric_value "$3" "$key")
        if [ -z "$cli_val" ] || [ -z "$yaml_val" ]; then
            fail "[$name] metric '$key' missing (cli='$cli_val' yaml='$yaml_val')"
        fi
        if [ "$cli_val" != "$yaml_val" ]; then
            fail "[$name] $key differs: cli=$cli_val yaml=$yaml_val"
        fi
    done
    pass "[$name] metrics identical (read=$(metric_value "$2" ReadCount), write=$(metric_value "$2" WriteCount))"
}

# Run a pipeline twice (direct CLI vs exported YAML) and compare results.
# Comparison modes:
#   metrics — compare --metrics-path JSON ReadCount/WriteCount (single-branch pipelines;
#             in a DAG every branch writes the same metrics file, last writer wins)
#   output  — byte-compare the -o output of both runs (deterministic pipelines)
run_pair() { # name, mode, args...
    local name="$1"; local mode="$2"; shift 2
    rm -f "$A/${name}_cli.json" "$A/${name}_yaml.json"
    # 0. Snapshot previous outputs so both runs are compared independently.
    rm -f "$A/${name}_out_cli.csv" "$A/${name}_out_yaml.csv"
    # 1. Export to YAML
    "$DTPIPE" "$@" --export-job "$A/${name}.yaml" 2>/dev/null
    [ -s "$A/${name}.yaml" ] || fail "[$name] exported YAML is empty"
    # 2. Run CLI directly
    "$DTPIPE" "$@" --metrics-path "$A/${name}_cli.json" > /dev/null 2>&1 \
        || fail "[$name] direct CLI run failed"
    if [ -n "${OUTPUT_FILE:-}" ]; then cp "$OUTPUT_FILE" "$A/${name}_out_cli.csv"; fi
    # 3. Run via YAML job
    "$DTPIPE" --job "$A/${name}.yaml" --metrics-path "$A/${name}_yaml.json" > /dev/null 2>&1 \
        || fail "[$name] YAML job run failed"
    if [ -n "${OUTPUT_FILE:-}" ]; then cp "$OUTPUT_FILE" "$A/${name}_out_yaml.csv"; fi
    # 4. Compare
    if [ "$mode" = "metrics" ]; then
        assert_same_metrics "$name" "$A/${name}_cli.json" "$A/${name}_yaml.json"
    else
        if diff -q "$A/${name}_out_cli.csv" "$A/${name}_out_yaml.csv" > /dev/null; then
            pass "[$name] outputs byte-identical ($(wc -l < "$A/${name}_out_cli.csv" | tr -d ' ') lines)"
        else
            fail "[$name] outputs differ between CLI and YAML runs"
        fi
    fi
}

# ----------------------------------------
echo "--- [1] Linear pipeline with transformers ---"
run_pair linear_fake_filter metrics \
    -i generate:50 \
    --fake "Name:name.firstName" \
    --filter "row.GenerateIndex != null" \
    -o "$A/lin_out.csv" --no-stats

# ----------------------------------------
echo "--- [2] DAG with SQL processor ---"
cat > "$A/src.csv" <<EOF
Id,Val
1,a
2,b
3,c
4,d
5,e
EOF
OUTPUT_FILE="$A/sql_out.csv" run_pair dag_sql output \
    -i "csv:$A/src.csv" --column-types "Id:int32" --alias s \
    --from s --sql "SELECT Id, Val FROM s WHERE Id >= 2" \
    -o "$A/sql_out.csv" --no-stats

# ----------------------------------------
echo "--- [3] Incremental cursor ---"
cat > "$A/events.csv" <<EOF
Id,Kind
1,x
2,y
3,z
EOF
rm -f "$A/state.json"
OUTPUT_FILE="$A/cursor_out.csv" run_pair incremental_cursor output \
    -i "csv:$A/events.csv" --cursor Id --state "$A/state.json" \
    -o "$A/cursor_out.csv" --no-stats

echo ""
echo "All export-job round-trip checks passed."
