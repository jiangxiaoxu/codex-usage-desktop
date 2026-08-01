# Complete workspace migration

## Complete cross-drive move

本流程将完整 local workspace 从 `C:\Projects\codex-usage-desktop` 复制到 `D:\Projects\codex-usage-desktop`,保留 Git history、tracked/untracked files 和 ignored local material.它不是 lean clone.

copy scope 包括 `.git`、`bin`、`obj`、`AppPackages`、`dist`、`release*`、`outputs`、`work`、`task-memory`、`.ven`、local ledger、`.env` 和其他 source-tree scratch.用户的 `%USERPROFILE%\.codex\sessions`、`archived_sessions` 和 `agents` 不是 project file,必须留在原处.

完整 workspace 可能包含 SQLite ledger、CSV export、environment secret、package cache 和 task scratch.目标目录不应位于未经明确批准的 shared/synchronized location.

## Preconditions

1. 从 tray `Exit` 退出应用,关闭 build、terminal 和可能写入 workspace 的 editor.
2. 记录 quiescent source state:

   ```powershell
   Set-Location 'C:\Projects\codex-usage-desktop'
   git status --short
   git branch --show-current
   git rev-parse HEAD
   ```

3. 记录 dashboard 显示的 ledger path.默认是 `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite`;`CODEX_USAGE_DATA_DIR` 可能指向其他位置.
4. 确认目标 drive 容量足够且 `D:\Projects\codex-usage-desktop` 不存在.
5. 不使用会丢弃 uncommitted work 的 clean、hard reset 或 forced checkout.

## Copy and verify

应用退出后运行:

```powershell
$source = [IO.Path]::GetFullPath('C:\Projects\codex-usage-desktop').TrimEnd('\\')
$target = [IO.Path]::GetFullPath('D:\Projects\codex-usage-desktop').TrimEnd('\\')

if ($source -eq $target) { throw 'Source and target must differ.' }
if (Test-Path -LiteralPath $target) { throw "Target already exists: $target" }

New-Item -ItemType Directory -Force -Path 'D:\Projects' | Out-Null
robocopy $source $target /E /COPY:DAT /DCOPY:DAT /R:1 /W:1
if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit code $LASTEXITCODE" }
```

在 target build 前比较 byte manifest:

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
  throw 'Copy verification failed. Keep the source and resolve the difference.'
}
```

然后验证 target:

```powershell
Set-Location 'D:\Projects\codex-usage-desktop'
git status --short
git branch --show-current
git rev-parse HEAD
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.3
```

Git status、branch 和 revision 必须与 source 记录一致.差异意味着 copy 不完整或 source 在 copy 期间发生变化;应停止并重新验证,不要手工猜测缺失内容.installer 输出应为 `release\winui-installer\codex-usage-desktop-setup-0.3.3-x64.exe`;缺少 NSIS 3.x 或 `makensis.exe` 时,build script 会明确失败.

## Ledger and environment

不要复制 live ledger.应用退出后,将 `usage.sqlite` 与存在的 `-wal`、`-shm` companion 作为一个集合处理.如果使用 `CODEX_USAGE_DATA_DIR`,新路径必须是 protected Codex tree 以外的绝对可写目录.

`.env` 和其他 machine-local file 会被 full-tree copy.运行 target 前审阅其中 absolute path 和 secret,保留 original copy 直到验收完成.

若 workspace 中存在旧 `release\codex-usage-data`,可在 target 运行一次 non-destructive migration:

```powershell
./scripts/migrate-usage-ledger.ps1 -WhatIf
./scripts/migrate-usage-ledger.ps1
```

该工具固定写入 `%LOCALAPPDATA%\Codex Usage Desktop`,执行 path boundary、确认、backup 和 hash verification,且不删除旧 source.

## Cutover and rollback

1. original app 完全退出后只启动 target app.
2. 执行 [testing.md](testing.md) 中的 native smoke,真实 Codex source 保持 read-only.
3. 确认 dashboard 显示预期 ledger path,且路径不在 `%USERPROFILE%\.codex` 下.
4. target 的 build、startup、tray、query、export 和 NSIS setup smoke 全部成功前保留 original tree.若验证从 Electron 0.2.6 升级,使用已备份 ledger 的 disposable install,确认 WinUI 3 原位替换、自启动迁移及 LocalAppData ledger 连续.
5. rollback window 结束后,source tree 删除必须由用户单独明确决定,copy/migration script 不执行删除.

rollback 时退出 target,恢复 original workspace 或其 verified copy,并指向 matching ledger.不要修改 `%USERPROFILE%\.codex` 作为 rollback 手段.
