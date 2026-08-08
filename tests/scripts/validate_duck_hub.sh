#!/usr/bin/env bash
# validate_duck_hub.sh — Integration test for duck+{provider}: hub connections and retry policy

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
DTPIPE="${DTPIPE:-$ROOT_DIR/src/DtPipe/bin/Debug/net10.0/DtPipe}"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts"
TMP_DIR="$(mktemp -d)"

cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

echo "=== Testing DuckDB Hub (duck+sqlite:) and Retry Policy ==="

mkdir -p "$ARTIFACTS_DIR"

# Ensure shared test dataset exists (reuses standard test_data.csv if present)
SRC_CSV="$ARTIFACTS_DIR/test_data.csv"
if [ ! -f "$SRC_CSV" ]; then
    echo "Creating shared test_data.csv dataset..."
    "$DTPIPE" -i "generate:100" \
      --fake "Id:random.guid" \
      --fake "FirstName:name.firstName" \
      --fake "LastName:name.lastName" \
      --fake "Email:internet.email" \
      --drop "GenerateIndex" \
      -o "$SRC_CSV" --no-stats
fi

SQLITE_TGT="$TMP_DIR/hub_target.sqlite"

# 1. Test writing to an attached SQLite database via duck+sqlite:
echo "Testing write via duck+sqlite: ..."
"$DTPIPE" -i "$SRC_CSV" -o "duck+sqlite:$SQLITE_TGT" --table "users" --strategy Recreate --no-stats --retry

if [ ! -f "$SQLITE_TGT" ]; then
    echo "FAIL: Target SQLite DB $SQLITE_TGT was not created."
    exit 1
fi

echo "  -> Write via duck+sqlite: PASSED"

# 2. Test reading from an attached SQLite database via duck+sqlite:
echo "Testing read via duck+sqlite: ..."
"$DTPIPE" -i "duck+sqlite:$SQLITE_TGT" --query "SELECT * FROM hub_target.users ORDER BY Id" -o "$TMP_DIR/read_out.csv" --no-stats --retry

if [ ! -f "$TMP_DIR/read_out.csv" ]; then
    echo "FAIL: Output file read_out.csv was not created."
    exit 1
fi

SRC_LINES=$(wc -l < "$SRC_CSV" | tr -d ' ')
OUT_LINES=$(wc -l < "$TMP_DIR/read_out.csv" | tr -d ' ')

if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
    echo "FAIL: Expected $SRC_LINES lines in output, got $OUT_LINES"
    exit 1
fi

echo "  -> Read via duck+sqlite: PASSED ($OUT_LINES lines matched)"
echo "=== All DuckDB Hub Integration Tests PASSED ==="
