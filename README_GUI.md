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

The application observes local Codex rollout JSONL files under `%USERPROFILE%\.codex` and does not upload data. On startup and every four hours while it remains running, it sends a version-only request to this project's public GitHub Releases API; if a newer release exists, the user can open its download page. Codex source paths are never opened for writing, locked, renamed, deleted, truncated, or repaired. SQLite locking is limited to the application's own `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite` ledger.

Features:

- Manually started, single-instance tray application; closing the window keeps collection active.
- Watcher-driven append ingestion with a periodic full inventory as a reliability fallback.
- Permanent SQLite accounting under `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`.
- Canonical rollout promotion across active and archived paths without duplicate accounting or ledger rollback.
- Visible collector health, source conflicts, last inventory, SQLite path, and offline observation gaps.
- One continuous live range slider with clickable, evenly spaced 1h, 4h, 12h, 1-day, 2-day, 4-day, 7-day, and 14-day anchors. The thumb can stop between anchors for ranges such as 1.5 or 10 days, and custom Singapore-time ranges are collapsed by default and available on demand.
- Inline live filtering by model and observed role, with all models and subjects selected by default. Roles come from actual rollout/session thread metadata: the main-thread role is normalized to `root`, while subagents retain each recorded role for independent filtering and aggregation and fall back to `unknown` when missing.
- Main-thread or subagent filtering and agent path or nickname search.
- Token and cost summaries by model and role; price shares are labeled explicitly, and all UI USD values use one decimal place. Usage-event counts are not exposed as user-facing metrics.
- Standard API token-cost estimate that always ignores the GPT-5.6 >272K input multiplier; non-GPT-5.6/5.5/5.4 models are grouped as zero-cost Others.
- Exact `source_model=unknown` values are isolated as Unknown attribution and reported as unpriced tokens instead of zero-cost Others.
- Forked subagent rollout replay is excluded; only the addressed child turn and its later usage are accounted.
- CSV export of the currently filtered event snapshot.
- Automatic GitHub Release checks on startup and every four hours, with a manual retry button and a user-initiated download-page link for newer versions.

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
