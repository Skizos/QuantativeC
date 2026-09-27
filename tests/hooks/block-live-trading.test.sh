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
check 2 "sed -i 's/true/false/' config/holdout.json"
check 2 'echo "{}" > config/holdout.json'
check 2 'python3 -c "import json" config/holdout.json'
check 2 'git checkout -- config/holdout.json'
check 2 'rm config/holdout.json'
check 2 'Set-Content config/holdout.json x'

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
check 0 'cat config/holdout.json'
check 0 'git diff -- config/holdout.json'
check 0 'grep -n locked config/holdout.json'

# Promotion is the owner's (ADR 0003 §3)
check 2 'qa promote --to Confirm'
check 2 './qa promote'
check 2 '.\qa.ps1 promote --to Confirm'
check 2 'dotnet run --project src/QuantAnalyst.Cli -- promote'
check 2 'dotnet src/QuantAnalyst.Cli/bin/Debug/net10.0/qa.dll promote'
check 2 'echo {} > promotion/state.json'
check 2 'cp promotion/state.template.json promotion/state.json'
check 2 'rm promotion/state.json'
check 0 'cat promotion/state.json'
check 0 'cat promotion/state.template.json'
check 0 'qa paper run'
check 0 'grep -rn promote docs/'
check 0 'qa kill --reset'

# Live sessions are the owner's (plan 07, rule 7): 'qa trade' whatever its flags, and 'qa rebalance --execute'
check 2 'qa trade run'
check 2 'qa trade'
check 2 'qa trade --help'
check 2 'QA TRADE RUN'
check 2 './qa trade run'
check 2 '.\qa.ps1 trade run'
check 2 'qa.exe trade run'
check 2 'dotnet run --project src/QuantAnalyst.Cli -- trade run'
check 2 'dotnet src/QuantAnalyst.Cli/bin/Debug/net10.0/qa.dll trade run'
check 2 'pwsh -c "qa trade run"'
check 2 'cd /repo && qa trade run'
check 2 'qa rebalance --execute'
check 2 'qa rebalance --tickers ERIC-B --execute'
check 2 'qa   rebalance   --execute'
check 2 './qa rebalance --execute'
check 2 '.\qa.ps1 rebalance --execute'
check 2 'dotnet run --project src/QuantAnalyst.Cli -- rebalance --execute'
check 0 'qa rebalance'
check 0 'qa rebalance --tickers ERIC-B'
check 0 'qa rebalance --executed-report'
check 0 'qa paper run'
check 0 'qa status'
check 0 'qa trades-export'
check 0 'grep -rn trade docs/'
check 0 'grep -rn rebalance docs/'
check 0 'dotnet test --solution QuantAnalyst.sln'
check 0 'git commit -F /tmp/message.txt'

exit "$fail"
