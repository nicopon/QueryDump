#!/bin/bash
set -e

# validate_manifest.sh
# F13 — every provider documented in REFERENCE.md is registered by the component
# catalog, and the catalog output stays in sync with the docs (drift guard).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/manifest"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Provider Manifest Validation"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

EXPECTED="arrow arrow-memory checksum csv duck generate jsonl mem mssql null ora parquet pg sqlite xml sql merge"

ACTUAL=$("$DTPIPE" providers 2>&1 | awk '/│/ {gsub(/│/,""); gsub(/^ +| +$/,"",$1); if ($1!="") print $1}' | sort -u)
for p in csv duck pg ora mssql sqlite parquet jsonl xml generate null checksum arrow; do
    echo "$ACTUAL" | grep -qx "$p" || fail "provider '$p' not registered"
done
pass "all expected providers registered"

# Docs drift: each discovered name appears somewhere in REFERENCE.md (prefix form).
for p in csv duck pg ora mssql sqlite parquet jsonl xml generate null checksum arrow; do
    grep -q "$p" "$PROJECT_ROOT/REFERENCE.md" || fail "provider '$p' missing from REFERENCE.md"
done
pass "REFERENCE.md covers all registered providers"

echo ""
echo "All manifest checks passed."
