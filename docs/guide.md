# Using QuantAnalyst: the user guide

How you use the program day to day, what you will see, and what is left before real orders. Every command is
described in `docs/cli.md`; the rules behind them are in ADR 0003. Commands are written `qa …`; from the repository
folder that is `.\qa …` (the launcher), or plain `qa` once you have added the profile line from `docs/cli.md`.

**When unsure, run `qa status`.** It is offline and read-only, shows what is set up and what is missing, and ends
with numbered next steps.

## 1. What it does today, and what it does not

| Mode | What happens | Available |
|---|---|---|
| **Backtest** | A strategy is replayed on history with Avanza's costs. Every run goes to the trial ledger. | now |
| **Paper** | The strategy trades **simulated** orders on **live** Avanza quotes, through every risk check. Nothing is sent to Avanza. | now |
| **Confirm** | The same, but real orders on your Avanza account. **You type a confirmation for every single order.** | Phase 7: section 7 below lists what is left |
| **Auto** | Real orders without confirmation, within hard limits and a kill switch. | Phase 8, after at least 20 confirmed live orders |

The program never moves money (no transfers, deposits or withdrawals exist in it), and Claude can never start Confirm
or Auto, or promote the mode: those are your commands.

## 2. One-time setup (about 30 minutes)

`qa status` shows which of these are still open.

1. **Build and check:** `.\qa status`. The first run builds everything (a few minutes). The "Native engine" line must be `ok`. Prerequisites are in `docs/setup.md`.
2. **Log in once:** `.\qa login`. Scan the QR code with the BankID app and approve. This checks the connection; nothing is changed on your account.
3. **Choose what may be traded** (the allowlist, 1–5 Swedish shares):
   ```powershell
   .\qa history import ERIC-B       # adds it to the instrument master and imports a year of daily bars
   .\qa universe add ERIC-B         # allows it to be traded
   ```
   Repeat for each name. `ERIC-B` and `"ERIC B"` are the same ticker.
   - **With 5,000 SEK the limits are small:** at most 500 SEK per order and 1,000 SEK (20 %) per name (`.\qa risk-limits`).
     - A share priced above 500 SEK can't be bought at all.
     - With one name, at most 20 % of the account is ever invested. With five names, up to all of it.
4. **Choose a strategy.** First see how it did on your names' history. Without `--tickers`, the backtest uses your allowlist:
   ```powershell
   .\qa backtest run --strategy ma-cross --param fast=20 --param slow=100
   ```
   - The output gives return, Sharpe and the **Deflated Sharpe**. The Deflated Sharpe discounts for how many variants you tried, so trying fewer is better.
   - To compare several parameter sets at once, use `qa backtest sweep`. It also reports the probability that the best one is overfitted (PBO).
   - The last year (the holdout, from 2025-10-01) stays locked, so you can test on it once at the end.
5. **Save the strategy Paper will trade:**
   ```powershell
   .\qa paper strategy ma-cross --param fast=20 --param slow=100
   ```
   `.\qa paper strategy` (no arguments) shows it again.
6. **Optional, so the trial ledger shows the trials as yours:** add `$env:QA_RUNNER = 'owner'` to your PowerShell profile.
7. **Check:** `.\qa status`. The last next step now says when to start the next session.

## 3. Every trading day (Paper)

**Morning, before 09:10:**

```powershell
.\qa paper run
```

This one command runs the whole day:
- **Logs in once:** approve in the BankID app.
- **Updates history:** brings each name's daily bars up to yesterday.
- **Waits until 09:10.** Then the strategy decides on bars through yesterday's close and places day limit orders on the paper account. The orders go 13 s apart, through every risk check.
- **Fills during the day:** orders fill on the live quotes and expire at the close.
- **Stops two minutes after the close** (17:32; earlier on half days). It prints the day's summary and writes the end-of-day report.

**Keep the window open all day** (the computer must not sleep). Started after 09:10 it decides at once; after 17:20 no
more orders go out that day.

What you will see (shortened; the numbers are only an example):

