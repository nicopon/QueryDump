#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REPO_ROOT="$( cd "$SCRIPT_DIR/../.." && pwd )"

# Read model arguments and gate flag.
GATE=false
GATE_ARGS=()
MODELS=()
for arg in "$@"; do
    case "$arg" in
         --gate)
            GATE=true
            GATE_ARGS+=(--gate)
             ;;
         --threshold=*)
            GATE_ARGS+=(--threshold="${arg#--threshold=}")
             ;;
         --max-mcp-errors=*)
            GATE_ARGS+=(--max-mcp-errors="${arg#--max-mcp-errors=}")
             ;;
         *)
            MODELS+=("$arg")
             ;;
    esac
done
if [ ${#MODELS[@]} -eq 0 ]; then
    MODELS=("${OLLAMA_MODEL:-gemma4:12b-mlx}")
fi

FAILED=false

# (F3/F7) When gated, run each mission with determinism replication and collect the
# observed variance so analyze-traces.sh can enforce the threshold.
VARIANCE_FILE="$REPO_ROOT/tests/agentic/artifacts/variance_results.jsonl"
if [ "$GATE" = "true" ]; then
    : > "$VARIANCE_FILE"
fi

for MODEL in "${MODELS[@]}"; do
    echo "=================================================="
    echo "Starting Agentic E2E Test Suite"
    echo "Model: $MODEL"
    echo "=================================================="
    echo

     AGENTIC_AGENT_REPEAT=1
     [ "$GATE" = "true" ] && AGENTIC_AGENT_REPEAT="${GATE_REPEAT:-3}"
     AGENTIC_AGENT_REPEAT="$AGENTIC_AGENT_REPEAT" "$SCRIPT_DIR/test-mission-aggregation.sh" "$MODEL" || FAILED=true
     AGENTIC_AGENT_REPEAT="$AGENTIC_AGENT_REPEAT" "$SCRIPT_DIR/test-mission-anonymisation.sh" "$MODEL" || FAILED=true
     AGENTIC_AGENT_REPEAT="$AGENTIC_AGENT_REPEAT" "$SCRIPT_DIR/test-mission-join.sh" "$MODEL" || FAILED=true
     AGENTIC_AGENT_REPEAT="$AGENTIC_AGENT_REPEAT" "$SCRIPT_DIR/test-mission-yaml.sh" "$MODEL" || FAILED=true
     AGENTIC_AGENT_REPEAT="$AGENTIC_AGENT_REPEAT" "$SCRIPT_DIR/test-mission-mcp-enrichment.sh" "$MODEL" || FAILED=true

    echo "🔌 Unloading model: $MODEL from Ollama memory..."
    curl -s -X POST http://localhost:11434/api/generate -d "{\"model\": \"$MODEL\", \"keep_alive\": 0}" > /dev/null

    echo "💤 Sleeping 5 seconds..."
    sleep 5
    echo
done

# Print final comparative report
"$SCRIPT_DIR/print-benchmark.sh"

# (F7) When gated, run the fail-closed analyzer over the produced traces.
if [ "$GATE" = "true" ]; then
    echo
    VARIANCE_FILE="$VARIANCE_FILE" "$SCRIPT_DIR/analyze-traces.sh" "${GATE_ARGS[@]}" || FAILED=true
fi

if [ "$FAILED" = "true" ]; then
    echo "❌ SOME AGENTIC E2E MISSIONS FAILED!"
    exit 1
else
    echo "🎉 ALL AGENTIC E2E MISSIONS SUCCEEDED!"
fi
