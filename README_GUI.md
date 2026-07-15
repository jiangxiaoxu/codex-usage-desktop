# Codex Local Usage GUI

Start the packaged desktop application by double-clicking:

```text
launch_codex_usage_gui.vbs
```

The launcher prefers the newest Portable executable under `release`. For development:

```powershell
npm install
npm start
```

The application observes local Codex rollout JSONL files under `%USERPROFILE%\.codex` and does not upload data. Codex source paths are never opened for writing, locked, renamed, deleted, truncated, or repaired. SQLite locking is limited to the application's own `codex-usage-data\usage.sqlite` ledger.

Features:

- Manually started, single-instance tray application; closing the window keeps collection active.
- Watcher-driven append ingestion with a periodic full inventory as a reliability fallback.
- Permanent SQLite accounting beside the Portable executable.
- Canonical rollout promotion across active and archived paths without duplicate accounting or ledger rollback.
- Visible collector health, source conflicts, last inventory, SQLite path, and offline observation gaps.
- One live range slider with 0.5h, 1h, 2h, 4h, 8h, 12h, 24h, 48h, and 72h stops, plus custom Singapore-time ranges.
- Inline live filtering by model and query-time agent role category, with all models and subjects selected by default. Subagent categories are discovered read-only from `%USERPROFILE%\.codex\agents\*.toml`; roles without a configuration are merged into Others.
- Main-thread or subagent filtering and agent path or nickname search.
- Token and cost summaries by model, role, and agent path; price shares are labeled explicitly, and all UI USD values use one decimal place. Usage-event counts are not exposed as user-facing metrics.
- Standard API token-cost estimate that always ignores the GPT-5.6 >272K input multiplier; non-GPT-5.6/5.5/5.4 models are grouped as zero-cost Others.
- Exact `source_model=unknown` values are isolated as Unknown attribution and reported as unpriced tokens instead of zero-cost Others.
- Forked subagent rollout replay is excluded; only the addressed child turn and its later usage are accounted.
- CSV export of the currently filtered event snapshot.
- A bounded, independently scrollable agent/thread audit table with a sticky header.

Validation:

```powershell
npm run typecheck
npm test
npm run package:portable
```

Cost notes:

- `reasoning_output_tokens` is treated as a subset of `output_tokens` and is not charged twice.
- The local rollout records do not expose cache-write or tool-call charges, so the displayed cost is a model token-cost estimate.
- Repeated cumulative `token_count` snapshots and zero-breakdown stale snapshots are removed before aggregation.
- Parser revisions atomically reparse still-present canonical sources without deleting permanent history whose source files are already gone.
