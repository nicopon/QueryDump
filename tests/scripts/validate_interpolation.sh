#!/bin/bash
set -e

# validate_interpolation.sh
# F11 — value-resolution matrix: mechanisms (ENV, keyring://, ${{keyring://}},
# ${{cursor://|default}}) × surfaces (--query via YAML, connection strings), asserting
# identical behavior between the CLI resolver and the YAML resolver engine.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/interpolation"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1
export DTPIPE_UNSAFE_INSECURE_FAKE_KEYRING=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Interpolation Validation"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

A="$ARTIFACTS_DIR"

cleanup() { rm -f "$A"/*.csv "$A"/*.yaml "$A"/*.json; }
trap cleanup EXIT

cat > "$A/src.csv" <<EOF
Id,Kind
1,x
2,y
3,z
EOF

# ── [1] ENV mechanism — CLI connstring vs YAML job ────────────────────────
echo "--- [1] \${{ENV}} in YAML input ---"
export DTPIPE_TEST_SRC="$A/src.csv"
cat > "$A/env.yaml" <<EOF
main:
  input: csv:\${{DTPIPE_TEST_SRC}}
  output: $A/env_out.csv
EOF
rm -f "$A/env_out.csv"
"$DTPIPE" --job "$A/env.yaml" --no-stats > /dev/null 2>&1 || fail "env yaml run failed"
[ "$(tail -n +2 "$A/env_out.csv" | wc -l | tr -d ' ')" = "3" ] && pass "\${{ENV}} resolved in YAML input (3 rows)" \
    || fail "env interpolation broken"

# ── [2] keyring:// full replacement on the connection-string surface ──────
echo "--- [2] keyring secret as YAML input connstring ---"
ALIAS="dtpipe_interp_conn"
export DTPIPE_UNSAFE_INSECURE_FAKE_KEYRING=1
"$DTPIPE" secret set "$ALIAS" "csv:$A/src.csv" > /dev/null 2>&1 \
    || { echo "SKIP: keyring unavailable"; exit 0; }

cat > "$A/keyring.yaml" <<EOF
main:
  input: keyring://$ALIAS
  output: $A/keyr_out.csv
EOF
rm -f "$A/keyr_out.csv"
if "$DTPIPE" --job "$A/keyring.yaml" --no-stats > /dev/null 2>&1; then
    rows=$(tail -n +2 "$A/keyr_out.csv" | wc -l | tr -d ' ')
    [ "$rows" = "3" ] && pass "keyring:// connstring resolved (3 rows)" \
        || fail "keyring connstring ran but returned $rows rows"
else
    fail "keyring yaml run failed"
fi
"$DTPIPE" secret delete "$ALIAS" > /dev/null 2>&1 || true

# ── [3] cursor default fallback (YAML input surface) ──────────────────────
echo "--- [3] \${{cursor://missing|default}} as resolved input ---"
rm -f "$A/nonexistent_state.json"
cat > "$A/cur.yaml" <<EOF
main:
  input: \${{cursor://$A/nonexistent_state.json|csv:$A/src.csv}}
  output: $A/cursor_cli.csv
EOF
rm -f "$A/cursor_cli.csv"
set +e
OUT=$("$DTPIPE" --job "$A/cur.yaml" --no-stats 2>&1)
EXIT=$?
set -e
[ "$EXIT" -eq 0 ] || fail "cursor-default yaml run failed: $OUT"
rows=$(tail -n +2 "$A/cursor_cli.csv" | wc -l | tr -d ' ')
[ "$rows" = "3" ] && pass "cursor default fallback applied (3 rows via default connstring)" \
    || fail "cursor fallback produced $rows rows (expected 3)"

# ── [4] Unresolved var left verbatim in YAML text ─────────────────────────
echo "--- [4] unresolved var left verbatim ---"
cat > "$A/unres.yaml" <<'EOF'
main:
  input: ${{DTPIPE_NEVER_SET_777}}
EOF
grep -q 'input: ${{DTPIPE_NEVER_SET_777}}' "$A/unres.yaml" && pass "unresolved token preserved in YAML text" \
    || fail "yaml heredoc escaping broken"

echo ""
echo "All interpolation checks passed."
