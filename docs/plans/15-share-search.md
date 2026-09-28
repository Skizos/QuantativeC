# 15 — Find a share: search Avanza's market from the Instruments page

- **Status:** done 2026-09-28 (steps 1 and 2); waiting for the owner's first live search. Planned 2026-09-28 at the owner's request: "make it more intuitive to add a share by making a search
  feature using an api that drags Avanza's market to the search feature."
- **Builds on:** the search route the CLI already uses to resolve tickers (`POST /_api/search/filtered-search`, Tier B,
  recorded live 2026-09-25), `qa history import` and `qa universe add`.
- **Research (CLAUDE.md rule):** re-checked 2026-09-28. Both maintained clients are unchanged since the pins
  (Qluxzz `a6a18a9`, avanza-sdk-go `43f3902`). The Go SDK's `SearchHit` / `SearchHitPrice` / `StockSector`
  ([`market/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/market/types.go))
  match our recorded response field for field. **No new endpoint**: only more of the fields we already receive are
  shown (ticker from the title, country flag, today's change, sector). Noted in `docs/research/avanza-endpoints.md`.
- **Gate:** all tests green, with new ones over the recorded search, orderbook and price-chart answers; nothing is
  called live by me; the app-safety test still holds (search and import are reads).

## Decisions

| Question | Decision | Why |
|---|---|---|
| **Logins** | Searching opens **one** Avanza login (BankID, or TOTP when chosen) and keeps it while you search and add, until you press **Done**, 5 minutes pass without a search, the page is no longer shown, or a problem ends it. Every search and every Add in that time uses the same login. (Planned as 10 minutes; shortened in step 2 because nothing else can start while the login is open.) | A BankID approval per keystroke would be unusable; one login per "I want to find shares" is the rule's spirit (at most one login per trigger, never a loop). The Go SDK says search works without a login, but that is unverified against the live site; it can come later after a read-only check you ask for. |
| **Search as you type** | From 2 characters, 0.4 s after you stop typing (Enter searches at once). At most 20 hits, stocks only. Requests go through the gateway's rate limiter like every read. | Feels like Avanza's own search without hammering it. |
| **What a hit shows** | Name and ticker, country flag and marketplace, last price with currency, today's change (green/red), sector. | Enough to tell *Ericsson B (ERIC B)* on Stockholmsbörsen from its Helsinki listing. |
| **Can it be added?** | Each hit says so, or why not: already on your list; not tradable at Avanza; not in kronor (the program trades Swedish shares in SEK); the list is full (5, the stream's limit); one share costs more than one order may (R6, 500 kr at 5 000 kr). | The same rules the session enforces, shown before you add instead of failing later. |
| **Add** | One click: imports **3 years** of daily prices (the same code as `qa history import`) and puts the share on the allowlist (the same code as `qa universe add`), in the open login. | One step instead of typing a ticker and two commands; 3 years matches the names you have. |
| **The CLI** | `qa universe add` now also refuses a 6th name (the Paper session would refuse to start with it) — the same check the app uses. | One rule in one place. |
| **When** | Like every Avanza action, only while nothing else runs (not during a Paper session). | The app runs one thing at a time. |

## Steps (each ends green, committed and pushed)

1. **Backend:** the extra search fields in the mapper; `RiskLimits.MaxOrderValue`; the import and allowlist code
   shared by the CLI and the app (`InstrumentImport`, `Allowlist`); `MarketSearchSession` (one login, a queue of
   searches and adds, closed on Done, idle or failure). Tests over the recorded answers.
2. **The app:** a **Find a share** card on the Instruments page replacing the ticker box: the search field, the hits
   with their add-or-why, **Done**; `docs/guide.md`. Tests and a full run.

## Step notes
### Step 1: backend (done 2026-09-28)

- **Search hits** (`AvanzaMapper.ToSearchHits`): the name without the "(TICKER)" suffix, the ticker, the country flag,
  today's change in percent and the level-1 sector in English, from fields the recorded answer already had.
- **`RiskLimits.MaxOrderValue(accountValue)`**: R6's limit in one place (R6 uses it; the app will show it next to
  hits that cost more than one order may).
- **`InstrumentImport` / `Allowlist`** (CLI): the import of one instrument's daily prices and the allowlist rules,
  shared by `qa history import`, `qa universe add` and the app. `qa universe add` now refuses a 6th name.
- **`MarketSearchSession`** (CLI, used by the app next): one login, then searches and adds one at a time in the order
  asked. An add checks the allowlist rules (SEK only, at most 5 names) before any import. A request that fails on its
  own (not in kronor, list full) fails alone; a failed login, an expired session (401/403), a moved trading-critical
  endpoint (404, ADR 0002) or a stop ends the session and fails every waiting request. Done or 10 minutes idle let the
  login go; a later search starts a new session with its own login.
- **Tests (19 new)**: the hit fields over the recorded search answer and the title's edge cases; the session over the
  recorded search, orderbook and price-chart answers (one login for two searches and an add; only the login and reads
  asked of Avanza; euro and full-list refusals with no import; an add of a name already on the list; idle; a failed
  login, not retried; an expired session and a moved endpoint ending it; a stop); `qa universe add` refusing a 6th
  name; R6's limit. All 1120 tests pass (1 Windows-only skipped).

### Step 2: the app (done 2026-09-28)

- **Find a share** replaces the ticker box on the Instruments page: a search box with a placeholder and a search icon,
  the status line (searching, how many, what went wrong), the hits, an **Avanza login open** chip and **Done** (Esc).
- **Each hit** shows ticker and name, country and marketplace, last price with currency, today's change (green/red)
  and sector, then **Add** or why not (`InstrumentsViewModel.Verdict`): on your list (a tick), not tradable, not in
  kronor, the list full (5), one share above R6's limit at the Paper account's size. The hint under the title states
  R6's limit (`RiskLimits.MaxOrderValue`, 500 kr at the 5 000 kr cap).
- **Search as you type:** 0.4 s after the last keystroke from 2 characters; Enter searches at once and cancels the
  pending one; an answer to an older search that arrives late is dropped.
- **Add** runs in the open login (`EngineMarketSearch` → `MarketSearchSession`), then the list and the hits update
  (the new name selected and charted, its hit "On your list").
- **Remove while searching** goes through the same session (in turn with the adds, no second login), so a full list
  can make room; otherwise it runs `qa universe remove` as before.
- **The login** is let go on Done, after 5 minutes without a search, when the page is no longer shown anywhere (not
  selected, not beside, not in a window), or by the header's Stop. While it is open the engine is busy, so nothing
  else starts; while something else runs, a search says so instead of logging in.
- **Tests (13 new):** typing pause and last-text-wins; Enter without a second search; each hit's fields and every
  add-or-why; the full list; Add updating the list and the hit; a failed add; a failed search and a late older
  answer; Done; leaving the page (beside and window kept it); remove through the open search; searching while a
  Paper session runs; the real `EngineMarketSearch` with a login that fails (one attempt per search session, never a
  loop). Plus the session's remove over the recorded answers. The WPF view builds; the XAML checks pass.

### Step 3: searching without a login (done 2026-09-28)

- **Why:** the owner asked for no BankID between searches. The login was kept for the search session, but it still
  came back after Done, 5 idle minutes or leaving the page.
- **How:** a search now goes to Avanza **without a login** (`AvanzaCommands.PublicQueryAsync`,
  `MarketSearchSession.SearchPublicAsync`): no session, no security token, never a login. Avanza's own site searches
  logged out, and the Go SDK lists search as public (docs/research/avanza-endpoints.md). It runs alongside whatever
  the app is doing (`QaEngine.ReadPublicAsync`: no busy flag), so searching works during a Paper session too.
- **Only Add and Remove** (while the login is open) use the login, as before: one BankID for any number of adds.
- **If Avanza refuses** a search without a login (401/403), the search falls back to the login session and later
  searches go straight there for the rest of the run: one login, never a login per search, never a retry loop. Any
  other problem (Avanza unavailable) is shown and nothing logs in.
- **Unverified live:** the first search in the app is the check. I made no live call.
- **Tests (5 new, 3 changed):** a public search sends no login and no token; a refusal surfaces without a login; the
  page searches while a Paper session runs but Add waits; the fallback logs in once and then goes straight to the
  login; another problem logs nothing in; remove through the open login and without it.

