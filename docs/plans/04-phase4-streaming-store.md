# 04 — Phase 4: Streaming + data store

- **Status:** implemented and green (2026-09-25). **Waiting for your stop-point run** (a live stream recording, one history import, and the calendar check) to close the gate.
- **Scope:** master plan §4 Phase 4; ADR 0002 §3 (streaming and quotes, accepted); `market-rules.md` §2 (calendar).
- **Gate:**
  - replay tests on recorded SSE fixtures pass
  - staleness: no depth or poll update for 10 s sets the flag
  - known-at: a restatement is not visible before its `known_at`
  - the calendar test classifies every weekday of 2026 and 2027

## Research refresh (CLAUDE.md gateway rule)

Re-checked on 2026-09-25, before any stream code was written:
- **Reference clients:** both HEADs are unchanged since Phase 3.
  - Qluxzz: `a6a18a94…`
  - avanza-sdk-go: `43f39025…`
- **SSE protocol:** read from the Go SDK at that commit:
  - `internal/sse/subscription.go`: framing, reconnect, `Last-Event-ID`, `retry`, 1 MB lines, and 4xx handling
  - `market/service.go`: the order-depth path, the page `Referer`, and the required cookies `csid`, `cstoken` and `AZACSRF`
  - `market/types.go`: the `ORDER_DEPTH` payload
  - the tests: `info` events with plain-text data (`connected`, `heartbeat`)
