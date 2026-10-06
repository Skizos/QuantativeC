# Using QuantAnalyst: the user guide

How you use the program day to day, what you will see, and what is left before real orders. Every command is
described in `docs/cli.md`; the rules behind them are in ADR 0003. Commands are written `qa …`; from the repository
folder that is `.\qa …` (the launcher), or plain `qa` once you have added the profile line from `docs/cli.md`.

**When unsure, run `qa status`.** It is offline and read-only, shows what is set up and what is missing, and ends
with numbered next steps.

## 0. The Windows app (no typing needed)

Everything in sections 2–3 can also be done with the mouse in the QuantAnalyst window.

**Start it:** from the repository folder in PowerShell:
```powershell
.\qa-app.ps1              # builds what changed, shows the status once, opens the window
.\qa-app.ps1 -Shortcut    # the same, plus a "QuantAnalyst" shortcut on your desktop; double-click that from then on
```

**What you see:**
- **Header, always visible:**
  - the amber **PAPER** chip: the app only trades on paper, on live prices; nothing is ever sent to Avanza from it
  - the login method (`bankid`, or `totp` for unattended logins)
  - the red **KILL** button, which stops everything at once, even while a session runs
- **Navigation rail** on the left: Overview, Trading, Charts, Accounts, Instruments, Strategy, Reports. The « button at
  its top folds it to icons for more room (hover an icon for its name).
- **Several pages at once:** at the top right of every page:
  - **Beside:** pick a second page to show on the right, e.g. Charts beside Instruments or Trading. Drag the divider to
    share the width; **×** closes it. Charts beside Charts is a second chart (another name, or Today next to History).
  - **New window:** the page in a window of its own, e.g. on another screen or snapped with Win + ← / →. Open as many as
    you like; they show the same numbers as the main window and close with it.
  - **Refresh** reads the page again from the files the terminal uses. Every page on screen also refreshes by itself.
  - Every page fits half the window: in a narrow pane the buttons move under the title, lists go above their details
    (they scroll), the Overview's tiles go two by two, and the charts shrink instead of being cut off. Drag the divider
    to give a page more room.
- **Overview** (the start page):
  - four tiles: the next session with a countdown ("Mon 28 Sep · decides at 09:10", "in 1 d 21 h"), the Confirm gate
    as 10 dots (one per clean Paper day), the live-trading account (masked, e.g. `***193`) and the kill switch
  - the paper account's value at the last close, the change since Paper started, and a chart of every day's close
    (the same numbers as `qa report eod`)
  - the same checklist as `qa status`, the next steps, and one button for the most useful next thing (e.g. **Add
    instruments**, **Choose a strategy**, **Start the Paper session**); the checklist includes the day's alerts and the
    last backup (plan 25)
- **Trading** (today's Paper session):
  - **Start** logs in (the BankID QR code appears in the window), updates the history, waits for 09:10 and trades on
    paper until the close. **Stop** ends it early, cancelling orders and writing the partial report.
  - while it runs: tiles for the account value and today's change, cash, invested, fees, orders and the decision; the
    account value today against the start of the day (green above, red below); a tile per instrument with its last
    price, change and a small chart; click one for its price today with your fills (▲ buy, ▼ sell) and working limits
  - today's orders with state chips (Working, Partly filled, Filled …) and the strategy's decision notes
  - the session log, folded away at the bottom of the page
  - after a KILL, clear it here by typing why trading may go on
- **Charts** (candlesticks for your instruments, the chart filling the page; pick a name from the chips at the top;
  **New chart window** opens another chart, as many as you like):
  - **History:** the stored daily prices as candles (green closed up, red closed down) by **Day**, **Week** or
    **Month**, starting at a range (1M 3M 6M 1Y 3Y All; 3M at first), with volume bars, the saved strategy's two moving averages
    (else 20 and 50 days) and your trades as ▲ buy / ▼ sell from the daily reports
  - **Today:** while a Paper session runs, **1, 5 or 15 minute** candles built from its quotes, growing as it trades,
    with your fills, working limits and yesterday's close
  - hover for a candle's date, open, high, low, close, change and volume; the mouse wheel zooms, dragging moves,
    double-click goes back to the range; the switches turn volume, averages and trades on or off
  - the history is read while nothing else runs and kept, so it still shows while a session trades
- **Accounts:**
  - the Paper account is always there: its cash, what it started with, fees and holdings
  - **Load** makes one Avanza login (BankID) and shows your accounts: name, type, the number masked (`***193`), value,
    cash, buying power and holdings with gain in kr and %. Nothing logs in by itself; press **Load** again for fresh
    numbers.
  - each account says whether it **can trade live** (R1: an ISK, tradable, not managed, no credit) and, if not, why
  - **Use for live trading** (on an account that can): type the account's last 3 digits and press the button. The app
    sets `AVANZA__ALLOWEDACCOUNTIDS` for your Windows user (what §7 step O6 has you type by hand), so new terminals see
    it. Close and reopen open terminals. **Stop live trading** clears it. Choosing trades nothing: Confirm still runs
    every startup check, in the terminal.
- **Instruments:** your allowed names on the left; on the right the selected name's price chart with ranges (1M 3M 6M
  1Y 3Y All), last close, the change over the range and, with an ma-cross strategy saved, its two moving averages.
  **Remove** takes a name off the list (its stored prices stay).
  - **Find a share** (at the top): type a name or a ticker, e.g. `ericsson`, `volvo` or `ERIC B`. It searches Avanza
    when you pause typing (from 2 characters; Enter searches at once) and lists up to 20 shares, each with its
    country and marketplace, last price, today's change and sector.
  - Each hit says **Add**, or why it can't be added:
    - **On your list** (a green tick)
    - **Trades in EUR** (or another currency): the program trades shares in SEK, USD and CAD (US and Canadian shares on
      paper only, ADR 0005). A US or Canadian hit also shows its price in kronor ("≈ 2 350 kr") once a fixing is stored.
    - **Your list is full (10 names)**: a Paper session polls at most 10; remove one first (you can, while searching)
    - **One share costs more than an order may (500,00 kr)**: R6 with the 5 000 kr account cap
    - **Not tradable at Avanza**
  - **Add** imports 3 years of daily prices and allows the share (the same code as `qa history import` and
    `qa universe add`). The next Paper session trades it.
  - **Searching needs no BankID:** searches go to Avanza without a login, as its own website does, even while a Paper
    session runs.
  - **One login for adding:** the first **Add** logs in (BankID, read-only); every add and remove after it uses that
    login. The green **Avanza login open** chip shows it. **Done** (or Esc) lets it go, and so do 5 minutes without an
    add or leaving the page. Adding waits while something else runs (a Paper session), and nothing else can start while
    the login is open: press **Done** first.
  - Should Avanza ever refuse a search without a login, the app says so and searches in one login instead for the rest
    of that run (never a login per search).
