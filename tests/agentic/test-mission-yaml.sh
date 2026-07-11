#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
source "$SCRIPT_DIR/agent_runner.sh"

setup_data() {
    echo "Creating test source data in CSV format..."
    mkdir -p "tests/agentic/artifacts"
    rm -f "tests/agentic/artifacts/users_raw.csv" "tests/agentic/artifacts/users_clean.csv"

    cat <<EOF > "tests/agentic/artifacts/users_raw.csv"
id,full_name,contact_email
1,Jean Dupont,jean.dupont@gmail.com
2,Alice Martin,alice.martin@yahoo.fr
3,Bob Vance,bob.vance@vancerefrigeration.com
EOF
}

validate_data() {
    local TARGET_FILE="tests/agentic/artifacts/users_clean.csv"
    
    if [ ! -f "$TARGET_FILE" ]; then
        echo "❌ FAILURE: Target file '$TARGET_FILE' was not generated."
        return 1
    fi

    # Validate row count
    local LINE_COUNT=$(wc -l < "$TARGET_FILE" | xargs)
    if [ "$LINE_COUNT" -ne 4 ]; then
        echo "❌ FAILURE: Target file has $LINE_COUNT lines instead of 4 (header + 3 data rows)."
        return 1
    fi

    # Verify name anonymization
    local NAMES_OK=true
    while IFS=, read -r id name email; do
        if [ "$id" = "id" ]; then continue; fi # Skip header
        if [ -z "$id" ]; then continue; fi
        
        if [ "$name" = "Jean Dupont" ] || [ "$name" = "Alice Martin" ] || [ "$name" = "Bob Vance" ] || [ -z "$name" ]; then
            echo "❌ FAILURE: Name in row $id was not anonymized: $name"
            NAMES_OK=false
        fi
    done < "$TARGET_FILE"

    # Clean up artifacts
    rm -f "tests/agentic/artifacts/users_raw.csv" "tests/agentic/artifacts/users_clean.csv"

    if [ "$NAMES_OK" = true ]; then
        return 0
    else
        return 1
    fi
}

run_mission \
    "Fuzzy YAML Job Execution" \
    "We have a CSV file at 'csv:tests/agentic/artifacts/users_raw.csv' containing users. Your task is to: 1. Configure a YAML job block that defines a branch 'main' that reads from 'csv:tests/agentic/artifacts/users_raw.csv', applies a transformer of type 'fake' to fake the 'full_name' column with 'name.fullName', and writes the result to 'csv:tests/agentic/artifacts/users_clean.csv'. 2. Execute the YAML configuration directly by calling the 'execute-yaml-job' tool. 3. Verify that the output was successfully generated." \
    setup_data \
    validate_data \
    "$1"
