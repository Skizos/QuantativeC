# 16 — Foreign shares (USD, CAD) on paper

- **Status:** planned 2026-09-28 at the owner's request: "Make it able to use CAD and USD too."
- **Decisions:** ADR 0005, taken by the owner on 2026-09-28: one session a day to 22:02, and foreign shares on paper
  only.
- **Research (CLAUDE.md rules):**
  - No new Avanza endpoint is needed. Search, orderbook, price chart and the quote stream already carry the currency.
  - **FX:** the Riksbank's SWEA API (ADR 0005 references), a new vendor adapter in QuantAnalyst.Data. It is tested over
    provisional fixtures in the documented `[{date, value}]` shape until a recording exists.
  - **Calendars:** exchange_calendars 4.13.2 (the library the XSTO draft was checked against).
  - **Foreign courtage:** a search-engine extract, UNVERIFIED (avanza.se is blocked here).
- **Gate:**
  - all tests green, with new ones per step
  - the native ABI tests on both sides for 1.3
  - `qa backtest` and `qa paper run` unchanged for a Swedish-only list (the existing tests stay green, untouched in
    meaning)
  - nothing live is called by me

## Steps (each ends green, committed and pushed)

1. **Markets and calendars.**
   - A market registry: SEK → XSTO, USD → XNYS, CAD → XTSE.
   - Calendars in their own time zone.
   - The XNYS and XTSE 2026–2027 drafts.
   - `qa calendar --market`.
2. **FX rates.**
   - The Riksbank adapter.
   - An FX table in the history store: date, currency, SEK per unit, source, known_at. A store migration.
   - `qa fx import|show`.
   - `qa history import` of a USD or CAD share imports its FX history too.
3. **The allowlist and the search.**
   - SEK, USD and CAD may join the list; other currencies are refused with the reason.
   - The app's hits show the SEK price next to the foreign one.
   - The R6 check ("one share costs more than an order may") uses the latest stored fixing.
