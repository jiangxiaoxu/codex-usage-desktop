# Codex Usage Desktop WinUI 3 shell

This directory contains the native WinUI 3 application shell. It does not use WebView2. `MainWindow` is the composition root for the application service and collector; the ViewModel only consumes the application contract and applies results through the UI dispatcher.

The collector reuses `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`. `CODEX_USAGE_DATA_DIR` retains the existing directory override contract, and `ProtectedPathPolicy` rejects any database path inside the protected Codex source directories.

## Design contract

- Figma Page 2 desktop target: node `36:2`.
- Figma Page 2 compact target: node `36:227`.
- Figma Page 2 implementation contract: node `36:304`.
- `VisualStateManager` switches at 960 effective pixels.
- The minimum AppWindow size is 720x560.
- Wide mode uses a persistent 284px filter pane. Compact mode exposes the filters through an `Expander` and keeps only Sync as a primary command.
- Tables use a virtualized native `ListView` until a WinUI 3-compatible DataGrid dependency is selected.

## Build and packaging

```powershell
dotnet restore .\src\CodexUsage.App\CodexUsage.App.csproj
dotnet build .\src\CodexUsage.App\CodexUsage.App.csproj -c Debug -p:Platform=x64
```

The production application is unpackaged and self-contained. The supported installer build publishes the x64 payload with the repository's release properties and then compiles the NSIS definition:

```powershell
$sevenZip = 'C:\Tools\7-Zip\7za.exe'
$sevenZipRuntime = 'C:\Tools\7-Zip\7zr.exe'
.\scripts\build-installer.ps1 -Version 0.3.3 -SevenZipPath $sevenZip -SevenZipRuntimePath $sevenZipRuntime
```

The build requires local 7-Zip Extra `7za.exe` and `7zr.exe`; it does not download build tools. It creates and validates a 7-Zip LZMA2 payload before compiling the NSIS installer. The result is `release\winui-installer\codex-usage-desktop-setup-0.3.3-x64.exe`. It is an all-users installer under `%ProgramFiles%` and therefore requires UAC. It can replace the legacy Electron 0.2.6 installation in place, invokes the legacy uninstaller without creating a ledger backup, and migrates the startup choice to HKCU Run. The legacy uninstaller may remove the LocalAppData ledger; uninstalling the WinUI payload does not remove it by default.

The setup EXE is currently unsigned and can show `Unknown Publisher` or SmartScreen. The SHA-256-only experimental GitHub Release metadata check runs at startup and every six hours; it requires a strict repository, SemVer tag, one x64 installer asset and a matching GitHub digest. Before launch, the user confirms a warning and the application rechecks both the local SHA-256 and current update generation; NSIS then closes the application and collector. Authenticode signing is still required before public distribution.

`Assets/app-logo.jpg` is currently linked to the existing dashboard preview as a development placeholder. Replace it with final application and installer artwork before distribution.
