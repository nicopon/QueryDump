#!/bin/bash
set -e

# validate_cancellation.sh
# F16 — cancellation must not mask as success.
# Case 1: Ctrl-C (SIGINT) on a linear pipeline exits non-zero (POSIX 130 convention).
# Case 2: Ctrl-C on a 3-branch DAG exits non-zero.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts/cancellation"
mkdir -p "$ARTIFACTS_DIR"

DTPIPE="$PROJECT_ROOT/dist/release/dtpipe"
export DTPIPE_NO_TUI=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Cancellation Validation"
echo "========================================"

if [ ! -f "$DTPIPE" ]; then
    echo "Building release..."
    "$PROJECT_ROOT/build.sh" > /dev/null
fi

A="$ARTIFACTS_DIR"

cleanup() {
    rm -f "$A"/*.csv "$A"/*.log
    if [ -n "${PROC:-}" ]; then
        kill "$PROC" 2>/dev/null || true
    fi
}
trap cleanup EXIT

# Launch a command with its SIGINT disposition restored to SIG_DFL.
# Background jobs of a non-interactive shell inherit SIGINT=SIG_IGN, which .NET
# respects (CancelKeyPress never fires); the perl launcher resets it before exec.
# `exec` replaces the background subshell so $! is the perl→dtpipe PID itself.
launch_interruptible() {
    exec perl -e 'setpgrp(0,0); $SIG{INT}="DEFAULT"; exec(@ARGV) or die "exec failed"' "$@"
}

# ----------------------------------------
# Case 1: linear pipeline interrupted by SIGINT
# ----------------------------------------
echo "--- [1] Linear pipeline Ctrl-C ---"
rm -f "$A/c.csv"
launch_interruptible "$DTPIPE" -i generate:100000000 -o "$A/c.csv" --no-stats &
PROC=$!
sleep 2
if ! ps -p $PROC > /dev/null; then
    fail "linear pipeline finished before SIGINT could be delivered"
fi
kill -INT $PROC 2>/dev/null || true
set +e
wait $PROC
EXIT=$?
set -e
PROC=""
if [ "$EXIT" -eq 0 ]; then
    fail "linear Ctrl-C returned 0 (exit code: $EXIT)"
fi
pass "linear Ctrl-C non-zero ($EXIT)"

# No partial output reported as a clean run — a partial file may exist on disk, but the
# process must not have exited successfully while leaving it behind.
if [ -s "$A/c.csv" ]; then
    echo "  WARN: partial file exists (expected for an interrupted write)"
fi

# ----------------------------------------
# Case 2: 3-branch DAG interrupted by SIGINT
# ----------------------------------------
echo "--- [2] DAG (fan-out, 3 branches) Ctrl-C ---"
rm -f "$A/d1.csv" "$A/d2.csv" "$A/d3.csv"
# --no-stats is global (GlobalOptions.NoStats): once for the whole run, not per branch.
launch_interruptible "$DTPIPE" -i generate:100000000 --alias s \
  --from s -o "$A/d1.csv" \
  --from s -o "$A/d2.csv" \
  --from s -o "$A/d3.csv" --no-stats &
PROC=$!
sleep 2
if ! ps -p $PROC > /dev/null; then
    fail "DAG finished before SIGINT could be delivered"
fi
kill -INT $PROC 2>/dev/null || true
set +e
wait $PROC
EXIT=$?
set -e
PROC=""
if [ "$EXIT" -eq 0 ]; then
    fail "DAG Ctrl-C returned 0 (exit code: $EXIT)"
fi
pass "DAG Ctrl-C non-zero ($EXIT)"

echo ""
echo "All cancellation checks passed."
