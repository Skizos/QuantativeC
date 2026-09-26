# 10 — The Windows app (WPF): QuantAnalyst without the terminal

- **Status:** planned and started 2026-09-26 at the owner's request: "construct a windows app with dotnet and wpf to make this tool easy to use without having to run commands manually through the terminal".
- **Scope:** a desktop front end for what exists today (Phases 3–6 plus Phase 7 step 1).
  - status, instruments, strategy + backtest, the daily Paper session, reports
  - the same rules as the CLI: CLAUDE.md "Absolute safety rules", ADR 0002–0004
- **Gate:**
  - the view models are tested on Linux in CI
  - the WPF project builds on every CI job (`EnableWindowsTargeting`)
  - architecture tests show the app has no path to an order route, promotion, or a live mode
  - **I can't run a WPF window here (Linux container).** You start it on Windows: `.\qa-app.ps1`.
- **Not in the app (v1):**
  - **Confirm and Auto:** they don't exist yet; Phase 7 adds them to the CLI first
  - **promotion (`qa promote`):** yours, rare and deliberate; it stays a terminal command
  - `qa secrets set`, recordings, the probe, `qa price|risk|optimize`

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| **How the app runs things** | **In-process.** The app calls the same `qa` command code (`QaCli.Run`) on a background thread. Every line it prints streams into the window. Structured screens (status, book, reports, allowlist) read the same library objects the CLI prints from (`StatusCommand.Build`, `PaperBook`, `EodReport`, `Universe`). | One implementation of every rule: the app can't drift from the CLI, and everything the tests prove for the CLI holds in the app. Nothing parses text output. |
| **BankID** | The CLI gains a seam for the BankID prompt. The app shows the QR code as an image in the window, redrawn as Avanza rotates it, with the status text and a Cancel button. | The terminal's text QR code is hard to scan; a real image is easy. |
| **Stopping a session** | The CLI gains a cancellation seam. **Stop** in the app is the same as Ctrl+C: the session cancels its working orders, writes the partial end-of-day report and releases the lock. **KILL** (always visible, red) is `qa kill`: it works even while another command runs. | Stop and kill are different things in the CLI too. |
| **Projects** | `src/QuantAnalyst.Desktop.Core` (net10.0 library: view models, the engine, no WPF). `src/QuantAnalyst.Desktop` (net10.0-windows WPF, the window and views only; the exe is `QuantAnalyst.exe`). `tests/QuantAnalyst.Desktop.Tests` (Linux-runnable). | Everything worth testing is testable in CI on Linux. The WPF part is thin XAML. |
| **No new MVVM package** | A small `ObservableObject` and command classes of our own (about 100 lines). QR images come from QRCoder's PNG renderer, already a pinned dependency. | No new third-party code to vet. |
| **Where it runs** | The app finds the repository by walking up from its own folder to `QuantAnalyst.sln` and works there, like the `.\qa` launcher, so `config\`, `data\`, `state\`, `audit\` and `reports\` are the same ones the CLI uses. | CLI and app share one state. |
| **Launch** | `.\qa-app.ps1` rebuilds what changed (native library, app) and starts the window. `.\qa-app.ps1 -Shortcut` puts a "QuantAnalyst" shortcut on your desktop. | One double-click after that. |
| **Language** | English UI; times in Stockholm time, amounts in SEK with a comma thousands separator, as in the CLI. | Matches the CLI and the docs. |

## Screens

1. **Status (start page):** every `qa status` line with a coloured mark, then the numbered next steps. A button does the most useful next thing (e.g. "Start today's session"). It refreshes by itself.
2. **Instruments:** the allowlist with each name's history and a **Remove** button. **Add** takes a ticker (`ERIC-B`), imports its history (one Avanza login) and allows it.
3. **Strategy:**
   - pick a strategy and fill in its parameters (named fields, not `key=value`)
   - **Backtest on my instruments** shows the result
   - **Use for Paper** saves it
4. **Paper session:**
   - **Start**: login, history update, then waiting for 09:10 with a countdown
   - the live log, the paper account (cash, positions, fees), and **Stop**
   - the BankID QR code appears here when a login needs it
5. **Reports:** each day, clean or not, with its summary and details, and the Confirm gate as "n of 10 clean days".

Always visible:
- the mode banner ("PAPER: nothing is sent to Avanza")
- the login method (BankID / TOTP)
- the red **KILL** button

## Steps (each ends green, committed and pushed)

1. **CLI seams, then the core:**
   - `AvanzaCliServices` gets `Cancellation` and a `BankIdPrompt` factory
   - `Desktop.Core`: MVVM basics, the in-process engine (one command at a time, line streaming, cancel, BankID events), the workspace, and the five view models
   - tests
2. **The WPF window:**
   - `QuantAnalyst.Desktop`: shell, the five views, the BankID overlay, the kill button, converters, QR image
   - builds in CI on Linux and Windows
3. **Launch and docs:**
   - `qa-app.ps1` (build if stale, native copy, start, `-Shortcut`)
   - architecture tests extended to the new assemblies
   - `docs/guide.md` "Using the app", `docs/cli.md` pointer, and this plan's results

## Test map (planned)

| Area | Must show |
|---|---|
| Engine | output streams line by line; one command at a time; cancel stops a running command and it exits cleanly; a failing command reports its exit code and error lines; the BankID prompt sends QR images to the UI |
| Status | lines and marks from `StatusCommand.Build`; the next steps; the primary action follows the first step |
| Instruments | add runs history import and then universe add, stopping at the first failure; remove; the list refreshes |
| Strategy | the catalog and each strategy's parameters; the arguments built from the fields; save writes `config/paper.json`; the backtest uses the allowlist |
| Session | start builds `paper run` with the login method; stop cancels; kill writes the flag even while the session runs; the book refreshes |
| Reports | days and their state from the audit log; the gate count |
| Architecture | Desktop assemblies never touch order routes, the order channel, the preflight or the promotion command; no `--mode` anywhere in their sources |
