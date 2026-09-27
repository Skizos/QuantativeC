# Your first Confirm day: the handover checklist (O9)

- **Status: DRAFT (2026-09-27).** It is finalised in plan 07 step 8, after step 7 has turned your capture (O4, O5) into the
  final order format. Lines that step 7 may still change are marked **(step 7)**.
- **Who runs it: you, in your own terminal.** Claude never starts Confirm, never promotes, and never calls an order route
  (CLAUDE.md). Every command here is yours.
- **How to use it:** tick every box in order. The "How you know" column tells you what to look at. If a box can't be
  ticked, stop there: nothing later makes up for it.
- **Background:** `docs/guide.md` §8 (how Confirm works, with the real order card), `docs/cli.md` "Confirm mode",
  plan 07 §"Remaining work".

## A. Before the day: every prerequisite, with its evidence

| # | Check | How you know |
|---|---|---|
| A1 | **The Paper gate is met (O1):** 10 clean Paper days in a row, with at least one order sent. Earliest Friday 2026-10-09 if Paper starts Monday 2026-09-28. | `.\qa report gate` prints `Confirm gate: MET` |
| A2 | **Calendar verified (O2)** for this year and next: `verified_on` set in `config/market-calendar.XSTO.<year>.json` after comparing with Nasdaq Stockholm's page | `.\qa status`: Calendar `ok` |
| A3 | **Courtage verified (O3):** `config/costs.avanza-start.json` against Avanza's price list, and your answer on the minimum courtage for an order that fills in parts sent to Claude. The file says `verified_on` 2026-09-26: courtage 0 and minimum 0 on the main market. | `.\qa status`: Paper account `ok` |
| A4 | **Avanza's pre-trade answers recorded:** `.\qa probe --preflight` once (nothing is placed), sanitized with `.\qa recordings sanitize …`, and handed to Claude. Without it, the first card is the first time the program sees Avanza's real `validate` and fee answers, and an unexpected field would halt the session (schema drift). | Claude reports the recorded answers parse (the strict fixture tests) |
| A5 | **The order format is final (O4, O5 → step 7):** your web-app buy and sell capture and the recorded deal handed to Claude, and step 7 merged. **(step 7)** | `.\qa status` no longer lists "order channel" under "Confirm checks" |
| A6 | **The one account that may trade (O6):** `AVANZA__ALLOWEDACCOUNTIDS` holds your ISK's account number (`docs/guide.md` §7) | `.\qa accounts`: the last line ends with `ISK, tradable, not managed, no credit: OK.` |
| A7 | **The limits reviewed (O7)**, with section B's first-day advice in mind | `.\qa risk-limits --account-value <your ISK's value>` shows what they allow on *your* account |
| A8 | **Promoted (O8):** `qa promote --init-key` once, then `qa promote --to Confirm` (you type `Confirm`) | `qa promote --verify` finds no problem |
| A9 | **The audit chain is intact** | `.\qa audit verify` says intact |
| A10 | **Everything together** | `.\qa status`: no `FAIL` line, and "Confirm checks" is `ok`: "every check that runs offline passes" |
| A11 | **Up to date:** `git pull` on main after step 7 was merged, then `.\qa status` (the launcher rebuilds what changed) | the status runs and the "Native engine" line is `ok` |

## B. The evening before

- [ ] **Know what the limits are sized on.** They are shares of the ISK's value (every holding at its price, plus
      cash), but never of more than the **account cap**, `max_account_value_sek` = 5,000 SEK (ADR 0003, Changes
      2026-09-27). So however much the ISK holds, the limits are at most:
      - 500 SEK per order
      - 1,000 SEK per name
      - 5,000 SEK of shares in total (R8)
      - a 100 SEK loss stop

      `.\qa risk-limits --account-value <your ISK's value>` shows it.
      - **Still recommended: a dedicated ISK** holding only what this program trades, funded in Avanza's app. (The
        program never moves money; you do that.) On an ISK with other holdings:
        - Those holdings count towards R8's 5,000 SEK. Once they are worth more, no buy is proposed at all ("R8 gross
          exposure leaves no room").
        - Their daily moves count towards the 100 SEK loss stop, so an ordinary market day can fire the kill switch.
        - The strategy treats every share of an allowlisted name there as its own. If you hold one of your names there
          for another reason, it may propose selling it. Such a card is a SELL card, which you can skip.
- [ ] **For the first day, set the orders small** (recommended). In `config/risk-limits.json`, lower
      `max_order_value_sek` to about 1.5 × the highest share price among your names, so each card is one or two shares.
      The plan clips every order to this cap, so a lower cap makes smaller cards, not rejections. A name priced above the
      cap gets no card at all.
      - Check with `.\qa risk-limits` and `.\qa rebalance` (below).
      - Leave the other limits alone. Lowering `max_orders_per_day`, for example, would turn planned orders into R10
        rejections, and three rejects in a row fire the kill switch.
      - The file is committed in git. `git diff config/risk-limits.json` shows your change, and
        `git checkout config/risk-limits.json` restores it. Going back to the committed values is not a raise. Anything
        above them needs a note in ADR 0003's log.
