# Architecture

## Scope

Codex Usage Desktop is a local Electron application. It observes Codex rollout JSONL files below `%USERPROFILE%\.codex`, persists normalized usage records to its own SQLite ledger, and presents token and estimated API cost views. It does not upload observed data. Its only network client requests this project's public GitHub Releases API at startup and every four hours while running to check for a newer version; a download page is opened only after the user clicks the update action.

## Process and data flow

```text
Codex sessions / archived_sessions JSONL --- read-only stat, readFile, open("r") --->
                    collector-worker.ts owns chokidar and reconciliation
                                  |
                                  +-> rollout-parser.ts -> usage-store.ts -> usage.sqlite
                                  |          |                    |
                                  |          v                    v
                                  |   normalized UsageEvent    query/export
                                  v
                          collector-client.ts <-> main.ts IPC <-> preload.ts <-> renderer.ts
```

## Electron layers

- `src/main.ts` owns the Electron lifecycle. It creates the `BrowserWindow`, the tray menu, the single-instance lock and the `CollectorClient`. Closing the window hides it; the collector continues in the tray until `Exit` is selected.
- `src/preload.ts` exposes only `window.usageApi` through `contextBridge`. The renderer has `contextIsolation: true`, `nodeIntegration: false` and `sandbox: true`.
- `src/renderer.html`, `src/renderer.ts` and `src/styles.css` implement the local dashboard. Renderer state is limited to UI filters and query results; it does not open source files or SQLite directly.
- `src/collector-client.ts` starts `collector-worker.js` as a Node `Worker`, correlates request IDs, applies a 10 minute timeout to initialization/reconciliation and a 60 second timeout to other requests, and forwards `usage-updated` events to `main.ts`. It does not own the filesystem watcher.
- `src/collector-worker.ts` serializes all worker operations through one promise queue. It owns source discovery, watch-triggered reconciliation, parser revision rebuild, SQLite access and CSV writing.

## IPC contract

`main.ts` registers the following IPC handlers. They are the only renderer-to-main operations exposed by `UsageApi`.

| Channel | Input | Result | Worker operation |
| --- | --- | --- | --- |
| `usage:sync` | none | `SyncResult` | `reconcile` |
| `usage:query` | `FilterSpec` | `QueryResult` | `query` |
| `usage:status` | none | `CollectorStatus` | `getStatus` |
| `usage:export` | `FilterSpec` | saved path and count | `exportCsv` |
| `updates:check` | none | `UpdateStatus` | requests the fixed GitHub latest-release endpoint in main |
| `updates:open-latest-release` | none | none | opens the fixed GitHub Releases page in the system browser |
| `usage:updated` | main-to-renderer event | `CollectorStatus` | forwarded worker event |

The worker protocol in `src/collector-protocol.ts` additionally contains `initialize` and `shutdown`; these are private to main/worker coordination. `FilterSpec` uses a half-open UTC interval `[startUtc, endUtc)`. `models: null` and `subjects: null` mean all available categories, while an empty array means none.

## Collection and canonicalization

The worker inventories `sessions` and `archived_sessions` recursively and only considers files named `rollout-*.jsonl`. It tracks source size, mtime, committed byte offset and a SHA-256 hash over the final 64 KiB of the consumed prefix. A stable source is read by stat-before/read/stat-after checks. Incremental reads verify the prior boundary before parsing appended bytes; otherwise the file is fully reparsed.

Each rollout has one canonical source in SQLite. Active and archived copies can therefore represent the same rollout without duplicate usage. Prefix divergence, attribution divergence and other unsafe candidate relationships are recorded as source conflicts instead of silently merged. A missing source is marked absent but its already-accounted permanent ledger history is retained. When `ROLLOUT_PARSER_REVISION` changes, each currently present viable rollout is replaced transactionally. The revision marker advances only after every required candidate has been discovered successfully and its selected rollout replacement succeeds. Completed rollout replacements can persist if a later candidate fails, and a later reconciliation retries the incomplete rebuild; there is no global atomic rebuild.

## Parser and normalized records

`rollout-parser.ts` accepts only newline-terminated JSONL records. A trailing partial line is deliberately deferred until it becomes complete. It derives rollout metadata from `session_meta`, resolves models from `turn_context`, `thread_settings_applied` and `task_started`, and extracts `token_count` deltas from `last_token_usage`.

The parser drops invalid token relationships, invalid timestamps, zero-breakdown snapshots and only adjacent complete cumulative snapshots. `reasoning_output_tokens` must not exceed `output_tokens`, and `cached_input_tokens` must not exceed `input_tokens`. Forked rollout replay is excluded so copied historical tokens are not double counted. Manual main-thread forks become live when `task_started.started_at` and, when needed, the UUIDv7 turn timestamp prove that the task began after the fork boundary. Subagent forks retain the addressed-child handshake proof.

`UsageEvent` is the normalized unit: timestamp, rollout and conversation identity, thread classification, agent metadata, model, input, cache input, output and reasoning output. `usage-core.ts` applies filters, facet calculation, grouping and cost aggregation after SQLite returns the time-scoped events.

## SQLite ledger

`usage-store.ts` owns `usage.sqlite`, with schema version in `PRAGMA user_version`. The schema stores `rollouts`, `usage_events`, `source_files`, `collector_runs`, `collector_diagnostics` and `collector_state`. Source and usage writes run in `BEGIN IMMEDIATE` transactions. The application ledger uses foreign keys, WAL mode and a 5 second SQLite busy timeout. On clean shutdown it performs `wal_checkpoint(TRUNCATE)` before closing the database.

All application modes store the default ledger at `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`, unless `CODEX_USAGE_DATA_DIR` is set. This keeps the ledger independent of executable, portable-copy, and NSIS installation paths.
