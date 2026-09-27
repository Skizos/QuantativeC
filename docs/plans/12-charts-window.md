# 12 — A charts window: candlesticks and trading charts for your instruments

- **Status:** planned 2026-09-27 at the owner's request: "Add a window for seeing candlewicks and trading graphs for the
  current instruments."
- **Builds on:** `docs/plans/11-app-redesign.md` (the chart maths in `Desktop.Core`, the session observer, the theme).
- **Gate:**
  - all tests green, with new ones for the candle maths, the aggregation, the minute candles built from a session's
    quotes, the trade markers and the page
  - the XAML check and the app-safety test still hold: charts only read; no order path, no new Avanza endpoint
  - **I can't see the window (Linux container).** You run it with `.\qa-app.ps1` and send screenshots.
- **Not in this plan:**
  - Drawing tools (trend lines), oscillators (RSI, MACD). They can come later on the same layout.
  - Intraday history from Avanza. That would use the price-chart endpoint with a new resolution, so the research rule
    applies first (CLAUDE.md). Today's candles come from the running session's own quotes.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| **Where** | A new **Charts** page in the navigation rail, and **Open in new window** on it, which opens the same page in its own window. You can open several, each on its own instrument, and keep them beside the Trading page. | "A window" to look at while trading, without losing the main window. |
| **Which instruments** | Your allowlist (the "current instruments"), plus any instrument the running session trades. | The names the strategy may trade are the ones worth watching. |
| **History** | Daily candles from the price store (open, high, low, close and volume, as imported from Avanza), with **Day / Week / Month** candles and the ranges 1M 3M 6M 1Y 3Y All. | The store has true OHLCV; weeks and months are built from the days (first open, highest high, lowest low, last close, summed volume). |
| **Today** | While a Paper session runs, candles of **1, 5 or 15 minutes** built from its quotes (the last trade, else the mid; about one a second per name, as the session reports them). Your fills and working limits are drawn on them, and yesterday's close as a line. No volume: the quotes don't carry it. | "Trading graphs for the current trading run", from data the app already receives. |
| **Trading overlays** | Volume bars under the candles; the saved strategy's two moving averages (else 20 and 50 days); your paper trades as ▲ buy / ▼ sell at their fill price, from the end-of-day reports (the same numbers as `qa report eod`). Each can be switched off. | What a trader wants on a chart: the price, the signal lines and what was actually done. |
| **Interaction** | Hover: a crosshair and a bubble with the candle's date, open, high, low, close, change and volume. Mouse wheel zooms around the mouse, dragging pans, double-click resets to the range. The last price sits on the right axis in its colour. | Like the charts in trading apps. |
| **Colours** | Green up candles (close ≥ open), red down candles, from the theme's tokens. | The app's convention (plan 11). |
| **Maths** | `CandleLayout` in `Desktop.Core` (slots, wicks, bodies, the volume pane, nice axis steps, hover, zoom and pan), tested on Linux. The WPF `CandleChart` only draws what it says. | Same rule as plan 11. |

## Steps (each ends green, committed and pushed)

1. **Candle maths:** `Candle`, the periods and the aggregation, the minute-candle builder, `CandleLayout` with its
   viewport, the data sources (daily candles from the store, trade markers from the reports). Tests.
2. **The page and the window:** minute candles in `LiveSession`; `ChartsViewModel`; the WPF `CandleChart`,
   `ChartsView` and `ChartsWindow`; the rail entry; `docs/guide.md` §0; a full test run.

## Test map (planned)

| Area | Must show |
|---|---|
| Aggregation | weeks and months from days (open, high, low, close, volume); a week across a month's end; minute buckets from ticks, 5 and 15 minutes from 1 |
| Layout | one slot per candle; a wick from high to low; a body from open to close (at least 1 px); up and down; the volume pane under the prices; y ticks at nice steps with at least three; markers on their candle; hover picks the nearest candle and gives its OHLC |
| Viewport | zoom keeps the candle under the mouse in place; pan stops at both ends; never fewer than 10 or more than all candles |
| Sources | daily candles from a real DuckDB store; trade markers from an audit log's paper fills, by ticker |
| Page | the allowlist; history with periods, ranges and toggles; today's candles from a session's quotes with fills; a new window's page is independent and lets go of the session when closed |
| Safety | the app still has no path to orders, preflight, promotion or a mode flag |

## Step notes

**Step 1: candle maths (done 2026-09-27).**
- `Charts/Candles.cs`: `Candle` (open, high, low, close, volume or unknown); `CandlePeriod` (Day, Week, Month; 1, 5
  and 15 minutes), each starting in Stockholm time (midnight, Monday, the 1st, the whole minute); `Candles.FromDaily`,
  `Candles.Aggregate` and `CandleBuilder`, which turns a session's prices into minute candles.
- `Charts/CandleLayout.cs`: `CandleChartData`, `CandleViewport` (zoom around the mouse, pan, follow new candles) and
  `CandleLayout` (slots, wicks, bodies at least 1 px, the volume pane, value ticks over what is on screen, markers on
  their candle, one overlay point per slot, time labels by period, hover with the change from the close before, the
  last price tag). The value-axis scaling is now shared with the line chart (`ChartLayout.ValueScale`).
- `ChartSources.DailyCandles` (the store) and `ChartSources.TradeMarkers` (Paper fills and real Confirm fills from the
  end-of-day reports, at noon of their day).
- **Tests (14 new):** periods, aggregation, minute candles, the layout, hover, markers, overlays, labels, zoom, pan,
  both sources. All pass.