4. **Costs and backtests.**
   - Foreign courtage in the costs files.
   - ABI 1.3 `qe_bt_set_courtage` (C++ engine, C shim, C# binding, tests on both sides).
   - Foreign prices converted to SEK per day in the backtest panel.
   - The report labels the FX source and the approximation.
5. **The trading core in SEK.**
   - An FX table in the risk context, the planner, the paper book (positions keep their currency) and the paper channel
     (buying power, reservation, courtage in the share's currency, the FX fee).
   - R16 per market.
   - The gateway refuses USD and CAD outside Paper and Backtest.
6. **One session across markets.**
   - A decision per market.
   - A market's orders end at its close.
   - The session ends 2 minutes after the last close; one report.
   - History brought up to each market's previous trading day.
   - FX fixing at the start.
   - `qa status` shows the next decision per market.
7. **The app and the guide.**
   - The Trading page and the Overview show both decision times and the end.
   - The Instruments page shows foreign prices with their SEK value.
   - `docs/guide.md`.

## Step notes
### Step 1: markets and calendars (done 2026-09-28)

- **Markets** (`Core/Market/Markets.cs`): SEK → XSTO (Europe/Stockholm), USD → XNYS (America/New_York), CAD → XTSE
  (America/Toronto). `Markets.ForCurrency` returns null for any other currency. `MarketTime.Zone` and `TryLocalToUtc`
  work in any of the three zones; the Windows ids are used as a fallback.
- **Calendars in their own time:** `CalendarYear.TimeZoneId` and `MarketCalendar.TimeZone` / `LocalDate` / `ToUtc`.
  `IsOpen` uses the market's clock. The loader requires each market's own zone and refuses unknown markets.
- **The drafts:** `config/market-calendar.{XNYS,XTSE}.{2026,2027}.json`, UNVERIFIED, from exchange_calendars 4.13.2.
  Session 09:30–16:00 and early closes 13:00, local time.
- **`qa calendar --market XNYS|XTSE`:** local times, and a foreign day's session in Stockholm time too, e.g. 15:30–19:00
  on the day after Thanksgiving.
- **Tests (23 new):**
  - the registry
  - every day of both years classified
  - the drafts recomputed from the US and Canadian holiday rules (observed days, early closes)
  - open/closed across both DST changes (New York opens 14:30 Stockholm on 9 March and 26 October 2026)
  - wrong time zone and unknown market refused
  - the CLI

### Step 2: FX rates (done 2026-09-28)

- **`RiksbankFxSource`** (QuantAnalyst.Data/Fx): `GET https://api.riksbank.se/swea/v1/Observations/SEK<CCY>PMI/{from}/{to}`,
  keyless, one call per import, a shared HTTP client, 30 s timeout. The answer is read strictly:
  - an array of `{date, value}`; a null value is a day without a fixing
  - anything else, a date twice, or a rate outside 1–100 SEK (a per-100 quote) is refused
  - 204 means no fixing in the range; 429 says to wait a minute
- **The FX table** `fx_rates` (currency, date, SEK per unit, known_at, source) is added in place; the schema version
  stays 1. Writes are append-only with restatements, like the bars. `LatestFxRate` gives the last fixing on or before a
  date.
- **`FxImporter`:** USD and CAD only. A range of 10 days or more without a fixing means a wrong series and stores
  nothing.
- **`qa fx import USD [CAD] --from … [--to …]`, `qa fx show USD`.**
- **`qa history import` of a USD or CAD share** (and the app's Add) imports the fixings **first**, from 10 days before the
  first bar, so a share whose rates can't be read stores nothing. USD and CAD shares are stored as continuously traded
  (their exchanges trade continuously).
- **Tests (21 new):**
  - the adapter over a fake handler: series, URL, keyless, sorting, null days, 9 kinds of bad answer, 204, a wrong series
  - the store: idempotent re-import, restatements invisible before they were known, latest on or before, an old store
    gaining the table
  - the CLI: import and show, other currencies refused, an unreachable Riksbank, a US share's history import with its
    fixings, a failed FX import storing nothing
- All 1180 tests pass.

### Step 3: the allowlist and the search (done 2026-09-28)

- **The allowlist** takes shares in SEK, USD and CAD (`Allowlist.Check`, used by `qa universe add` and the app's Add).
  Any other currency is refused with the reason. The Paper session trades USD and CAD names from step 6 on; steps 3–6
  land together.
- **The app's hits:**
  - A US or Canadian share can be added.
  - Its price shows in kronor too ("≈ 2 350 kr") at the latest stored fixing.
  - The R6 check ("one share costs more than an order may") compares that SEK price.
  - With no fixing stored yet, no SEK value is shown and Add decides (it imports the rates first).
  - The hint says foreign shares trade on paper and make the session run to 22:02.
- **Tests (5 new):**
  - US and Canadian hits without and with a stored fixing
  - other currencies refused
  - the SEK price
  - adding a US share through the session (fixings first, then the list)
  - `qa universe add AAPL`
- All 1183 tests pass.

### Step 4: costs and backtests (done 2026-09-29)

- **Foreign courtage** in every `config/costs.avanza-*.json` (`foreign_courtage.USD|CAD` with min, rate, source_url,
  verified_on null, basis): Start and Mini 0.25 % min 1; Small 0.15 % min 6; Medium 0.089 % min 8; Fast pris 0.079 %
  min 12. UNVERIFIED. `CostModel.CourtageIn(currency, value)` charges it in the share's currency. Another currency in
  the section is refused.
- **Native ABI 1.3:** `qe_bt_set_courtage(backtest, instrument, min, rate)` gives one instrument its own courtage
  (before the first step). The engine's cash check counts it. The managed side requires 1.3 (`QeBacktest.SetCourtage`).
  The ABI 1.2 tests are unchanged apart from "minor ≥ 2"; the export check still matches the header.
- **Backtests in SEK** (`BacktestCommands.LoadStorePanel`, also used by the Paper decision):
  - a USD or CAD share's bars are converted day by day at the latest Riksbank fixing on or before the bar; a fixing
    more than 7 days old, or none for the first bar, is refused with the `qa fx import` command to run
  - the tick table and the courtage minimum are converted at the last fixing (an approximation, printed in the report)
  - the runner charges the class's foreign courtage per instrument; a class without one for that currency refuses the
    run instead of charging Swedish courtage
- **A flaky app test fixed at its root:** showing a page starts a refresh that the UI doesn't await. In tests its
  continuation ran on a thread-pool thread while the test's search rebuilt the same list. `ShellViewModel.Refreshing`
  lets tests await it. In the app both run on the UI thread.
- **Tests (13 managed, 4 native new):**
  - the costs of every class
  - `CourtageIn`
  - a foreign currency refused
  - a US share paying its courtage in a backtest, and refused without one
  - the SEK panel: each day's fixing, the scaled tick table, a missing or stale fixing
  - ABI 1.3: version, per-instrument courtage, the cash check, bad input, after the first step
- All 1196 managed and 131 native tests pass.

### Step 5: the trading core in SEK (done 2026-09-29)

- **Money in SEK, prices in the share's currency.** `IFxRates` / `FxTable` (Trading.Model) give SEK per unit; SEK is
  always 1. A Paper session uses one table for the day (step 6 fills it from the latest fixing).
- **The gateway** (`GatewayEnvironment.Fx`, `Schedules`):
  - shares in SEK, USD and CAD only; another currency is not prepared, with the reason
  - USD and CAD in Paper and Backtest only: Confirm (and Auto) refuse them before any card, preflight or risk check
  - a foreign share without a known rate is not prepared (its value in SEK can't be judged)
  - `OpenOrders` carry each order's rate, so R7, R8 and the planner count working buys in SEK
- **The risk checks:** R6–R9 compare the order's value in SEK (the R6 line shows both: "7,518.00 SEK (751.80 at
  10.0000 SEK per unit)"). R16 judges a foreign order on its own market's clock and calendar ("09:45:00 New York (Full
  day, XNYS)", window "09:35–15:50 New York"); a holiday there or a missing schedule fails it. A SEK order is judged as
  before.
- **Per-market schedules** (`TradingSchedule` with the Stockholm calendar): a foreign market keeps Stockholm's
  distances from the session in its own time: window from 5 minutes after the open to 10 minutes before the close
  (early closes too), decision 10 minutes after the open (09:40 New York = 15:40 Stockholm, 14:40 in the March weeks
  when only the US is on summer time). Stockholm's plan is unchanged.
- **The paper book and channel:** buying power, reservations, cash, cost basis and fees in SEK; fills in the share's
  currency at the day's rate; the class's foreign courtage in the share's currency, converted, plus the FX fee.
  Positions keep their currency (written to `book.json` and `fills.jsonl` for foreign shares only, so a Swedish book
  reads and writes as before) and are valued at the mark × the rate. `EndOfDay` can end one market's orders.
- **The planner** sizes a foreign share at its SEK price and keeps the limit in its currency; without a rate it skips
  the share with a note.
- **Tests (16 new):** a US buy and sell through the gateway, OMS, channel and book, and the book reopened; a Swedish
  book unchanged; R6 in SEK; R16 on New York's clock, on Thanksgiving and without a schedule; no rate; another
  currency; Confirm refusing a US share; reservations in SEK and one market's orders ending; the planner in SEK and its
  working-buy room; the US schedule (a full day, the early close, the March DST gap, phases and the next decision); the
  Stockholm schedule unchanged; the FX table. OrderPreparation no longer refuses USD (the rule moved to the gateway),
  so its test row went with it.
- All 1210 managed tests pass (1 skipped).
