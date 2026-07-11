#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REPO_ROOT="$( cd "$SCRIPT_DIR/../.." && pwd )"
STATS_FILE="$REPO_ROOT/tests/agentic/artifacts/benchmark_results.jsonl"

if [ "$1" = "--clear" ]; then
    rm -f "$STATS_FILE"
    echo "🧹 Benchmark database cleared."
    exit 0
fi

if [ ! -f "$STATS_FILE" ] || [ ! -s "$STATS_FILE" ]; then
    echo "ℹ️ No benchmark results found. Run some E2E tests first."
    exit 0
fi

echo "=========================================================================================="
echo "                                 DTPIPE AGENT BENCHMARK REPORT                            "
echo "=========================================================================================="
echo

# Format as a Markdown Table
echo "| Model | Mission | Success | Iterations | Tool Calls |"
echo "| :--- | :--- | :--- | :--- | :--- |"

# Parse the JSONL file and format as Markdown table rows
jq -r '
  [
    .model,
    .mission,
    (if .success then "🟢 PASS" else "❌ FAIL" end),
    (.iterations | tostring),
    (.tool_calls | to_entries | map("\(.key):\(.value)") | join(", "))
  ] | 
  "| " + join(" | ") + " |"
' "$STATS_FILE"

echo
