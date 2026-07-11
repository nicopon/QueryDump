#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
source "$SCRIPT_DIR/agent_runner.sh"

setup_data() {
    echo "Creating test source data in JSONL format..."
    mkdir -p "tests/agentic/artifacts"
    rm -f "tests/agentic/artifacts/users_raw.jsonl" "tests/agentic/artifacts/users_clean.jsonl"

    # Write a temporary CSV and import it to JSONL using dtpipe
    local TEMP_CSV="tests/agentic/artifacts/users_temp.csv"
    cat <<EOF > "$TEMP_CSV"
id,full_name,contact_email
1,Jean Dupont,jean.dupont@gmail.com
2,Alice Martin,alice.martin@yahoo.fr
3,Bob Vance,bob.vance@vancerefrigeration.com
EOF

    dotnet run --project src/DtPipe/DtPipe.csproj -- -i "csv:$TEMP_CSV" -o "jsonl:tests/agentic/artifacts/users_raw.jsonl"
    rm -f "$TEMP_CSV"
}

validate_data() {
    local TARGET_FILE="tests/agentic/artifacts/users_clean.jsonl"
    
    if [ ! -f "$TARGET_FILE" ]; then
        echo "❌ FAILURE: Target file '$TARGET_FILE' was not generated."
        return 1
    fi

    # Validate row count (JSONL has one line per JSON object)
    local LINE_COUNT=$(wc -l < "$TARGET_FILE" | xargs)
    if [ "$LINE_COUNT" -ne 3 ]; then
        echo "❌ FAILURE: Target file has $LINE_COUNT lines instead of 3."
        return 1
    fi

    # Verify email masking
    local EMAILS_OK=true
    while read -r line; do
        if [ -z "$line" ]; then continue; fi
        local email=$(echo "$line" | jq -r '.contact_email')
        local id=$(echo "$line" | jq -r '.id')
        
        if [ "$email" = "jean.dupont@gmail.com" ] || [ "$email" = "alice.martin@yahoo.fr" ] || [ "$email" = "bob.vance@vancerefrigeration.com" ] || [ -z "$email" ] || [ "$email" = "null" ]; then
            echo "❌ FAILURE: Email in row $id was not masked/anonymized: $email"
            EMAILS_OK=false
        fi
    done < "$TARGET_FILE"

    # Clean up artifacts
    rm -f "tests/agentic/artifacts/users_raw.jsonl" "tests/agentic/artifacts/users_clean.jsonl"

    if [ "$EMAILS_OK" = true ]; then
        return 0
    else
        return 1
    fi
}

# Run the mission with a fuzzy description
run_mission \
    "Fuzzy Email Anonymization" \
    "There is a JSONL file at 'jsonl:tests/agentic/artifacts/users_raw.jsonl' containing registered user accounts. Your task is to: 1. Inspect the file to find the column containing the user's email address. 2. Configure a YAML job block to anonymize that column using the 'internet.email' Bogus faker and save the result to 'jsonl:tests/agentic/artifacts/users_clean.jsonl'. 3. Execute the pipeline by calling the 'execute-yaml-job' tool." \
    setup_data \
    validate_data \
    "$1"
