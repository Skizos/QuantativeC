# 11 — The Windows app, redesigned: readable, modern, with charts and your account

- **Status:** steps 1–4 done 2026-09-27; waiting for the owner's look at the window (screenshots). Planned the same
  day at the owner's request: "Make it easily readable. Make it look modern and also let
  me choose which account I wanna use. Make graphs and stuff alike for the current trading run. Generally make the app
  better with inspirations from other apps, like Avanza."
- **Builds on:** `docs/plans/10-windows-app.md` (the app as it is: five pages, in-process `qa`, Paper only).
- **Gate:**
  - all tests green, including new ones for the chart maths, the live session feed and the account choice
  - a Linux test that every `{StaticResource …}` in the XAML exists and every `{Binding …}` names a real property. A
    missing key only fails when the window opens, and I can't open it here.
  - the architecture and app-safety tests still hold: no order route, no order channel, no preflight, no promotion,
    no `--mode` anywhere in the app
  - **I can't see the window (Linux container).** You run it with `.\qa-app.ps1` and tell me what looks wrong; screenshots
    help most.
- **Not in this plan:**
  - Confirm in the app. Real orders stay a terminal command, typed card by card (plan 07).
  - A dark theme (the colours are tokens, so it can come later).
  - New Avanza endpoints. Everything shown comes from reads the CLI already makes (accounts, positions, quotes,
    daily history), from the audit log, or from the Paper session itself.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| **The look** | Light, calm, card-based, in the spirit of Avanza: white cards on a light grey page, one green accent, black numbers in a large tabular font, **green for up and red for down** with ▲/▼, small tinted chips for states. A slim navigation rail with icons on the left, the mode and KILL always in the header. | Readable at a glance; the numbers carry the page, not the chrome. It is inspired by Avanza, not a copy: no Avanza name, logo or brand colours. |
