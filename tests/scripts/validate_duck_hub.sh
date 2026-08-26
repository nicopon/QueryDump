#!/usr/bin/env bash
# validate_duck_hub.sh — Integration test for duck+{provider}: hub connections (SQLite, Postgres, MySQL, S3/MinIO, Azure/Azurite, Excel) and retry policy

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
DTPIPE="${DTPIPE:-$ROOT_DIR/src/DtPipe/bin/Debug/net10.0/DtPipe}"
if [ ! -f "$DTPIPE" ]; then
    DTPIPE="$ROOT_DIR/dist/release/dtpipe"
fi

ARTIFACTS_DIR="$SCRIPT_DIR/artifacts"
TMP_DIR="$(mktemp -d)"

cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

echo "=== Testing DuckDB Hub (duck+{provider}:) Integration Suite ==="

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

SRC_LINES=$(wc -l < "$SRC_CSV" | tr -d ' ')

# ------------------------------------------------------------------------------
# 1. Test SQLite via duck+sqlite:
# ------------------------------------------------------------------------------
echo -e "\n--- Test 1: DuckDB Hub (duck+sqlite:) ---"
SQLITE_TGT="$TMP_DIR/hub_target.sqlite"

echo "Testing write via duck+sqlite: ..."
"$DTPIPE" -i "$SRC_CSV" -o "duck+sqlite:$SQLITE_TGT" --table "users" --strategy Recreate --no-stats --retry

if [ ! -f "$SQLITE_TGT" ]; then
    echo "FAIL: Target SQLite DB $SQLITE_TGT was not created."
    exit 1
fi
echo "  -> Write via duck+sqlite: PASSED"

echo "Testing read via duck+sqlite: ..."
"$DTPIPE" -i "duck+sqlite:$SQLITE_TGT" --query "SELECT * FROM users ORDER BY Id" -o "$TMP_DIR/sqlite_read.csv" --no-stats --retry

OUT_LINES=$(wc -l < "$TMP_DIR/sqlite_read.csv" | tr -d ' ')
if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
    echo "FAIL: Expected $SRC_LINES lines in SQLite output, got $OUT_LINES"
    exit 1
fi
echo "  -> Read via duck+sqlite: PASSED ($OUT_LINES lines matched)"

# ------------------------------------------------------------------------------
# 2. Test PostgreSQL via duck+pg:
# ------------------------------------------------------------------------------
echo -e "\n--- Test 2: DuckDB Hub (duck+pg:) ---"
PG_CONN="duck+pg:host=127.0.0.1 port=5440 dbname=integration user=postgres password=password"

