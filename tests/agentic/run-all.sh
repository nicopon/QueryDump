#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

MODEL="${1:-${OLLAMA_MODEL:-gemma4:12b-mlx}}"

echo "=================================================="
echo "Starting Agentic E2E Test Suite"
echo "Model: $MODEL"
echo "=================================================="
echo

"$SCRIPT_DIR/test-mission-anonymisation.sh" "$MODEL"
"$SCRIPT_DIR/test-mission-join.sh" "$MODEL"
"$SCRIPT_DIR/test-mission-aggregation.sh" "$MODEL"

echo "🎉 ALL AGENTIC E2E MISSIONS SUCCEEDED!"
