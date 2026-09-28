# ADR 0005 — Foreign shares (USD, CAD) on paper

- **Status:** Accepted (2026-09-28), decided by the project owner
- **Related:** ADR 0002 (fail-safe gateway), ADR 0003 (modes and limits: all limits in SEK), CLAUDE.md "Numerical & money
  rules", docs/plans/16-foreign-shares.md

## Context

Until now the program traded only Swedish shares in SEK: `OrderPreparation` refused any other currency, every limit is
in SEK, and the trading calendar is Nasdaq Stockholm's. The owner asked (2026-09-28): *"Make it able to use CAD and USD
too."*

US and Canadian shares trade 15:30–22:00 Stockholm time. Stockholm closes at 17:30.

## Decision

The owner chose, on 2026-09-28 (the two recommended options):
- **"One session to 22:02":** one Paper session a day. Swedish names decide at 09:10, US and Canadian names at 15:40.
  With foreign names on the list the session ends two minutes after the US close.
- **"Paper only for now":** foreign shares trade on paper. Confirm and Auto refuse them until their fees and currency
  exchange have been checked against Avanza's own pre-trade answer.

Recorded as:

1. **Markets follow the currency.**
   - SEK: Nasdaq Stockholm (calendar XSTO), as before.
   - USD: the US market (calendar XNYS; Nasdaq keeps the same holidays).
   - CAD: the Canadian market (calendar XTSE; TSX Venture keeps the same holidays).
   - Any other currency is still refused (EUR, NOK, DKK, …).
2. **Money stays in SEK.**
   - Prices, limits, tick sizes and fills stay in the share's own currency.
   - Every money figure is converted to SEK at the day's FX rate: R6–R9 and R19, the paper cash and positions, the plan's
     sizing, the reports.
3. **FX rates come from the Riksbank's daily fixing** (SWEA API, series `SEKUSDPMI` and `SEKCADPMI`, SEK per unit;
   keyless). They are stored in the history store with the time they became known.
   - A session uses the latest fixing at its start. If it is more than 4 days old, or missing, the foreign names are
     skipped that day and the Swedish ones still trade.
   - Backtests use each day's fixing. The fixing is published about 16:15 Stockholm, before the US and Canadian close,
     so a bar's own date is point-in-time for them. A day without a fixing uses the one before.
4. **Costs** (per courtage class, from Avanza's price list for foreign trading):
   - **Courtage** in the share's currency:
     | Class | Courtage | Minimum |
     |---|---|---|
     | Start, Mini | 0.25 % | 1 USD / 1 CAD |
     | Small | 0.15 % | 6 USD/CAD |
     | Medium | 0.089 % | 8 USD/CAD |
     | Fast pris | 0.079 % | 12 USD/CAD |
   - **Currency exchange fee:** the class's existing `fx_fee_rate` (Start 0 %, confirmed by the owner 2026-09-26; the
     others 0.25 %).
   - UNVERIFIED: a search-engine extract, since avanza.se is not reachable from the development container. The owner
     checks them against Avanza's "Prislista för utlandshandel" page. Paper runs with them; live trading would refuse
     them (R20) in any case.
5. **One session, market by market.** Each market uses its own calendar, in its own local time.
   - **Decision:** at the market's open plus the same offset as Stockholm's decision time (09:10 is 10 minutes after
     09:00, so 09:40 in New York and Toronto: 15:40 Stockholm, 14:40 during the few weeks when US and European daylight
     saving differ).
   - **Order window:** the same distances from the session as Stockholm's (R16): from 5 minutes after the open to
     10 minutes before the close, early closes included.
   - A market's day orders end at its own close. The session ends 2 minutes after the last close of the markets on the
     list, and writes one end-of-day report then.
   - Started late, the session still trades the markets whose decision time has not passed (or whose window is open).
6. **Paper only.** The gateway refuses USD and CAD orders in Confirm and Auto ("foreign shares trade on paper only,
   ADR 0005") before any risk check. Changing this needs a new decision here.
7. **Calendars:** XNYS and XTSE for 2026 and 2027, as UNVERIFIED drafts computed with exchange_calendars 4.13.2
   (Apache-2.0), the library the XSTO draft was checked against. Session times are in exchange local time
   (America/New_York, America/Toronto).
8. **Backtests in SEK:**
   - Foreign prices are converted to SEK day by day.
   - Courtage is charged per instrument: native ABI 1.3 adds `qe_bt_set_courtage`, an additive change.
   - The FX fee applies to foreign trades.
   - The report says where the FX rates came from, and that a foreign tick table is approximated in SEK at the last
     day's rate.

## Alternatives

- **A separate evening session for foreign names:** two reports and two starts a day. Declined by the owner.
- **Refusing a list that mixes markets:** simpler, but it forces a choice between the Swedish and the foreign names.
  Declined by the owner.
- **Live trading of foreign shares at once:** declined by the owner until fees and FX are checked on real order cards.
- **FX from Avanza:** no maintained open-source client documents a currency endpoint, and CLAUDE.md forbids inventing
  one. The Riksbank's fixing is official, free and documented by two open-source clients.

## Consequences

- On days with foreign names the app must stay open until 22:02 for the day to be complete and count as clean.
- A day is still one Stockholm date; the gate counts it once.
- **Unverified until the first evening session:** whether Avanza streams live (not delayed) quotes for US and Canadian
  shares. With delayed quotes, R15 (fresh market data) rejects the orders, and the report shows it.
- The price store gains an FX table (a new store schema version, migrated in place).

## Open items

1. The owner verifies the foreign courtage and the FX fee of the chosen class (a screenshot of Avanza's price list is
   enough).
2. The owner verifies the XNYS and XTSE calendars against the exchanges' own pages, then sets `verified_on`.
3. The first import of a Canadian share confirms the series id `SEKCADPMI` (taken from the Riksbank's `SEK<CCY>PMI`
   naming; an unknown series answers 204, which the import reports).
4. The first evening Paper session confirms that US and Canadian quotes arrive live.

## References

- Riksbank SWEA API, base `https://api.riksbank.se/swea/v1`, `GET /Observations/{seriesId}/{from}/{to}` returning
  `[{date, value}]`, as used by
  [thpe/riksbank `fab7803`](https://github.com/thpe/riksbank/blob/fab7803b9a5d0db75e86e95a68001ebd7cad8af5/riksbank/riksbank.py)
  and [pipeworx-io/mcp-riksbank-se `4ad1747`](https://github.com/pipeworx-io/mcp-riksbank-se/blob/4ad17470397031ec01db2f89883ffebd5fba8acf/src/index.ts)
  (keyless; an unknown series answers 204 No Content).
- exchange_calendars 4.13.2: `exchange_calendar_xnys.py`, `exchange_calendar_xtse.py`
  (<https://github.com/gerrymanoim/exchange_calendars>).
- Avanza foreign courtage and FX fee: search-engine extracts of avanza.se "Prislista för utlandshandel" and "Handla
  aktier i USA från 1 USD och Kanada från 1 CAD" (2026-09-28), UNVERIFIED.
