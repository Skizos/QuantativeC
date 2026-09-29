# ADR 0006 — An intraday strategy: research first, paper only

- **Status:** Accepted (2026-09-29). The owner: "go with your recommendations, start phase A" (D1–D7 in
  `docs/plans/17-intraday.md`, each the recommended option).
- **Related:** ADR 0003 (modes, limits R10–R16, unchanged here), ADR 0004 (automation), ADR 0005 (foreign shares),
  CLAUDE.md "Backtesting rules", docs/plans/17-intraday.md

## Context

The program decides once a day per market (09:10 Stockholm; 15:40 for US and Canadian shares) and places day limit
orders that can fill until the close. The owner asked (2026-09-29) for a strategy that trades **during** the day.

Four facts shape the answer (sources below):
1. **Data:**
   - Avanza's price chart has intraday resolutions (`minute` to `hour`), but the server offers them only for short
     periods. For `one_month` our recording allows `hour`, `day` and `week`.
   - Months of minute bars must be collected from now on, or bought.
2. **Costs:**
   - Orders are at most 500 kr (the 5 000 kr account cap, R6).
   - Start is free for 500 trades a year, then Mini (0.25 %, at least 1 kr): about 0.5 % per round trip, plus the
     spread. UNVERIFIED.
3. **Window:** R16 ends orders at 17:20, and the closing auction is not traded. Strategies whose edge is earned into
   the close lose it here.
4. **Evidence:**
   - Opening-range breakout has the most published support for single stocks, but it is US evidence, long and short.
   - Intraday momentum is documented for the S&P 500 ETF and several markets, and reported weak in Stockholm.
   - Nothing published is known to survive our costs at our sizes.

## Decision

1. **Research before trading.**
   - Phase A: collect 1- and 5-minute bars (the allowlist plus a research list of at most 30 names) and measured
     spreads.
   - Then an intraday backtest with a conservative fill model: a limit fills at its limit, only on a trade-through,
     never at a better bar open. It is native ABI 1.4, an additive change.
   - The cost model includes the free-trade allowance.
   - Phase B (trading) is built only after the owner reads the go/no-go report.
2. **The candidate** is a long-only opening-range breakout on 5-minute bars in Stockholm, with at most one entry per
   name per day, flat by 17:20. Two controls run beside it: a late-day momentum variant and a buy-open/sell-close
   baseline. Every evaluation goes to the TrialLedger. Statistics are on daily P&L (Sharpe, PSR, Deflated SR, PBO). The
   final holdout is the last 20 collected days, locked until the owner unlocks it.
3. **The limits of ADR 0003 stay as they are.** The strategy budgets its orders up front: at most 15 a day against
   R10's 20, with R11 and R12 respected by construction. Stops are the session's own (a marketable sell limit through
   the gateway on a quote trigger); Avanza's stop-loss orders are not used. Nothing trades the auctions.
4. **Paper only.** Live intraday trading needs a new ADR. Confirm's typed answer per order cannot serve a stop, so live
   waits for Phase 8 (Auto, ADR 0004) or an owner at the screen all day.
5. **Stockholm only.** US and Canadian shares pay at least 1 USD/CAD per trade, about 2 % on a 500 kr order.

## Alternatives

- **Build the trading loop now and test it on paper directly:** faster to see orders, but paper days are too few to
  tell an edge from luck, and the costs are known to be large. Not recommended.
- **Buy intraday history** (vendors list Nasdaq Stockholm minute data): skips months of collection. It costs money,
  and coverage, point-in-time quality and terms are unverified. The owner's choice (D6).
- **Relax R16 to trade into the close, or trade the closing auction:** needed for the classic "last half hour"
  strategy. Not proposed: it reopens the auction rules chosen for MAR hygiene.
- **Run the daily strategies on 5-minute bars:** kept only as a control if at all; nothing suggests an edge there.

## Consequences

- The go/no-go needs roughly 6 months of collected data (at least 120 trading days), unless history is bought.
- The store gains `intraday_bars` and a spread table, added in place like `fx_rates`.
- The paper book counts trades for the free-trade allowance; the daily strategy's costs become more exact too.
- The answer may well be "no-go": the report is built to say so clearly.

## Open items

1. The owner runs the read-only chart probe, `qa intraday probe` (which periods give minute and 5-minute bars, and how
   many days).
2. The owner checks the Start class's 500-trade allowance and Mini's terms on Avanza's price list.

## References

- Avanza price chart:
  - resolutions and periods: Qluxzz/avanza [`a6a18a94` `constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py)
    (`TimePeriod`, `Resolution`) and `get_chart_data` in `avanza.py`
  - `metadata.resolution.availableResolutions`: vmorsell/avanza-sdk-go [`43f39025` `market/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/market/types.go)
  - our recording `recordings/fixtures/avanza/2026-09-25/035-price-chart.json` (`one_month`: `hour`, `day`, `week`)
- Gao, Han, Li, Zhou, "Market intraday momentum", *Journal of Financial Economics* 129(2), 2018:
  <https://www.sciencedirect.com/science/article/abs/pii/S0304405X18301351> (SSRN
  <https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2440866>).
- Zarattini & Aziz, "Can Day Trading Really Be Profitable?", 2023: <https://papers.ssrn.com/sol3/papers.cfm?abstract_id=4416622>.
- Zarattini, Barbon & Aziz, "A Profitable Day Trading Strategy For The U.S. Equity Market", 2024:
  <https://papers.ssrn.com/sol3/papers.cfm?abstract_id=4729284>.
- Julin, "Intraday Momentum and Return Predictability: A High Frequency Analysis of the Swedish OMX30 and Selected
  Constituent Stocks", master's thesis, Örebro University, 2024:
  <https://www.diva-portal.org/smash/get/diva2:1878991/FULLTEXT01.pdf>. Search-engine extract only, UNVERIFIED (the
  full text is blocked here).
- Avanza Start (500 free trades per 12 months, then Mini): `docs/research/market-rules.md` §4, UNVERIFIED.
