# Market rules: Nasdaq Stockholm, tick sizes, Avanza courtage

- **Researched:** 2026-09-25.
- **Egress limits:** this research container's egress proxy blocks `nasdaq.com`, `nasdaqomxnordic.com`, `tradinghours.com`, `eur-lex.europa.eu`, `esma.europa.eu`, `fi.se` and `*.avanza.se`. Every figure below that comes from a **secondary** source (search-engine extract) is marked **UNVERIFIED**.
- **Before Phase 4:** the Scheduler and cost model must not ship with unverified constants. Each constant below gets a `source_url` + `verified_on` field in `config/market-calendar.*.json` / `config/courtage.json`, and the app refuses to start Confirm/Auto if any `verified_on` is empty.

## 1. Nasdaq Stockholm trading sessions (equities)

| Phase | Time (Europe/Stockholm) | Status |
|---|---|---|
| Opening call auction (order entry) | 08:45–09:00, random uncross 09:00:00–09:00:05 | UNVERIFIED (search extract of Nasdaq INET Nordic auction material) |
| Continuous trading | 09:00–17:25 | UNVERIFIED (search extract) |
| Closing call auction | 17:25–17:29:25, random uncross 17:29:30–17:30 | UNVERIFIED (search extract) |
| Half trading day | 09:00–13:00 (closing call ahead of 13:00) | UNVERIFIED (search extract). The exact half-day closing-call times need the Nasdaq calendar. |

Primary sources to check:
- <https://www.nasdaq.com/european-market-activity/trading-hours>
- <https://www.nasdaq.com/INETNordicAuctionsFactsheet>
- The current *Nasdaq Nordic Market Model* (INET Nordic) PDF, which defines the phases, auction uncross and volatility guards.

**Auction facts relevant to the backtest and fill model** (from the INET auction fact sheet extract; UNVERIFIED):
- Individual auction orders are not displayed. The feed shows Net Order Imbalance Information: equilibrium price, paired quantity, imbalance.
- The uncross is random within a short window.

**First North:** Nasdaq has announced a First North Growth Market **auction model** with **three scheduled intraday auctions** for some instruments (<https://view.news.eu.nasdaq.com/view?id=b6011b00da7e9e3cc866517ad4adb111d&lang=en>, extract only). **Consequence:** some First North names do not trade continuously. The instrument master needs a `trading_model` field (continuous vs periodic auction), and the backtest fill model must respect it.

Volatility guards and trading halts are not modelled in v1. When a halt occurs, the stale-data rule blocks orders.

## 2. Holidays and half days

- Holidays: closed on Swedish public holidays plus exchange-specific days such as Christmas Eve, Midsummer Eve and New Year's Eve. A search extract says 2026 has about 10 weekday closures and several half days (UNVERIFIED). Christmas Eve 2026 (Thu 24 Dec) is a closed day per the same extract.
- **I did not transcribe a date list**, because no primary source could be read. Plan: Phase 4 adds `config/market-calendar.XNSA.2026.json` and `…2027.json`. You fill them from Nasdaq's official calendar (<https://www.nasdaq.com/european-market-activity/trading-hours>) or a vendor, and a test checks every weekday of the year is classified as full, half or closed.

## 3. Tick sizes (MiFID II RTS 11)

- **Legal basis:** Commission Delegated Regulation (EU) **2017/588** (RTS 11), Annex, consolidated version: <https://eur-lex.europa.eu/eli/reg_del/2017/588/2019-04-09/eng>.
  - The tick size depends on the instrument's **liquidity band**, set by the **average daily number of transactions (ADNT)** on the most liquid market, and on the **price range** of the order.
  - The Annex has six ADNT bands, each with price ranges and their ticks.
  - ADNT is recalculated periodically by the competent authority, published via ESMA FITRS, so an instrument's band can change over time.
- **Not transcribed here:** the Annex table could not be fetched (EUR-Lex blocked), and CLAUDE.md forbids tables from memory.

**Design consequence:**
1. **The authoritative tick table per instrument is Avanza's `orderbook/{id}` → `tickSizeList.tickSizeEntries[{min,max,tick}]`** (`avanza-endpoints.md` §3). It reflects the venue's current band and is what Avanza will validate against. We store it with a known-at timestamp.
2. The RTS 11 Annex is transcribed into `native/data/rts11_tick_table.csv` in Phase 5, when EUR-Lex is reachable or you paste it. It is used for the backtest over history (with the historical band) and as a **cross-check**: if Avanza's table disagrees with RTS 11 for the stored band, log a warning. It is not a blocker.
3. The verification requirement "tick-size rounding for every liquidity band" is tested against the transcribed RTS 11 table, with fixture-based tests for Avanza's table.

Rounding rule for orders: round **buy limits down** and **sell limits up** to the nearest valid tick, both passive. Do this before risk checks, and re-run the price collar on the rounded price.

## 4. Avanza courtage (price list)

Primary pages (blocked here; UNVERIFIED extracts):
- <https://www.avanza.se/konton-lan-prislista/prislista/courtageklasser.html>
- <https://www.avanza.se/konton-lan-prislista/prislista/handel-sverige.html>

| Class | Swedish equities (extract) | Status |
|---|---|---|
| Start | 0 kr courtage for customers who never had ≥ 50,000 SEK with Avanza. Up to 500 free trades per 12 months, then automatically moved to Mini. | UNVERIFIED |
| Mini | 1 kr per order up to 400 kr order value; above that 0.25 % | UNVERIFIED |
| Small | 39 kr per order up to 26,000 kr; above that 0.15 % | UNVERIFIED |
| Medium | 69 kr per order up to 100,000 kr; above that 0.069 % | UNVERIFIED |
| Fast Pris | max 99 kr per order (flat) | UNVERIFIED |
| Pro / Private Banking | separate classes. A Pro extract says a minimum of 49 kr or 0.034 %, plus a monthly minimum commission. | UNVERIFIED, out of scope for v1 |

- **Currency exchange fee** for non-SEK instruments: **0.25 %** per a search extract (UNVERIFIED).
- **Switching class:** free and immediate, per Avanza help pages (extract).

**Design consequences** (see ADR 0003):
- `config/courtage.json` holds the table with `source_url` and `verified_on`. The backtest cost model reads it, and the report prints the class and the table version.
- In Confirm/Auto, the order card shows **Avanza's own `preliminaryfee` figure** (commission + market fees + FX fee + transaction tax). If it differs from the local model by more than 1 SEK, that is a warning in the audit log.
- With a max order value of 25,000 SEK (risk limit), Mini costs 0.25 % × 25,000 = 62.5 SEK and Small costs 39 SEK per order. At our order sizes the courtage class materially changes cost drag, so it must be a config value, not an assumption.

## 5. Market abuse (MAR) hygiene

Regulation (EU) No 596/2014 (MAR) applies to retail traders too. Finansinspektionen's market-abuse pages are at <https://www.fi.se> (blocked here). The risk limits in ADR 0003 are built to avoid order patterns that could look like:
- **spoofing/layering:** rapid place/cancel, orders not intended to execute
- **wash trades**
- **marking the close:** no auction participation unless configured, and never repeated closing-auction orders that move the price

Rules we build in:
- at least 5 s between place/cancel on the same instrument
- at most 5 order actions per minute and at most 20 orders per day
- no simultaneous buy and sell working in the same instrument
- no self-crossing
