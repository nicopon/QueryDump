#!/bin/bash
set -e

# validate_checkpoint.sh
# Cycle 1.7 voie 3 — materialisation, on the real binary.
#
# Four claims that only a real run can settle:
#   1. --checkpoint materialises, and what lands on disk carries no readable values
#   2. --from-checkpoint resumes and replays the same rows
#   3. the store ignores itself (.dtpipe/.gitignore)
#   4. destroying the key makes the artefacts unreadable — the purge property

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Checkpoint Validation"
echo "========================================"

[ -x "$DTPIPE" ] || fail "dist/release/dtpipe not built — run ./build.sh"

WORK="$(mktemp -d)"
export DTPIPE_STATE_HOME="$WORK/state"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

cd "$WORK"
printf 'id,name,amount\n1,alpha,10.5\n2,bravo,20.25\n3,charlie,30.75\n' > source.csv

# ── 1. Materialise ────────────────────────────────────────────────────────────
"$DTPIPE" -i csv:source.csv --session e2e --checkpoint stage1 -o parquet:out.parquet --no-stats >/dev/null 2>&1 \
    || fail "materialising run failed"

CK_DIR="$WORK/.dtpipe/sessions/e2e"
[ -d "$CK_DIR" ] || fail "no session store created under .dtpipe/sessions/"
DATA_FILE="$(find "$CK_DIR" -name 'data.dtck' | head -1)"
[ -n "$DATA_FILE" ] || fail "no checkpoint file written"
pass "a checkpoint was materialised"

# ── 2. The bytes on disk are inert ────────────────────────────────────────────
if grep -qa "charlie" "$DATA_FILE"; then
    fail "plaintext values found in the checkpoint file — a copied project directory would carry data"
fi
head -c 8 "$DATA_FILE" | grep -qa "DTPCKPT1" || fail "checkpoint file lacks its format header"
pass "what lands on disk carries no readable values"

# ── 3. The store ignores itself ───────────────────────────────────────────────
[ -f "$WORK/.dtpipe/.gitignore" ] || fail ".dtpipe/.gitignore was not written"
[ "$(tr -d '[:space:]' < "$WORK/.dtpipe/.gitignore")" = "*" ] || fail ".dtpipe/.gitignore does not ignore everything"
pass "the store ignores itself, whatever the project's .gitignore says"

# ── 4. Resume replays the same rows ───────────────────────────────────────────
KEY="$(basename "$(dirname "$DATA_FILE")")"
"$DTPIPE" --session e2e --from-checkpoint "$KEY" -o csv:resumed.csv --no-stats >/dev/null 2>&1 \
    || fail "resuming from the checkpoint failed"

"$DTPIPE" -i csv:source.csv -o csv:direct.csv --no-stats >/dev/null 2>&1 || fail "reference run failed"
if ! diff <(sort resumed.csv) <(sort direct.csv) >/dev/null; then
    echo "--- resumed ---"; cat resumed.csv
    echo "--- direct ----"; cat direct.csv
    fail "resuming from a checkpoint did not replay the same rows"
fi
pass "resuming replays exactly what the source produced"

# ── 5. Destroying the key makes it unreadable ─────────────────────────────────
rm -rf "$DTPIPE_STATE_HOME/keys"
if "$DTPIPE" --session e2e --from-checkpoint "$KEY" -o csv:after-purge.csv --no-stats >/dev/null 2>&1; then
    fail "the checkpoint was still readable after its key was destroyed"
fi
[ -f "$DATA_FILE" ] || fail "test precondition lost: the data file should still exist"
pass "destroying the key makes the artefacts unreadable while the bytes remain"

echo ""
echo -e "${GREEN}Checkpoint checks passed.${NC}"
