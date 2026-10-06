# 26 — The morning start

- **Status:** built 2026-10-06 at the owner's request ("do all of them": item 8 of the 2026-10-01 improvement review,
  both versions).
- **Gate:** tests for every part; all managed tests green; nothing live. Task Scheduler itself can't run in the Linux
  CI: `qa schedule --install` is for the owner's PC.

## Why

Paper trades only on days a session runs. A missed morning is a missed day for the Confirm gate (10 clean days in a
row) and for every comparison (backtest, holding the list). Today the session must be started by hand before 09:10.

## What

- **`qa morning`**, the reminder: on a trading day whose session window is still open (Stockholm, or a US/Canadian
  market on the list), with no session running (the session lock), an info alert (plan 25: a Windows notification and
  `qa alerts`): "Paper has not started today: start it before 09:10". After the decision time it says a session
  started now still decides at once. Quiet on weekends, holidays (the calendars), after the close and while a session
  runs. With the kill switch still on: a warning that the session would not start. Offline; no login.
- **`qa schedule`**: the Windows scheduled tasks, weekdays, started when available, as the owner:
  - *QuantAnalyst morning reminder*: `qa morning` at 08:50;
  - *QuantAnalyst intraday import*: `qa intraday import` at 18:05 (the same name as the guide's earlier snippet, so it
    replaces it);
  - only with `--unattended`: *QuantAnalyst Paper*: `qa paper run --login totp` at 08:50, its window visible and its
    output also in `data\paper-run.log`; the reminder then moves to 09:03 (it must come after the start) and fires
    only if the session did not start.
  Without `--install` it shows the tasks and the Windows PowerShell it would run (`Register-ScheduledTask`, the same
  pattern as the guide's import snippet); `--install` runs it; `--remove` unregisters the three.
- **Unattended is the owner's choice** (asked 2026-10-01; the owner said "do all of them" on 2026-10-06): it needs the
  Avanza username, password and TOTP secret in Windows Credential Manager (`qa secrets set`; the TOTP login exists
  since Phase 3), and `--unattended --install` refuses without them. The trade-off, written in the guide: anyone using
  the owner's Windows account could log in to Avanza as the owner. The program stays read-only towards Avanza in Paper;
  a failed start is a critical alert (plan 25); a login is still tried once per trigger, never in a loop.

| Part | Code | Tests |
|---|---|---|
| The reminder | `MorningCommands` (`qa morning`) | `CliMorningTests.OnATradingMorningWithoutASession_…`, `WithTheKillSwitchStillOn_…` |
| The tasks | `MorningCommands.Tasks`, `Script`, `RemoveScript` (`qa schedule`) | `CliMorningTests.TheTasks_…`, `TheScript_…` |

## Not in this plan

- Waking the PC from sleep for the task (Task Scheduler's "wake the computer" needs the task to run as SYSTEM or with a
  stored Windows password: not done; keep the PC on, or start the session by hand).
- A reminder by phone (plan 25: e-mail and push are the owner's call).