```
Paper session: ma-cross(fast=20, slow=100) on ERIC B, VOLV B; courtage class Start; mode Paper (promotion: Paper).
History: ERIC B brought up to 2026-09-25 (1 new bar(s), 0 restated).
Running until 2026-09-28 17:32 (Stockholm). Stop early with Ctrl+C or 'qa kill'.
09:10:00 decision: 2 order(s).
09:10:00 Buy 6 ERIC B: Accepted (Filled, filled 6/6 @ 70.86)
09:10:13 Buy 1 VOLV B: Accepted (Working, filled 0/1)
...
Report: 2026-09-28 CLEAN: 2 sent, 2 accepted, 0 risk-rejected, 2 fill(s) (0 outside ±200 bps), reconciliation 1010/1010 clean, 0 violation(s); value 5,001.20 SEK (+0.02%), fees 2.00. Saved to reports/eod/2026-09-28.json.

Session over: 2 order(s) sent to the paper channel, 2 accepted, 0 stopped by the risk checks, 2 with fills.
Paper account: value 5,001.20 SEK (start of day 5,000.00), cash 4,329.94, fees paid 2.00.
Reconciliation: clean. Audit: audit (check with 'qa audit verify').
```

**During the day (in a second window):**

| You want to… | Command |
|---|---|
| see the paper account | `.\qa paper status` |
| see everything at once | `.\qa status` |
| **stop everything now** | `.\qa kill --reason "…"`. The session halts within a second and cancels every working order. |

**After the close:**

| You want to… | Command |
|---|---|
| read the day's report | `.\qa report eod` (it is also printed at the end of the session) |
| see the progress towards Confirm | `.\qa report gate` |

A day is **clean** when it ran to the close with:
- no violations
- every reconciliation matched
- every fill within ±200 bps of the market's price at the time

**Events** are the rules doing their job and do not spoil a day. Examples: a risk check rejecting an order, your own kill.

**Unattended mornings (optional):** BankID needs you at every login.
1. To run without you, store TOTP credentials once with `.\qa secrets set` (see `docs/setup.md` §5).
2. Then schedule the session with Windows Task Scheduler, e.g. at 08:55 on weekdays:
   ```powershell
   schtasks /Create /TN "QuantAnalyst Paper" /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 08:55 /TR "pwsh -NoProfile -File C:\dev\QuantativeC\qa.ps1 paper run --login totp"
   ```
On an exchange holiday the command simply says there is no session today.

## 4. What the numbers mean

- **The limits** (ADR 0003 §4, `config/risk-limits.json`): every order must pass R1–R21. The rejection message names the check, e.g. "R6 order value 620 SEK > 500 SEK".
  - A rejected order is not an error. The next day's decision tries again, and a large target is reached over several days.
- **Daily loss stop (R19):** if the account falls 2 % below its start-of-day value (100 SEK at 5,000 SEK), the kill switch fires and nothing more trades that day.
- **Fills:**
  - An order at or through the best price fills at once, up to the displayed volume.
  - A resting order fills only when a later trade prints *through* its limit, and then for at most 10 % of that trade's volume. This is deliberately pessimistic.
- **Paper is not the backtest:** Paper applies the per-order and per-name limits and trades at live prices, so it can hold less than the backtest did.

## 5. When something goes wrong

| You see | It means | Do this |
|---|---|---|
| `ALERT: KILL SWITCH …`, exit code 3 | Trading stopped: your `qa kill`, the loss stop, three rejects in a row, an order in an unknown state, or a reconciliation mismatch | Read `.\qa report eod` and `.\qa kill --status`. When you understand why: `.\qa kill --reset` |
| `The kill switch is active` at start | A kill from earlier is still on | As above |
| `login locked`, exit code 4 | A login failed and the program will not retry on its own | Check that BankID login works on avanza.se, then `.\qa login --clear-lock` |
| `schema drift` or `endpoint gone`, exit code 3 | Avanza changed its site; trading halts | Nothing is lost. Tell Claude and include the error text |
| `ABI 1.1 … requires 1.2` | The native engine is older than the code | Use the `.\qa` launcher, which rebuilds it (or `.\build.ps1`) |
| `'X' is not in the instrument master` | The ticker was never imported | `.\qa history import X` first |
| `No session left today` | Started after the day's session ended, or on a holiday | Start it the next trading morning (the message says when) |
| `Reconciliation: MISMATCH` | The paper book and the order records disagree | Don't reset anything; send the day's report and audit file to Claude |
| `audit … BROKEN` | Someone or something edited an audit file | Don't trade until it is explained (`.\qa audit verify` says where) |

