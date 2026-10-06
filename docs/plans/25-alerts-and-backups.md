# 25 — Alerts and backups

- **Status:** built 2026-10-05 at the owner's request ("build the alerts and backups next"): items 6 and 7 of the
  2026-10-01 improvement review.
- **Gate:** tests for every part; all managed tests green; nothing live. The Windows notification itself can't be shown
  in the Linux CI: `qa alerts test` shows one on the owner's PC.

## A — Alerts

Today a kill, a halt or a failed decision is a line in a console nobody may be watching (a scheduled task's window, or
a log file). Alerts make them reach the owner.

**What raises one** (level: what it means):

| Kind | Level | When |
|---|---|---|
| `kill` | critical | The kill switch fires: the KILL file, the daily loss stop, 3 broker rejects in a row, schema drift, a gone endpoint (the existing `KillSwitch.Alerted`) |
| `halt` | warning | Trading halts for another reason: reconciliation, stale data, the session expired, the circuit breaker, an OMS invariant, the account. At most one per reason per 30 minutes |
| `session-failed` | critical | `qa paper run` or the Confirm session stops on an error: it could not start (login, history, no market today is not one), or it stopped before its end (a price feed failed, the session expired) |
| `decision-failed` | warning | A market's decision failed: no orders that day |
| `import-failed` | warning | The evening intraday import (`qa intraday import`, or the one after a session) missed shares or failed: a missed day can't be fetched later |
| `backup-failed` | warning | A backup or its check failed (B) |
| `day-summary` | info | A Paper session ended normally: the day's value and change, orders, fills, reconciliation. Can be switched off |
| `test` | info | `qa alerts test` |

The owner's own stop (Ctrl+C, the app's Stop, `qa kill` — its kill alert still comes) is not a failure.

**Where it goes:**
- the console, as now (`ALERT: …`);
- `state/alerts.jsonl`, one line each (read by `qa alerts`, `qa status` and the app's Overview);
- a **Windows notification**, through Windows PowerShell 5.1's WinRT toast API with PowerShell's own app id (no
  module, nothing to install). The text goes in as environment variables and is XML-escaped inside the script, so it
  can't change the script. Started and forgotten: a notification that can't be shown never stops a session.
- Every title and text goes through the command's `Redactor` first (no secret, cookie, token or full account id).

**Settings:** `config/alerts.json` (optional; defaults: notifications on, day summary on), changed with
`qa alerts set --notifications on|off --day-summary on|off`.

**Commands:** `qa alerts [--days 7]` lists them; `qa alerts test` raises a test alert, to see that notifications reach
the screen. `qa status` shows the last day's warnings and criticals.

Research: the toast pattern (load `Windows.UI.Notifications` and `Windows.Data.Xml.Dom` with
`ContentType = WindowsRuntime`, PowerShell's app id `{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe`,
`ToastNotificationManager.CreateToastNotifier(appId).Show(...)`) is from
<https://github.com/GitHub30/toast-notification-examples> (fetched 2026-10-05); it works in Windows PowerShell 5.1, not
in PowerShell 7, so the program starts `powershell.exe`, which every Windows 10/11 has.

**Not built:** e-mail or phone push (each needs a stored credential or a third-party service: the owner's call).

## B — Backups

The audit log (the Confirm gate's evidence), the Paper book, the trial ledger and the price store exist on one PC.

- `qa backup setup --to <folder> [--keep 7] [--automatic on|off]` saves `config/backup.json`. A OneDrive folder or
  another disk is best; a folder on the workspace's own disk is accepted with a warning.
- `qa backup [--to <folder>]` makes one now; with `automatic` on, every `qa paper run` and every evening import makes
  one when it ends.
- `qa backup verify [--path <backup>]` checks the latest (or a given) backup again.

**What is copied** (a list, so nothing else ever is): `audit/`, `state/paper/` (the book and the manual requests),
`config/`, `promotion/`, the trial ledger, and the price store. **Never:** `state/auth.json` and the rest of `state/`
(login state), the Windows credential store, recordings, reports (rebuilt with `qa report eod`), logs.

**How:**
- Each backup is a new folder `qa-backup-yyyy-MM-dd_HHmmss` under the destination, written as `….partial` and renamed
  only when complete and checked; a stale `.partial` is removed next time.
- Files are copied and re-read: each copy's SHA-256 must equal the source's. The audit copy's hash chain is verified
  (`AuditLog.Verify`), and the ledger copy's (`TrialLedger.Verify`).
- The price store is copied by DuckDB itself (`ATTACH` a new file, `COPY FROM DATABASE`, `DETACH`), a consistent copy
  even of a store in use; the copy is opened again and its row count per table must equal the source's. (Checked on
  the bundled DuckDB v1.5.5 on 2026-10-05: tables, views and data copied; duckdb.org could not be fetched from the
  development container.)
- `manifest.json` lists every file with its size and SHA-256, the store's tables and rows, and the checks. `verify`
  re-reads all of it.
- The newest `keep` backups stay; older ones are removed, and only folders with this program's manifest are ever
  touched.
- While a session runs (the session lock is held by another process) a backup is refused: the session makes its own
  when it ends.

**Restore** is by hand, documented in the guide: stop everything, `qa backup verify`, copy the folders back.
A restore command that overwrites the audit log is not built.

`qa status` shows the last backup and warns when none is set up, the last one is over 3 days old, or it failed.

## Done

| Part | Code | Tests |
|---|---|---|
| Alerts | `Trading.Alerts` (`Alert`, `AlertLog`, `AlertSettings`, `Alerter`), `WindowsToastNotifier` and `Alerting` (CLI); `AvanzaCliServices.Notifier` (none in tests; the Windows one only in `Default`, so a test run never pops notifications) | `AlertsTests` (console, log, notifier, redaction, 30-minute quiet, settings), `CliAlertsAndBackupTests.TheWindowsNotification_…` (a fixed script, the alert only in the environment) |
| Wiring | The command runner's `AlertAs` (a failed or crashed `qa paper run`, Confirm session or evening import; "no session today" and your own stop are not failures); kill and halts in the Paper and Confirm sessions; `PaperSession.Alerts` (decision failed); the evening import's missed shares; the day summary | `PaperSpyTests` (kill, decision failed, did not start, could not log in, no session today, day summary) |
| Commands | `qa alerts`, `qa alerts test`, `qa alerts set`; `qa backup`, `qa backup setup`, `qa backup verify` | `CliAlertsAndBackupTests` |
| Backups | `Trading.Backup` (`Backups.Run`/`Verify`/`List`, `BackupManifest`, `BackupSettings`), `HistoryStore.CopyTo`/`RowCounts`/`RowCountsOf`; the automatic backup after `qa paper run` and `qa intraday import` (`BackupCommands.After`) | `BackupTests` (the list, the checks, tampering found, keep, nothing else touched, a folder inside a source refused), `PaperSpyTests.WithBackupsSetUp_…` |
| Status and app | `qa status` lines (the day's alerts, the last backup); the app's Overview shows the same checklist | `CliAlertsAndBackupTests.Status_…`, `Backup_…` |
