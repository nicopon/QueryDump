#!/bin/bash
set -e

# validate_core_boundary.sh
# F10 — source-level boundary guard: no concrete SQL/dialect/cursor-persistence classes
# in DtPipe.Core; standalone Arrow libraries stay DtPipe-free; ArrowBridge (when present)
# must not reference DtPipe projects.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}OK: $1${NC}"; }
fail() { echo -e "  ${RED}FAIL: $1${NC}"; exit 1; }

echo "========================================"
echo "    DtPipe Core Boundary Validation"
echo "========================================"

# 1. Concrete SQL/dialect/cursor-store code in Core?
if grep -rlE "class (BaseSqlDataWriter|[A-Za-z]*SqlDialect|DatabaseRetryPolicy|CursorStateStore)\b" "$PROJECT_ROOT/src/DtPipe.Core/" --include="*.cs" 2>/dev/null | grep -v bin > /dev/null; then
    fail "concrete SQL/dialect/retry/cursor-store code found in DtPipe.Core"
fi
pass "no concrete SQL/dialect infrastructure in DtPipe.Core"

# 2. Standalone Arrow libraries untouched (no project refs into DtPipe.*, no using DtPipe.*).
if grep -rqE 'ProjectReference Include="[^"]*DtPipe' "$PROJECT_ROOT/src/Apache.Arrow.Serialization/" --include="*.csproj" 2>/dev/null \
   || grep -rq "using DtPipe\." "$PROJECT_ROOT/src/Apache.Arrow.Serialization/" --include="*.cs" 2>/dev/null; then
    fail "Apache.Arrow.Serialization references DtPipe"
fi
if grep -rqE 'ProjectReference Include="[^"]*DtPipe' "$PROJECT_ROOT/src/Apache.Arrow.Ado/" --include="*.csproj" 2>/dev/null \
   || grep -rq "using DtPipe\." "$PROJECT_ROOT/src/Apache.Arrow.Ado/" --include="*.cs" 2>/dev/null; then
    fail "Apache.Arrow.Ado references DtPipe"
fi
pass "standalone Arrow libraries remain DtPipe-free"

# 3. ArrowBridge project, when extracted, must not reference DtPipe projects.
if [ -d "$PROJECT_ROOT/src/DtPipe.ArrowBridge" ]; then
    if grep -q "DtPipe" "$PROJECT_ROOT/src/DtPipe.ArrowBridge/"*.csproj 2>/dev/null; then
        fail "ArrowBridge must not reference DtPipe projects"
    fi
    pass "ArrowBridge has zero DtPipe references"
else
    echo "  INFO: ArrowBridge not yet extracted (deferred — see remediation notes)"
fi

echo ""
echo -e "${GREEN}Core boundary checks passed.${NC}"