## 6. From Paper to Confirm (your decision)

1. Run Paper until `.\qa report gate` says **MET**. That takes 10 clean Paper trading days in a row after the last day that was not clean, with at least one order sent in them, and an intact audit chain. The earliest possible date, starting Monday 2026-09-28, is Friday 2026-10-09.
2. Once, create your promotion key: `qa promote --init-key`. It lives in Windows Credential Manager, is never shown, and only you have it.
3. Promote: `qa promote --to Confirm`. It rebuilds every report from the audit log, re-checks the gate, asks you to type `Confirm`, and writes a signed record.
4. Nothing trades for real until Phase 7 is built, and you then start Confirm yourself.

## 7. What is left before real orders (Confirm)

The exact list, with who does what, is in `docs/plans/07-phase7-confirm.md` §"Remaining work". In short:

- **You:**
  - the Paper gate
  - verify the calendar and courtage files (`verified_on`)
  - capture one real buy and one real sell from Avanza's web app in DevTools, sanitized. This finalises the order format.
  - name the one account that may trade
  - create the key and promote
- **Claude** (tested on recordings, never run live):
  - Avanza's pre-trade `validate` and fee calls
  - the live account and positions feeding the limits
  - the order card with typed confirmation
  - startup checks of the signed promotion
  - the live end-of-day execution report
- **Then you** place the first minimal-size orders yourself.

## 8. How Confirm will work (Phase 7, planned)

**Your day:** almost the same as Paper, but you must be at the computer at the decision time.

```powershell
.\qa trade run --mode confirm       # instead of: .\qa paper run
```

At 09:10 every order the strategy wants is shown as a card, one at a time:

```
──────────────────────────────────────────────────────────────────────
 ORDER 1 of 2 · CONFIRM MODE · a real order on your Avanza account
──────────────────────────────────────────────────────────────────────
 Account     ISK ***123
 Instrument  Ericsson B   ERIC B · orderbook 5240 · SE0000108656
 Side        BUY
 Volume      6
 Limit       70.85 SEK    (rounded down from 70.857 to the 0.01 tick)
 Value       425.10 SEK
 Fee         Avanza 1.06 SEK · model 1.06 SEK
 Reason      ma-cross(fast=20, slow=100): target 20 % of the account in ERIC B
 Decided     09:10:00 at 70.62 (close 2026-09-25: 70.40)
 Market      bid 70.84 × 1,200 · ask 70.86 × 950 · last 70.85 · 2 s old

 Risk checks: 21 of 21 pass
   R1  account          ***123 is allowed                         ok
   R5  price collar     +0.3 % from 70.64 (max ±2 %)              ok
   R6  order value      425 SEK (max 500)                         ok
   R7  position after   425 SEK (max 1,000)                       ok
   R9  cash             426 SEK of 4,210 available                ok
   …
   R21 Avanza validate  valid                                     ok

 Type  ERIC-B JA  within 30 s to send. Anything else skips this order.
 > ERIC-B JA
 Re-checked: 21 of 21 pass (quote 1 s old). Sent: Avanza order ***456, Working.
 09:10:21 filled 6 @ 70.85, fee 1.06 SEK.
──────────────────────────────────────────────────────────────────────
```

- **The confirmation** is the ticker plus `JA`: `ERIC-B JA` (a space instead of the hyphen and lower case also work). `y`, `JA` alone, Enter or anything else **skips** the order. Nothing is sent.
- **30 seconds** from when the card appears. A late answer is a skip, even a correct one.
- **Re-checked before sending:** if anything changed to fail after you typed (the price moved outside the collar, the quote went stale, the kill switch fired), the order is skipped and the card says why.
- **One order per confirmation.** After each fill or skip, the next card is re-planned with the new state.
- **Ad hoc:** `.\qa rebalance` shows what the strategy would trade right now, sending nothing. `.\qa rebalance --mode confirm --execute` runs the same cards now instead of waiting for 09:10.
- **The kill switch, the loss stop and the end-of-day report** work exactly as in Paper. The report adds each fill's slippage against the decision and arrival prices, and Avanza's fee against the model's.

After at least 20 confirmed live orders with no unresolved unknown states and slippage within the backtest's
assumption, `.\qa report gate` will show the Auto gate (Phase 8).