if nc -z 127.0.0.1 5440 2>/dev/null || nc -w 2 127.0.0.1 5440 2>/dev/null; then
    echo "Testing write via duck+pg: ..."
    "$DTPIPE" -i "$SRC_CSV" -o "$PG_CONN" --table "duck_pg_users" --strategy Recreate --no-stats --retry

    echo "Testing read via duck+pg: ..."
    "$DTPIPE" -i "$PG_CONN" --query "SELECT * FROM duck_pg_users ORDER BY Id" -o "$TMP_DIR/pg_read.csv" --no-stats --retry

    OUT_LINES=$(wc -l < "$TMP_DIR/pg_read.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines in Postgres output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read via duck+pg: PASSED ($OUT_LINES lines matched)"
else
    echo "SKIP: PostgreSQL container (port 5440) not reachable."
fi

# ------------------------------------------------------------------------------
# 3. Test MySQL via duck+mysql:
# ------------------------------------------------------------------------------
echo -e "\n--- Test 3: DuckDB Hub (duck+mysql:) ---"
MYSQL_CONN="duck+mysql:host=127.0.0.1 port=3306 database=integration user=testuser password=password"

if nc -z 127.0.0.1 3306 2>/dev/null || nc -w 2 127.0.0.1 3306 2>/dev/null; then
    echo "Testing write via duck+mysql: ..."
    "$DTPIPE" -i "$SRC_CSV" -o "$MYSQL_CONN" --table "duck_mysql_users" --strategy Recreate --no-stats --retry

    echo "Testing read via duck+mysql: ..."
    "$DTPIPE" -i "$MYSQL_CONN" --query "SELECT * FROM duck_mysql_users ORDER BY Id" -o "$TMP_DIR/mysql_read.csv" --no-stats --retry

    OUT_LINES=$(wc -l < "$TMP_DIR/mysql_read.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines in MySQL output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read via duck+mysql: PASSED ($OUT_LINES lines matched)"
else
    echo "SKIP: MySQL container (port 3306) not reachable."
fi

# ------------------------------------------------------------------------------
# 4. Test S3 Object Storage (MinIO) via DuckDB httpfs / duck+s3:
# ------------------------------------------------------------------------------
echo -e "\n--- Test 4: DuckDB S3 / MinIO (httpfs) ---"
if nc -z 127.0.0.1 9000 2>/dev/null || nc -w 2 127.0.0.1 9000 2>/dev/null; then
    S3_INIT="INSTALL httpfs; LOAD httpfs; SET s3_endpoint='127.0.0.1:9000'; SET s3_access_key_id='minioadmin'; SET s3_secret_access_key='minioadmin'; SET s3_use_ssl=false; SET s3_url_style='path';"
    S3_TARGET="s3://dtpipe-test-bucket/users.parquet"
    STAGE_DB="$TMP_DIR/s3_stage.duckdb"

    # Object-storage targets must go through the DuckDB engine (httpfs): a plain
    # '-o s3://...' is rejected by design — file providers never claim remote schemes.
    echo "Testing write to MinIO S3 bucket via DuckDB httpfs (stage + post-exec COPY): ..."
    "$DTPIPE" -i "$SRC_CSV" -o "duck:$STAGE_DB" --table "users" --strategy Recreate \
        --post-exec "$S3_INIT COPY (SELECT * FROM users) TO '$S3_TARGET' (FORMAT PARQUET);" --no-stats

    echo "Testing read from MinIO S3 bucket via DuckDB httpfs: ..."
    "$DTPIPE" -i "duck:memory" --duck-init "$S3_INIT" \
        --query "SELECT * FROM read_parquet('$S3_TARGET')" -o "$TMP_DIR/s3_read.csv" --no-stats

    OUT_LINES=$(wc -l < "$TMP_DIR/s3_read.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines in S3/MinIO output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read Parquet on MinIO S3: PASSED ($OUT_LINES lines matched)"
else
    echo "SKIP: MinIO container (port 9000) not reachable."
fi

# ------------------------------------------------------------------------------
# 5. Test Excel (.xlsx) via DuckDB excel extension
# ------------------------------------------------------------------------------
echo -e "\n--- Test 5: DuckDB Excel Extension (.xlsx) ---"
EXCEL_FILE="$TMP_DIR/users.xlsx"
EXCEL_INIT="INSTALL excel; LOAD excel;"

echo "Testing export to Excel (.xlsx) via DuckDB excel extension..."
"$DTPIPE" -i "$SRC_CSV" -o "duck:$TMP_DIR/excel_temp.duckdb" --table "users" --post-exec "$EXCEL_INIT COPY (SELECT * FROM users) TO '$EXCEL_FILE' WITH (FORMAT XLSX);" --no-stats >/dev/null 2>&1

if [ -f "$EXCEL_FILE" ]; then
    echo "Testing reading from Excel (.xlsx) via DuckDB read_xlsx..."
    "$DTPIPE" -i "duck:memory" --duck-init "$EXCEL_INIT" --query "SELECT * FROM read_xlsx('$EXCEL_FILE')" -o "$TMP_DIR/excel_read.csv" --no-stats
    OUT_LINES=$(wc -l < "$TMP_DIR/excel_read.csv" | tr -d ' ')
    if [ "$OUT_LINES" -lt 900 ]; then
        echo "FAIL: Expected at least 900 lines in Excel output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read Excel (.xlsx) via DuckDB excel extension: PASSED ($OUT_LINES lines matched)"
else
    echo "FAIL: Target Excel file $EXCEL_FILE was not created."
    exit 1
fi

# ------------------------------------------------------------------------------
# 6. Test Azure Blob Storage (Azurite) via DuckDB azure extension
# ------------------------------------------------------------------------------
echo -e "\n--- Test 6: DuckDB Azure Blob Storage (Azurite) ---"
if nc -z 127.0.0.1 10000 2>/dev/null || nc -w 2 127.0.0.1 10000 2>/dev/null; then
    AZURE_INIT="INSTALL azure; LOAD azure; CREATE SECRET azurite_secret (TYPE AZURE, CONNECTION_STRING 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;');"
    AZURE_TARGET="azure://dtpipe-azure-bucket/users.parquet"
    AZ_STAGE_DB="$TMP_DIR/az_stage.duckdb"

    # Same rule as test 4: remote schemes go through the DuckDB engine.
    echo "Testing write to Azure Blob Storage (Azurite) via DuckDB azure (stage + post-exec COPY): ..."
    "$DTPIPE" -i "$SRC_CSV" -o "duck:$AZ_STAGE_DB" --table "users" --strategy Recreate \
        --post-exec "$AZURE_INIT COPY (SELECT * FROM users) TO '$AZURE_TARGET' (FORMAT PARQUET);" --no-stats

    echo "Testing read from Azure Blob Storage (Azurite) via DuckDB azure: ..."
    "$DTPIPE" -i "duck:memory" --duck-init "$AZURE_INIT" \
        --query "SELECT * FROM read_parquet('$AZURE_TARGET')" -o "$TMP_DIR/azure_read.csv" --no-stats

    OUT_LINES=$(wc -l < "$TMP_DIR/azure_read.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines in Azure output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read Parquet on Azure Blob (Azurite): PASSED ($OUT_LINES lines matched)"
else
    echo "SKIP: Azurite container (port 10000) not reachable."
fi

# ------------------------------------------------------------------------------
# N. Test --duck-init with keyring:// resolution (in-memory DuckDB)
# ------------------------------------------------------------------------------
echo -e "\n--- Test N: --duck-init keyring:// resolution ---"
SECRET_ALIAS="dtpipe_duckhub_init_test"
INIT_TABLE="keyring_init_probe"
WRITE_TABLE="keyring_write_target"

# Stub secret (fake keyring keeps the test hermetic; real keyring also works).
export DTPIPE_UNSAFE_INSECURE_FAKE_KEYRING=1
if ! "$DTPIPE" secret set "$SECRET_ALIAS" "CREATE TABLE IF NOT EXISTS ${INIT_TABLE} (Id INTEGER)" > /dev/null 2>&1; then
    echo "SKIP: could not store stub secret (keyring unavailable)."
else
    # Init creates INIT_TABLE; the write targets a different table. Exit 0 proves the
    # secret was resolved (an unresolved keyring:// literal would fail DuckDB parsing).
    if "$DTPIPE" -i "generate:3" \
        -o "duck::memory:" \
        --table "$WRITE_TABLE" \
        --duck-init "keyring://${SECRET_ALIAS}" \
        --no-stats > /dev/null 2>&1; then
        echo "  -> --duck-init keyring:// resolution: PASSED"
    else
        echo "FAIL: pipeline with --duck-init keyring:// exited non-zero."
        "$DTPIPE" secret delete "$SECRET_ALIAS" > /dev/null 2>&1 || true
        exit 1
    fi
    "$DTPIPE" secret delete "$SECRET_ALIAS" > /dev/null 2>&1 || true
fi

echo -e "\n=== All DuckDB Extender & Hub Integration Tests Completed Successfully ==="
