#!/usr/bin/env bash
# Common agentic ReAct loop test runner for dtpipe MCP with Trajectory Tracing

run_mission() {
    local MISSION_NAME="$1"
    local PROMPT="$2"
    local SETUP_FUNC="$3"
    local VALIDATE_FUNC="$4"
    local MODEL_ARG="$5"
    
    # Configure model (priority: argument to script, then OLLAMA_MODEL env, then default)
    local MODEL="${MODEL_ARG:-${OLLAMA_MODEL:-gemma4:12b-mlx}}"
    local OLLAMA_URL="${OLLAMA_URL:-http://localhost:11434/api/chat}"

    echo "=================================================="
    echo "Running Agentic Mission: $MISSION_NAME"
    echo "Model: $MODEL"
    echo "=================================================="

    # Verify jq
    if ! command -v jq &> /dev/null; then
        echo "[ERROR] 'jq' is required but not installed."
        exit 1
    fi

    # Set up directories and files
    local SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
    local REPO_ROOT="$( cd "$SCRIPT_DIR/../.." && pwd )"
    cd "$REPO_ROOT"

    # Setup test data
    $SETUP_FUNC

    # Start MCP Server via FIFOs
    local FIFO_NAME_SAFE="${MISSION_NAME// /_}"
    FIFO_NAME_SAFE="${FIFO_NAME_SAFE//\//_}"
    local FIFO_IN="tests/agentic/artifacts/mcp_in_${FIFO_NAME_SAFE}"
    local FIFO_OUT="tests/agentic/artifacts/mcp_out_${FIFO_NAME_SAFE}"
    mkdir -p "tests/agentic/artifacts"
    rm -f "$FIFO_IN" "$FIFO_OUT"
    mkfifo "$FIFO_IN" "$FIFO_OUT"

    # Harness consent (F2): explicitly approve writes for sanctioned test missions.
    export DTPIPE_MCP_APPROVE_WRITES=1
    dotnet run --project src/DtPipe/DtPipe.csproj -- mcp < "$FIFO_IN" > "$FIFO_OUT" &
    local MCP_PID=$!

    # Open file descriptors
    exec 3> "$FIFO_IN"
    exec 4< "$FIFO_OUT"

    call_mcp() {
        local payload="$1"
        echo "$payload" >&3
        local response
        read -r response <&4
        echo "$response"
    }

    # Setup Trace File
    local MODEL_SAFE="${MODEL//:/_}"
    MODEL_SAFE="${MODEL_SAFE//\//_}"
    local TRACE_DIR="tests/agentic/artifacts/traces/${MODEL_SAFE}"
    mkdir -p "$TRACE_DIR"
    local TRACE_FILE="${TRACE_DIR}/${FIFO_NAME_SAFE}.md"

    cat <<EOF > "$TRACE_FILE"
# Agentic Mission Trace: ${MISSION_NAME}
- **Model**: \`${MODEL}\`
- **Date**: $(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")

## Mission Prompt
> ${PROMPT}

## Trajectory Log

EOF

    # Initialize MCP
    local INIT_RESPONSE=$(call_mcp '{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"tester","version":"1.0.0"}}}')
    local TOOLS_RESPONSE=$(call_mcp '{"jsonrpc":"2.0","method":"tools/list","id":2}')

    local TOOLS_JSON=$(echo "$TOOLS_RESPONSE" | jq '
      .result.tools |
      map({
        type: "function",
        function: {
          name: .name,
          description: .description,
          parameters: .inputSchema
        }
      })
    ')

    # System prompt encouraging explicit reasoning and obstacle identification
    local SYSTEM_PROMPT="You are an expert data integration agent. Analyze user requests and use the most appropriate MCP tools to complete tasks end-to-end.
BEHAVIORAL REQUIREMENTS:
Before calling any tool or giving a final answer, state in text:
1. INTENT: What is your current sub-goal?
2. REASONING: Why did you choose this tool and arguments?
3. OBSTACLES / REFLECTION: If a previous tool call returned an error or unexpected result, explain what went wrong and how you will adjust your approach."

    # Prepare ReAct loop
    local MESSAGES_JSON=$(jq -n --arg sys "$SYSTEM_PROMPT" --arg prompt "$PROMPT" '[
      {
        "role": "system",
        "content": $sys
      },
      {
        "role": "user",
        "content": $prompt
      }
    ]')

    local ITERATION=1
    local MAX_ITERATIONS=25
    local SUCCESS=false
    local TOOL_COUNTS_JSON="{}"

    while [ $ITERATION -le $MAX_ITERATIONS ]; do
        echo "--- Iteration $ITERATION ---"
        
        local OLLAMA_RESPONSE=$(curl -s "$OLLAMA_URL" -d "{
          \"model\": \"$MODEL\",
          \"messages\": $MESSAGES_JSON,
          \"tools\": $TOOLS_JSON,
          \"options\": {
            \"num_ctx\": 16384
          },
          \"stream\": false
        }")

        local ERR_MSG=$(echo "$OLLAMA_RESPONSE" | jq -r '.error // empty')
        if [ ! -z "$ERR_MSG" ]; then
            echo "❌ Ollama Error: $ERR_MSG"
            echo "### Iteration $ITERATION (Ollama Error)" >> "$TRACE_FILE"
            echo "Error: \`$ERR_MSG\`" >> "$TRACE_FILE"
            echo >> "$TRACE_FILE"
            break
        fi

        local MESSAGE_OUT=$(echo "$OLLAMA_RESPONSE" | jq '.message')
        local CONTENT_TEXT=$(echo "$MESSAGE_OUT" | jq -r '.content // empty')
        local TOOL_CALLS=$(echo "$MESSAGE_OUT" | jq '.tool_calls')

        MESSAGES_JSON=$(echo "$MESSAGES_JSON" | jq --argjson msg "$MESSAGE_OUT" '. + [$msg]')

        echo "### Iteration $ITERATION" >> "$TRACE_FILE"
        if [ ! -z "$CONTENT_TEXT" ]; then
            echo "**Agent Thinking / Message**:" >> "$TRACE_FILE"
            echo "> ${CONTENT_TEXT//$'\n'/$'\n'> }" >> "$TRACE_FILE"
            echo >> "$TRACE_FILE"
        fi

        if [ "$TOOL_CALLS" = "null" ] || [ "$(echo "$TOOL_CALLS" | jq '. | length')" -eq 0 ]; then
            echo "Agent considers its mission complete."
            echo "Final message: $CONTENT_TEXT"
            echo "**Result**: Mission marked complete by agent." >> "$TRACE_FILE"
            echo >> "$TRACE_FILE"
            SUCCESS=true
            break
        fi

        local TOOL_CALL=$(echo "$TOOL_CALLS" | jq '.[0]')
        local TOOL_NAME=$(echo "$TOOL_CALL" | jq -r '.function.name')
        local TOOL_ARGS=$(echo "$TOOL_CALL" | jq -c '.function.arguments | if type == "string" then fromjson else . end')

        TOOL_COUNTS_JSON=$(echo "$TOOL_COUNTS_JSON" | jq --arg name "$TOOL_NAME" '.[$name] = (.[$name] // 0) + 1')

        echo "Agent calls tool: $TOOL_NAME with args: $TOOL_ARGS"
        echo "**Tool Call**: \`$TOOL_NAME\`" >> "$TRACE_FILE"
        echo "\`\`\`json" >> "$TRACE_FILE"
        echo "$TOOL_ARGS" | jq '.' >> "$TRACE_FILE" 2>/dev/null || echo "$TOOL_ARGS" >> "$TRACE_FILE"
        echo "\`\`\`" >> "$TRACE_FILE"
        echo >> "$TRACE_FILE"

        local TOOL_EXEC_RESPONSE=$(call_mcp "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"id\":$((ITERATION + 2)),\"params\":{\"name\":\"$TOOL_NAME\",\"arguments\":$TOOL_ARGS}}")
        local TOOL_RESULT=$(echo "$TOOL_EXEC_RESPONSE" | jq -r '.result.content[0].text // .result')

        local ERR_VAL=$(echo "$TOOL_EXEC_RESPONSE" | jq -r '.error // empty')
        if [ ! -z "$ERR_VAL" ]; then
            echo "⚠️ MCP tool returned an error: $ERR_VAL"
            TOOL_RESULT=$(echo "$TOOL_EXEC_RESPONSE" | jq -c '.error')
        fi

        echo "Tool result: $TOOL_RESULT"
        echo "**Tool Result**:" >> "$TRACE_FILE"
        echo "\`\`\`" >> "$TRACE_FILE"
        echo "$TOOL_RESULT" >> "$TRACE_FILE"
        echo "\`\`\`" >> "$TRACE_FILE"
        echo >> "$TRACE_FILE"

        MESSAGES_JSON=$(echo "$MESSAGES_JSON" | jq --arg name "$TOOL_NAME" --arg content "$TOOL_RESULT" '. + [{
          "role": "tool",
          "name": $name,
          "content": $content
        }]')

        ITERATION=$((ITERATION + 1))
    done

    # Clean up MCP server
    echo "Closing MCP server..."
    kill "$MCP_PID" || true
    exec 3>&- || true
    exec 4<&- || true
    rm -f "$FIFO_IN" "$FIFO_OUT"

    if [ "$SUCCESS" != "true" ]; then
        echo "❌ FAILURE: The ReAct loop did not complete successfully."
        echo "## Final Status" >> "$TRACE_FILE"
        echo "❌ **ReAct Loop Failed** (did not finish or exceeded max iterations)" >> "$TRACE_FILE"
        exit 1
    fi

    # Run validation
    echo "Validating target data..."
    local VALIDATION_SUCCESS=false
    if $VALIDATE_FUNC; then
        VALIDATION_SUCCESS=true
    fi

    # Append summary to trace file
    echo "## Final Summary" >> "$TRACE_FILE"
    if [ "$VALIDATION_SUCCESS" = "true" ]; then
        echo "- **Status**: 🟢 **SUCCESS**" >> "$TRACE_FILE"
    else
        echo "- **Status**: ❌ **FAILURE (Validation Failed)**" >> "$TRACE_FILE"
    fi
    echo "- **Iterations**: $ITERATION" >> "$TRACE_FILE"
    echo "- **Tool Calls**: \`$(echo "$TOOL_COUNTS_JSON" | jq -c '.')\`" >> "$TRACE_FILE"
    echo >> "$TRACE_FILE"

    # Persist benchmark stats
    local STATS_FILE="tests/agentic/artifacts/benchmark_results.jsonl"
    mkdir -p "$(dirname "$STATS_FILE")"

    local TS=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || echo "0")

    local ENTRY_JSON=$(jq -n \
        --arg ts "$TS" \
        --arg model "$MODEL" \
        --arg mission "$MISSION_NAME" \
        --argjson success "$VALIDATION_SUCCESS" \
        --argjson iter "$ITERATION" \
        --argjson tools "$TOOL_COUNTS_JSON" \
        '{timestamp: $ts, model: $model, mission: $mission, success: $success, iterations: $iter, tool_calls: $tools}')

    echo "$ENTRY_JSON" >> "$STATS_FILE"

     # (F3) Determinism variance is deliberately NOT recorded here.
     #
     # This runner drives its own ReAct loop in bash (curl -> Ollama, FIFO -> dtpipe mcp);
     # it never invokes `dtpipe agent`, so no DeterminismReport is produced and there is no
     # observed distinct-YAML count to report. An earlier version wrote a row using
     # ${AGENT_REPEATED_VARIANCE:-0} — a variable nothing ever exported — so every row said
     # "variance: 0" by construction and the gate's variance criterion could never fire.
     #
     # analyze-traces.sh treats absent variance data as "not a failure", so writing nothing is
     # both honest and safe: the criterion simply does not apply to these missions.
     #
     # To produce real variance data, the mission must run `dtpipe agent --repeat N` (which does
     # replicate the planning loop from a fresh conversation — AgentExecutor.cs) and emit the
     # resulting DeterminismReport.Variance into variance_results.jsonl.

    echo "📊 Execution Stats:"
    echo "  - Model: $MODEL"
    echo "  - Success: $VALIDATION_SUCCESS"
    echo "  - Iterations: $ITERATION"
    echo "  - Tool Calls: $(echo "$TOOL_COUNTS_JSON" | jq -c '.')"
    echo "  - Trace Log: $TRACE_FILE"
    echo

    if [ "$VALIDATION_SUCCESS" = "true" ]; then
        echo "🎉 MISSION SUCCESS: $MISSION_NAME completed successfully!"
        echo "=================================================="
        echo
        return 0
    else
        echo "❌ MISSION FAILURE: $MISSION_NAME validation failed!"
        echo "=================================================="
        echo
        exit 1
    fi
}
