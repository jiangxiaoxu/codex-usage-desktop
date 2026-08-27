# Codex Usage Desktop WinUI 3 shell

This directory contains the native WinUI 3 application shell. It does not use WebView2. `MainWindow` is the composition root for the application service and collector; the ViewModel only consumes the application contract and applies results through the UI dispatcher.

The collector reuses `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`. `CODEX_USAGE_DATA_DIR` retains the existing directory override contract, and `ProtectedPathPolicy` rejects any database path inside the protected Codex source directories.

## Design contract

- The minimum AppWindow size is 900x720 effective pixels.
- `VisualStateManager` uses Compact below 1000,Medium from 1000 to 1279,and Wide at 1280 or above. The detail tables stack below 1440 and display side by side at or above 1440.
- `AuditFilterContent` is a full-width filter surface in the page scroll owner. It presents time,model,subject and main-thread filters without a persistent filter pane.
- The main-thread filter is an `AutoSuggestBox`: it offers at most 20 recent main threads ordered by activity and shows `project name - ID prefix - title`. The project name is the main session `session_meta.cwd` directory name and the title is the authoritative `thread_name` in `session_index.jsonl`. It accepts a complete UUIDv7 session ID, normalizes valid input, shows a red validation state for nonempty invalid input without clearing the applied filter, and has a dedicated clear action. Filtering uses an exact main `ConversationId` as the root and includes all descendant-agent events.
- Tables use native WinUI controls and keep vertical scrolling at the page level.

## Build and packaging

```powershell
dotnet restore .\src\CodexUsage.App\CodexUsage.App.csproj
dotnet build .\src\CodexUsage.App\CodexUsage.App.csproj -c Debug -p:Platform=x64
```

The production application is unpackaged and self-contained. From the repository root, the supported installer build publishes the x64 payload with the repository's release properties and then compiles the NSIS definition:

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.21 -AutoDetectDependencies
```

`-AutoDetectDependencies` finds the locally installed .NET 8 SDK, NSIS 3.x `makensis.exe`, and 7-Zip Extra `7za.exe` and `7zr.exe`; it does not download or install build tools. If 7-Zip Extra is in a non-standard directory, add `-DependencySearchDirectory 'D:\tools\7-Zip'`; pass multiple directories as an array separated by commas or as a semicolon-delimited string. It creates and validates a 7-Zip LZMA2 payload before compiling the NSIS installer. The result is `release\winui-installer\codex-usage-desktop-setup-0.3.21-x64.exe`. It is an all-users installer under `%ProgramFiles%` and therefore requires UAC. It stops the running application before replacing the current WinUI payload. Uninstalling the WinUI payload does not remove the LocalAppData ledger by default.

The SHA-256-only experimental GitHub Release metadata check runs at startup and every six hours; it requires a strict repository, SemVer tag, one x64 installer asset and a matching GitHub digest. Before launch, the user confirms a warning and the application rechecks both the local SHA-256 and current update generation; NSIS then closes the application and collector.

`Assets/app-logo.jpg` is currently linked to the existing dashboard preview as a development placeholder. Replace it with final application and installer artwork before distribution.
