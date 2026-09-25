# Avanza terms: automated access

- **Researched:** 2026-09-25.
- **Scope:** what Avanza's published terms say about robots, automated tools and API use. **This is not legal advice and draws no legal conclusion.** You decide, ideally after asking Avanza in writing.

> **Verification status: UNVERIFIED against the primary pages.** This research container's egress proxy blocks `*.avanza.se`, so I could not open the terms myself. The quotes below come from search-engine extracts of Avanza's pages. Before relying on them, open the linked pages yourself, read the current versions, and save a dated PDF copy under `docs/research/terms-snapshots/` (git-ignored if you prefer).

## 1. Website user terms (Användarvillkor)

- Page: <https://www.avanza.se/sakerhet-villkor/anvandarvillkor.html>
- Company-site variant with the same wording: <https://foretagswebb.avanza.se/anvandarvillkor/>

Wording as extracted by the search engine (Swedish, paraphrase-level; check against the page):

> Som besökare och användare av sajten är det otillåtet att använda alla former av robotar, spindlar, scrapers och andra automatiska verktyg (med undantag för större sökmotorer såsom Google och Yahoo) utan ett skriftligt medgivande från Avanza.

> … inte vidta någon åtgärd som åsamkar en orimligt eller oproportionerligt stor börda på sajten, … eller aktivt eller maskinellt påverka eller störa åtkomst till eller funktionalitet på sajten.

In English: robots, spiders, scrapers and other automatic tools are not allowed without Avanza's **written consent** (major search engines excepted). You also must not place an unreasonable load on the site or mechanically interfere with its access or function.

## 2. Customer agreements (apply to account holders)

These are the documents that govern your account relationship. The user terms above say separate agreements apply to customers.

- *Allmänna villkor för handel med finansiella instrument*: <https://www.avanza.se/avanzabank/hem/konton/blanketter/aktie-och-fondkonto/Allmanna_villkor_handel_FinansInstrument.pdf>
- *Allmänna villkor för kontoinformationstjänster och överföringstjänster* (PSD2 account-information/payment-initiation terms, i.e. the regulated third-party access route): <https://www.avanza.se/avanzabank/hem/konton/blanketter/aktie-och-fondkonto/allmanna_villkor_for_kontoinformationtjanst.pdf>
- Your own account agreement (*depå-/kontoavtal*) for the ISK/AF/KF you use.

**Not reviewed:** I could not open these PDFs. Read them for clauses on:
- the security and confidentiality of login credentials, and giving them to "third parties", which a program you run arguably is or is not
- automated or algorithmic order entry
- Avanza's right to block access or close accounts
- your liability for orders placed with your credentials

## 3. Official alternatives worth asking Avanza about

- **Avanza Pro / Infront:** Avanza offers the Infront trading system to Pro customers (<https://www.avanza.se/private-banking-pro/pro/infront.html>). Pro has a minimum monthly commission (a search extract says 2,000 SEK/month for "Pro 1"; unverified). Ask whether Infront or Pro offers any sanctioned programmatic or API order entry.
- **Avanza's own algorithmic products** (e.g. the 2026 *Avanza Sigma* launch, <https://investors.avanza.se/media/press/2026/avanza-utmanar-storbankernas-miljardmarknad-lanserar-algoritmstyrd-aktieforvaltning-till-halva-priset/>) show that Avanza runs automation internally. They do not imply permission for customer automation.
- **Written request:** the user terms explicitly contemplate written consent. A short message to customer service is the cleanest route. Suggested content:
  - read-only account data plus a small number of limit orders per day
  - a dedicated account
  - username + TOTP login, conservative rate limits
  - no scraping of public pages

## 4. What the program does to limit exposure (engineering, not a legal position)

- Log in **once** per trigger and never loop (lockout risk, Part 0 §3).
- Use a conservative global rate limit (about 2 req/s), polling intervals of 5 s or more, and SSE instead of aggressive polling.
- Prefer public, no-login endpoints for research data (`avanza-endpoints.md` §3), and use only **one** allowlisted account for orders.
- Rate-limit order actions: at most 5 per minute, at most 20 per day, and at least 5 s between place/cancel on the same instrument. This is also MAR hygiene.
- Keep a kill switch, and halt on drift so the program never guesses.

## 5. Decision (recorded)

**Recorded 2026-09-25 in ADR 0004.** The original request follows.


Before Phase 3's first live read-only call, record your decision in `docs/adr/` (e.g. ADR 0004 "Authorization to automate"): whether you asked Avanza, what they answered, and which scope you accept. Phase 3 is gated on this.
