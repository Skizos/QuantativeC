# 13 — Several pages at once, and bigger charts

- **Status:** steps 1–2 done 2026-09-27; waiting for the owner's look at the window (screenshots). Planned the same
  day at the owner's request: "Make me be able to pull up several different tabs at once,
  i.e. the chart and instrument tab for example, make the chart easier to see as it's currently a bit small."
- **Builds on:** `docs/plans/11-app-redesign.md` (the shell and pages) and `docs/plans/12-charts-window.md` (the charts).
- **Gate:**
  - all tests green, with new ones for the side-by-side and window logic in the shell and the chart's first view
  - the XAML check and the app-safety test still hold (nothing here trades)
  - **I can't see the window (Linux container).** You run it with `.\qa-app.ps1` and send screenshots.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| **Two pages in one window** | Every page header gets **Beside:** a dropdown of the other pages. The one you pick opens on the right, with a divider you can drag to share the width. Its own small header has **Open in new window** and **×** (close). Picking the page on the left from the rail closes it on the right (it moves over). | Chart + Instruments (or Trading + Charts) on one screen, without arranging windows. |
| **Two charts** | Charts beside Charts is a second, independent chart (another instrument, or History next to Today). | A trader often wants two names, or two time frames, at once. |
| **Any page in its own window** | Every page header gets **Open in new window**. The window shows the same page as the main window: the same numbers and buttons, kept in step. Charts opens an independent chart, as today. Open as many as you like; they refresh with the main window and close with it. | Several screens, or Windows' own snapping (Win + ←/→). |
| **More room** | The navigation rail folds to icons (the « button at its top; hover an icon for its name). | The chart and a second page get the width. |
| **Bigger charts** | The instrument list becomes a row of chips above the chart (the chart gets the full width). The price, change and date sit on one line; the latest candle's numbers on one line under the chart; the explanation shortens to one line, whole on hover. The chart is at least 420 px high and fills the rest. Larger axis text (12 px), wider candles (¾ of their slot), a slimmer volume pane (16 %), bigger trade markers. Day candles open on **3M**, and a chart never opens with candles thinner than 4 px (zoom out for more). | The candles, not the controls, should fill the page. |

## Steps (each ends green, committed and pushed)

1. **Bigger charts:** the first view's minimum candle width in `CandleLayout`, the new Charts page layout, the
   control's sizes, the 3M default. Tests.
2. **Several pages at once:** the shell's beside page and window pages (with their refresh), the main window's
   split with its divider, the generic page window, the folding rail, `docs/guide.md` §0. Tests and a full run.

## Step notes

**Step 1: bigger charts (done 2026-09-27).**
- `CandleLayout`: a chart opens with at most as many candles as fit at 4 px each (`MinOpeningSlot`); the range still
  decides when it fits. Candle bodies are ¾ of their slot (up to 30 px); the volume pane takes 16 %.
- The Charts page is one full-width card: the instruments as chips with **New chart window**, the price on one line,
  the switches on one line, the chart (at least 420 px, filling the rest), and the latest candle's numbers and the
  explanation (whole on hover) on a line each under it. Day candles open on 3M.
- Axis, level and price-tag text 12 px, the hover strip 13 px, trade markers larger; the line charts' axes 12 px too.
- **Tests:** 1 new (the opening view), 2 updated (body width, the 3M default). All 120 app tests pass.

**Step 2: several pages at once (done 2026-09-27).**
- **The shell:** `BesideChoices` / `SelectedBeside` / `BesidePage` (a page on the right; Charts there is a chart of its
  own; choosing the left page again does nothing; picking the right-hand page in the rail moves it left);
  `CloseBesideCommand`; `OpenWindowCommand` / `OpenInWindow` with `PageWindowRequested` and `WindowClosed` (the same
  page in a window, a chart of its own for Charts; a chart is disposed once nothing shows it); `RefreshVisibleAsync`
  (the left, right and window pages, each once; while a command runs only the Trading page); `NavCollapsed` with
  `ToggleNavCommand`. The Charts page's **New chart window** uses the same path, so `ChartsWindow` became the generic
  `PageWindow`.
- **The main window:** the page header has **Beside** (a list), **New window** and **Refresh**; the page on the right
  has its own small header (icon, title, new window, refresh, ×) and message line; a divider to drag between them
  (both at least 380–460 px; a new page on the right starts at half and half). The rail folds to 84 px of icons.
  The page views' templates and a shared message-line template moved to `App.xaml`, so every window can show every
  page. The timer refreshes every page on screen.
- **Docs:** `docs/guide.md` §0.
- **Tests (4 new, 1 updated):** the page on the right (charts of their own, replacing, the left page refused, moving
  left, closing); page windows (the same page, a chart of its own, closing); what is refreshed; the rail. The charts
  window test now goes through the page-window path. The XAML checks cover the new main window and page window.
