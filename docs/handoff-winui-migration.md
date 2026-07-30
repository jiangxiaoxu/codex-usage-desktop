# WinUI 3 migration handoff

## Resume target

Continue the breaking migration from Electron to unpackaged, self-contained WinUI 3. The immediate acceptance target is to build and install the latest native version, then verify that an existing ledger is shown immediately while full reconciliation continues in the background.

Repository state at handoff:

- Branch: `main`.
- Electron, Node, renderer, and legacy Python production paths have been removed.
- Runtime solution: `CodexUsageDesktop.sln`, containing App, Application, Domain, Infrastructure, and test projects.
- Delivery: x64 all-users NSIS setup containing a self-contained unpackaged WinUI 3 publish.
- Local changes were intentionally committed for transfer before final installed-machine validation.

## Implemented behavior

- Native WinUI 3 dashboard based on the Figma Page 2 design. Page 1 is unrelated and was not modified.
- Figma final layout node: `38:2`; design notes: `38:253`.
- AppInstance single instance, tray residency, hidden `--startup`, HKCU Run startup, ledger mutex, bounded shutdown, DPI handling, and CSV export.
- Windows Efficiency Mode is applied through EcoQoS and below-normal process priority.
- Collector retains strict read-only access to `%USERPROFILE%\.codex\sessions`, `archived_sessions`, and `agents`.
- Ledger remains under `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`.
- Installer performs direct break-replace migration, terminates old processes without prompting, invokes the legacy Electron uninstaller when detected, preserves the ledger, and installs into Program Files.
- Installer builds require PowerShell Core 7.4 or later through `pwsh`.
- Publish validation requires the application PRI/XBF resources, and `--smoke-test` initializes the real WinUI composition against temporary data.

## Reconciliation incident and fix

The installed UI previously stayed at `Reconciliation local rollouts` and displayed zero totals. The ledger was not empty: the observed machine had 4,633 rollouts and 145,384 usage events. The root cause was startup awaiting a full scan of roughly 4,216 JSONL files and 7.2 GiB before querying the ledger.

The current collector changes:

- Return from `StartAsync` after store, watcher, timer, and run initialization.
- Run initial inventory in the background while allowing existing ledger queries immediately.
- Preserve a single actor and single SQLite owner.
- Slice enumeration, parsing, reads, and hashing by time, bytes, and records so Query, Status, and Heartbeat commands can run between slices.
- Remove all artificial `Task.Delay`, `CooperativeDelay`, and 1/2/4/8 ms backoff throttling. `Task.Yield` and mailbox dispatch preserve responsiveness; Windows Efficiency Mode controls OS scheduling priority.
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
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.0
git diff --check
```

Then perform installed validation:

1. Run the generated `release\winui-installer\codex-usage-desktop-setup-0.3.0-x64.exe` with elevation. The normal automated flow previously used `/S /CURRENTADMIN=1`.
2. Confirm all required resources exist beside the installed executable: application `.pri`, `App.xbf`, `MainWindow.xbf`, `Controls\AuditFilterContent.xbf`, and `Controls\CostRow.xbf`.
3. Run installed `Codex Usage Desktop.exe --smoke-test` and require exit code 0.
4. Launch normally with the existing real ledger. Existing totals must appear promptly while status may still show background reconciliation.
5. Confirm Query, Status, and Heartbeat continue updating during reconciliation and that totals are nonzero.
6. Confirm CPU scheduling shows Windows Efficiency Mode and that there is no application-level sleep/backoff throttle.
7. Confirm no new Application Error, Windows Error Reporting, or .NET Runtime crash event is recorded.

## Known remaining risks

- Final independent review of the no-throttle revision was interrupted and should be rerun.
- Final installer build and installed real-ledger validation were interrupted and remain required.
- A single synchronous SQLite query or write is not internally preemptible, although full inventory no longer blocks the actor for hours.
- The setup and binaries are unsigned. Authenticode signing and a stable release identity remain required before public distribution.
- The release feed is intentionally unconfigured; the application remains offline by default.

## Safety boundaries

- Never write, rename, lock, repair, truncate, or delete anything under the observed `.codex` source directories.
- Do not delete or overwrite the LocalAppData ledger during install or test.
- Do not stage build output, `node_modules`, local databases, logs, or `task-memory`.
