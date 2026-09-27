#!/usr/bin/env bash
# PreToolUse hook (matcher: Bash). Hard guarantee that Claude Code cannot trade live:
# blocks any shell command that would start the app in Confirm/Auto mode, switch the
# configured mode to Confirm/Auto, or hit an Avanza order endpoint directly.
# Exit code 2 = block the tool call; stderr is shown to Claude.
# Dependency-free on purpose (no jq/python): it greps the raw hook JSON from stdin.
set -u
input="$(cat)"

deny() {
  echo "BLOCKED by .claude/hooks/block-live-trading.sh: $1" >&2
  echo "Claude Code must never run QuantAnalyst in Confirm/Auto mode or call a live order endpoint (see CLAUDE.md, 'Absolute safety rules'). Ask the user to run it themselves." >&2
  exit 2
}

# Terminator after the mode word: anything that is not part of an identifier (or end of input).
end='([^A-Za-z0-9_]|$)'
# Optional quoting between flag and value, including JSON-escaped quotes (\").
q='[\\"'"'"']*'

# 1) CLI flag: --mode Confirm | --mode=Auto | --mode "Auto" | -m auto
if printf '%s' "$input" | grep -Eiq -- "(--mode|[[:space:]]-m)[[:space:]=]+${q}(confirm|auto)${end}"; then
  deny "command selects Confirm/Auto trading mode via --mode."
fi

# 2) Config/env: TRADING__MODE=Auto, Trading:Mode Auto, user-secrets set "TRADING__MODE" "Confirm"
if printf '%s' "$input" | grep -Eiq -- "trading(__|:)mode${q}[[:space:]=:]*${q}[[:space:]]*${q}(confirm|auto)${end}"; then
  deny "command sets TRADING__MODE / Trading:Mode to Confirm/Auto."
fi

# 3) Direct calls to Avanza order-entry endpoints (place/modify/delete, stop-loss, fund orders).
if printf '%s' "$input" | grep -Eiq -- "(order-entry/order|rest/order/(new|modify|delete)|stoploss/(new|modify)|fund-order-page/(buy|sell))"; then
  deny "command references an Avanza order endpoint."
fi

# 4) Money movement under Avanza's API (ADR 0004): transfers, withdrawals, deposits, payments.
#    Matched by keyword because no maintained client documents these paths; "transactions" stays allowed.
if printf '%s' "$input" | grep -Eiq -- "_api/[A-Za-z0-9/_.-]*(transfer|withdraw|deposit|payment|uttag|overforing|insattning)"; then
  deny "command references an Avanza money-transfer endpoint (ADR 0004: the program never moves money)."
fi

# 5) The final backtest holdout (CLAUDE.md "Final holdout window locked unless I unlock it"): Claude may read
#    config/holdout.json but never change, move, delete or restore it. Only the owner edits it.
if printf '%s' "$input" | grep -Eiq -- "holdout\.json" \
   && printf '%s' "$input" | grep -Eiq -- "(>|sed[^|;&]*-i|(^|[^a-z])(tee|mv|cp|rm|truncate|dd|install|chmod|ln)[[:space:]]|python|perl|ruby|node|pwsh|powershell|git[[:space:]]+(checkout|restore|rm|mv|reset|stash|apply)|Set-Content|Out-File|Remove-Item|Move-Item|Copy-Item)"; then
  deny "command would change config/holdout.json (the final backtest holdout is the owner's to unlock)."
fi

# 6) Promotion (ADR 0003 §3): 'qa promote' raises the highest allowed trading mode. Only the owner runs it, and
#    Claude never writes promotion/state.json (reading it is fine).
if printf '%s' "$input" | grep -Eiq -- "(^|[^A-Za-z0-9_-])(qa(\.ps1|\.exe|\.dll)?|--)[[:space:]]+promote([^A-Za-z0-9_-]|$)"; then
  deny "command runs 'qa promote' (promotion is the owner's step, ADR 0003 §3)."
fi
if printf '%s' "$input" | grep -Eiq -- "promotion/state\.json" \
   && printf '%s' "$input" | grep -Eiq -- "(>|sed[^|;&]*-i|(^|[^a-z])(tee|mv|cp|rm|truncate|dd|install|chmod|ln)[[:space:]]|python|perl|ruby|node|pwsh|powershell|git[[:space:]]+(checkout|restore|rm|mv|reset|stash|apply)|Set-Content|Out-File|Remove-Item|Move-Item|Copy-Item)"; then
  deny "command would change promotion/state.json (the owner's promotion record, ADR 0003 §3)."
fi

# 7) Live sessions (Phase 7): 'qa trade' runs a live trading session, whatever its flags, and 'qa rebalance --execute'
#    sends the rebalance orders. Both are the owner's to start. Plain 'qa rebalance' (a plan that sends nothing) and
#    'qa paper run' stay allowed.
live_verb='(^|[^A-Za-z0-9_-])(qa(\.ps1|\.exe|\.dll)?|--)[[:space:]]+'
if printf '%s' "$input" | grep -Eiq -- "${live_verb}trade([^A-Za-z0-9_-]|$)"; then
  deny "command runs 'qa trade' (a live trading session is the owner's to start, plan 07)."
fi
if printf '%s' "$input" | grep -Eiq -- "${live_verb}rebalance([^A-Za-z0-9_-]|$)" \
   && printf '%s' "$input" | grep -Eiq -- "--execute([^A-Za-z0-9_-]|$)"; then
  deny "command runs 'qa rebalance --execute' (sending rebalance orders is the owner's to start, plan 07)."
fi

exit 0