- **Strategy:**
  - pick a strategy and fill in its named fields (e.g. fast 20, slow 100)
  - **Backtest on my instruments** shows how it did
  - **Use for Paper** saves it
- **Reports:** the gate dots, each day marked CLEAN / NOT CLEAN / INCOMPLETE, and the selected day's details. The week
  card shows the **week of the selected day** (the same summary as `.\qa report week`): its return, whether Paper is
  within, below or above the backtest's range this week and since the first Paper day, how far it is ahead of or
  behind **holding the list** (coloured once it is more than noise), the clean days, and the whole summary beside it.
  Select a day of an earlier week to see that week.
- **Bottom bar:** what is running now, and (click it) the activity log with every command's full output.

Numbers on screen are Swedish style (`5 012,40 kr`, `+0,20 %`); commands and files keep their usual format.

**What stays in the terminal:**
- promoting to Confirm (`qa promote`, your deliberate step, §6)
- storing credentials (`qa secrets set`)
- recordings and the probe

The app and the terminal share the same files, so you can use either, or both.

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
3. **Choose what may be traded** (the allowlist, 1–10 shares). In the app: **Instruments → Find a share**, type a
   name and press **Add** (§0). In the terminal:
   ```powershell
   .\qa history import ERIC-B       # adds it to the instrument master and imports a year of daily bars
   .\qa universe add ERIC-B         # allows it to be traded
   ```
   Repeat for each name, at most 10 (`qa universe add` refuses an 11th; Confirm, later, takes at most 5). `ERIC-B` and `"ERIC B"` are the same ticker.
   - **With 5,000 SEK the limits are small:** at most 500 SEK per order and 1,000 SEK (20 %) per name (`.\qa risk-limits`).
     - A share priced above 500 SEK can't be bought at all.
     - With one name, at most 20 % of the account is ever invested. With five names, up to all of it. More names share
       the same capital: ten names at equal weight are about 500 SEK each.
     - **They never grow past that:** the limits are sized on the account's value, but never on more than the
       **account cap**, 5,000 SEK (`max_account_value_sek` in `config/risk-limits.json`). Money added to the account
       doesn't raise them. You may lower the cap; raising it needs a note in ADR 0003's Changes.
