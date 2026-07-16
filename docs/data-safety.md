# Data safety and read-only boundary

## Protected Codex directories

The following paths form the strict no-write boundary:

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`
- `%USERPROFILE%\.codex\agents`

The application observes `sessions` and `archived_sessions` read-only. It must never open any file below these paths for writing, lock it, rename it, delete it, truncate it, repair it or create an output beneath it. The `agents` directory remains protected by the same no-write boundary, but the collector does not read or watch it.

## How the boundary is enforced

- `main.ts` validates the data directory before and after it creates it. `write-boundary.ts` resolves real existing ancestors and rejects a candidate located inside any protected directory. This prevents a configured ledger directory from being a protected directory itself, including through an existing symlink or junction ancestor.
- The CSV save path is checked by the same boundary validator before the worker writes it.
- `collector-worker.ts` reads rollout files with `stat`, `readFile` and `open(filePath, "r")`. `chokidar` watches the rollout source directories but does not change their contents.
- The worker opens and writes only the application SQLite ledger and a user-selected CSV path that has passed the protected-directory check.
- `src/write-boundary.test.ts` currently covers direct protected-path rejection for fixture `sessions` and `agents` directories, plus a junction-mediated `agents` path. It does not yet provide standalone fixture coverage for `archived_sessions` or Windows case-variant paths.

There is no `flock`, Windows file lock, exclusive-open flag, rename-based handoff or file-repair behavior for Codex source paths. A source that changes during a stat/read/stat window is not accepted as stable; the worker records a retry diagnostic and waits for later reconciliation rather than modifying the source.

## Read behavior and consistency

The watcher observes `add`, `change` and `unlink` events for `sessions` and `archived_sessions`. `chokidar` first waits for its 2 second `awaitWriteFinish` stability threshold. The worker then applies its own 2 second reconciliation debounce, so a quiet change normally reaches reconciliation after two delay phases. A complete inventory runs at startup and every 10 minutes, which covers missed watcher events.

For an append-only source, the worker remembers a byte offset and a SHA-256 boundary hash. It reads only the appended stable bytes if the prior prefix still matches. If the source became shorter, its prefix hash changed, the parser needs to resolve a prior unknown model, or canonicalization requires it, the worker reparses the current source. No source mutation is used to establish consistency.

## Data retained locally

The permanent ledger contains rollout metadata, token event fields, source-file metadata, collector run status, diagnostics and parser revision state. It may contain local agent paths, nicknames, conversation IDs and rollout IDs. Treat `usage.sqlite`, exported CSV files and packaged application logs as local usage data.

Archiving or later deleting a Codex rollout file does not delete previously collected events. The ledger marks a source absent, preserving history. This is intentional for accounting continuity and means deletion of a source file is not a deletion request for the ledger.

## Operational safeguards

- Keep the application data directory outside `%USERPROFILE%\.codex\sessions`, `archived_sessions` and `agents`.
- Close the application before copying or restoring `usage.sqlite`, `usage.sqlite-wal` or `usage.sqlite-shm`. A clean exit checkpoints WAL; copying while running can otherwise omit recent WAL frames.
- Do not use a network-synced location for the live SQLite ledger unless its synchronization behavior is known to be safe for SQLite WAL files.
- If source conflicts or degraded status appear, preserve the ledger and source files first. Use `Sync now` or restart to retry observation; do not edit rollout JSONL to make it parse.
- The application has no remote audit API integration. Its source of truth is local Codex rollout history plus its own ledger.
