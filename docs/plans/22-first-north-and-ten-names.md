# 22 — First North shares that trade continuously, and up to 10 names in Paper

- **Status:** done 2026-09-30 (planned and built the same day), at the owner's request. The app refused AIRA: "Could not add AIRA: AIRA is listed on
  'FNSE', not Nasdaq Stockholm's main market (XSTO), and its trading model is Unknown …". The owner: "fix this, and why
  can't I add more than 5 instruments. I want to be able to trade several stocks at once".
- **Gate:** tests for every part; all managed tests green; nothing live called (the classification is tested on
  chart answers built from the recorded fixture).

## A. First North shares: classified by evidence, not refused as a group

Plan 18 refused every share outside XSTO, because "a First North share may trade only in auctions". That is true for
**some** First North shares only. Research (2026-09-30):

- Nasdaq's First North **auction model**: shares whose spread was above 7 % over two quarters may trade in five
  daily auctions (09:00, 11:00, 13:00, 15:00 and the 17:30 close) instead of continuously; 15 shares in Sweden and
  Finland used it in January 2024 ([Biostock](https://biostock.se/en/2024/01/nasdaq-introduces-auction-trading-on-first-north/),
  [Nasdaq, Copenhagen expansion](https://view.news.eu.nasdaq.com/view?id=b7a88413341a94c95dea2b6f3a527d1ae&lang=en)).
  Every other First North share trades continuously, like the main market.
- AIRA (AI Revenue Assistant Software Stockholm AB, SE0028778498) was listed on First North on 2026-06-03, spun off
  from Upsales ([TradingView/Modular Finance](https://www.tradingview.com/news/modular_finance:e5f735774cfdb:0-first-day-of-trading-in-aira-s-shares-on-nasdaq-first-north-growth-market/)).

So the trading model of a share outside XSTO is **measured**, from the last week's 10-minute bars of Avanza's public
price chart (`one_week` / `ten_minutes`: the route and parameters the intraday catch-up already uses, plan 17; no
login, one call):

- A bar with volume **outside the auction slots** (Stockholm time 09:00, 11:00, 12:50, 13:00, 15:00, 17:20 and 17:30;
  12:50 covers a half day's close) is a trade in continuous trading. An auction-model share has none.
- **Continuous:** such trades on at least 2 different days. **Periodic auction:** at least 3 days with trades and
  none outside the auction slots. **Unknown** otherwise (too few trades to tell): refused as now, with the reason.
- XSTO, US and Canadian shares stay continuous without a call.
- Where: `qa history import`, the app's Add (before its check, so a refused share costs no import), and the Paper
  history refresh. A new week of evidence is a new version of the instrument (known-at). An Unknown answer (a quiet
  week) keeps the model already stored; only evidence of auctions turns a continuous share into an auction one.
- **A listed share that turns out to trade in auctions** no longer stops every decision (plan 18's worry): the Paper
  decision leaves it out with a note (and sells nothing it cannot fill), and `qa status` FAILs it with the remove step.

### Costs on First North

The Start and Mini classes are free (Start) or 0.25 % on the main market only. First North costs **0.25 %, minimum
1 SEK** in both classes (search extract of Avanza's price list, 2026-09-30; **verified 2026-10-01** against the
owner's screenshot of the whole price list for trading in Sweden, saved in `docs/research/assets/` and transcribed in
`docs/research/market-rules.md` §4: every class's First North row matches the cost files). Costs are always on (CLAUDE.md), so:

- `costs.*.json` gains `marketplace_courtage: { "FNSE": { "min", "rate" }, "source_url", "verified_on", "basis" }`.
- A SEK share on a marketplace other than XSTO pays that marketplace's courtage in the backtest (per instrument, as a
  foreign share does, ADR 0005), in Paper fills, in the Confirm pre-trade fee comparison, and in the intraday report.
  A marketplace with no entry in the class is refused (never charged the main market's price).
- Start's 500 free trades are for the main market; First North trades pay their courtage and are not counted
  (not on the 2026-10-01 screenshot: the Start footnote is still to be checked).

## B. Up to 10 names in Paper

The 5 came from ADR 0002 §3's load budget when Paper opened one order-book stream per share. Since 2026-09-30 Paper
opens no stream (Avanza refuses it) and polls each share's market data. What limits the list now is the request
budget (2 requests/s, burst 5) and R15 (a quote older than 10 s blocks orders):

- **Poll interval** = 0.7 s × the number of shares polled, at least 5 s and at most 7 s: 5 shares 5 s (1.0 req/s, as
  today), 8 shares 5.6 s, 10 shares 7 s (1.43 req/s, about 70 % of the budget). A quote is at most about 7 s old.
- **The list: at most 10 names** (`Allowlist.MaxNames`), in the CLI and the app. Exiting shares are polled too but do
  not count; a session refuses to start with more than 14 shares to poll (only possible after several removals
  without a session in between).
- **Confirm keeps 5:** it still requires one order-book stream per share (ADR 0002 §3). It refuses to start with more
  than 5 shares to stream and says so. `qa stream` keeps 5.
- The risk limits do not change: 10 % of the account per order, 20 % per share, the 5,000 SEK account cap. Ten names
  share the same capital: about 500 SEK each at equal weight.
- **Limit:** with 10 names a failed poll leaves a quote stale for a few seconds (7 s + 7 s > 10 s). An order due in
  that moment is rejected by R15 (a risk rejection, not a broker reject: no kill), and the share waits for the next
  day. The weekly summary's rejection counts show whether that happens.

## Done (2026-09-30)

| Part | Code | Tests |
|---|---|---|
| Measuring the trading model | `TradingModelCheck` (`Classify`, `MeasureAsync`, `Apply`, `KnownContinuous`); `InstrumentImport.RecordAsync` used by `qa history import`, the app's Add (before its check; a quiet week keeps the stored model) and the Paper history refresh; `qa history import` prints the measurement | `TradingModelCheckTests` (continuous, auction, too few, zero volume, a quiet week, the one chart call), `CliDataTests.HistoryImport_OfAFirstNorthShare_…` (both ways, then `qa universe add`), `MarketSearchTests.AFirstNorthShare…` (refused before its import; added and measured once) |
| The list's rules | `Allowlist.Check` (known Swedish marketplaces `XSTO`, `FNSE`; auction and unknown with the measured reason), `qa status` | `CliTradingTests.Universe_Refuses…` (FNSE unknown, FNSE auction, Spotlight), `…AddsAFirstNorthShareMeasuredAsContinuous`, `CliUsabilityTests` |
| First North courtage | `marketplace_courtage.FNSE` in all five cost files (verified 2026-10-01), `CostModel.Marketplaces`/`MarketplaceFor`/`CourtageIn(…, marketPlace)`, `PanelInstrument.MarketPlace`; the backtest (per share), the allowance (not counted), `Recost`, the intraday report, Paper fills and R9's estimate, Confirm's `ModelFees`; a Paper share on a marketplace without a courtage is skipped with a warning | `FirstNorthCostTests` (every class, 0.25 %/1 SEK, unknown marketplace refused, XSTO not allowed as an entry, a backtest charges it) |
| Paper decision | a listed share not continuous is left out of the panel with a note (and holds) | `PaperSpyTests.AListedShareThatTradesOnlyInAuctions_IsLeftOut_TheOthersStillTrade` |
| 10 names | `Allowlist.MaxNames = 10`, `PaperPolling` (interval, `MaxPolled` 14), `PaperSetup`, Confirm's 5-stream check before any login, the app's texts | `CliTradingTests.PaperPolls_…`, `…Universe_TakesASixthName_ButRefusesAnEleventh…`, `ConfirmSpyTests.AListOfMoreThanFive…`, `PaperSpyTests.TenNames_AreTraded_EachPolledAboutEverySevenSeconds` (10 orders; 7–11 polls a share in 60 s, 13 with the old 5 s: checked by reverting the interval), `SearchPageTests`, `MarketSearchTests` |

ADR 0002 §3 has the polling amendment and ADR 0003 the Changes entry. **Not live-tested:** the 10-minute measurement
has not run against Avanza from this code; it uses the route and parameters the intraday catch-up already calls.

## Not in this plan

- More than 10 names (the polls would pass the budget or R15's age), or a batch market-data call (none is known in
  the maintained clients; no endpoint is invented).
- Spotlight, NGM and other marketplaces: no courtage entry, so they stay refused.
- The allowance question for First North trades (see above).
