# 18 — Trading lessons: what went wrong, and what stops it happening again

- **Status:** done 2026-09-30 (planned and built the same day), at the owner's request: "see over all past mistakes
  and problems weve had with the trading and see how to mitigate them and mitigate them".
- **Scope:** the problems met in live use (the first real Paper sessions on 2026-09-29/30) and those the tests found
  while building the intraday research. Each one gives its cause, what was already done about it, and what this plan
  adds.
- **Gate:** every mitigation has a test; all managed tests green; nothing live called by Claude.

## The problems

| # | What happened | Cause | Done before this plan | Still open |
|---|---|---|---|---|
| P1 | 09:54: the decision skipped every share, "no fresh live price" | a session started after 09:10 decided in its first second, before the first quote | `PriceGate`: wait up to 60 s for every share's price (7b0a200) | A **feed outage** at decision time (no share priced) still loses the whole day after 60 s. The wait also counts a price R15 would refuse as ready. |
| P2 | Every order-book stream connection refused (HTTP 429) | Avanza refuses the stream, even a single connection | diagnostics (52096b0); Paper on the 5-second polls (b5b831f, owner's decision); O11 in plan 07 for Confirm | In Confirm the session would still plan, and R15 would reject card after card until the kill switch. It should say why before deciding. |
| P3 | R15 rejected the only order | P2: the quote was stale while the stream reconnected | Paper no longer needs the stream | Covered by P1/P2's work. |
| P4 | FASTAT (about 0.64 SEK) buy rests, "filled 0/782" | the limit is last + 0.5 %, rounded down; on a low-priced share the spread is often wider than that, so the order sits below the ask | nothing | 1) The decision does not say that a limit sits inside the spread. 2) On a share whose price step is large against its price, rounding can take the whole 0.5 % away, leaving the limit at the last price. |
| P5 | A share off Nasdaq Stockholm's main market can be allowlisted (found in this review; it has not happened yet) | the instrument master marks a SEK share whose marketplace is not `XSTO` as trading model `Unknown`, and the backtest refuses it. The Paper decision loads the **whole list** through the same panel, so one such name would make every decision of every session fail, for every share. Its courtage is also higher than the paper fees assume. | nothing | refuse it where the allowlist is changed, and flag one already on it |
| P6 | 1- and 5-minute bars of a missed evening are gone | Avanza keeps them for today only | 10-minute catch-up for a week (e5f85c6); scheduled task in the guide | Nothing tells the owner that a day is missing while it can still be caught up. |
| P7 | Backtest: a position held overnight after a quiet last minute; positions resized by the no-trade band; PBO on per-bar returns | found while building plan 17 | fixed with tests (6c40a57, 6cea45b) | nothing |
| P8 | `qa` not found in PowerShell; the scheduled task's log "missing" | usage | guide: `.\qa`, the profile function, `Start-ScheduledTask` | nothing |

## Mitigations built here

1. **The price gate (P1, P2, P3):**
   - A share counts as ready only with a price R15 would accept: a usable reference price and a quote that is not
     stale.
   - Some shares ready but not all: decide after 60 s, as before; the rest are skipped for the day.
   - **None ready (a feed outage): keep waiting, up to 30 minutes**, then decide anyway. A short outage delays the
     decision instead of costing the day.
   - Every message names what is missing, e.g. "ERIC B: stale: depth stream reconnecting (HTTP 429)", so Confirm with a
     refused stream says so before it plans.
   - Both sessions (Paper and Confirm) use the same gate.
2. **The limit price (P4):**
   - A buy's limit is at least one price step above the reference and a sell's one step below, after rounding. The
     "toward the market" offset can no longer round away on a large-step share. A step that would leave R5's collar
     (2 %) is not taken: a resting order is better than a rejected one.
   - The decision note says when a limit rests inside the spread, e.g. "FASTAT: buy limit 0.642 is below the ask
     0.645 (spread 0.94 %): it rests until a trade prints at or below it".
3. **The marketplace (P5):**
   - `qa universe add` and the app's **Add** refuse a share that does not trade continuously, with the reason: the
     same rule the backtest and the Paper decision apply, now checked before the name joins. (The first draft of this
     plan said "a warning"; tracing the decision showed that such a name breaks every decision, so it is a refusal.)
   - `qa status` shows a `FAIL` line for one already on the list, and its first step is the `qa universe remove` for
     it, so no session is suggested until it is off.
4. **Intraday collection (P6):** `qa status` shows the last collected day and any trading day of the last two weeks
   without bars. While a day is within the week the 10-minute catch-up reaches, it says to run
   `qa intraday import` now; after that, the day is lost.

## Done (2026-09-30)

| Mitigation | Code | Tests |
|---|---|---|
| 1 Price gate | `PriceGate` (`PriceCoverage`, `OutageWait` 30 min), `DailyPlanner.Coverage` (R15-aware, names the first missing share); Paper and Confirm pass it | `PriceGateTests` (minute wait, outage wait, decide after 30 min, empty decision), `DailyPlannerTests.Coverage_…` (no quote, stale, no price), `PaperSessionTests` / `ConfirmSessionTests` (the new messages) |
| 2 Limit price | `DailyPlanner.TowardTheMarket`, the "rests inside the spread" note | `DailyPlannerTests`: a 1 SEK share buys at 1.01 and sells at 0.99 (not at the last price); at 0.40 SEK the 2.5 % step is not taken (R5); a 2 % spread gets the note, a tight one doesn't |
| 3 Marketplace | `Allowlist.NotContinuous` in `Allowlist.Check` (CLI and app), `qa status` allowlist check | `CliTradingTests.Universe_RefusesAShareThatDoesNotTradeContinuously`, `CliUsabilityTests.Status_Flags…` |
| 4 Intraday gaps | `IntradayImporter.Gaps`, `qa status` "Intraday bars" line and steps | `IntradayDataTests.TheGaps_…` (catch-up vs lost, holiday, weekend, today after the close), `CliUsabilityTests.Status_Flags…` |

All managed tests: 1321 passed, 1 skipped; `dotnet format` clean. Nothing live was called.

## Not changed, on purpose

- The execution policy (limit = reference ± 0.5 %, resting orders filled pessimistically) stays as the backtest
  assumes. Crossing the spread on every order would be a different strategy cost. The note makes the choice visible,
  and the owner can revisit it with evidence from the end-of-day reports.
- Confirm's need for the stream (ADR 0002 §3) stays; plan 07 O11 tracks it.
