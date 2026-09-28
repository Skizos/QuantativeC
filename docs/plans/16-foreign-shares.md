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
