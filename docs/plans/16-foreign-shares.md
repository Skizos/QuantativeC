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

