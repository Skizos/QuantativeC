# 12 — A charts window: candlesticks and trading charts for your instruments

- **Status:** steps 1–2 done 2026-09-27; waiting for the owner's look at the window (screenshots). Planned the same
  day at the owner's request: "Add a window for seeing candlewicks and trading graphs for the
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

**Step 2: the page and the window (done 2026-09-27).**
- **Live candles:** each of the session's instrument tiles keeps one-minute candles from every price it reports (not
  only the thinned points of its line chart). `LiveSession` says which instrument changed (`InstrumentChanged`) and
  gives its fills and working limits (`FillsOf`, `WorkingLimitsOf`, now also used by the Trading page's price chart).
- **`ChartsViewModel`:**
  - the allowlist, plus any name the running session trades; History or Today; Day/Week/Month or 1/5/15 min; the
    range (History); switches for volume, averages and trades
  - the header (last price, change over the range or since yesterday's close, as of when) and the latest candle's
    numbers under the chart (open, high, low, close, volume, the range's high and low; Today: the day's)
  - History reads the store and the audit log only while no command runs, into `HistoryCandles`, which every charts
    page and window share; after a command ends it reads them again. Today redraws on each quote or order of the
    selected name.
  - moving averages come from the daily closes, so they are complete at the range's left edge and on week and month
    candles show the value at the period's last day
- **The shell:** a Charts entry in the rail (after Trading); `NewCharts` makes a page for another window, starting like
  the one it came from; `ChartsWindowRequested` asks the window to open it.
- **WPF:** `CandleChart` draws the layout (candles, volume, averages, ▲/▼ trades with their label on hover, levels,
  the last price tag, crosshair with the price at the mouse, and a strip with the hovered candle's numbers), and turns
  the wheel, dragging and double-click into viewport changes; a chart that gets more of the same candles keeps its
  zoom and follows the newest. `ChartsView` is the page; `ChartsWindow` shows it on its own, refreshes when it opens
  and lets go of the session and the engine when it closes. Two icons (candles, new window).
- **Docs:** `docs/guide.md` §0.
- **Tests (5 new):** History with volume, averages, periods, ranges and switches over a real DuckDB store; your paper
  trades from the audit log; what it says without instruments or prices; Today from a session's quotes with fills,
  limits and yesterday's close; a new window that starts like this one, is independent, and stops following the
  session when closed. The shell's page list and the XAML checks cover the new views.
