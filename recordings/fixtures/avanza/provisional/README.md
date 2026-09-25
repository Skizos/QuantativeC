# Provisional Avanza fixtures (hand-written, NOT captures)

These payloads were **written by hand** from the reference clients' models. They are not recordings of Avanza responses. They exist so the Tier A/B DTOs, mappers and drift tests have something to run against before real recordings exist.

| File | Route | Modelled on |
|---|---|---|
| `session-info.json` | `GET /_api/authentication/session/info/session` | Go `auth.SessionInfo` |
| `accounts-overview.json` | `GET /_api/account-overview/overview/categorizedAccounts` | Qluxzz `models/overview.py`, Go `accounts.AccountOverview` |
| `trading-accounts.json` | `GET /_api/trading-critical/rest/accounts` | Go `accounts.TradingAccount` |
| `positions.json` | `GET /_api/position-data/positions` | Qluxzz `models/account_posititions.py`, Go `accounts.AccountPositions` |
| `orders.json` | `GET /_api/trading/rest/orders` | Go `trading.GetOrdersResponse` |
| `orderbook-5240.json` | `GET /_api/trading-critical/rest/orderbook/{id}` | Go `market.Orderbook`, Qluxzz `models/order_book.py` |
| `marketdata-5240.json` | `GET /_api/trading-critical/rest/marketdata/{id}` | Qluxzz `models/market_data.py`, Go `market.MarketData` |
| `search-eric.json` | `POST /_api/search/filtered-search` | Qluxzz `models/search_result.py`, Go `market.SearchResponse` |
| `price-chart-5240.json` | `GET /_api/price-chart/stock/{id}` | Go `market.StockPriceChart` |
| `transactions.json` | `GET /_api/transactions/list` | Go `accounts.TransactionsResponse` |
| `order-depth-stream-5240.json` (format `qa-stream-recording/1`) | SSE `GET /_push/order-depth-web-push/{id}` | Go `market.OrderDepthData` and the event sequence in its `order_depth_test.go` (`info` + `ORDER_DEPTH`, `retry: 1000`); `info` data is kept as a length placeholder, like real recordings |

Client commits: Qluxzz/avanza `a6a18a948f88cb7e340051e480b203b2ee917eed`; vmorsell/avanza-sdk-go `43f39025751c05ff73a85e708dadee4bfa9da2ca` (MIT). See `docs/research/avanza-endpoints.md`.

**What the data is:**
- Account ids (`999000x`), `urlParameterId`s, prices, volumes and amounts are **synthetic**.
- The tick table in `orderbook-5240.json` is illustrative, not the official RTS 11 table.
- Orderbook `5240` / ISIN `SE0000108656` (Ericsson B) are public identifiers, used for realism only.

**Updated 2026-09-25 from the owner's first live probe.** The names came from the probe; the values are still placeholders:
- `interestRates`, `creditAccountClearingAccountNumber` and `autoDistribution` were added to accounts
- `isDiscretionaryAccount` was added to trading accounts
- `orderbookStatus` is absent from the orderbook

These files are replaced by your sanitized recordings (`qa probe`, then `qa recordings sanitize`). The real recordings will go to a dated sibling folder, e.g. `../2026-09-26/`, which the tests then prefer.
