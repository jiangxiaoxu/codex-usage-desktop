# WinUI 3 handoff

## Resume target

Continue the unpackaged, self-contained WinUI 3 application. The immediate acceptance target is to build and install the latest native version, then verify that an existing ledger is shown immediately while full reconciliation continues in the background.

Repository state at handoff:

- Branch: `main`.
- The production runtime is .NET 8 / WinUI 3.
- Runtime solution: `CodexUsageDesktop.sln`, containing App, Application, Domain, Infrastructure, and test projects.
- Delivery: x64 all-users NSIS setup containing a self-contained unpackaged WinUI 3 publish.
- Local changes were intentionally committed for transfer before final installed-machine validation.

## Implemented behavior

- Native WinUI 3 dashboard target is the confirmed Figma Page 2 product candidate. Page 1 is unrelated and was not modified.
- Final layout node: `90:2`. Responsive contract node: `90:329`.
- Minimum window is `900 x 720 DIP`. Wide is `>=1280`,Medium is `1000-1279`,and Compact is `<1000`; detail tables are side by side at `>=1440`.
- The four top-level filters each occupy one row: time,model,subject and main thread. The main-thread editable dropdown shows at most 20 recent choices by activity as `project name - ID prefix - title`; the project name is the main session `session_meta.cwd` directory name and the title is the authoritative `thread_name` in `session_index.jsonl`. Manual input accepts a complete UUIDv7 session ID,uses an exact main `ConversationId` as its root,and includes every descendant-agent event. The page owns the only vertical scroll; each table owns horizontal scrolling only. Model order is Sol,Terra,Luna,codex-auto-review,Others.
- AppInstance single instance, tray residency, hidden `--startup`, HKCU Run startup, ledger mutex, bounded shutdown and DPI handling.
- Focus-aware Efficiency Mode and process-priority work is deferred and must not be treated as implemented acceptance scope.
- The multi-size product icon is embedded in the EXE and reused by AppWindow/taskbar, tray, setup, uninstaller, shortcuts, and Apps & features.
- Collector retains strict read-only access to `%USERPROFILE%\.codex\sessions`, `archived_sessions`, and `agents`.
- Safe source-conflict recovery writes only the application ledger. It accepts only stable metadata-exact candidates with `Equal` or `Extension` semantics,selects deterministically,and retains the last valid ledger with internal degraded diagnostics and background retry for unsafe candidates. Source conflict is not displayed in the GUI.
- Ledger remains under `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`.
- Installer terminates the current application before replacing the WinUI payload and installs into Program Files. It does not delete the LocalAppData ledger.
- Installer builds require PowerShell Core 7.4 or later through `pwsh`.
- Publish validation requires the application PRI/XBF resources, and `--smoke-test` initializes the real WinUI composition against temporary data.

## Reconciliation incident and fix

The installed UI previously stayed at `Reconciliation local rollouts` and displayed zero totals. The ledger was not empty: the observed machine had 4,633 rollouts and 145,384 usage events. The root cause was startup awaiting a full scan of roughly 4,216 JSONL files and 7.2 GiB before querying the ledger.

The current collector changes:

- Return from `StartAsync` after store, watcher, timer, and run initialization.
- Run initial inventory in the background while allowing existing ledger queries immediately.
- Preserve a single actor and single SQLite owner.
- Slice enumeration, parsing, reads, and hashing by time, bytes, and records so Query, Status, and Heartbeat commands can run between slices.
- Remove all artificial `Task.Delay`, `CooperativeDelay`, and 1/2/4/8 ms backoff throttling. `Task.Yield` and mailbox dispatch preserve responsiveness without relying on the deferred Efficiency Mode work.
- Publish progress every 250 ms and persist heartbeat every second during inventory.
- Do not reread unchanged conflict sources on every startup.
- Drop/coalesce timer ticks while inventory is active and require a full five-minute interval after inventory completion before another timer inventory.
- Keep cancellation checks before interactive dispatch and retain deterministic test hooks for concurrency tests.

## Validation completed

The final source revision before handoff passed:

- Release tests: 75/75.
- Four concurrency-sensitive tests repeated 10 times: passed.
- `dotnet format --verify-no-changes`: passed.
- `git diff --check`: passed.

An earlier installer build passed publish, PRI/XBF validation, WinUI smoke, and NSIS compilation, but it predates the final removal of artificial throttling. Do not use the existing `release/` artifact as final evidence.

## Resume checklist

Run from the repository root:

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet test CodexUsageDesktop.sln -c Release
dotnet format CodexUsageDesktop.sln --verify-no-changes
$sevenZip = 'C:\Tools\7-Zip\7za.exe'
$sevenZipRuntime = 'C:\Tools\7-Zip\7zr.exe'
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.9 -SevenZipPath $sevenZip -SevenZipRuntimePath $sevenZipRuntime
git diff --check
```

Then perform installed validation:

1. Run the generated `release\winui-installer\codex-usage-desktop-setup-0.3.9-x64.exe` interactively with elevation. Do not use silent installer arguments for user acceptance.
2. Confirm all required resources exist beside the installed executable: application `.pri`, `App.xbf`, `MainWindow.xbf`, `Controls\AuditFilterContent.xbf`, and `Controls\CostRow.xbf`.
3. Run installed `Codex Usage Desktop.exe --smoke-test` and require exit code 0.
4. Launch normally with the existing real ledger. Existing totals must appear promptly while status may still show background reconciliation.
5. Confirm Query, Status, and Heartbeat continue updating during reconciliation and that totals are nonzero.
6. Confirm no new Application Error, Windows Error Reporting, or .NET Runtime crash event is recorded.

## Known remaining risks

- Independent review of the no-throttle revision completed without an actionable finding.
- The final installer build, installed smoke test, and real-ledger validation completed. Existing totals appeared at inventory `0/3327`, remained queryable during reconciliation, and updated when background processing completed.
- The historical installed payload matched all 520 published files, the ledger was preserved in that validation, and no new matching Application Error, Windows Error Reporting, or .NET Runtime crash event was recorded.
- Focus-aware Efficiency Mode and process-priority behavior remains deferred.
- A single synchronous SQLite query or write is not internally preemptible, although full inventory no longer blocks the actor for hours.
- The SHA-256-only release check fetches fixed GitHub Release metadata at startup and every 6 hours. It never downloads or starts setup without separate user actions.

## Safety boundaries

- Never write, rename, lock, repair, truncate, or delete anything under the observed `.codex` source directories.
- Do not point installer smoke tests at valuable ledger data; use a disposable installation and confirm that the LocalAppData ledger remains unchanged.
- Do not stage build output, `node_modules`, local databases, logs, or `task-memory`.