| **Pages** | 1. **Overview** (new start page) 2. **Trading** (today's Paper session) 3. **Accounts** (new) 4. **Instruments** 5. **Strategy** 6. **Reports**. The old Status checklist lives on the Overview. | You open the app to see how things are, not a checklist. |
| **Charts** | Our own small chart control, not a charting package. The maths is in `Desktop.Core` and tested on Linux: scaling, "nice" axis steps, time labels, the hover point and markers. The WPF part only draws. | No new third-party code to vet (plan 10's rule), and the maths is checked by the same test run as everything else. |
| **What is charted** | **Trading:** the paper account's value today against the start of the day, green above and red below like Avanza's day chart; each instrument's price today with your fills marked (▲ buy, ▼ sell) and working limits as lines. **Instruments:** each name's daily history with ranges (1M 3M 6M 1Y 3Y All) and the strategy's moving averages. **Overview:** the paper account's value day by day, from the end-of-day reports. | The first thing a trader looks at is "how is it going", then "what did it do". |
| **Live data from the session** | A small observer seam, `ISessionObserver`, which the Paper session calls with quotes, the paper account's value, order updates and the day's decision. The CLI passes none, so the terminal is unchanged. The app passes its own and draws from it. | Structured data straight from the running session; nothing parses printed text (plan 10's rule). |
| **"Choose which account"** | The **Accounts** page loads your Avanza accounts with **one BankID login** per refresh: type, name, masked id (`***193`), value, buying power and holdings. Pick one to see it; the Paper account is always there too. For an account that may trade live (an ISK, tradable, not managed, no credit: R1), **"Use for live trading"** makes it the one account Confirm may use. You confirm by typing its last 3 digits. The app then sets `AVANZA__ALLOWEDACCOUNTIDS` for you (your user environment), exactly what `docs/guide.md` §7 has you type by hand today. **"Stop live trading"** clears it. | One place to see and choose. The choice is the same R1 setting as before, so Confirm's startup checks are unchanged. The full account number is never shown, logged or written anywhere but that one variable. |
| **Logins** | Still at most one login per button press (CLAUDE.md). Nothing logs in by itself; account values stay in memory while the app is open. | The safety rule and BankID's one-scan-per-login. |
| **Numbers** | Swedish style where it helps reading: a space as the thousands separator (`5 000,00 kr`) on screen. Commands and files keep the invariant format. | It reads like your bank. |

## Screens

1. **Overview:**
   - the paper account's value (large), today's change, cash and invested %
   - a chart of its value day by day
   - the Confirm gate as 10 dots (green clean, red not clean, amber incomplete)
   - the next session with a countdown, and the kill switch state
   - the setup checklist as chips, collapsed when everything is ok, with the next steps and one primary button
2. **Trading (the Paper session):**
   - Start/Stop and the session's state (waiting for 09:10 with a countdown, running, stopped)
   - KPI tiles: value, today's change, cash, invested, fees, orders and fills
   - the account-value chart
   - one tile per instrument: last price, change today, a sparkline and your position. Click one for its price chart
     with fills and limits.
   - today's orders with status chips, and the strategy's decision notes
   - the session log, collapsed by default
3. **Accounts:**
   - the Paper account card and your Avanza account cards
   - the selected account's holdings (value, gain, %)
   - the live-trading account (R1): which one, and why an account can't be it
4. **Instruments:** the allowlist on the left. On the right, the selected name's chart with ranges, last close and
   change, and the strategy's moving averages. Add/Remove as today.
5. **Strategy:** the form as today. The backtest shows its key numbers as tiles (return, Sharpe, Deflated Sharpe,
   max drawdown, costs), with the full output below.
6. **Reports:** the gate dots, the days as a list with state chips, and the selected day's details.

Always visible: the header (name, the **PAPER** mode chip, the login method, **KILL**) and the bottom bar (what runs
now, the activity log).

## Steps (each ends green, committed and pushed)

1. **Design system:**
   - `Theme.xaml`: colours, type sizes, buttons, inputs, lists and tables, cards, chips, icons
   - the navigation rail and header
   - all current pages restyled
   - the XAML check test
2. **Charts:**
   - `Desktop.Core/Charts` (model, layout, ticks, hover) and the WPF `SeriesChart`
   - the Instruments page chart with ranges and moving averages
   - the paper value history for the Overview
3. **The live session:**
   - `ISessionObserver` in Trading, wired in `qa paper run` through `AvanzaCliServices`
   - the Trading page with live KPIs, charts, instrument tiles and orders
4. **Accounts and Overview:**
   - the one-login account overview, shared by `qa accounts` and the app
   - the live-trading choice with typed confirmation
   - the Overview page
   - docs (`docs/guide.md` §0) and a full test run

## Test map (planned)

| Area | Must show |
|---|---|
| XAML | every static resource key is defined (and before use inside a dictionary); every binding path names a public property of an app type |
| Chart maths | nice steps for any range (including a flat line and one point); points map into the box; hover picks the nearest point; markers land on their time; baseline split into above/below |
| Session feed | a Paper session over the fake Avanza server reports quotes, the decision, order updates with fills, and account values to an observer; with no observer the output is unchanged |
| Trading page | the feed becomes the value series, the per-instrument series, fills as markers and the order list; start of day as the baseline; the change in SEK and % |
| Accounts | one login loads accounts, holdings and R1 eligibility; ids shown masked; only an eligible account can be chosen; the typed digits must match; the variable is set to the full id and cleared on request; nothing logs the full id |
| Overview | the gate dots from the reports; the value history from the reports' end values; the checklist and next action as before |
| Safety | the app still has no path to orders, preflight, promotion or a mode flag |

## Step notes

**Step 1: the design system (done 2026-09-27).**
- **`Themes/Theme.xaml`** holds every colour, text style, icon and control look:
  - buttons: default, Primary, Ghost, Kill and Segment
  - inputs, the drop-down, tooltips, tables, list rows
  - cards, tiles, chips and the mode chip
- **Icons** are simple line drawings of my own on a 24 × 24 grid (no icon font or package). The `Icon` control draws
  them in the surrounding text colour, so a selected navigation item or a white button turns its icon along with it.
- **The window:**
  - a white header with the PAPER chip, the login method and KILL
  - a navigation rail with icons, where Trading now comes second
  - the page title with Refresh
  - a message bar (blue, red for errors)
  - a restyled BankID card and the activity log at the bottom
- **Pages restyled** on the same view models: Status as two cards (setup chips, next steps), Instruments, Strategy
  (form + "Paper trades now"), Trading (still the text session; step 3 makes it live), and Reports with the gate as
  10 dots.
- **Numbers:** `Fmt` formats Swedish style (`5 000,00 kr`, `+1,23 %`, `▲`/`▼`), and `Tone` gives each state word its
  colour. The new pages use them from step 2.
- **Checks:** `XamlResourceTests` finds a missing or out-of-order resource key and a misspelled binding in the XAML
  sources, with positive controls. It checks every XAML file.
- **Tests:** 26 new (XAML checks 7, formatting and tones 19); app tests 58; all 1,009 managed tests pass, 1 skipped
  (Windows only).

**Step 2: charts (done 2026-09-27).**
- **`Desktop.Core/Charts`:**
  - `ChartData` and `ChartLayout`: pixels, nice value steps with at least three labelled lines, hour labels for a day
    and date labels for history, hover, markers, levels and the baseline
  - `ChartRange`: 1M 3M 6M 1Y 3Y All
  - `Indicators.Sma`
  - `ChartSources`: stored closes, and the paper value per close from the end-of-day reports
- **`SeriesChart`** (WPF) draws what the layout says:
  - a soft area and a line, green at or above the baseline and red below
  - averages as thin blue and amber lines, fills as dots, limits as dashed lines with their price
  - values at the right and times below
  - a crosshair with a bubble (value, change, time) on hover
  - a sparkline mode for step 3
- **Instruments:** the allowlist is a list on the left. On the right is the selected name's chart:
  - the last close and its date
  - the change over the range, with its arrow and colour
  - range buttons
  - when the saved strategy is ma-cross, its two moving averages and a line saying what they mean
  The store is read only while no command runs, and a name is read once until its history changes.
- **Status:** a Paper account card with the value at the last close, the change since Paper started, and the chart of
  every close (from the end-of-day reports, so the same numbers as `qa report eod`).
- **Found by the tests:** a narrow price range (70,1–71,3) got only two axis labels. The layout now always gives at
  least three.
- **Tests:** 24 new (chart maths 21, instrument chart 2, paper history 1); all 1,033 managed tests pass, 1 skipped.

**Step 3: the live session (done 2026-09-27).**
- **The seam:**
  - `Trading/Observation`: `ISessionObserver`, its events, and `GuardedObserver`, which swallows anything an observer
    throws
  - `OrderManager.Changed`, raised after every order change, outside its lock, with a throwing handler ignored
  - the Paper session reports the account every 5 s (and at the start and the end), and the decision (including a
    skipped or failed one)
  - `qa paper run` reports the day's frame once ready: open, close, decision time, the strategy, and each instrument
    with its last stored close, read right after the history refresh. It reports every order change and the quotes,
    at most one a second per name.
  - `AvanzaCliServices.SessionObserver` is null in the terminal, so the terminal is unchanged.
- **`LiveSession`** (Desktop.Core) applies the events on the UI thread:
  - the account value today against the start of the day (a point per 15 s; in between the last point moves)
  - KPIs: value and today's change, cash, invested %, fees, orders, the decision
  - a tile per instrument: last price, change against yesterday's close (else the first quote), a sparkline, the
    holding
  - the selected instrument's price today, with fills as dots and working limits as dashed lines
  - today's orders with status chips ("Sending", "Working", "Partly filled", "Filled" …), and the decision's notes
- **The Trading page:**
  - a state chip (Not running / Waiting for 09:10 / Trading) and the schedule, Start, Stop and the kill switch
  - the KPI tiles, the value chart, the instrument tiles and price chart, and the orders beside the decision
  - the session log, collapsed
  - Before a session it shows the paper account as last saved.
- **Tests (11 new):**
  - Paper spy with an observer: day, quotes at most one a second, decision, the 7-share fill, account ticks; the
    session is the same as without one
  - Paper spy with a throwing observer: nothing changes
  - order book: every change is told, and a throwing handler stops nothing
  - the guard, 2
  - `LiveSession`, 5
  - Start passes the page's observer
  All 1,044 managed tests pass, 1 skipped.

**Step 4: Accounts and Overview (done 2026-09-27).**
- **One overview, two users:** `AccountOverview` (Cli) reads the accounts, the trading accounts and the holdings after
  one login, with R1's verdict per account (`AccountAllowlist.Problems`) and whether it is the one
  `AVANZA__ALLOWEDACCOUNTIDS` names. `qa accounts` builds its rows with the same `Summaries`; its output is unchanged.
  The app runs it in-process through `QaEngine.QueryAsync`: busy while it runs, Stop cancels it, the BankID QR code in
  the window, one line in the activity log. No new endpoint: the three reads the CLI already makes.
- **The Accounts page:**
  - the Paper account always, and your Avanza accounts after **Load** (one login per press, never by itself)
  - each card: name, type, masked number, value and a chip (Live trading / Can trade live / Can't trade live /
    Simulated). The selected account shows cash, buying power, holdings with gain in kr and %, and why it can't trade
    live.
  - **Use for live trading** needs the last 3 digits typed. It then writes the full number to
    `AVANZA__ALLOWEDACCOUNTIDS` for your Windows user and for the app's own process; nothing else holds it. **Stop live
    trading** clears it. Neither needs a new login.
  - rows are keyed by their place in Avanza's list, not by the masked number, so two accounts ending in the same 3
    digits can't be mixed up
  - a load opens the live account, else your first Avanza account
- **The Overview:** tiles for the next session with a countdown (from the calendar, `risk_limits.json` and the decision
  time), the Confirm gate as 10 dots (`PromotionGate.Confirm` over the rebuilt reports and the verified audit chain),
  the live-trading account masked, and the kill switch; then the paper account card and chart, the checklist and the
  next steps as before.
- **Docs:** `docs/guide.md` §0 rewritten for the new pages; §7 points to the Accounts page for naming the account.
- **Tests (14 new):**
  - the overview over the recorded 2026-09-25 answers: every account, holdings, R1's verdict; only reads after the
    login; without a choice no account is the live one, 2
  - the Accounts page with a fake source and environment, 6: before loading; load with the chosen login method; the
    digits must match, then set and clear; a choice made in the terminal, and one naming two accounts; two accounts
    ending in the same digits; a failed load. Each checks that no full number (nor its unmasked part) appears in any
    text on the page.
  - the Overview's tiles, 1, and its countdown text, 5
  All 1,058 managed tests pass, 1 skipped (Windows only). The app tests use a fake environment, so they never touch
  your real variables.
