# Changelog

All notable changes are documented in this file. Versions use Semantic Versioning.

## [Unreleased]

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
