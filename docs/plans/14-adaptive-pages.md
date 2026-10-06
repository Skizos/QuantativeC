# 14 — Every page works side by side: adaptive layouts

- **Status:** done 2026-09-27; waiting for the owner's screenshots of the split view again. Planned the same day from
  the owner's screenshots of the split view (Charts beside Instruments): "make sure
  all tabs work and scale properly with each other in split view."
- **What the screenshots showed:**
  - the left page's title cut to "Ch…" by its header buttons
  - the narrow chart clipped at the bottom: no volume bars, no dates (the chart kept a 420 px minimum the pane didn't
    have)
  - Instruments on the right: its 340 px list kept its width, so its chart got a sliver and the "Add" text a narrow
    column
- **The same pattern elsewhere (read from the XAML):** Accounts (a 380 px column), Reports (300 px), Overview (four
  tiles in one row, a checklist with fixed label columns), Trading (a 340 px reason box, orders and decision side by
  side), Strategy (the form beside the saved box).
- **Gate:** all tests green, with new ones for the sizing rules; a XAML guard that no page uses a fixed column of
  200 px or more or a fixed-column tile grid; screenshots from the owner.

## Decisions

| What | Decision |
|---|---|
| **Two-part pages** (a list and its details) | `AdaptiveSplit`: side by side when the pane is wide enough (a breakpoint per page, ~720–820 px), else stacked: the list on top (at most a few rows high, it scrolls) and the details below with the rest. |
| **Header rows** (text on the left, buttons on the right) | `AdaptiveRow`: in one row while the text keeps at least ~200 px, else the buttons move under the text. Used for the page header, the Charts chips, Trading's start/stop bar, the Add card, the "can trade live" box, the gate card, the Strategy form and the Overview checklist lines. |
| **Tiles** | `AdaptiveGrid`: as many equal columns as fit at ~210 px (the Overview's four tiles: 4, 2 or 1 per row). |
| **Charts** | No fixed minimum height the pane may not have: the candle chart and the line charts shrink (from 220 px and 200 px) instead of being clipped. A hover strip wider than a narrow chart wraps onto more lines, and the chart never draws outside its box. |
| **The divider** | A thin line that turns green under the mouse and while dragged, with a wider grip. |
| **Maths** | `Adaptive` in `Desktop.Core/Presentation` (split widths, stacked heights, row fitting, columns), tested on Linux. The WPF panels only measure and place. |

## Notes (done 2026-09-27)

- `Adaptive` (Desktop.Core/Presentation): `Split`, `Stack`, `Row`, `Columns`. The WPF panels `AdaptiveSplit`,
  `AdaptiveRow` (with the attached `Fill`) and `AdaptiveGrid` in `Controls/AdaptivePanels.cs` only measure and place.
- Per page:

  | Page | In a narrow pane |
  |---|---|
  | Every page header | the tools (Beside, New window, Refresh) move under the title (title ≥ 170 px) |
  | Overview | tiles 4 → 2 → 1 per row; next steps under the account and checklist (< 820 px); each checklist finding under its label (< 200 px left for it) |
  | Trading | Start/Stop under the state (< 260 px left); the reason box 280 px and its button wrap; orders above the decision (< 760 px); order and position tables share their width with minimums instead of fixed widths |
  | Charts | New chart window under the chips; the chart from 220 px (was a fixed 420 px minimum, which clipped volume and dates) |
  | Accounts | the accounts above the selected account (< 800 px; the list at most 340 px, it scrolls); the "use for live trading" box under its text; holdings with shared widths |
  | Instruments | the ticker box under the explanation; the list above the chart (< 760 px; at most 210 px, it scrolls; the chart keeps ≥ 260 px); the ranges under the price; the chart from 200 px |
  | Strategy | "Paper trades now" under the form (< 320 px left); each parameter's explanation under its box; the buttons wrap |
  | Reports | the gate lines under the dots; the days above the selected day (< 720 px; at most 200 px, they scroll) |

- The charts clip to their own box, and the candle chart's hover strip wraps onto more lines in a narrow chart.
- The divider is a thin line with a 12 px grip, green under the mouse and while dragged.
- **Tests (19 new):** the sizing rules with the screenshot's numbers (split, stacked heights, the header row, tile
  columns), and the XAML guard with its positive and negative controls. All app tests pass.
