# Changelog

All notable changes are documented in this file. Versions use Semantic Versioning.

## [Unreleased]

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