4. **Choose a strategy.** First see how it did on your names' history. Without `--tickers`, the backtest uses your allowlist:
   ```powershell
   .\qa backtest run --strategy ma-cross --param fast=20 --param slow=100
   ```
   - The output gives return, Sharpe and the **Deflated Sharpe**. The Deflated Sharpe discounts for how many variants you tried, so trying fewer is better.
   - To compare several parameter sets at once, use `qa backtest sweep`. It also reports the probability that the best one is overfitted (PBO).
   - Strategies: `buy-and-hold`, `ma-cross`, and (plan 27) `inverse-vol` and `risk-parity`, which stay invested and weigh
     each share by its risk (calm shares get more; shares that move together share one share's risk). **Choose two or
     three to compare before you start**, backtest those, and keep one only if it beats `buy-and-hold` on the same list
     after costs: every run is in the trial ledger, and the more you try, the less the best result means (the Deflated
     Sharpe and the PBO say how much less).
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

**Keep the window open all day** (the computer must not sleep). Started after 09:10 it decides as soon as every share
has a live price (usually within seconds, at most a minute); after 17:20 no more orders go out that day.

What you will see (shortened; the numbers are only an example):

```
Paper session: ma-cross(fast=20, slow=100) on ERIC B, VOLV B; courtage class Start; mode Paper (promotion: Paper).
History: ERIC B brought up to 2026-09-25 (1 new bar(s), 0 restated).
Running until 2026-09-28 17:32 (Stockholm). Stop early with Ctrl+C or 'qa kill'.
09:10:00 decision: 2 order(s).
09:10:00 Buy 6 ERIC B: Accepted (Filled, filled 6/6 @ 70.86)
09:10:13 Buy 1 VOLV B: Accepted (Working, filled 0/1)
...
Report: 2026-09-28 CLEAN: 2 sent, 2 accepted, 0 risk-rejected, 2 fill(s) (0 outside ±200 bps), reconciliation 1010/1010 clean, 0 violation(s); value 5,001.20 SEK (+0.02%), fees 0.00. Saved to reports/eod/2026-09-28.json.

Session over: 2 order(s) sent to the paper channel, 2 accepted, 0 stopped by the risk checks, 2 with fills.
Paper account: value 5,001.20 SEK (start of day 5,000.00), cash 4,329.84, fees paid 0.00.
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
| see how often the limit orders fill, and what the unfilled ones cost | the `Limits:` part of `.\qa report eod`, its `limit` lines, and its last line `All N days …` |
| see the progress towards Confirm | `.\qa report gate` |
| the week at a glance (Friday evening or the weekend) | `.\qa report week`, or the week card on the app's Reports page |

**Reading the fill rate** (plan 19). The backtest fills an order whole whenever the day trades through its limit. Paper
fills a resting order only from trades after it was placed, and at most 10 % of their volume. "Missed vs the backtest"
adds up what the unfilled part of the orders the backtest *would* have filled missed by the close:
- positive (e.g. `+15.00 SEK (+71 bps)`): the price moved away from the unfilled orders, so the backtest's results are
  that much better than Paper can do. Consistently positive over a few weeks is evidence for a limit further toward the
  market (a different strategy cost: re-run the backtest with it).
- around zero or negative: the unfilled orders did not cost anything; keep the limits as they are.

**Reading the weekly summary** (plan 20). `.\qa report week` compares Paper's return with what the strategy's recorded
backtest (the one in the trial ledger) makes on an average day, scaled by how much of the account Paper had invested:
- "within the range" is the normal answer for a week: a week is too short to tell much. Look at the "since" line, whose
  range narrows relative to the expectation as the weeks add up.
- "BELOW the range" on the since-line, week after week, means Paper does not do what the backtest did: read the fill
  rate and what the limits missed, and tell Claude.
- "no session" days count against nothing, but each one is a day the Confirm gate and the fill statistics did not get.
- The intraday line says whether the evening collection ran, and how far the collection is from the intraday
  go/no-go.

**Against holding the list** (plan 24). The summary also compares Paper with simply holding the shares on your list at
equal weights (the same close prices Paper used, their dividends included, no costs):
- "the list +2.10%" is what the list did fully invested; "at Paper's 46 % invested +0.97%" is the same with Paper's
  cash, the fair comparison for what the strategy did with the money it had in shares.
- "Paper 0.57 points behind at the same exposure" is the strategy's timing and costs against doing nothing. A week of it
  says little: watch the "since" line and its t. Once it reads "more than noise" and **behind**, week after week, the
  strategy loses to holding the same shares: a simpler strategy (`buy-and-hold` in the backtest) would have earned more.
- If Paper is far behind fully invested but close at its own exposure, the cash is the cost: with few names R7's 20 %
  per name keeps much of the account in cash. More names (up to 10) let more of it work.
- **The market (plan 24 B), once set up:** open OMX Stockholm 30 on avanza.se, copy the number from the page address,
  then `.\qa benchmark set --orderbook-id <number> --name "OMX Stockholm 30"` and `.\qa benchmark import`. The summary
  then also says how the market did over the same days. This uses the price chart for an index, which no reference
  client documents: if the import reports a schema problem, it doesn't work that way, and nothing was stored.

A day is **clean** when it ran to the close with:
- no violations
- every reconciliation matched
- every fill within ±200 bps of the market's price at the time

**Events** are the rules doing their job and do not spoil a day. Examples: a risk check rejecting an order, your own kill.

**The morning start (plan 26).** Paper trades only on days a session runs, and a missed morning is a missed Confirm
gate day. Let Windows remind you (or start it for you):
```powershell
.\qa schedule            # shows the tasks; nothing is registered
.\qa schedule --install  # registers them with Task Scheduler (weekdays; your PC on, you logged in)
```
- **The morning reminder** (08:50): on a trading day without a running session, a notification "Paper has not
  started today: start it before 09:10". Weekends, holidays and a running session are quiet; a kill switch still on is a
  warning instead.
- **The evening import** (18:05): `qa intraday import`, as in the intraday section below (the same task name, so an
  older one is replaced).
- **Unattended (optional, your choice):** BankID needs you at every login. To run without you, store your Avanza
  username, password and TOTP secret once with `.\qa secrets set` (see `docs/setup.md` §5), then
  `.\qa schedule --install --unattended`: the session starts by itself at 08:50 (`--start` to change it) with the TOTP
  login, its window shows and its output also goes to `data\paper-run.log`; the reminder moves to 09:03 and fires only
  if it did not start. The trade-off: anyone who can use your Windows account could then log in to Avanza as you
  (read-only in this program, but still your account). A failed start is a critical alert (plan 25); it never retries.
- A task missed while the PC was off runs when it is on again; a late session still decides at once.
- `.\qa schedule --remove` takes them away. On an exchange holiday the session says there is no session today (no
  alert).

### Alerts and backups (plan 25)

**Alerts** reach you when you are not watching the console: a Windows notification, an `ALERT: …` line, and an entry
in `.\qa alerts`. You get one when:
- the kill switch fires (your KILL, the loss stop, three rejects in a row): **critical**;
- a session could not start (the login failed, the kill switch was still on) or stopped on an error: **critical**;
- trading halts for another reason (prices stale, a reconciliation mismatch): a warning, once per reason in 30 minutes;
- a decision failed (no orders that day), the evening import missed shares, or a backup failed: a warning;
- a Paper day ended: an info summary (value, change, orders). Turn it off with `.\qa alerts set --day-summary off`.

Try it once: `.\qa alerts test` should show a notification (from "Windows PowerShell"). None? Check Windows'
notification settings; Do not disturb hides them. `.\qa status` lists the day's warnings, and a critical one comes
first in its next steps.

**Backups.** Your audit log is the Confirm gate's evidence; the Paper book, the trial ledger and the price store took
weeks to build. Set a backup folder once, on OneDrive or another disk:
```powershell
.\qa backup setup --to "$env:OneDrive\QuantAnalyst-backup"
.\qa backup
```
From then on every Paper session and every evening import makes a backup when it ends (keeping the newest 7), each
copy checked. `.\qa status` says when the last one was made; `.\qa backup verify` checks one again.

**Restoring** (by hand, on purpose):
1. Stop everything: no session, no scheduled task running, the app closed.
2. `.\qa backup verify --path <the backup folder>`: it must say "intact".
3. Copy its `audit`, `config` and `promotion` folders back into the workspace, its `paper` folder to `state\paper`, its
   `ledger\trial-ledger.jsonl` to `research\`, and its `store\quant.duckdb` to `data\`. Keep the old files aside
   until `.\qa audit verify` and `.\qa status` look right.

### Buying and selling by hand (Paper, plan 23)

You can trade a share on your list yourself, in Paper only:
- **In the app:** the Trading page's **Buy or sell by hand** card: pick the share, Buy or Sell, the number of shares and,
  if you want, a limit; press **Place**.
- **In the terminal:** `.\qa paper manual buy ERIC-B 7` (or `sell`; add `--limit 70.5` for a limit).

What happens:
1. The order waits as a request. The running session (or the next one) sends it within seconds in the trading
   window, 09:05–17:20, through the same risk checks as the strategy's orders. Without a limit it buys at the ask
   (sells at the bid). A request is for one trading day: the first window that ends after you place it (placed after
   17:20, it is for the next trading day). One not sent by then (say, no live price came) expires at that day's close
   and shows in the day's report. If the strategy had orders working for the share, they are cancelled first and your
   order follows 5 seconds later (R12).
2. Once it is sent, the share is **yours**: the strategy cancels its own orders for it and leaves it alone at every
   decision ("ERIC B: yours (manual)").
3. **Release** (or `.\qa paper release ERIC-B`) gives the share back: from the next decision the strategy trades it to its
   target again.
4. `.\qa paper orders` shows what is waiting and what happened today; `--cancel <id>` removes a waiting request.
   `.\qa status` lists waiting requests and your manual shares too. The
   day's report lists your manual orders, and the weekly summary notes a week with them (the comparison with the
   backtest then says less, because the returns are not the strategy's alone).

### US and Canadian shares (paper only, ADR 0005)

- **Adding one** works like a Swedish share: find it in the app and press **Add**, or
  `.\qa history import AAPL` then `.\qa universe add AAPL`. The import brings the Riksbank's USD/SEK (or CAD/SEK)
  fixings first. The Instruments list then says "USD · US (NYSE, Nasdaq), paper only".
- **The day is still one session**, started in the morning as before:
  - Swedish names decide at 09:10, US and Canadian ones at 09:40 their time: **15:40 Stockholm** (14:40 in the few
    weeks when US and European summer time differ).
  - Each market's orders expire at its own close: 17:30 in Stockholm, **22:00** for New York and Toronto (19:00 on a
    US early close).
  - The session ends at **22:02**, with one end-of-day report. Keep the window open until then; stopped earlier, the
    day is INCOMPLETE.
  - The Overview shows it, e.g. "Mon 28 Sep · decides at 09:10 (XSTO), 15:40 (XNYS) · ends 22:02", and so do the
    Trading page and `.\qa status`.
- **Money stays in kronor.** The session reads the latest Riksbank fixing at its start and uses it all day:
  - every limit, the cash, the fees and the positions' values are in SEK
  - prices, limits sent and fills stay in dollars (or Canadian dollars)
  - a fixing older than 4 days (or none) skips the foreign names that day; the Swedish ones still trade
- **Costs:** the courtage class's foreign courtage in the share's currency (Start and Mini 0.25 %, at least 1 USD/CAD),
  plus the currency exchange fee (0.25 %, none on Start).
- **Account cap:** with a 5 000 kr cap one order may be 500 kr, and many US shares cost more per share. The search says
  so ("One share costs more than an order may").
- **Backtests** convert a foreign share's prices to kronor at each day's fixing and charge its courtage and FX fee.
- **Paper only:** Confirm (and later Auto) refuses US and Canadian shares before any order card.
- **Not yet checked** (you, once):
  - the foreign courtage and FX fee against Avanza's "Prislista för utlandshandel"
  - the XNYS and XTSE calendars against the exchanges' own pages; then set `verified_on` in
    `config/market-calendar.XNYS.<year>.json` and `…XTSE…`
  - that the first Canadian import finds the Riksbank series `SEKCADPMI`
  - that the first evening session gets live (not delayed) US quotes. With delayed ones, R15 rejects the orders and the
    report shows it.

### Intraday research (plan 17, ADR 0006)

Nothing trades intraday yet. For about six months the program collects 1- and 5-minute bars and spreads; then a report
says whether an intraday strategy is worth trying on paper. What you do:

1. **Once (done 2026-09-29):** `.\qa intraday probe`. It asks Avanza's public chart (no login) how much minute
   history it gives. The answer: today's only (see step 3).
2. **Once:** put up to 30 Stockholm shares on the research list, e.g. the large ones you know:
   `.\qa intraday research add VOLV-B SEB-A ATCO-A`. The search needs no login; nothing on the list is traded.
3. **Every trading day:** nothing new. `.\qa paper run` stores the spreads it saw and, after 17:30, the day's bars.
   On a day without a session, run `.\qa intraday import` in the evening (or schedule it, e.g. 18:05 on weekdays).
   **A missed day can't be fetched later:** Avanza gives 1- and 5-minute bars for today only (your probe,
   2026-09-29). Run the import the same evening, between 17:30 and midnight; a scheduled task is safest.
   `.\qa schedule --install` registers it (with the morning reminder, plan 26); by hand it is this, pasted once into
   PowerShell 7 (it runs weekdays at 18:05 while you are logged in, or as soon as the PC is on again, and
   appends to `data\intraday-import.log`; a second import the same evening changes nothing):

   ```powershell
   $run = "& 'C:\dev\QuantativeC\qa.ps1' intraday import *>> 'C:\dev\QuantativeC\data\intraday-import.log'"
   $action = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument "-NoProfile -Command `"$run`"" -WorkingDirectory 'C:\dev\QuantativeC'
   $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday,Tuesday,Wednesday,Thursday,Friday -At 18:05
   Register-ScheduledTask -TaskName 'QuantAnalyst intraday import' -Action $action -Trigger $trigger -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable)
   ```

   The log file appears after the task's first run, the next weekday at 18:05. To try it at once:
   `Start-ScheduledTask 'QuantAnalyst intraday import'`, wait a minute, then
   `Get-Content data\intraday-import.log -Tail 5` (and `Get-ScheduledTaskInfo 'QuantAnalyst intraday import'`: a
   `LastTaskResult` of 0 means it ran fine). Remove it with `Unregister-ScheduledTask 'QuantAnalyst intraday import'`.

   **If a day is missed anyway** (the PC was off), the next import catches it up: for each share, the trading days
   of the last week without 5-minute bars are fetched once as 10-minute bars (Avanza keeps those for a week). The
   output lists them. Older days are gone. Backtests and the report use those days only with `--fallback`, and
   only for days no share has 5-minute bars for.
4. **Whenever you like, once there are some weeks of bars:** `.\qa intraday backtest --strategy orb-long` (or
   `late-momentum`, `open-close`; `--grid range=5,15,30` tries several). It uses only the days before the intraday
   holdout: the newest 20 collected days stay unseen until you unlock `config/holdout.intraday.json` (your file, like
   `holdout.json`). Every run is logged, mine too, and counts against the result (Deflated Sharpe, PBO). A few weeks
   of data prove nothing, so read these runs as a check that it works, not as a verdict. The verdict is the go/no-go
   report after about six months.
5. **After about six months (120 trading days before the holdout):** `.\qa intraday report`. Each line of its bar
   says PASS, FAIL or WAIT, and the verdict is one of NOT YET, NO-GO, PASSES SO FAR (the holdout still locked) or GO.
   Only when everything else passes, unlock `config/holdout.intraday.json` (set `locked` to false and fill in who,
   when and why) and run the report once more: it then checks the chosen range on the held-out days. A GO means
   Phase B (trading it on paper) may be built; that decision is yours.

## 4. What the numbers mean

- **The limits** (ADR 0003 §4, `config/risk-limits.json`): every order must pass R1–R21. The rejection message names the check, e.g. "R6 order value 620 SEK > 500 SEK".
  - A rejected order is not an error. The next day's decision tries again, and a large target is reached over several days.
- **Daily loss stop (R19):** if the account falls 2 % below its start-of-day value (100 SEK at 5,000 SEK), the kill switch fires and nothing more trades that day. Like the other limits it is sized on at most the 5,000 SEK account cap, so it is never more than 100 SEK a day.
- **Fills:**
  - An order at or through the best price fills at once, up to the displayed volume.
  - A resting order fills only when a later trade prints *through* its limit, and then for at most 10 % of that trade's volume. This is deliberately pessimistic.
- **Paper is not the backtest:** Paper applies the per-order and per-name limits and trades at live prices, so it can hold less than the backtest did.

## 5. When something goes wrong

| You see | It means | Do this |
|---|---|---|
| `ALERT: KILL SWITCH …`, exit code 3 | Trading stopped: your `qa kill`, the loss stop, three rejects in a row, an order in an unknown state, or a reconciliation mismatch | Read `.\qa report eod` and `.\qa kill --status`. When you understand why: `.\qa kill --reset` |
| `The kill switch is active` at start | A kill from earlier is still on | As above |
| A notification "QuantAnalyst: Paper session stopped" | The session could not start (often the login) or stopped on an error; the text says which | Read `.\qa alerts` and the session's output; start it again with `.\qa paper run` when the cause is fixed |
| `qa alerts test` shows no notification | Windows' notification settings (Do not disturb), or no Windows PowerShell | The alert is still in `.\qa alerts` and `.\qa status`. Check Settings › System › Notifications |
| `Backup INCOMPLETE` with `store: NOT COPIED: the store could not be copied …` | Something held the price store (the app, a running import) | Close the app's pages that read prices, or run `.\qa backup` later; the other parts were copied and checked |
| `warn  Backup … over 3 days old` in `qa status` | No session or import made one lately (or automatic is off) | `.\qa backup` |
| `order-depth-stream dropped (HTTP 429)` | Avanza refuses the live order-book stream (seen 2026-09-30). Paper doesn't use it any more: it runs on the 5-second price polls. Only `qa stream` and the live modes (Confirm) still open it | Nothing for Paper. Before Confirm, this must be solved (asking Avanza is the clean way) |
| `R15 market data is stale` | No price poll arrived for 10 s (Avanza slow or down), or in Confirm the order-book stream is down | Paper retries the next day; look for `poll failed` lines in the log |
| `skipped, no fresh live price` in the decision | No quote for that share when it decided: no trade in it, the market data failed, or (before 2026-09-30) a session started after 09:10 that decided before its first quote | Look in the session log for stream or market-data errors. There is one decision a day, so the share waits for the next day |
| `waiting for live prices before deciding (0 of 3 ready; ERIC B: no quote yet) …` | The decision waits for prices the risk checks accept. With some shares priced it waits at most 60 s; with none (a feed outage) up to 30 minutes, so a short outage delays the day instead of losing it | Nothing, unless it ends in `deciding anyway`: then the named share (and why, e.g. `stale: no poll update for 12 s`) is skipped today |
| `FASTAT: the buy limit 0.642 is below the ask 0.645 (spread 0.94 %): it rests until a trade prints through it` | The limit is the last price + 0.5 %, and this share's spread is wider, so the order waits in the book instead of buying at the ask. That is the strategy's (and the backtest's) choice | Nothing. Such an order may stay unfilled and expires at the close; the end-of-day report shows it |
| `… is still held: it moves to the exiting list` after `qa universe remove` | You took a share off the list that Paper still holds. It is not dropped (it would be stranded): the next session sells it, and only sells it | Nothing. When `qa status` says `sold; take it off with qa universe remove …`, run that |
| `manual Buy 7 ERIC B [M…]: rejected (RiskRejected: R6 …)` | Your manual order broke a risk check (here: more than 10 % of the account in one order). Nothing was sent and the share stays the strategy's | Place a smaller order |
| `manual … expired (placed before the trading window of … closed …)` or `(the trading window closed at 17:20 before it was sent …)` | A manual request was not sent in its trading day: no session ran, or no live price came. A request is for one trading day | Place it again on the day you want it |
| `ERIC B: yours (manual), the strategy leaves it` in the decision | You bought or sold ERIC B by hand; the strategy holds it until you release it | `.\qa paper release ERIC-B` (or Release on the Trading page) when the strategy should take it back |
| `ERIC B: dividend 1.45 SEK × 7 (ex-date …) = 10.15 SEK credited` at the start of a session | A share you hold went ex-dividend since the last session. The book is credited on the ex-date (gross), so the day's price drop is matched by the cash and is not a loss for the loss stop | Nothing. The end-of-day report lists it. The real cash comes on the payment date |
| `ERIC B: split 2:1 (Avanza's share count went from … to …)` at the start of a session | A share you hold split: the book now holds twice as many at half the price, same cost | Nothing. If it looks wrong (not a real split), tell Claude with the line |
| `ERIC B: HELD BACK, its price … against its last close … looks like a 2:1 split` in the decision | The price is about half (or a third, double, …) of yesterday's close and no split is known yet. The share is not traded today and is valued at yesterday's close | Check the news on avanza.se. A split: `.\qa paper split ERIC-B 2:1` (after the session). A real fall or rise: `.\qa paper accept-price ERIC-B`. Until then it is held back each day |
| `WARNING: dividends and the split check are off for … today` | Avanza's stock details could not be read for that share | Nothing: the session trades on and catches up the dividends next time. If it repeats with `schema drift`, tell Claude |
| `FAIL  Allowlist … trades only in auctions` (or `… can't be told yet whether it trades continuously`) in `qa status` | A First North share on the list was measured as trading only in Nasdaq's daily auctions (plan 22). Paper leaves it out (`not traded, it does not trade continuously`) and trades the others | Do the step it names: `.\qa universe remove <TICKER>` |
| `Could not add AIRA: … can't be told yet whether it trades continuously (too few trades last week …)` | A First North share traded too little last week to tell whether it trades continuously or only in auctions | Try again after a busier week, or pick a main-market share |
| `warn  Intraday bars … missing 2026-09-23 …` in `qa status` | An evening's intraday collection did not run | Run `.\qa intraday import` now: it catches up the last week at 10 minutes. Days older than a week are lost |
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
  - Avanza's pre-trade `validate` and fee calls (done)
  - the live account and positions feeding the limits, and the one-account check (done)
  - the order card with typed confirmation (done)
  - startup checks of the signed promotion (done)
  - the Confirm session: `qa trade run --mode confirm` and `qa rebalance` (done)
  - the live end-of-day execution report and the Auto gate (done)
  - the final order format from your capture
- **Then you** place the first minimal-size orders yourself.

**Naming the account that may trade (you can do this now):**

The easy way is the app: **Accounts**, **Load**, pick your ISK, type its last 3 digits and press **Use for live
trading** (§0). Then check it in a new terminal with step 3. By hand:
1. Find your ISK's full account number in Avanza (`qa accounts` shows only the last 3 digits).
2. Store it as a user environment variable, in PowerShell:
   `[Environment]::SetEnvironmentVariable('AVANZA__ALLOWEDACCOUNTIDS', '<account number>', 'User')`
3. Open a new terminal and run `.\qa accounts`. The last line must end with `OK`. If it says `refused`, it says why:
   - not set
   - more than one id
   - not one of your accounts
   - not an ISK
   - not tradable
   - a managed account
   - an account with credit

The number never appears in any output, log or file; everything shows `***` and the last 3 digits.

## 8. How Confirm works (Phase 7)

**Built, but it can't start yet.** The session, the cards and the startup checks exist and are tested against a fake
Avanza server. The last startup check refuses the Avanza order channel until your capture (O4, O5) finalises its
format (plan 07 step 7). Until then, `.\qa trade run --mode confirm` prints its checks and stops before any login.
`.\qa status` shows what is still open under "Confirm checks".

**Before your first real day, go through `docs/handover-confirm.md`.** It is the checklist for O9: every prerequisite
with its evidence, what to set the evening before (small orders, no open orders on the ISK, no scheduled Paper run),
what you will see, what to send Claude, and when to step back. It is a draft until your capture is in (plan 07 step 7).

**Your day:** almost the same as Paper, but you must be at the computer at the decision time.

```powershell
.\qa trade run --mode confirm       # instead of: .\qa paper run
```

At 09:10 every order the strategy wants is shown as a card, one at a time. This is the real card (built in step 3;
the numbers are an example):

```
──────────────────────────────────────────────────────────────────────
 ORDER 1 · CONFIRM MODE · a real order on your Avanza account
──────────────────────────────────────────────────────────────────────
 Account     ***123
 Instrument  Ericsson B   ERIC B · orderbook 5240 · SE0000108656
 Side        BUY
 Volume      6
 Limit       70.84 SEK    (rounded down from 70.857 to the 0.02 tick)
 Value       425.04 SEK
 Fee         Avanza 0.00 SEK · model 0.00 SEK
 Reason      ma-cross(fast=20, slow=100): target 20 % of the account in ERIC B
 Decided     09:10:00 at 70.62
 Market      bid 70.84 × 1,200 · ask 70.86 × 950 · last 70.86 · 2 s old

 Risk checks: 21 of 21 pass
   R1  account allowlist             ***123  (limit 1 allowed account(s))  ok
   R2  instrument allowlist          ERIC B (5240)  (limit 1 instrument(s))  ok
   R3  order type                    Limit, condition NORMAL, limit 70.84  (limit LIMIT, NORMAL)  ok
   R4  no short selling              buy  (limit sells <= position)  ok
   R5  price collar                  limit 70.84 is 0.03 % from 70.86  (limit ±2 %)  ok
   R6  max order value               425.04 SEK  (limit 500.00 SEK = min(25,000.00 SEK, 10 % of 5,000.00 SEK))  ok
   R7  max position per instrument   425.04 SEK after the order (incl. working buys)  (limit 1,000.00 SEK)  ok
   R8  max gross exposure            425.04 SEK after the order  (limit 5,000.00 SEK)  ok
   R9  available cash                425.04 SEK incl. fees 0.00 SEK  (limit 4,210.00 SEK)  ok
   R10 max orders per day            1 with this one  (limit 20)  ok
   R11 max order actions per minute  1 in the last minute with this one  (limit 5)  ok
   R12 min interval same instrument  no earlier action  (limit 5 s)  ok
   R13 no opposite working order     0 opposite working order(s)  (limit 0)  ok
   R14 duplicate intent              0 identical intent(s) in the window  (limit 0 within 60 s)  ok
   R15 fresh market data             age 2 s  (limit <= 10 s, stream connected)  ok
   R16 trading window                09:10:02 Stockholm (Full day)  (limit 09:05–17:20)  ok
   R17 no halt                       none  (limit none)  ok
   R18 no unknown orders             0 unknown order(s) in the instrument  (limit 0)  ok
   R19 daily loss stop               0 % today  (limit > -2 %)  ok
   R20 verified constants            courtage verified, calendar verified, tick table verified  (limit all verified)  ok
   R21 broker preflight              all valid  (limit all valid)  ok

 Type  ERIC-B JA  within 30 s to send. Anything else skips this order.
 > ERIC-B JA
 Confirmed. Re-checking before sending …
 Re-checked: 21 of 21 pass (quote 1 s old). Sending.
 Sent: order 123456789, Working.
```

- **The confirmation** is the ticker plus `JA`: `ERIC-B JA` (a space instead of the hyphen and lower case also work). `y`, `JA` alone, Enter or anything else **skips** the order. Nothing is sent.
- **30 seconds** from when the card appears. A late answer is a skip, even a correct one.
- **Not in the first second:** an answer that arrives within 1 s of the card appearing was typed ahead (or meant for the card before), so it skips. A line typed while no card was shown never counts.
- **A card only for an order that passes everything:** all 21 checks, including Avanza's own validation. Avanza is asked only about orders that pass our checks first.
- **Re-checked before sending:** if anything changed to fail after you typed (the price moved outside the collar, the quote went stale, the kill switch fired), the order is skipped and the card says why.
- **One order per confirmation.** Before every card the plan is made again on the current account and prices.
- **One card per instrument a day:** a skipped, rejected or sent one is not proposed again that day, so a late `JA` can never confirm a different card.
- **Ad hoc:** `.\qa rebalance` shows what the strategy would trade right now, sending nothing. `.\qa rebalance --mode confirm --execute` runs the same cards now instead of waiting for 09:10.
- **Stopping** (Ctrl+C, `.\qa kill`) cancels the session's working orders at Avanza, so nothing it placed is left unwatched. A stop between your `JA` and the send sends nothing.
- **The kill switch and the loss stop** work exactly as in Paper, on your live account's values.
- **The end-of-day report** has a live section for every confirmed order:
  - its fills against the decision price and against the mid when it was sent, in bps (positive is a cost)
  - Avanza's quoted fee against the model's
  - the day's value-weighted mean slippage against the backtest's cost assumption (half-spread + slippage)

**Towards Auto (Phase 8):** `.\qa report gate` shows the Auto gate under the Confirm gate, and `.\qa status` shows how
many confirmed orders you have. The gate needs:
- 20 confirmed live orders
- none still Unknown at the end of a day
- mean slippage within the backtest's assumption
- no violations on Confirm days
- an intact audit chain
