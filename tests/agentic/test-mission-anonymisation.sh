#!/usr/bin/env bash
set -eo pipefail

# Determine directories to execute the script from any location
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REPO_ROOT="$( cd "$SCRIPT_DIR/../.." && pwd )"

# Go to repo root for consistent dotnet/dtpipe commands
cd "$REPO_ROOT"

# Verify jq is installed
if ! command -v jq &> /dev/null; then
    echo "[ERROR] 'jq' is required but not installed (run: brew install jq)."
    exit 1
fi

OLLAMA_URL="${OLLAMA_URL:-http://localhost:11434/api/chat}"

# Check if local Ollama is available
if ! curl -s -f "$OLLAMA_URL" &> /dev/null && [ "$OLLAMA_URL" = "http://localhost:11434/api/chat" ]; then
    # Test base URL if chat endpoint did not respond to GET
    if ! curl -s -f "http://localhost:11434" &> /dev/null; then
        echo "[WARNING] Ollama does not seem to be running locally on http://localhost:11434."
        echo "Make sure Ollama is started."
    fi
fi

# Query and list available models
echo "Listing available Ollama models:"
if curl -s -f "http://localhost:11434/api/tags" &> /dev/null; then
    curl -s http://localhost:11434/api/tags | jq -r '.models[].name' | sed 's/^/  - /'
else
    echo "  (Could not fetch models, Ollama might be offline)"
fi
echo

# Configure Ollama model (priority: 1st argument, then OLLAMA_MODEL env, then default)
MODEL="${1:-${OLLAMA_MODEL:-gemma4:12b-mlx}}"

# --- 1. PREPARE TEST DATA ---
echo "1. Creating test source data..."
SOURCE_FILE="tests/agentic/test_source.csv"
TARGET_FILE="tests/agentic/test_target.csv"

# Clean up old files
rm -f "$SOURCE_FILE" "$TARGET_FILE"
mkdir -p "tests/agentic"

cat <<EOF > "$SOURCE_FILE"
id,name,email
1,Jean Dupont,jean.dupont@gmail.com
2,Alice Martin,alice.martin@yahoo.fr
3,Bob Vance,bob.vance@vancerefrigeration.com
EOF

# --- 2. START MCP SERVER VIA FIFOS ---
echo "2. Initializing bidirectional communication channel with MCP server..."
FIFO_IN="tests/agentic/mcp_in"
FIFO_OUT="tests/agentic/mcp_out"

# Clean up old pipes
rm -f "$FIFO_IN" "$FIFO_OUT"
mkfifo "$FIFO_IN" "$FIFO_OUT"

# Launch MCP server in background connected to FIFOs
dotnet run --project src/DtPipe/DtPipe.csproj -- mcp < "$FIFO_IN" > "$FIFO_OUT" &
MCP_PID=$!

# Open file descriptors (3 for writing, 4 for reading)
exec 3> "$FIFO_IN"
exec 4< "$FIFO_OUT"

# Function to interact with the MCP server
call_mcp() {
    local payload="$1"
    echo "$payload" >&3
    # MCP server writes its responses line-by-line (NDJSON)
    read -r response <&4
    echo "$response"
}

# Required initialization handshake to start MCP session
echo "-> Initializing MCP session..."
INIT_RESPONSE=$(call_mcp '{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"tester","version":"1.0.0"}}}')

# Retrieve tool list
echo "-> Retrieving MCP tools list..."
TOOLS_RESPONSE=$(call_mcp '{"jsonrpc":"2.0","method":"tools/list","id":2}')

