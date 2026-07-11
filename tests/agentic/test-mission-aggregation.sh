#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
source "$SCRIPT_DIR/agent_runner.sh"

setup_data() {
    echo "Creating test source data for Aggregation and Compute mission (JSONL)..."
    mkdir -p "tests/agentic/artifacts"
    rm -f "tests/agentic/artifacts/invoices.jsonl" "tests/agentic/artifacts/high_invoices.jsonl"

    # Temp CSV for invoices
    local INVOICES_TEMP="tests/agentic/artifacts/invoices_temp.csv"
    cat <<EOF > "$INVOICES_TEMP"
invoice_ref,subtotal,tax_multiplier
I-901,100.0,0.2
I-902,200.0,0.1
I-903,50.0,0.0
EOF

    # Import invoices into JSONL
    dotnet run --project src/DtPipe/DtPipe.csproj -- -i "csv:$INVOICES_TEMP" -o "jsonl:tests/agentic/artifacts/invoices.jsonl"
    rm -f "$INVOICES_TEMP"
}

validate_data() {
    local TARGET_FILE="tests/agentic/artifacts/high_invoices.jsonl"

    if [ ! -f "$TARGET_FILE" ]; then
        echo "❌ FAILURE: Target file '$TARGET_FILE' was not generated."
        return 1
    fi

    # Validate row count (JSONL has one line per JSON object)
    # Expected rows: I-901 (100 * 1.2 = 120 > 100), I-902 (200 * 1.1 = 220 > 100). I-903 filtered.
    local LINE_COUNT=$(wc -l < "$TARGET_FILE" | xargs)
    if [ "$LINE_COUNT" -ne 2 ]; then
        echo "❌ FAILURE: Target file has $LINE_COUNT lines instead of 2."
        return 1
    fi

    # Verify computed totals and filters
    local COMPUTES_OK=true
    while read -r line; do
        if [ -z "$line" ]; then continue; fi
        local invoice_ref=$(echo "$line" | jq -r '.invoice_ref')
        local gross_total=$(echo "$line" | jq -r '.gross_total')

        if [ "$invoice_ref" = "I-901" ]; then
            # gross_total should be 120
            if (( $(echo "$gross_total <= 100" | bc -l) )); then
                echo "❌ FAILURE: Invoice I-901 gross_total computed as '$gross_total' but expected ~120."
                COMPUTES_OK=false
            fi
        fi
        if [ "$invoice_ref" = "I-902" ]; then
            # gross_total should be 220
            if (( $(echo "$gross_total <= 100" | bc -l) )); then
                echo "❌ FAILURE: Invoice I-902 gross_total computed as '$gross_total' but expected ~220."
                COMPUTES_OK=false
            fi
        fi
        if [ "$invoice_ref" = "I-903" ]; then
            echo "❌ FAILURE: Invoice I-903 (gross_total 50) should have been filtered out but was found."
            COMPUTES_OK=false
        fi
    done < "$TARGET_FILE"

    # Clean up artifacts
    rm -f "tests/agentic/artifacts/invoices.jsonl" "tests/agentic/artifacts/high_invoices.jsonl"

    if [ "$COMPUTES_OK" = true ]; then
        return 0
    else
        return 1
    fi
}

run_mission \
    "Fuzzy Compute and Filter" \
    "We have an invoices file at 'jsonl:tests/agentic/artifacts/invoices.jsonl'. Your tasks are: 1. Inspect its schema to identify the fields representing the subtotal and the tax multiplier. 2. Compute a new column 'gross_total' using JavaScript, calculating the subtotal multiplied by (1 + tax_multiplier). Hint: make sure to parse them as floats. 3. Filter the results to keep only the invoices where the gross total is strictly greater than 100. 4. Configure a YAML job block to perform these tasks (using 'compute' and 'filter' transformers) and save the result to 'jsonl:tests/agentic/artifacts/high_invoices.jsonl'. Execute the pipeline by calling the 'execute-yaml-job' tool." \
    setup_data \
    validate_data \
    "$1"
