# Operations

## Start and lifecycle

For the packaged portable application, double-click `launch_codex_usage_gui.vbs`. The launcher chooses the newest Portable executable below `release`. For development, use:

```powershell
npm install
npm start
```

Electron enforces a single application instance. The second launch focuses the existing dashboard. Closing the dashboard window hides it and leaves collection resident in the system tray. Use the tray `Exit` command for a clean shutdown. The tray has `Open dashboard`, `Sync now`, collector status and `Exit`.

## Collector schedule

At initialization, the worker creates its own SQLite ledger, starts the source watcher, waits until that watcher is ready, and then runs a full reconciliation. This ordering avoids a startup gap between initial inventory and watch registration.

After startup:

- `chokidar` first requires 2 seconds of source-write stability, then the worker applies a separate 2 second debounce before reconciliation; a quiet change therefore normally has two delay phases;
- full inventory runs every 10 minutes;
- collector-run heartbeat is written every 60 seconds;
- `Sync now` runs the same reconciliation operation manually;
- worker operations are serialized, so a concurrent UI query cannot mutate collector state halfway through another worker operation.

The status panel reports phase, last full inventory, known files/conflicts, observation coverage and ledger path. Phases are `initializing`, `syncing`, `watching`, `degraded` and `stopped`. A watcher or reconciliation failure changes the visible state to `degraded` and preserves the error message.

## Observation coverage

On startup, the worker compares the prior collector run's last completion or heartbeat with the new run start. If there was a gap, status records its UTC interval. A gap means the application did not continuously observe file updates during that time; it does not by itself mean the later full inventory cannot account for files still present.

For accurate historical daily use, keep the collector running in the tray and allow normal Codex session archival to occur. The periodic inventory and canonical promotion cover active-to-archived moves while both paths remain discoverable. If a rollout is deleted before the application has ever observed it, no local ledger can reconstruct its missing token events.

## Ledger location and backup

The default ledger for portable, installed, and development launches is:

```text
%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
```

`CODEX_USAGE_DATA_DIR` overrides that location. The data directory is rejected if it resolves beneath Codex source directories.

For backup or migration, exit the application first, then copy `usage.sqlite` together with any WAL companion files that may still exist in `%LOCALAPPDATA%\Codex Usage Desktop`. Do not overwrite a live ledger from another process. If a ledger fails to open after a crash, retain a copy and investigate it before replacing it; source rollout JSONL must not be modified as a repair step.

## Recovery and diagnosis

1. Open the dashboard and check collector status, conflict count and last inventory time.
2. Use `Sync now` to request one immediate full inventory.
3. If status remains degraded, restart the application to recreate the watcher and inspect the persistent ledger's `collector_diagnostics` and `collector_runs` tables using a SQLite-compatible read-only tool.
4. Preserve conflicting source paths and the ledger before upgrading or rebuilding. A source conflict is an intentional protection against silently replacing a canonical rollout.
5. If parser revision changes, leave present sources available until reconciliation finishes. The rebuild replaces each rollout in its own transaction. The revision marker advances only after every required candidate is discovered successfully and its selected rollout replacement succeeds; successful per-rollout work can remain persisted when a later candidate fails, and the incomplete rebuild is retried. This is not a global atomic rebuild.

CSV export is an explicit user action. It writes the currently selected snapshot to a user-chosen path after the protected-source boundary check. Spreadsheet-formula-leading values are prefixed during CSV generation to avoid formula interpretation.
