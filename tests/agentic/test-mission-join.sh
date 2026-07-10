#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
source "$SCRIPT_DIR/agent_runner.sh"

setup_data() {
    echo "Creating test source data for Join mission (SQLite & Parquet)..."
    mkdir -p "tests/agentic/artifacts"
    
    local CUSTOMERS_DB="tests/agentic/artifacts/business.db"
    local SALES_PARQUET="tests/agentic/artifacts/sales.parquet"
    local TARGET_FILE="tests/agentic/artifacts/sales_report.csv"
    rm -f "$CUSTOMERS_DB" "$SALES_PARQUET" "$TARGET_FILE"

    # Temp CSV for clients
    local CLIENTS_TEMP="tests/agentic/artifacts/clients_temp.csv"
    cat <<EOF > "$CLIENTS_TEMP"
client_id,client_name,country
C-101,Jean Dupont,France
C-102,Alice Martin,Canada
C-103,Bob Vance,USA
EOF

    # Import clients into SQLite database under table 'company_clients'
    dotnet run --project src/DtPipe/DtPipe.csproj -- -i "csv:$CLIENTS_TEMP" -o "sqlite:Data Source=$CUSTOMERS_DB" --table "company_clients" --strategy Recreate
    rm -f "$CLIENTS_TEMP"

    # Temp CSV for sales
    local SALES_TEMP="tests/agentic/artifacts/sales_temp.csv"
    cat <<EOF > "$SALES_TEMP"
order_id,client_ref,product_name,amount
O-5001,C-101,Laptop,1200
O-5002,C-101,Mouse,25
O-5003,C-102,Keyboard,80
O-5004,C-999,Unknown,10
EOF

    # Import sales into Parquet file
    dotnet run --project src/DtPipe/DtPipe.csproj -- -i "csv:$SALES_TEMP" -o "parquet:$SALES_PARQUET" --strategy Recreate
    rm -f "$SALES_TEMP"
}

validate_data() {
    local TARGET_FILE="tests/agentic/artifacts/sales_report.csv"

    if [ ! -f "$TARGET_FILE" ]; then
        echo "❌ FAILURE: Target file '$TARGET_FILE' was not generated."
        return 1
    fi

    # Validate line count (Header + 3 joined order rows = 4 lines)
    local LINE_COUNT=$(wc -l < "$TARGET_FILE" | xargs)
    if [ "$LINE_COUNT" -ne 4 ]; then
        echo "❌ FAILURE: Target file has $LINE_COUNT lines instead of 4."
        return 1
    fi

    # Verify matching rows and correct joins using robust content search
    local JOINS_OK=true

    # O-5001 row validation
    if ! grep -q "O-5001" "$TARGET_FILE" || ! grep -q "Jean Dupont" "$TARGET_FILE" || ! grep -q "Laptop" "$TARGET_FILE" || ! grep -q "1200" "$TARGET_FILE"; then
        echo "❌ FAILURE: Order O-5001 row not found or matches incorrectly in target."
        JOINS_OK=false
    fi

    # O-5003 row validation
    if ! grep -q "O-5003" "$TARGET_FILE" || ! grep -q "Alice Martin" "$TARGET_FILE" || ! grep -q "Keyboard" "$TARGET_FILE" || ! grep -q "80" "$TARGET_FILE"; then
        echo "❌ FAILURE: Order O-5003 row not found or matches incorrectly in target."
        JOINS_OK=false
    fi

    # O-5004 row validation (should have been filtered out by inner join)
    if grep -q "O-5004" "$TARGET_FILE"; then
        echo "❌ FAILURE: Order O-5004 (unknown customer C-999) should have been filtered out but was found."
        JOINS_OK=false
    fi

    # Clean up artifacts directory
    rm -rf "tests/agentic/artifacts"

    if [ "$JOINS_OK" = true ]; then
        return 0
    else
        return 1
    fi
}

run_mission \
    "Fuzzy Join SQLite and Parquet" \
    "We want to perform a sales analysis. We have two data sources: 1. A sales Parquet file at 'parquet:tests/agentic/artifacts/sales.parquet' containing transactions. 2. An SQLite database at 'sqlite:tests/agentic/artifacts/business.db'. Your tasks are: 1. Find out which table in the SQLite database holds the customer details (such as names) and inspect its schema (Tip: use a query like \"SELECT name FROM sqlite_master WHERE type='table'\" to list tables). 2. Join the sales transactions with the customer table (hint: link the client reference from the sales transactions to the customer identifier in the SQLite table) to produce a combined output showing order ID, customer name, product name, and amount. You must specify the query for the SQLite reader using the '--query \"SELECT * FROM company_clients\"' option. 3. Save the result to 'csv:tests/agentic/artifacts/sales_report.csv'." \
    setup_data \
    validate_data \
    "$1"
