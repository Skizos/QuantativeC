#!/usr/bin/env bash
# Self-test for .claude/hooks/block-live-trading.sh. Run: bash tests/hooks/block-live-trading.test.sh
# Feeds PreToolUse-style JSON to the hook and checks exit codes (2 = blocked, 0 = allowed).
set -u
here="$(cd "$(dirname "$0")" && pwd)"
hook="$here/../../.claude/hooks/block-live-trading.sh"
fail=0

check() { # expected_exit  command_string
  local expected="$1" cmd="$2" json actual
  # JSON-escape backslashes and double quotes the way Claude Code serializes tool_input.
  json="$(printf '%s' "$cmd" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')"
  printf '{"tool_name":"Bash","tool_input":{"command":"%s","description":"test"}}' "$json" \
    | bash "$hook" 2>/dev/null
  actual=$?
  if [ "$actual" -ne "$expected" ]; then
    echo "FAIL (expected $expected, got $actual): $cmd"; fail=1
  else
    echo "ok   ($actual) $cmd"
  fi
}

# Must be blocked
check 2 'dotnet run --project src/QuantAnalyst.Cli -- paper run --mode Auto'
check 2 'dotnet run --project src/QuantAnalyst.Cli -- rebalance --execute --mode Confirm'
check 2 'qa --mode=auto'
check 2 'qa --mode "Confirm"'
check 2 "qa --mode 'Auto'"
check 2 'qa -m auto'
check 2 'TRADING__MODE=Auto dotnet run --project src/QuantAnalyst.Cli'
check 2 'export TRADING__MODE=Confirm'
check 2 'dotnet user-secrets set "TRADING__MODE" "Auto"'
check 2 'dotnet user-secrets set Trading:Mode Confirm'
check 2 'curl -X POST https://www.avanza.se/_api/trading/order-entry/order/new -d {}'
check 2 'curl https://www.avanza.se/_api/trading-critical/rest/order/modify'
check 2 'curl https://www.avanza.se/_api/trading-critical/rest/order/delete'
check 2 'curl https://www.avanza.se/_api/trading/stoploss/new'
check 2 'curl -X POST https://www.avanza.se/_api/transfer/internal -d {}'
check 2 'curl https://www.avanza.se/_api/payment/withdrawal/new'
check 2 'curl https://www.avanza.se/_api/account-overview/Deposit'
check 2 'curl https://www.avanza.se/_api/konto/uttag'

# Must be allowed
check 0 'dotnet build QuantAnalyst.sln'
check 0 'dotnet test QuantAnalyst.sln'
check 0 'dotnet run --project src/QuantAnalyst.Cli -- backtest --mode Backtest'
check 0 'dotnet run --project src/QuantAnalyst.Cli -- paper run --mode Paper'
check 0 'TRADING__MODE=Paper dotnet test'
check 0 'qa --mode Automatic-docs-only-word'
check 0 'cmake --preset dev && ctest --preset dev --output-on-failure'
check 0 'git status'
check 0 'grep -rn "OrderGateway" src/'
check 0 'grep -rn "_api/transactions/list" docs/'
check 0 'git log --oneline -- docs/adr/0004-authorization-to-automate.md'

exit "$fail"
