# Changelog

All notable changes are documented in this file. Versions use Semantic Versioning.

## [0.3.23] - 2026-08-14

- Added compact token counts for every cost-composition detail item and row total, with a unified `token count · percentage` display order.

## [0.3.22] - 2026-08-14

- Changed cost details to stable, click-to-expand two-line cards. Refreshes update their content without closing them automatically.

## [0.3.21] - 2026-08-14

- Distributed the cost-composition legend evenly across wide layouts and centered it in narrow layouts.

## [0.3.20] - 2026-08-14

- Unified the color-indicated cost-composition legend across total, by-model, and by-execution-subject views, replacing the former plain-text details.

## [0.3.19] - 2026-08-14

- Reworked the cost-composition legend with color indicators, labels, percentages, and responsive spacing.

## [0.3.18] - 2026-08-13

- Removed dollar amounts from model and execution-subject cost-composition rows; they now show only their shares of the current filtered total. The overall total-cost amount remains visible.

## [0.3.17] - 2026-08-13

- Restored the dashboard's four-part filter layout to avoid an overly compressed control strip while retaining the compact cost-composition dashboard.

## [0.3.16] - 2026-08-13

- Replaced the dashboard's wide detail tables with compact model and execution-subject cost-composition cards.
- Added a standalone overall cost-composition row with persistent four-category percentages.
- Showed compact composition percentages for model and execution-subject rows on whole-bar hover or keyboard focus, including an indented subagent hierarchy and synthetic subagent total.
- Made the card layout responsive at 1000 DIP available dashboard width and removed the voice-conversation presentation row.

## [0.3.15] - 2026-08-11

- Stabilized main-thread filtering across refreshes and rapid query changes; recent choices retain full `ConversationId` identity.
- Added visible invalid UUIDv7 validation and normalization to the main-thread input without silently clearing the applied filter.
- Expanded visible main-thread ID prefixes to 12 characters so recent threads remain distinguishable.
- Made pointer presses outside the main-thread input immediately close suggestions and move focus away, including custom title-bar input.

## [0.3.14] - 2026-08-08

- Added a dedicated thread-count column to the "By thread type and role" table. Main threads are counted by unique `ConversationId`; subagents and unknown threads are counted by unique `RolloutId`, all within the active filters.

## [0.3.13] - 2026-08-08

- Replaced path search with an exact main-thread filter that accepts a complete UUIDv7 session ID,includes all descendant-agent usage,and has a dedicated clear action.
- Added a recent-main-thread dropdown with up to 20 choices ordered by activity.The labels use `project name - ID prefix - title`,where the project name comes from the main session `session_meta.cwd` directory name and the title comes from the authoritative `thread_name` in `session_index.jsonl`.

## [0.3.9] - 2026-08-06

- Unified the command bar's font, font size, and vertical alignment.
- Removed the manual sync and CSV export commands from the command bar.
- Added clear user feedback after a manual update check, including confirmation when the installed version is current.
- Distinguished routine data updates from actionable retry and degraded collector states.

## [0.3.8] - 2026-08-06

- Improved diagnostics layout with a wider value column, 32 px column spacing, and wrapping for long values.

## [0.3.7] - 2026-08-04

- Widened the diagnostics panel's first column to keep its labels readable.

## [0.3.6] - 2026-08-01

- Made the collector status header use concise user-facing descriptions instead of technical phase and reconciliation text.
- Expanded the diagnostics panel to use the available window width while preserving responsive horizontal scrolling.

## [0.3.5] - 2026-08-01

- Installer process shutdown no longer recursively kills its own in-app update launcher.

## [0.3.4] - 2026-08-01

- Installer upgrades now directly replace the legacy payload without creating or restoring a ledger backup.
- Self-contained publish now removes unused Windows AI/ML native runtime assets, including ONNX Runtime and DirectML.
- Installer builds now create and validate a 7-Zip LZMA2 payload, embed it with the standalone `7zr.exe` extractor, and avoid NSIS double compression.
- The installer finish-page run option is now checked by default for new installs.

## [0.3.3] - 2026-08-01

- Default the installer start-at-sign-in option to checked for new installations while preserving the existing choice during upgrades.
- Simplified the cost composition legend to show percentages without repeating prices.
- Added update download progress feedback to the desktop command bar.

## [0.3.2] - 2026-08-01

- Enlarged the cost composition price total, legend labels, and color swatches while preserving the compact responsive layout and safe wrapping at the minimum window width.
- Added the current software version to the native title bar and ignored Python cache artifacts in the repository.

## [0.3.1] - 2026-08-01

- Added SHA-256-only GitHub Release checks and downloads for the unsigned experimental update channel. Metadata is checked at startup and every six hours; installer launch requires explicit confirmation plus a final local SHA-256 and generation check.

- Added safe automatic ledger recovery for stable same-source canonical rollout rewrites, while preserving conflicts for ambiguous changes and keeping Codex JSONL read-only.
- Reduced collector CPU spikes with deduplicated watcher-path batches, bounded retries, non-reentrant sliced inventories and a guaranteed fresh trailing inventory for manual sync.
- Added cooperative 256 KiB or 256-record parser yields and chunked full-content hashing. Full snapshots still retain complete source buffers, and synchronous parsing of one oversized JSON record remains non-preemptible.

## [0.2.6] - 2026-07-27

- Fixed dashboard scroll jumps caused by restoring dynamic filter focus during background refreshes.
- Fixed GPT realtime voice rollouts being classified as unknown threads, including automatic ledger reattribution for existing records.

## [0.2.5] - 2026-07-21

- Changed NSIS-installed Windows updates to download, verify, silently install, and restart from the dashboard instead of opening a GitHub Release page.

## [0.2.4] - 2026-07-20

- Changed the shortest continuous time-range anchor from one hour to 30 minutes.

## [0.2.3] - 2026-07-20

- Changed Windows Startup launches to open directly in the notification area instead of showing the dashboard.

## [0.2.2] - 2026-07-18

- Fixed manual main-thread fork accounting so replayed ancestor usage is excluded while post-fork usage is collected.
- Added conservative replay-boundary validation, incremental collector coverage and automatic parser revision rebuilding for existing rollouts.

## [0.2.1] - 2026-07-17

- Corrected Codex subscription API cost estimates to always use base token rates without a long-context premium.

## [0.2.0] - 2026-07-16

- Added automatic GitHub Release checks at startup and every four hours, with a user-initiated download-page link.

## [0.1.0] - 2026-07-16

- Added Windows portable and NSIS packaging workflows.
- Added LocalAppData ledger migration and Windows Startup shortcut controls.