- [ ] **No open orders on that ISK**, in Avanza's app or with `.\qa orders`. Reconciliation treats an open order on the
      allowed account that the session did not place as a mismatch ("the broker lists order … that the OMS did not
      place"). That halts trading, and after 60 s it fires the kill switch. Orders on your other accounts don't matter.
- [ ] **Enough cash** on the ISK for what you mean to buy (`.\qa accounts`: available for purchase).
- [ ] **No Paper session that morning.** Disable a scheduled one, e.g.
      `schtasks /Change /TN "QuantAnalyst Paper" /DISABLE`. Only one session can hold the session lock, and a Paper
      session that starts first makes Confirm refuse ("one session").
- [ ] **The computer:** sleep and automatic updates off for the day, on mains power, a stable network, and the BankID
      phone charged.
- [ ] **Your time:** at the computer from about 09:00 until the cards are done. That is a few minutes: one card per name,
      13 s apart, 30 s each at most. Then the window stays open until 17:32. Pick a full trading day; `.\qa calendar`
      shows the half days.

## C. The morning, before 09:10

1. [ ] **Open a new PowerShell window yourself** (Windows Terminal or the Start menu). Don't use a terminal that
       Claude Code opened: the first startup check refuses to start when `CLAUDECODE` or `CLAUDE_CODE_ENTRYPOINT` is set.
2. [ ] `cd` to the repository folder, then run `.\qa status`. Check for no `FAIL`, "Confirm checks" `ok`, and "Kill
       switch" off.
3. [ ] **Optional:** run `.\qa rebalance`. It is read-only: one BankID login, then the plan the cards would follow on
       your live account and current prices, with nothing sent. It prints the value and available cash (account masked)
       and one line per name (buy/sell, volume, limit, or why not). Before 09:00 some prices may be missing ("no fresh
       live price"). It also fixes the day's start value for the loss stop if it is the day's first account read, which
       is harmless.
4. [ ] **Close the Windows app's Paper session** if it is open. The app never runs Confirm, but its red KILL button stops
       a Confirm session too: it writes the same `KILL` file.
5. [ ] **Start:**
       ```powershell
       .\qa trade run --mode confirm
       ```
       What you should see, in this order:
       - `Confirm session: <strategy> on <your names>; model courtage class Start.`
       - The startup checks. The ones that need no login run first. If one fails, it prints
         `Confirm can't start; nothing was logged in to or sent:` and the whole list, where `[--]` marks each failing
         line with its reason. Fix those and start again. A refusal at this point never asks for BankID.
       - The BankID QR code. Approve in the app.
       - `Confirm startup checks: all passed.` followed by nine `[ok]` lines:
         `not started from Claude Code`, `promotion`, `verified constants (R20)`, `kill switch`, `trading not disabled`,
         `one session`, `audit chain`, `account (R1)` (masked, "ISK, tradable, not managed, no credit"), and
         `order channel` ("'avanza', order format final" **(step 7)**).
       - The history brought up to yesterday.
       - `Running until <date> 17:32 (Stockholm). The cards start at 09:10; answer each within 30 s. …`
6. [ ] **Open a second PowerShell window** in the repository folder, ready for `.\qa kill --reason "…"`.

## D. Each card

At 09:10 the plan is made, and `decision: N order(s), one card each.` lists every name. Then comes one card at a time.
The plan is made again before every card on the current account and prices.

- [ ] **Read the whole card.** Check:
  - the account (`***` and the last 3 digits of your ISK)
  - instrument, ticker and ISIN
  - side (**BUY** or **SELL**), volume, limit, and value
  - the fee (Avanza's quote next to the model's; a difference over 1 SEK is flagged)
  - the reason, the decision price, the market, and all 21 checks
- [ ] **On the first day, compare with Avanza's app:** the same instrument, and a price near the bid/ask the card shows.
- [ ] **To send:** type the ticker and `JA` (e.g. `ERIC-B JA`; `eric b ja` works too) and press Enter. The answer must
      come at least 1 s after the card appears and within 30 s of it.
- [ ] **In any doubt, don't type it.** Enter, `n`, `JA` alone or waiting 30 s all skip the order, and nothing is sent. A
      skip is audited as a skip, not a rejection. It never spoils the day. That name gets no more cards in this session.
- [ ] **On the first day, confirm one or two small orders and skip the rest.**
- After `JA`, it re-checks everything on a fresh account read, a fresh quote and a fresh answer from Avanza:
  - `Re-checked: 21 of 21 pass … Sending.` then `Sent: order <id>, Working.` (or `Filled`)
  - or a skip, with the check that now fails. Nothing is sent.
- A status line follows each card, e.g. `<time> Buy 1 ERIC B: Accepted (Working, filled 0/1)`. The other outcomes are:
  - `Skipped`
  - `RiskRejected` (our checks)
  - `BrokerRejected` (Avanza refused it)
  - `Blocked` (the account couldn't be read)
  - `Unknown` (Avanza's reply was lost). An Unknown order is never sent again. Reconciliation looks for it at Avanza,
    and the kill switch fires if it stays Unknown for more than 2 minutes. Check Avanza's app.
- After a send, **watch the order in Avanza's app.** It is a day limit order. It may fill at once, later in the day, or
  not at all, in which case Avanza removes it at the close. The session sees fills through reconciliation every 30 s.

## E. During the day

- [ ] **Leave the session window open until it ends by itself** (17:32; earlier on a half day). It keeps reconciling the
      fills and writes the day's report at the close.
- **Lines that need you:**

  | Line | Meaning | Do |
  |---|---|---|
  | `ALERT: KILL SWITCH …` | Trading stopped (loss stop, three rejects in a row, an order Unknown too long, a lasting mismatch, drift, or your kill) | Let the session end, then do section F. Don't reset the switch until you know why. |
  | `RECONCILIATION MISMATCH: …` | Avanza and the order records disagree: a manual order on the ISK, or a fill the session can't explain | Check Avanza's app for orders you placed by hand. Send the line to Claude. |
  | `RECONCILIATION FAILED (…)` | A read that halts: session expired, or drift **(step 7: the deals format)** | The session sends nothing more today. Send the line to Claude. |
  | `no more cards today: trading is halted (…)` | A halt is active | As above |
  | `WARNING: N order(s) may still be open at Avanza` | At the stop, a cancel's outcome was unknown | Check Avanza's app and cancel them there if they are still open |

- **Don't, while the session runs:**
  - trade that ISK by hand
  - change `AVANZA__ALLOWEDACCOUNTIDS`
  - edit `config/`
  - start a second session
- **To stop early:** Ctrl+C in the session window, or `.\qa kill --reason "…"` in the second window (or the app's KILL
  button). Either one cancels the session's working orders at Avanza. A stop between your `JA` and the send sends
  nothing. Afterwards, check Avanza's app, or run `.\qa orders`, to confirm nothing is left.
- **If the program dies** (a crash, a power cut or a lost network): the order stays at Avanza as a day order until the
  close. Cancel it in Avanza's app if you don't want it. **Before starting again the same day, cancel every leftover
  order on the ISK.** The new session didn't place them, so reconciliation would halt on them. A restart keeps the day's
  start value for the loss stop.

## F. After the close

- [ ] **The session's last lines:**
  - `close: Avanza ends the day orders. …`
  - `Report: <date> CLEAN …` (or `NOT CLEAN`, with the reason). This ends with
    `Live: N confirmed order(s), M filled, slippage +x.x bps vs the arrival mid (the backtest assumes 10).`
  - `Session over: …`, the ISK's value against the start of the day, and `Reconciliation: clean`
- [ ] `.\qa report eod`: one `live` line per confirmed order. Each gives the average fill against the decision price and
      against the mid when it was sent (in bps, positive is a cost), then the fees Avanza quoted, the model's, and the
      booked fees **(step 7: booked fees come from the deals)**.
- [ ] **Compare with Avanza's app:** the fill price and volume in the ISK's transactions against the report, and the
      courtage charged (0 expected on Start) against the booked fees.
- [ ] `.\qa audit verify` says intact.
- [ ] `.\qa report gate`: the Auto gate now counts your confirmed orders ("confirmed live orders: 1 (need 20)"). As built
      today, a violation on **any** Confirm day keeps the Auto gate shut (open decision D2 below).
- [ ] **Limits:** keep the first-day cap for a few days, or restore the file (section B) when you are satisfied.
- [ ] **Scheduled Paper:** re-enable the task if you go back to Paper
      (`schtasks /Change /TN "QuantAnalyst Paper" /ENABLE`).

## G. What to send Claude, and what never

**Send:**
- the session window's whole output (account ids in it are masked)
- `reports/eod/<date>.json`
- the output of `.\qa report gate` and `.\qa audit verify`
- for anything unexpected, the exact line and the time

**Never send:**
- your full account number
- screenshots that show it
- cookies, headers or tokens from DevTools
- anything about BankID, TOTP secrets or codes
- an unsanitized `recordings/live/…` folder

Claude then compares the report with what you saw and checks the fees, and fixes what it finds in code, tested on your
recordings. Claude never runs Confirm.

## H. When to step back

- **A violation, a reconciliation mismatch, a drift halt, or anything you didn't expect:** don't start Confirm again
  until it is explained. Send section G to Claude.
- **Back to Paper:** `qa promote --to Paper` (typed and signed like the promotion). After it, Confirm refuses to start,
  and `.\qa paper run` works as before.

## Open before this checklist is final

- **Step 7 (O4, O5):**
  - the `order channel` check's exact line
  - the deals format behind reconciliation and the booked fees
  - the first real deal replayed through reconciliation
- **A4:** the real preflight answers (`qa probe --preflight`) parsed. If `commissionWarning` fires for ordinary small
  orders, ADR 0003's "any `valid:false` rejects" needs your decision (plan 07 step 1).
- **Your open decisions** (tell Claude if you want any changed):
  - D1: the 1 s minimum before an answer counts
  - D2: no violations on any Confirm day for the Auto gate
  - D3: refusing managed accounts and accounts with credit (R1)
- **O3:** the minimum courtage once or per part when an order fills in parts.
- **Step 8:** a full test run with its output, and this file's status set to final.
