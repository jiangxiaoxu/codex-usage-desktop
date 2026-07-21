# Codex Local Usage GUI

Start the packaged desktop application by double-clicking:

```text
launch_codex_usage_gui.vbs
```

The launcher runs the packaged restart flow. For development:

```powershell
npm install
npm start
```

The application observes local Codex rollout JSONL files under `%USERPROFILE%\.codex` and does not upload data. An NSIS-installed Windows application checks this project's public GitHub Releases at startup and every four hours while it remains running. When a newer version is available, one click downloads, verifies, silently installs, and restarts the application after the collector stops cleanly. Portable and development builds do not run automatic updates. Codex source paths are never opened for writing, locked, renamed, deleted, truncated, or repaired. SQLite locking is limited to the application's own `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite` ledger.

Features:

- A manually started, single-instance tray application; a Windows Startup launch opens directly in the notification area, and closing the dashboard keeps collection active.
- Watcher-driven append ingestion with a periodic full inventory as a reliability fallback.
- Permanent SQLite accounting under `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`.
- Canonical rollout promotion across active and archived paths without duplicate accounting or ledger rollback.
- Visible collector health, source conflicts, last inventory, SQLite path, and offline observation gaps.
- One continuous live range slider with clickable, evenly spaced 30-minute, 4h, 12h, 1-day, 2-day, 4-day, 7-day, and 14-day anchors. The thumb can stop between anchors for ranges such as 1.5 or 10 days, and custom Singapore-time ranges are collapsed by default and available on demand.
- Inline live filtering by model and observed role, with all models and subjects selected by default. Roles come from actual rollout/session thread metadata: the main-thread role is normalized to `root`, while subagents retain each recorded role for independent filtering and aggregation and fall back to `unknown` when missing.
- Main-thread or subagent filtering and agent path or nickname search.
- Token and cost summaries by model and role; price shares are labeled explicitly, and all UI USD values use one decimal place. Usage-event counts are not exposed as user-facing metrics.
- Standard API token-cost estimate that always uses base rates for Codex subscription usage and never applies a long-context multiplier; non-GPT-5.6/5.5/5.4 models are grouped as zero-cost Others.
- Exact `source_model=unknown` values are isolated as Unknown attribution and reported as unpriced tokens instead of zero-cost Others.
- Forked rollout replay is excluded. Manual main-thread forks start accounting at the first post-fork task, while subagent forks start at the addressed child turn.
- CSV export of the currently filtered event snapshot.
- Automatic GitHub Release checks for NSIS-installed applications, with a manual retry button, download progress, checksum verification, silent installation, and automatic restart for newer versions.

Validation:

```powershell
npm run typecheck
npm test
npm run package:portable
npm run package:installer
```

Cost notes:

- `reasoning_output_tokens` is treated as a subset of `output_tokens` and is not charged twice.
- The local rollout records do not expose cache-write or tool-call charges, so the displayed cost is a model token-cost estimate.
- Adjacent repeated complete cumulative `token_count` snapshots and zero-breakdown stale snapshots are removed before aggregation.
- Parser revisions replace each present rollout transactionally. The revision marker advances only after all required candidates succeed; partial completed work can persist and is retried without deleting permanent history whose source files are already gone.
