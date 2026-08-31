#!/usr/bin/env bash
# validate_duck_hub.sh — Integration test for the duck+{provider}: hub prefix (now an empty
# allowlist: every attachable database has a native provider), plus the DuckDB-engine routes
# that remain — S3/MinIO, Azure/Azurite, Excel — and --duck-init secret resolution.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Test infrastructure endpoints. Sourcing this is what declares that this script
# needs tests/infra running (see lib/test_connections.sh).
# shellcheck source=lib/test_connections.sh
source "$SCRIPT_DIR/lib/test_connections.sh"
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
# 1. duck+sqlite: is rejected — the native "sqlite:" provider covers this with
#    more capability (COPY/bulk, upsert) than an ATTACH catalog ever could.
# ------------------------------------------------------------------------------
echo -e "\n--- Test 1: DuckDB Hub (duck+sqlite:) is rejected ---"
SQLITE_TGT="$TMP_DIR/hub_target.sqlite"

if "$DTPIPE" -i "$SRC_CSV" -o "duck+sqlite:$SQLITE_TGT" --table "users" --strategy Recreate --no-stats >/dev/null 2>&1; then
    echo "FAIL: duck+sqlite: was accepted; SQLite has a native provider and is not a hub target."
    exit 1
fi
echo "  -> duck+sqlite: rejected as expected"

# ------------------------------------------------------------------------------
# 2. duck+pg: is rejected — the native "pg:"/"postgres:" provider covers this
#    with more capability (COPY, upsert) than an ATTACH catalog ever could.
# ------------------------------------------------------------------------------
echo -e "\n--- Test 2: DuckDB Hub (duck+pg:) is rejected ---"
PG_CONN="duck+pg:host=127.0.0.1 port=5440 dbname=integration user=postgres password=password"

if "$DTPIPE" -i "$SRC_CSV" -o "$PG_CONN" --table "duck_pg_users" --strategy Recreate --no-stats >/dev/null 2>&1; then
    echo "FAIL: duck+pg: was accepted; PostgreSQL has a native provider and is not a hub target."
    exit 1
fi
echo "  -> duck+pg: rejected as expected"

# ------------------------------------------------------------------------------
# 3. duck+mysql: is rejected in both directions — the native "mysql:" provider reaches
#    MySqlBulkCopy and ON DUPLICATE KEY UPDATE, which an ATTACH catalog cannot. The hub
#    allowlist is empty. End-to-end coverage of the native provider is in validate_mysql.sh.
# ------------------------------------------------------------------------------
echo -e "\n--- Test 3: DuckDB Hub (duck+mysql:) is rejected ---"
MYSQL_CONN="duck+mysql:host=127.0.0.1 port=3306 database=integration user=testuser password=password"

if "$DTPIPE" -i "$SRC_CSV" -o "$MYSQL_CONN" --table "duck_mysql_users" --strategy Recreate --no-stats >/dev/null 2>&1; then
    echo "FAIL: duck+mysql: was accepted for write; MySQL has a native provider and is not a hub target."
    exit 1
fi
echo "  -> duck+mysql: rejected for write as expected"

if "$DTPIPE" -i "$MYSQL_CONN" --query "SELECT 1" -o "$TMP_DIR/mysql_read.csv" --no-stats >/dev/null 2>&1; then
    echo "FAIL: duck+mysql: was accepted for read; MySQL has a native provider and is not a hub target."
    exit 1
fi
echo "  -> duck+mysql: rejected for read as expected"