- **Correction to `avanza-endpoints.md` §5.** In the Go SDK, the `ORDER_DEPTH` levels are **flat**: `{buyPrice, buyVolume, sellPrice, sellVolume}`, with `orderbookId` as a string. The nested `{buySide, sellSide}` shape that §5 listed belongs to the **marketdata** REST response (confirmed by the owner's 2026-09-25 recording). §5 is fixed.
- **Market code is `XSTO`, not `XNSA`.** Avanza's own orderbook response (recorded 2026-09-25) says `marketPlace: "XSTO"`, and XSTO is Nasdaq Stockholm's ISO 10383 MIC. The calendar files are named `market-calendar.XSTO.<year>.json`, and the master plan is corrected.
- **DuckDB.NET:** `DuckDB.NET.Data.Full` **1.5.5** is the newest release on nuget.org (2026-09-25).
  - MIT licence, bundles native DuckDB for win-x64 and linux-x64, and targets net8.0 and net10.0.
  - Source read at [Giorgi/DuckDB.NET@a1d3afd](https://github.com/Giorgi/DuckDB.NET/tree/a1d3afdf558ab00baa3a9c04af6f91655cd5ce08): named `$param` parameters, `DATE` ↔ `DateOnly`, `DECIMAL` ↔ `decimal`.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| Own-order stream (`ORDER`) | **Deferred to Phase 6**, where the OMS consumes it. | With no orders on the account, a recording of it would show only heartbeats. Its DTO can't be verified before Phase 7's first order anyway. |
| Staleness rule | ADR 0002 §3 exactly: **stale ⇔ `now − max(depthAt, pollAt) > 10 s`, or the depth stream is not connected, or no data yet.** `depthAt`/`pollAt` are *our* receipt times. | `quote.updated` from Avanza is the last server-side change. After a quiet spell it is old even when the data is current, so it can't measure *our* freshness. |
| Where bid/ask come from | From the **newer** of the last `ORDER_DEPTH` event and the last poll's `orderDepth`. Last trade and volume always come from the poll. | The poll carries a full depth too. A silently hung stream then can't pin an old bid/ask as long as polls succeed. |
| Unknown SSE event names | `ORDER_DEPTH` and `info` are known; any other event name ⇒ `SchemaDriftException` (Tier A). | The ADR puts the depth stream in Tier A. The recording will show the real event names before anything trades on them. |
| `info` event data | Treated as a heartbeat; never parsed, logged or printed (length only). | Its format is unknown live. |
| Store location | `data/quant.duckdb` (already git-ignored), overridable with `--store`. | Mirrors `state/` and `recordings/live/`. |
| Price types in the store | `DECIMAL(18,6)` for prices, `BIGINT` for volume. | Exact values from Avanza's JSON (CLAUDE.md money rule). Models convert to `double` in one place. |
| Bar time | A daily bar's `valid_from` is its **Stockholm trading date**. Avanza stamps daily bars at Stockholm midnight: the recorded `1787608800000` is 2026-08-24T22:00Z = 2026-08-25 00:00 CEST. Any other time of day ⇒ refuse the import. | Keeps UTC in storage (CLAUDE.md) without guessing dates. |
| Avanza history is not point-in-time | Every import is a new snapshot with `known_at = import time`. `point_in_time = false` and `survivorship_free = false` are stored with the source and printed on every `history show`. | CLAUDE.md backtest rule. Known-at protects against *our own* later restatements. It cannot make Avanza's already-adjusted history point-in-time. |
| Calendar content | I **drafted** 2026 and 2027 from the Swedish holiday rules. Cross-checked against the open-source [`exchange_calendars` XSTO calendar @ bbda29f](https://github.com/gerrymanoim/exchange_calendars/blob/bbda29fed902374bdb75acab008f421fbd567823/exchange_calendars/exchange_calendar_xsto.py) (Apache-2.0). **`verified_on` stays `null` until you check it against Nasdaq's page.** | This container can't reach nasdaq.com. A test recomputes every draft date from the rules, so a transcription slip fails the build. |
| Chart periods | Unchanged: `today` … `five_years`. `infinity`/`all_time` is not added. | The two clients spell it differently. Add it only after a recording shows which spelling Avanza accepts. |

## Projects

| Project | Contents | Dependencies |
|---|---|---|
| `src/QuantAnalyst.Core` (extended) | `Quote`, `OrderDepthUpdate`, stream events, `MarketCalendar`, `DataSourceInfo`, `PriceHistory`; `IBrokerGateway.StreamOrderDepthAsync` | none |
| `src/QuantAnalyst.Avanza` (extended) | `SseParser`, `AvanzaStreamClient`, the `order-depth-stream` route and Tier A DTO, the stream recorder, and sanitizer support for stream recordings | as before |
| `src/QuantAnalyst.Data` (**new**) | `QuoteComposer`, `Broadcaster<T>` (fan-out), `HistoryStore` (DuckDB, known-at), the instrument master, `IHistoricalDataProvider`, `AvanzaChartImporter`, and the calendar loader | Core, `DuckDB.NET.Data.Full` 1.5.5, `Microsoft.Extensions.Logging.Abstractions` |
| `tests/QuantAnalyst.Data.Tests` (**new**) | Composer, fan-out, store, importer and calendar | Data, `Microsoft.Extensions.TimeProvider.Testing` |

`QuantAnalyst.Data` does **not** reference `QuantAnalyst.Avanza`. It only sees `IBrokerGateway`, which an architecture test checks. The importer is named for its source: it labels data "Avanza price chart", but it calls the gateway interface.

## Design

### SSE (`SseParser`, `AvanzaStreamClient`)
- **Parser:**
  - Follows the WHATWG event-stream rules:
    - lines end in CRLF, LF or CR
    - a leading BOM is dropped
    - `:` comments are ignored
    - one space after the colon is stripped
    - `data` lines join with `\n`
    - `id` with NUL is ignored
    - `retry` accepts only ASCII digits
    - a blank line dispatches; an empty `event` means `message`
  - It is incremental over arbitrary chunk boundaries.
  - **A line or an event over 1 MB ⇒ protocol error.** The connection drops and the stream reconnects (ADR 0002 §3).
- **Connection pipeline:** Recording → RateLimit → SecurityToken → Cookies → primary. There is no read-retry handler: the stream's own loop reconnects.
- **Headers:**
  - sent: `Accept: text/event-stream`, `aza-do-not-touch-session: true`, `Cache-Control: no-cache`, the page `Referer` (`/handla/order.html/kop/{id}`, as the Go SDK sends), the cookies and token, and `Last-Event-ID` on reconnect
  - not sent: the browser fingerprint headers
- **Timeouts:** the response headers must arrive within `AttemptTimeout` (15 s). After that the body has no timeout, but an **idle watchdog** reconnects after 60 s without a byte. Staleness has long since been flagged by then.
- **Reconnect:**
  - Delay: `max(server retry, 3 s) · 2^min(n,5)`, capped at 30 s, with ±20 % jitter.
  - `n` resets after a connection that delivered at least one event.
  - Transport errors, a clean end of stream, 408, 429 and 5xx all reconnect.
- **Terminal outcomes** (no reconnect; the enumerator throws):
  - 401/403 ⇒ `SessionExpiredException`
  - 404 ⇒ `EndpointGoneException`
  - any other 4xx ⇒ `SchemaDriftException`
  - a 200 whose content type is not `text/event-stream` ⇒ `SchemaDriftException`
  - an `ORDER_DEPTH` payload that fails the Tier A check ⇒ `SchemaDriftException`
- **Events out:** `IAsyncEnumerable<MarketStreamEvent>`, one of:
  - `DepthUpdate(OrderDepthUpdate)`
  - `StreamHeartbeat`
  - `StreamStateChanged(Connecting | Connected | Reconnecting, reason)`
- **Depth semantics:** every `ORDER_DEPTH` is a **full snapshot** (Go SDK: "complete order book snapshot"). A side with price 0 and volume 0 is an empty side (null price), never a zero price.

### Stream recording
- **Format:** `qa-stream-recording/1`, one file per connection (`NNN-order-depth-stream.json`). It holds:
  - the request metadata: path, header **names**, the `Last-Event-ID` flag
  - the response status, content type and header names
  - an `events` array of `{offsetMs, event, id, retry, data}`, where `data` is kept as JSON when it parses and as text otherwise; `info` text is kept only as its length
- **Cap:** 10,000 events per file. The file is written when the connection ends or is cancelled.
- **Sanitizer:**
  - maps event ids to `ev-N`
  - leaves market data alone (public order-book prices and volumes)
  - runs the same leak scan as REST recordings (forbidden values, tokens, JWTs, random-looking strings); a hit ⇒ exit 2

### `QuoteComposer` (Data)
- **Inputs, per instrument:**
  - `IBrokerGateway.StreamOrderDepthAsync`
  - a poll of `GetMarketSnapshotAsync` every `PollInterval` (5 s by default; ADR 0002 §3 budget)
- **State:** the last depth (and when it arrived), the last successful poll (and when), and the stream state.
- **Publishing:**
  - A `Quote` goes out on every depth event and every poll.
  - A 1 s timer publishes the stale transition: **fresh → stale** as soon as the rule above trips, and **stale → fresh** on the next update.
  - `Quote.StaleReason` says why: `"no depth or poll update for 10.4 s"`, `"depth stream reconnecting"`, `"no data yet"`.
- **Errors:**
  - Poll `BrokerUnavailableException` ⇒ logged; the quote goes stale on its own.
  - `SessionExpired`, `SchemaDrift` or `EndpointGone` from either input ⇒ the composer stops and rethrows (halt).
- **Fan-out (`Broadcaster<T>`):**
  - every subscriber gets its own bounded `Channel<T>` (default 64, `DropOldest` as ADR 0002 §3 says for depth); dropped counts are exposed per subscriber
  - completion and errors propagate to every subscriber
  - a slow consumer never blocks the producer

### `HistoryStore` (DuckDB, known-at)
Schema version 1:

```sql
CREATE TABLE sources      (name VARCHAR PRIMARY KEY, point_in_time BOOLEAN NOT NULL, survivorship_free BOOLEAN NOT NULL, notes VARCHAR NOT NULL);
CREATE TABLE instruments  (orderbook_id VARCHAR NOT NULL, isin VARCHAR, ticker VARCHAR NOT NULL, name VARCHAR NOT NULL,
                           currency VARCHAR NOT NULL, market_place VARCHAR NOT NULL, instrument_type VARCHAR NOT NULL,
                           trading_model VARCHAR NOT NULL, volume_factor DECIMAL(18,6) NOT NULL, tick_table VARCHAR NOT NULL,
                           valid_from DATE NOT NULL, known_at TIMESTAMP NOT NULL, source VARCHAR NOT NULL, source_version VARCHAR NOT NULL);
CREATE TABLE daily_bars   (orderbook_id VARCHAR NOT NULL, valid_from DATE NOT NULL,
                           open DECIMAL(18,6) NOT NULL, high DECIMAL(18,6) NOT NULL, low DECIMAL(18,6) NOT NULL, close DECIMAL(18,6) NOT NULL,
                           volume BIGINT NOT NULL, known_at TIMESTAMP NOT NULL, source VARCHAR NOT NULL, source_version VARCHAR NOT NULL,
                           PRIMARY KEY (orderbook_id, source, valid_from, known_at));
```

- **Times:** `known_at` is UTC with microsecond precision.
- **Reads, "as known at T":** for each key, the row with the greatest `known_at ≤ T`. The query uses `QUALIFY row_number() … = 1`. The default is "latest known".
- **Writes are append-only.** A bar or instrument row is inserted only when it is new or differs from the latest known version. That makes a re-import idempotent, and a change becomes a **restatement** with its own `known_at`. The import report counts new, restated and unchanged rows.
- **Instrument master:**
  - Rows come from the orderbook response: ISIN, ticker, name, currency, market place, type, volume factor, and the tick table as canonical JSON.
  - `trading_model` is `continuous` for XSTO and `unknown` otherwise. The Phase 5 backtest refuses `unknown` (First North auction model, `market-rules.md`).
- **Sources:** each source is registered with its traits. `avanza-price-chart`: not point-in-time, not survivorship-free.

### History providers
- **Interface:** `IHistoricalDataProvider { DataSourceInfo Source; Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(OrderbookId, DateOnly from, DateOnly to, CancellationToken) }`. That is the vendor slot.
- **`AvanzaChartImporter`:**
  - Picks the smallest chart period that covers `from` and asks for `resolution=day`.
  - Refuses the import if Avanza answers with another resolution. Longer periods may only offer week bars; the recorded `one_month` offered `hour, day, week`.
  - Maps timestamps to Stockholm dates.
  - Warns about bars on days the calendar says are closed.
- **Gateway change:** `GetPriceHistoryAsync` now returns `PriceHistory(Bars, Resolution, PreviousClose)`, so the resolution Avanza actually used is visible.

### Calendar
- **Files:** `config/market-calendar.XSTO.2026.json` and `….2027.json`, format `qa-market-calendar/1`. Each has:
  - `source_url`, `verified_on` (null until you verify) and `draft_basis`
  - the regular session 09:00–17:30 and the half-day session 09:00–13:00 (`market-rules.md`, UNVERIFIED)
  - the closed days and half days, each with a name
- **Drafted closed days:**
  - New Year's Day, Epiphany, Good Friday, Easter Monday, 1 May, Ascension Day
  - National Day (6 June), Midsummer Eve, Christmas Eve, Christmas Day, Boxing Day, New Year's Eve
- **Drafted half days:**
  - the day before Epiphany, Maundy Thursday, 30 April, the day before Ascension Day, and All Saints' Eve (the Friday between 30 Oct and 5 Nov)
- **Weekends:** a date on a weekend is dropped from the list.
- **`MarketCalendar` (Core):**
  - `Classify(date)` returns `Full | Half | Closed | Weekend` with open/close times in Stockholm local time.
  - It also offers `IsOpen(utc)` and `NextTradingDay`.
  - A date outside the loaded years throws: it is never guessed.
  - `IsVerified` is false while any loaded year has `verified_on: null`. Confirm/Auto will refuse to start on that (ADR 0003).

### CLI (all read-only)
| Verb | Needs login | What it does |
|---|---|---|
| `qa stream <TICKER…> [--duration 60] [--poll 5] [--record-dir DIR \| --no-record]` | yes (BankID) | Streams depth for up to 5 instruments, polls marketdata, and prints each composed quote: bid×size, ask×size, last, age, and **STALE** with its reason. Records by default, like `probe`. |
| `qa history import <TICKER> [--period one_year]` | yes | Adds or updates the instrument master and imports daily bars into `data/quant.duckdb`. Prints the new/restated/unchanged counts and the source labels. |
| `qa history show <TICKER> [--from] [--to] [--as-of]` | no | Reads from the store, with labels (source, not survivorship-free, not point-in-time, SEK, Stockholm dates). |
| `qa instruments [--as-of]` | no | Lists the instrument master. |
| `qa calendar [--year] [--date]` | no | Shows counts or one day's classification, plus the verification status. |

## Test map (gate items in bold)

| Area | Tests |
|---|---|
| SSE parser | WHATWG cases (CR/LF/CRLF, BOM, comments, multi-line data, space stripping, `id` with NUL, bad `retry`, no trailing blank line), 1-byte chunking, 1 MB limit |
| Stream client | reconnect with `Last-Event-ID`; server `retry`; jittered backoff within bounds and capped at 30 s; `n` reset; 401/403/404/400 terminal; wrong content type; idle watchdog; Tier A drift on a new field; unknown event name; heartbeat; cancellation closes the connection |
| **Replay** | **the provisional fixture (from the Go SDK tests) and then your recording, replayed through parser → DTO → mapper → composer** |
| Recorder / sanitizer | stream files have no header or cookie values; ids are mapped; leak scan; idempotent |
| **Composer** | **no depth or poll update for 10 s ⇒ stale**; an update clears it; disconnected ⇒ stale even with fresh polls; the newest source wins bid/ask; drift or session expiry stops it; poll error ⇒ stale later, no crash |
| Fan-out | two subscribers; a slow one drops oldest and a fast one gets everything; errors and completion propagate |
| **Store** | **a restatement is invisible before its `known_at`**; re-import idempotent; instrument versions; decimals exact; file round trip |
| Importer | Stockholm-midnight mapping; wrong resolution refused; period selection; labels |
| **Calendar** | **every weekday of 2026 and 2027 classified**; the draft equals the rule computation; no weekend or duplicate entries; unverified flagged; out-of-range throws; DST days |
| Architecture | Data doesn't reference Avanza; stream routes are GET; still no order or transfer routes |

## Stop point: what I need from you (a trading day, 09:00–17:30 Stockholm)

The next trading day is Monday 2026-09-28.
1. Record two streams on one session. This also answers ADR 0002 open item 1 for two instruments:

   ```
   qa stream ERIC-B VOLV-B --duration 120
   ```
2. Sanitize the recording and commit it:

   ```
   qa recordings sanitize --in recordings/live/<folder> --out recordings/fixtures/avanza/2026-09-28-stream
   ```
3. Run `qa history import ERIC-B --period one_year`, then `qa history show ERIC-B`.
4. Open <https://www.nasdaq.com/european-market-activity/trading-hours>, compare its holidays and half days with the two calendar files, fix any difference, and set `verified_on`.

## Results (2026-09-25, clean tree)

| Gate item | Status | Evidence |
|---|---|---|
| Replay tests on recorded SSE fixtures | **pass on the provisional fixture; your recording pending** | `StreamReplayTests` replays every `qa-stream-recording/1` file under `recordings/fixtures/avanza/` through parser → Tier A DTO → mapper → `QuoteComposer`. Today that is the hand-written `provisional/order-depth-stream-5240.json`; your sanitized recording is picked up automatically once committed. |
| Staleness (10 s) | **pass** | `QuoteComposerTests.NoDepthOrPollUpdateFor10Seconds_SetsTheStaleFlag_AndAnUpdateClearsIt`: fresh at 9.5 s, stale by 10.25 s ("no depth or poll update for 10.x s"), fresh again on the next depth event. Also: stale while the stream reconnects even with fresh polls, and "no data yet". |
| Known-at restatement | **pass** | `HistoryStoreTests.Restatement_IsNotVisibleBeforeItsKnownAt`: as of T2 − 1 µs the old close (96.2) is returned; from T2 the restated one (48.1). Unchanged neighbours keep their original `known_at`, and a re-import writes nothing. |
| Calendar classifies every weekday | **pass (draft)** | `MarketCalendarTests`: all 2 × 261 weekdays of 2026–2027 are classified. The drafts equal the holiday rules and `exchange_calendars` 4.13.2. **`verified_on` is still null** until you check Nasdaq's page. |

| Command | Result |
|---|---|
| `ctest --preset dev` / `--preset asan` | 107/107 / 107/107 (native code unchanged in Phase 4) |
| `dotnet build QuantAnalyst.sln` | 0 warnings, 0 errors |
| `dotnet test --solution QuantAnalyst.sln` | **380 passed, 1 skipped** (the Windows-only Credential Manager round trip). By project: Core 25, Data 54, Avanza 212 (+1 skipped), Native and Analytics unchanged. |
| `dotnet format --verify-no-changes` / `clang-format --dry-run -Werror` | clean / clean |
| `bash tests/hooks/block-live-trading.test.sh` | 29/29 |
| CI (8 checks: Linux, Windows, ASan, format + guardrails) | green on `deaaa53` and `e2ac7c2`, including DuckDB's native library on Windows |

**Timing tests were run repeatedly** (the composer and stream tests mix fake and real time): Data tests 8×, Avanza tests 5×, no failures.

**Still open:**
- **Your stop-point run** (above). The recording also settles:
  - the real `ORDER_DEPTH` field set and event names (`info`?)
  - how often heartbeats come
  - whether two depth streams can share one session (ADR 0002 open item 1)
- **Calendar `verified_on`**, from Nasdaq's page.
- **Daily bars over longer periods.** Whether `one_year`/`five_years` offer `resolution=day` is unknown until your import. The importer refuses the import rather than silently storing week bars.