# Filter out validate-pipeline and list-providers to guide the agent cleanly
TOOLS_JSON=$(echo "$TOOLS_RESPONSE" | jq '
  .result.tools | 
  map(select(.name != "validate-pipeline" and .name != "list-providers")) |
  map({
    type: "function",
    function: {
      name: .name,
      description: .description,
      parameters: .inputSchema
    }
  })
')

# --- 3. AGENTIC ReAct LOOP ---
echo "3. Starting mission with Ollama model: $MODEL..."

# History of messages for the LLM
MESSAGES_JSON=$(jq -n --arg src "$SOURCE_FILE" --arg tgt "$TARGET_FILE" '[
  {
    "role": "system",
    "content": "You are a data integration agent. Analyze user requests and use the most appropriate MCP tools at your disposal to complete the task from end to end."
  },
  {
    "role": "user",
    "content": ("Migrate data from '\''csv:" + $src + "'\'' to '\''csv:" + $tgt + "'\'' and mask the '\''email'\'' column to anonymize it.")
  }
]')

ITERATION=1
MAX_ITERATIONS=10
SUCCESS=false

while [ $ITERATION -le $MAX_ITERATIONS ]; do
    echo "--- Iteration $ITERATION ---"
    
    # Call the LLM with context and tools
    OLLAMA_RESPONSE=$(curl -s "$OLLAMA_URL" -d "{
      \"model\": \"$MODEL\",
      \"messages\": $MESSAGES_JSON,
      \"tools\": $TOOLS_JSON,
      \"stream\": false
    }")

    # Check for Ollama errors
    ERR_MSG=$(echo "$OLLAMA_RESPONSE" | jq -r '.error // empty')
    if [ ! -z "$ERR_MSG" ]; then
        echo "❌ Ollama Error: $ERR_MSG"
        break
    fi

    MESSAGE_OUT=$(echo "$OLLAMA_RESPONSE" | jq '.message')
    TOOL_CALLS=$(echo "$MESSAGE_OUT" | jq '.tool_calls')

    # Update history with assistant message
    MESSAGES_JSON=$(echo "$MESSAGES_JSON" | jq --argjson msg "$MESSAGE_OUT" '. + [$msg]')

    # If no tool calls, the agent considered its work done
    if [ "$TOOL_CALLS" = "null" ] || [ "$(echo "$TOOL_CALLS" | jq '. | length')" -eq 0 ]; then
        echo "Agent considers its mission complete."
        echo "Final message: $(echo "$MESSAGE_OUT" | jq -r '.content')"
        SUCCESS=true
        break
    fi

    # Process first tool call (Ollama usually outputs them one by one)
    TOOL_CALL=$(echo "$TOOL_CALLS" | jq '.[0]')
    TOOL_NAME=$(echo "$TOOL_CALL" | jq -r '.function.name')
    TOOL_ARGS=$(echo "$TOOL_CALL" | jq -c '.function.arguments | if type == "string" then fromjson else . end')

    echo "Agent calls tool: $TOOL_NAME with args: $TOOL_ARGS"

    # Execute tool on MCP server
    TOOL_EXEC_RESPONSE=$(call_mcp "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"id\":$((ITERATION + 2)),\"params\":{\"name\":\"$TOOL_NAME\",\"arguments\":$TOOL_ARGS}}")

    # Extract tool result
    TOOL_RESULT=$(echo "$TOOL_EXEC_RESPONSE" | jq -r '.result.content[0].text // .result')

    # Log tool errors if any
    ERR_VAL=$(echo "$TOOL_EXEC_RESPONSE" | jq -r '.error // empty')
    if [ ! -z "$ERR_VAL" ]; then
        echo "⚠️ MCP tool returned an error: $ERR_VAL"
        TOOL_RESULT=$(echo "$TOOL_EXEC_RESPONSE" | jq -c '.error')
    fi

    echo "Tool result: $TOOL_RESULT"

    # Add tool result to message history
    MESSAGES_JSON=$(echo "$MESSAGES_JSON" | jq --arg name "$TOOL_NAME" --arg content "$TOOL_RESULT" '. + [{
      "role": "tool",
      "name": $name,
      "content": $content
    }]')

    ITERATION=$((ITERATION + 1))
done

# --- 4. CLOSE AND CLEAN UP MCP SERVER ---
echo "Closing MCP server..."
kill "$MCP_PID" || true
exec 3>&- || true
exec 4<&- || true
rm -f "$FIFO_IN" "$FIFO_OUT"

# If the loop failed
if [ "$SUCCESS" != "true" ]; then
    echo "❌ FAILURE: The LLM dialog loop did not complete successfully."
    exit 1
fi

# --- 5. OBJECTIVE VALIDATION (TARGET DATA) ---
echo "5. Verifying target data..."

if [ ! -f "$TARGET_FILE" ]; then
    echo "❌ FAILURE: Target file '$TARGET_FILE' was not generated."
    exit 1
fi

# Validate line count
LINE_COUNT=$(wc -l < "$TARGET_FILE" | xargs)
if [ "$LINE_COUNT" -ne 4 ]; then # Header + 3 rows = 4 lines
    echo "❌ FAILURE: Target file has $LINE_COUNT lines instead of 4."
    exit 1
fi

# Verify email masking
EMAILS_OK=true
while IFS=, read -r id name email; do
    # Skip header
    if [ "$id" = "id" ]; then continue; fi
    
    if [ "$email" = "jean.dupont@gmail.com" ] || [ "$email" = "alice.martin@yahoo.fr" ] || [ "$email" = "bob.vance@vancerefrigeration.com" ] || [ -z "$email" ]; then
        echo "❌ FAILURE: Email in row $id was not masked/anonymized: $email"
        EMAILS_OK=false
    fi
done < "$TARGET_FILE"

# Clean up temp files
rm -f "$SOURCE_FILE" "$TARGET_FILE"

if [ "$EMAILS_OK" = true ]; then
    echo "🎉 MISSION COMPLETE: The pipeline executed successfully and data targets conform to expectations!"
    exit 0
else
    exit 1
fi