# ------------------------------------------------------------------------------
# 4. Test S3 Object Storage (MinIO) via DuckDB httpfs / duck+s3:
# ------------------------------------------------------------------------------
echo -e "\n--- Test 4: DuckDB S3 / MinIO (httpfs) ---"
if nc -z 127.0.0.1 9000 2>/dev/null || nc -w 2 127.0.0.1 9000 2>/dev/null; then
    S3_OPTS=(--s3-endpoint "$MINIO_ENDPOINT" --s3-access-key "$MINIO_ACCESS_KEY" --s3-secret-key "$MINIO_SECRET_KEY")
    S3_TARGET="s3://dtpipe-test-bucket/users.parquet"

    # Primary route: the s3:// provider streams through DuckDB's httpfs in-process.
    echo "Testing write to MinIO S3 bucket via the s3:// provider: ..."
    "$DTPIPE" -i "$SRC_CSV" -o "$S3_TARGET" "${S3_OPTS[@]}" --no-stats

    echo "Testing read from MinIO S3 bucket via the s3:// provider: ..."
    "$DTPIPE" -i "$S3_TARGET" "${S3_OPTS[@]}" -o "$TMP_DIR/s3_read.csv" --no-stats

    OUT_LINES=$(wc -l < "$TMP_DIR/s3_read.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines in S3/MinIO output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read Parquet on MinIO S3 via provider: PASSED ($OUT_LINES lines matched)"

    # Globbing is native to the read function, not something the provider re-implements.
    echo "Testing glob read across multiple S3 objects: ..."
    "$DTPIPE" -i "$SRC_CSV" -o "s3://dtpipe-test-bucket/glob/a.csv" "${S3_OPTS[@]}" --no-stats
    "$DTPIPE" -i "$SRC_CSV" -o "s3://dtpipe-test-bucket/glob/b.csv" "${S3_OPTS[@]}" --no-stats
    "$DTPIPE" -i "s3://dtpipe-test-bucket/glob/*.csv" "${S3_OPTS[@]}" -o "$TMP_DIR/s3_glob.csv" --no-stats

    GLOB_LINES=$(wc -l < "$TMP_DIR/s3_glob.csv" | tr -d ' ')
    EXPECTED_GLOB=$(( (SRC_LINES - 1) * 2 + 1 ))
    if [ "$GLOB_LINES" -ne "$EXPECTED_GLOB" ]; then
        echo "FAIL: Expected $EXPECTED_GLOB lines from the S3 glob, got $GLOB_LINES"
        exit 1
    fi
    echo "  -> Glob read across S3 objects: PASSED ($GLOB_LINES lines matched)"

    # Legacy route kept as a regression: --duck-init + read_parquet must keep working for
    # anything the closed format map does not cover.
    S3_INIT="INSTALL httpfs; LOAD httpfs; SET s3_endpoint='127.0.0.1:9000'; SET s3_access_key_id='minioadmin'; SET s3_secret_access_key='minioadmin'; SET s3_use_ssl=false; SET s3_url_style='path';"
    echo "Testing legacy --duck-init read of the same object: ..."
    "$DTPIPE" -i "duck:memory" --duck-init "$S3_INIT" \
        --query "SELECT * FROM read_parquet('$S3_TARGET')" -o "$TMP_DIR/s3_read_legacy.csv" --no-stats

    OUT_LINES=$(wc -l < "$TMP_DIR/s3_read_legacy.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines from the legacy duck-init route, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Legacy --duck-init route: PASSED ($OUT_LINES lines matched)"

    # duck+s3: is not a hub target and must fail closed with the working route named.
    echo "Testing that duck+s3: is rejected: ..."
    if "$DTPIPE" -i "duck+s3:$S3_TARGET" --query "SELECT 1" -o "$TMP_DIR/hub_s3.csv" --no-stats >/dev/null 2>&1; then
        echo "FAIL: duck+s3: was accepted; object storage is not an ATTACH target."
        exit 1
    fi
    echo "  -> duck+s3: rejected as expected"
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
    AZ_CONN="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"

    # Primary route: the azure:// provider streams through DuckDB's azure extension.
    echo "Testing write to Azure Blob Storage (Azurite) via the azure:// provider: ..."
    "$DTPIPE" -i "$SRC_CSV" -o "$AZURE_TARGET" --azure-connection-string "$AZ_CONN" --no-stats

    echo "Testing read from Azure Blob Storage (Azurite) via the azure:// provider: ..."
    "$DTPIPE" -i "$AZURE_TARGET" --azure-connection-string "$AZ_CONN" -o "$TMP_DIR/azure_read.csv" --no-stats

    OUT_LINES=$(wc -l < "$TMP_DIR/azure_read.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines in Azure output, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Write and Read Parquet on Azure Blob via provider: PASSED ($OUT_LINES lines matched)"

    # Legacy route kept as a regression.
    echo "Testing legacy --duck-init read of the same blob: ..."
    "$DTPIPE" -i "duck:memory" --duck-init "$AZURE_INIT" \
        --query "SELECT * FROM read_parquet('$AZURE_TARGET')" -o "$TMP_DIR/azure_read_legacy.csv" --no-stats

    OUT_LINES=$(wc -l < "$TMP_DIR/azure_read_legacy.csv" | tr -d ' ')
    if [ "$SRC_LINES" -ne "$OUT_LINES" ]; then
        echo "FAIL: Expected $SRC_LINES lines from the legacy Azure duck-init route, got $OUT_LINES"
        exit 1
    fi
    echo "  -> Legacy --duck-init route (Azure): PASSED ($OUT_LINES lines matched)"
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

# The fake keyring is a plaintext file in the user's real profile, not under artifacts/.
# Remove it on every exit path, including the failure branch below.
case "$(uname -s)" in
    Darwin)         FAKE_KEYRING_FILE="$HOME/Library/Application Support/dtpipe/fake_keyring.json" ;;
    MINGW*|MSYS*)   FAKE_KEYRING_FILE="$APPDATA/dtpipe/fake_keyring.json" ;;
    *)              FAKE_KEYRING_FILE="$HOME/.config/dtpipe/fake_keyring.json" ;;
esac
trap 'rm -f "$FAKE_KEYRING_FILE"' EXIT
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
