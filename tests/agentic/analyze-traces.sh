#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REPO_ROOT="$( cd "$SCRIPT_DIR/../.." && pwd )"
TRACES_DIR="$REPO_ROOT/tests/agentic/artifacts/traces"

if [ ! -d "$TRACES_DIR" ]; then
    echo "ℹ️ No trace directory found at '$TRACES_DIR'. Run some agentic tests first."
    exit 0
fi

echo "=========================================================================================="
echo "                            DTPIPE AGENT TRACE ANALYSIS & DIAGNOSTICS                    "
echo "=========================================================================================="
echo

MODELS=$(ls "$TRACES_DIR")

for MODEL in $MODELS; do
    echo "🤖 Model: $MODEL"
    echo "------------------------------------------------------------------------------------------"
    
    TRACES=$(find "$TRACES_DIR/$MODEL" -name "*.md")
    
    for TRACE in $TRACES; do
        MISSION_NAME=$(basename "$TRACE" .md)
        STATUS=$(grep -E "Status" "$TRACE" | tail -n 1 || echo "Unknown")
        
        echo "  📌 Mission: $MISSION_NAME | $STATUS"
        
        # Check for tool errors in the trace
        ERRORS=$(grep -i "error" "$TRACE" | grep -v "Ollama Error" | head -n 5 || true)
        if [ ! -z "$ERRORS" ]; then
            echo "     ⚠️ Detected MCP Errors/Warnings:"
            echo "$ERRORS" | sed 's/^/       /g'
        fi
        
        # Check for repeated tool calls
        REPEATED=$(grep "\*\*Tool Call\*\*" "$TRACE" | sort | uniq -c | sort -nr | head -n 3)
        echo "     🔄 Top Tool Calls:"
        echo "$REPEATED" | sed 's/^/       /g'
        echo
    done
    echo
done
