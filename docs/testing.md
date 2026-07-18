# Testing and smoke matrix

## Automated verification

Run the following from the repository root:

```powershell
npm run typecheck
npm test
npm run package:portable
git diff --check
```

`typecheck` verifies the TypeScript project with no emission. `test` first builds `dist`, then runs Node's test runner over `dist/*.test.js`. `package:portable` rebuilds the renderer static assets and produces the Windows Portable package. `git diff --check` detects whitespace errors in modified tracked files.

Current test sources cover these boundaries:

| Test source | Focus |
| --- | --- |
| `usage-core.test.ts` | filtering, grouping, model category, observed role, cost components and CSV output |
| `rollout-parser.test.ts` | JSONL parsing, stable partial lines, token validation, adjacent complete cumulative snapshot deduplication, manual main-thread fork replay exclusion and subagent fork replay exclusion |
| `collector-worker.test.ts` | reconciliation, active/archive canonical promotion, incremental ingestion, parser revision rebuild, watcher behavior and diagnostics |
| `usage-store.test.ts` | schema, transactional event/source operations, canonical source state, diagnostics and collector state |
| `write-boundary.test.ts` | source output boundary and resolved-path protection |

Test fixtures that append, move, delete or edit rollout and agent configuration files must use temporary directories and test databases. Do not point automated tests at the user's real `%USERPROFILE%\.codex` directories or production `usage.sqlite`.

## Desktop smoke matrix

Run this matrix against a disposable copy of the packaged application or a development run with a non-production application ledger. The real Electron smoke is observation-only: it may query, filter, use the tray, export outside protected directories and inspect the resolved data directory, but it must not mutate any Codex source file.

| Area | Action | Expected result |
| --- | --- | --- |
| Startup | Launch once, then launch again | A single instance remains; the second launch focuses the first |
| Tray lifecycle | Close the dashboard, then use tray Open dashboard and Exit | Closing hides the window, collection remains active, Exit stops cleanly |
| Baseline | Observe the existing dashboard after initial inventory | Initial inventory completes and known file count appears without changing any source |
| Status and query | Inspect collector status and run an immediate sync, then query a known time range | Status, known file count and query results remain available; no source file is changed |
| Filters | Toggle model and subject selections independently, change quick range and search an agent path | Results update live and one facet does not remove the other facet's available options |
| Cost | Check an event where output includes reasoning output | Reasoning and other output sum to output cost exactly once |
| Export | Export a filtered CSV outside `%USERPROFILE%\.codex` | CSV is written with selected event count; a protected destination is rejected |
| Data safety | Monitor source-directory metadata while collecting | No source lock, write, rename, deletion, truncation or repair occurs |
| Responsive UI | Inspect at minimum window size, default size, wide landscape, short height and high-DPI scaling | Controls reflow without clipping, dashboard scrolls normally and agent table keeps its own scroll region |

## Manual UI checks

The current window contract is `minWidth: 980` and `minHeight: 640`, with a default size of `1280 x 860`. Check the time range, model and subject controls after changing dimensions because these areas have container-query reflow rules. In particular, verify that the status/summary cards do not form a visually stranded final row, and that the bounded agent/thread table preserves a sticky header while its own body scrolls.

Use actual Electron window smoke testing for tray behavior and native save dialogs. Browser-only CSS inspection is useful for viewport coverage but cannot validate the tray, single-instance lock, data directory resolution or native CSV selection.

Do not append to, partially write, move, delete or rename a rollout JSONL during a real Electron smoke. Do not create, remove or edit `%USERPROFILE%\.codex\agents\*.toml`. Those mutation scenarios belong only in temporary-directory automated tests, never in a manual or packaged-app smoke.

## Release acceptance

Before replacing a user-facing executable:

1. Run the automated verification commands.
2. Launch the newly packaged executable with a non-production or backed-up application ledger.
3. Verify startup, tray hide/open/exit, an immediate sync, a filtered query and one CSV export without modifying any Codex source.
4. Confirm the data directory resolves outside the three protected Codex source directories.
5. Record the executable SHA-256 and keep the prior executable until the new build completes the smoke matrix.
