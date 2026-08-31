#!/bin/bash
set -e

# validate_upsert_dialect.sh
# F9 — dialect-aware upsert convergence: the same CSV source is upserted twice into
# every available driver; final row counts must equal the source row count (conflicts
# resolved, no duplicates).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Test infrastructure endpoints. Sourcing this is what declares that this script
# needs tests/infra running (see lib/test_connections.sh).
# shellcheck source=lib/test_connections.sh
source "$SCRIPT_DIR/lib/test_connections.sh"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/upsert_dialect"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Upsert Dialect Convergence"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

A="$ARTIFACTS_DIR"

cleanup() { rm -f "$A"/*.csv "$A"/*.db "$A"/*.duckdb; }
trap cleanup EXIT

# Seed CSV with duplicate keys across passes (exercises ON CONFLICT paths).
cat > "$A/up.csv" <<EOF
Id,Val
1,alpha
2,beta
3,gamma
4,delta
5,epsilon
EOF
ROWS=5

check_target() { # label, connstring, table
    local label="$1" cs="$2" table="$3"
    rm -rf "$(dirname "${cs#*:}")"/.upsert_probe 2>/dev/null || true
    # pass 1: create via Recreate; pass 2+3: Upsert with key (conflicts on all rows)
    "$DTPIPE" -i "csv:$A/up.csv" -o "$cs" --table "$table" --strategy Recreate --no-stats > /dev/null 2>&1 \
        || fail "[$label] recreate failed"
    "$DTPIPE" -i "csv:$A/up.csv" -o "$cs" --table "$table" --strategy Upsert --key Id --no-stats > /dev/null 2>&1 \
        || fail "[$label] upsert pass 1 failed"
    "$DTPIPE" -i "csv:$A/up.csv" -o "$cs" --table "$table" --strategy Upsert --key Id --no-stats > /dev/null 2>&1 \
        || fail "[$label] upsert pass 2 failed"

    local out="$A/${label}_count.csv"
    "$DTPIPE" -i "$cs" --query "SELECT COUNT(*) AS cnt FROM ${table}" -o "$out" --no-stats > /dev/null 2>&1 \
        || fail "[$label] verification read failed"
    local cnt
    cnt=$(tail -n +2 "$out" | head -1 | tr -d ' \r')
    [ "$cnt" = "$ROWS" ] || fail "[$label] expected $ROWS rows after double upsert, got $cnt"
    pass "[$label] $ROWS rows after double upsert (no duplicates)"
}

# Always-available file drivers exercise the dialect builders in-process.
check_target "duckdb"  "duck:$A/up.duckdb"   "up_tbl"

# SQLite requires a pre-existing PRIMARY KEY (same contract as server drivers).
rm -f "$A/up.db"
sqlite3 "$A/up.db" "CREATE TABLE up_tbl (Id INTEGER PRIMARY KEY, Val TEXT);"
run_sqlite_upsert() {
    "$DTPIPE" -i "csv:$A/up.csv" -o "sqlite:$A/up.db" --table up_tbl --strategy Upsert --key Id --no-stats > /dev/null 2>&1
}
run_sqlite_upsert || fail "[sqlite] upsert pass 1 failed"
run_sqlite_upsert || fail "[sqlite] upsert pass 2 failed"
out="$A/sqlite_count.csv"
"$DTPIPE" -i "sqlite:$A/up.db" --query "SELECT COUNT(*) AS cnt FROM up_tbl" -o "$out" --no-stats > /dev/null 2>&1 \
    || fail "[sqlite] verification read failed"
cnt=$(tail -n +2 "$out" | head -1 | tr -d ' \r')
[ "$cnt" = "$ROWS" ] || fail "[sqlite] expected $ROWS rows after double upsert, got $cnt"
pass "[sqlite] $ROWS rows after double upsert (no duplicates)"

# Server drivers when the persistent infra is reachable.
if nc -z localhost 5440 2>/dev/null; then
    # PG upsert requires a matching unique constraint — seed it via psql in the container.
    docker exec dtpipe-integ-postgres psql -U postgres -d integration -c \
        "DROP TABLE IF EXISTS up_tbl; CREATE TABLE up_tbl (Id INTEGER PRIMARY KEY, Val TEXT);" > /dev/null 2>&1
    "$DTPIPE" -i "csv:$A/up.csv" -o "$PG" \
        --table up_tbl --strategy Upsert --key Id --no-stats > /dev/null 2>&1 || fail "[postgres] upsert pass 1 failed"
    "$DTPIPE" -i "csv:$A/up.csv" -o "$PG" \
        --table up_tbl --strategy Upsert --key Id --no-stats > /dev/null 2>&1 || fail "[postgres] upsert pass 2 failed"
    out="$A/postgres_count.csv"
    "$DTPIPE" -i "$PG" \
        --query "SELECT COUNT(*) AS cnt FROM up_tbl" -o "$out" --no-stats > /dev/null 2>&1 || fail "[postgres] verification read failed"
    cnt=$(tail -n +2 "$out" | head -1 | tr -d ' \r')
    [ "$cnt" = "$ROWS" ] || fail "[postgres] expected $ROWS rows after double upsert, got $cnt"
    pass "[postgres] $ROWS rows after double upsert (no duplicates)"
else
    echo "SKIP: postgres infra not reachable (start tests/infra)"
fi

# MySQL fires ON DUPLICATE KEY UPDATE off the table's own unique indexes — there is no named
# conflict target — so the PRIMARY KEY must exist before the upsert, same contract as PG.
if nc -z 127.0.0.1 3306 2>/dev/null; then
    MY_CS="$MYSQL"
    docker exec dtpipe-integ-mysql mysql -u testuser -ppassword integration -e \
        "DROP TABLE IF EXISTS up_tbl; CREATE TABLE up_tbl (Id INT PRIMARY KEY, Val LONGTEXT);" > /dev/null 2>&1
    "$DTPIPE" -i "csv:$A/up.csv" -o "$MY_CS" --table up_tbl --strategy Upsert --key Id --no-stats > /dev/null 2>&1 \
        || fail "[mysql] upsert pass 1 failed"
    "$DTPIPE" -i "csv:$A/up.csv" -o "$MY_CS" --table up_tbl --strategy Upsert --key Id --no-stats > /dev/null 2>&1 \
        || fail "[mysql] upsert pass 2 failed"
    cnt=$(docker exec dtpipe-integ-mysql mysql -u testuser -ppassword integration -N -B -e \
        "SELECT COUNT(*) FROM up_tbl" 2>/dev/null | tr -d ' \r')
    [ "$cnt" = "$ROWS" ] || fail "[mysql] expected $ROWS rows after double upsert, got $cnt"
    pass "[mysql] $ROWS rows after double upsert (no duplicates)"
else
    echo "SKIP: mysql infra not reachable (start tests/infra)"
fi

echo ""
echo "All upsert dialect convergence checks passed."
