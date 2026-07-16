# Complete workspace migration

## Complete cross-drive move

This procedure moves the complete local project from `C:\Projects\codex-usage-desktop` to `D:\Projects\codex-usage-desktop`. It preserves Git history, tracked and untracked files, and ignored local material that exists under the source tree. This is deliberately not a lean clone.

The copy scope includes `.git`, `node_modules`, `dist`, every `release*` directory, `outputs`, `work`, `task-memory`, `.ven`, `__pycache__`, local `codex-usage-data` directories, `.env` and `.env.*` files, and other local scratch files below the source root. `%USERPROFILE%\.codex\sessions`, `archived_sessions` and `agents` are not project files and must remain in place. The application observes `sessions` and `archived_sessions` read-only; `agents` remains inside the protected no-write boundary but is not read or watched.

The copied tree can contain sensitive local data: SQLite ledger records, CSV exports, `.env` secrets, package cache material and task scratch state. Restrict access to `D:\Projects\codex-usage-desktop` before copying, and do not place it in a shared or synchronized location without an explicit data-handling decision.

## Preconditions

1. Exit the tray application from its `Exit` menu. Close Electron development runs, package builds, terminals and editors that can write below the source tree.
2. Record the source state while it is quiescent:

   ```powershell
   Set-Location 'C:\Projects\codex-usage-desktop'
   git status --short
   git branch --show-current
   git rev-parse HEAD
   ```

3. In the desktop app, note the resolved ledger path. The default ledger lives at `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`; an external `CODEX_USAGE_DATA_DIR` ledger needs the separate handling described below.
4. Confirm that `D:` is available, `D:\Projects` has enough capacity for the entire source tree, and `D:\Projects\codex-usage-desktop` does not already exist.
5. Do not use `git clean`, `git reset --hard`, forced checkout or any operation that discards uncommitted work.

## Copy and verify

Run PowerShell after the application has exited. This copies all source-tree contents, including ignored files, across drives. It does not delete the source.

```powershell
$source = [IO.Path]::GetFullPath('C:\Projects\codex-usage-desktop').TrimEnd('\\')
$target = [IO.Path]::GetFullPath('D:\Projects\codex-usage-desktop').TrimEnd('\\')

if ($source -eq $target) {
  throw 'Source and target must differ.'
}
if (Test-Path -LiteralPath $target) {
  throw "Target already exists: $target"
}

New-Item -ItemType Directory -Force -Path 'D:\Projects' | Out-Null
robocopy $source $target /E /COPY:DAT /DCOPY:DAT /R:1 /W:1
if ($LASTEXITCODE -gt 7) {
  throw "robocopy failed with exit code $LASTEXITCODE"
}
```

Verify the byte content before building or starting anything in the target. The manifest includes hidden `.git` files and ignored files.

```powershell
function Get-TreeManifest([string]$root) {
  $normalizedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\\')
  Get-ChildItem -LiteralPath $normalizedRoot -Force -Recurse -File |
    ForEach-Object {
      $relative = $_.FullName.Substring($normalizedRoot.Length).TrimStart('\\')
      $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
      '{0}|{1}|{2}' -f $relative, $_.Length, $hash
    } |
    Sort-Object
}

$difference = Compare-Object (Get-TreeManifest $source) (Get-TreeManifest $target)
if ($null -ne $difference) {
  $difference | Format-Table -AutoSize
  throw 'Copy verification failed. Keep the source and recopy after resolving the difference.'
}
```

Then validate the target repository and rebuild there. These commands may update target build output after the copy verification; that is expected.

```powershell
Set-Location 'D:\Projects\codex-usage-desktop'
git status --short
git branch --show-current
git rev-parse HEAD
npm run typecheck
npm test
npm run package:portable
```

Compare the three Git outputs with the values recorded from the source. A different status, branch or revision means the relocation is incomplete or the source changed during copying; stop and recopy rather than reconstructing changes manually.

## Ledger and environment handling

The source-tree copy already includes every co-located Portable ledger, `usage.sqlite-wal` and `usage.sqlite-shm` file. Do not copy a live ledger: the tray application must be exited first.

If the noted ledger path is outside the source tree because `CODEX_USAGE_DATA_DIR` is set or the application uses the default LocalAppData location, copy `usage.sqlite` together with its WAL companion files after the application exits. To move that ledger into the target, use a target-owned directory such as `D:\Projects\codex-usage-desktop\data`, verify it with the same manifest approach, and configure the target launch environment to set `CODEX_USAGE_DATA_DIR` to that directory before launching. Do not copy only `usage.sqlite` while WAL companion files exist.

`.env` and `.env.*` files are included by the full-tree copy. Review their absolute paths, credentials and machine-specific settings before running the target. Preserve an original copy until the target has completed validation.

## Cutover, smoke and source deletion

1. Start only the target application after the old tray instance has exited. Update any shortcut that still points at the old launcher.
2. Run the release checks in [testing.md](testing.md). The real Electron smoke is read-only with respect to Codex sources: query, filter, tray, export and data-directory checks are allowed; editing rollout JSONL or `agents/*.toml` is not.
3. Confirm the dashboard reports the intended ledger path and that it is outside `%USERPROFILE%\.codex`.
4. Keep the original source tree as rollback until the target build, application startup and read-only smoke all succeed.
5. Only after that acceptance and an explicit decision that the rollback window has ended, remove the old tree. This is irreversible and must be run manually, never as part of the copy script:

   ```powershell
   Remove-Item -LiteralPath 'C:\Projects\codex-usage-desktop' -Recurse -Force
   ```

## Rollback

Before source deletion, rollback means exiting the target app and relaunching the original app with its original ledger. Keep both trees unchanged until the target is accepted.

After source deletion, do not alter `%USERPROFILE%\.codex`. Restore the original project location by copying the verified target tree back to a new or empty original path, then restore or point `CODEX_USAGE_DATA_DIR` at the matching copied ledger. Retain the failed target tree for diagnosis rather than overwriting it.

## Alternative not selected: lean migration

A lean migration would create a fresh clone at `D:\Projects\codex-usage-desktop`, run `npm ci`, rebuild `dist` and `release`, and intentionally omit ignored dependencies, ledgers, environment files and scratch state. It is useful for a clean development checkout, but it is not the selected procedure because it does not preserve the complete local working environment.
