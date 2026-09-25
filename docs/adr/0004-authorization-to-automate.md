# ADR 0004 — Authorization to automate

- **Status:** Accepted (2026-09-25), decided by the project owner
- **Related:** `docs/research/avanza-terms.md`, CLAUDE.md "Absolute safety rules", ADR 0002, ADR 0003, master plan §2 item 10 and §4 Phase 3

## Context

Avanza has no official API, so this program uses the private web API that Avanza's own web app uses (ADR 0002).

Avanza's website user terms, as extracted by a search engine, say that robots, scrapers "and other automatic tools" are not allowed **without Avanza's written consent**. This is UNVERIFIED: this container cannot open avanza.se, and the details are in `avanza-terms.md` §1. The customer agreements that govern the account itself were not reviewed.

Master plan Phase 3 made the first live call conditional on the owner recording a decision here.

## Decision

On 2026-09-25 the owner wrote: *"I agree to avanzas terms of use"*, and instructed that Phase 3 proceed using **a dedicated ISK account**.

Recorded as:
1. **Scope accepted:**
   - automated **read-only** access in Phase 3
   - later, gated order entry (Phases 6–8, ADR 0003), on **one dedicated ISK** that is allowlisted by account id
2. **No written consent from Avanza is on record.** "Agreeing to the terms" is not the same as the written consent the terms reportedly require for automated tools. The owner accepts the risk that Avanza treats this use as a breach, which could mean blocked login, a closed account or other measures.
3. **Recommended, not required by this ADR:**
   - send Avanza the short written request described in `avanza-terms.md` §3 and record the answer below
   - save a dated copy of the current user terms and customer agreements under `docs/research/terms-snapshots/`
4. **If Avanza says no**, or asks you to stop:
   - stop all live use (reads and orders)
   - set this ADR to "Superseded"
   - keep only backtesting on imported data

## Constraints that follow from this decision (engineering, not a legal position)

- **Load:**
  - at most one login per trigger, never a retry loop
  - a global limit of ~2 requests/s (burst 5)
  - conservative polling (ADR 0002 §3)
  - no scraping of HTML pages
- **Credentials:**
  - **BankID** (default, owner's choice 2026-09-25): you approve every login in the BankID app. Nothing is stored, and approval is never automated.
  - **Username + password + TOTP** from the OS secret store, when you enable it later.
- **Accounts:** one dedicated ISK for orders. Other accounts are only ever read.
- **No money movement, ever:**
  - The program implements **no** transfer, withdrawal, deposit or payment endpoints.
  - `AvanzaRoutes` has none, an architecture test fails the build if one appears, and the Claude Code hook blocks such paths in shell commands.
  - Moving money between accounts or to your bank stays a manual action in Avanza's app or website.
- **Claude Code** never makes live calls on its own. You run the live commands on your machine (CLAUDE.md).

## Alternatives considered

| Option | Why not (for now) |
|---|---|
| Wait for written consent before any live call | The safest option legally; the owner chose not to wait. It is still recommended in parallel. |
| Avanza Pro / Infront | Has a monthly minimum and no known sanctioned API for customers; still worth asking Avanza (`avanza-terms.md` §3). |
| Another broker with an official API | Out of scope: the owner wants Avanza and an ISK. |

## Consequences

- Phase 3's stop point may proceed: the owner may run the read-only `qa` commands.
- This ADR is revisited if Avanza replies, if the terms change, or before Phase 7 (first live order).

## Log

| Date | Event |
|---|---|
| 2026-09-25 | Owner decision recorded (above). No written consent requested yet. |
| 2026-09-25 | Owner chose BankID login per run for now; TOTP stays available for later. |
