# Operations

## Start and lifecycle

开发运行从 `CodexUsageDesktop.sln` 启动 `CodexUsage.App`.正式用户运行 NSIS setup EXE,将 unpackaged、self-contained 的 WinUI 3 应用安装到 `%ProgramFiles%\Codex Usage Desktop`.这是全用户安装,安装、升级和卸载需要 UAC.

应用维持单一 instance.普通的第二次启动激活现有 dashboard.HKCU Run launch 可以直接进入 notification area.关闭 dashboard 会隐藏 window 并保留 tray collector;使用 tray `Exit` 执行 clean shutdown.常用 tray command 包括 `Open dashboard`、collector status 和 `Exit`.

开机自启动由当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry 管理.应用和安装器可启用或禁用同一 entry;开机启动不会使用独立 collector path.

## Collector schedule

startup sequence 先打开应用 ledger,注册 source watcher,确认 watcher ready,再运行 full inventory,避免 initial inventory 与 watch registration 之间出现 observation gap.

运行期间:

- FileSystemWatcher callback 只规范化 source path 并写入 channel.
- event 使用 2 second debounce、path deduplication 和最多 16 path 的 bounded batch.
- failed path 自动进行最多五级指数 backoff;后续 watch event 复用既有失败状态并在允许时间重试,不会把 backoff 清零.相同 source/error diagnostic 被节流,成功后才清除 retry state.
- full inventory 每 5 分钟兜底运行.
- directory enumeration、source processing、JSONL parsing 和 hashing 被切成小片,在片之间 cooperative yield.
- actor 串行化 collector state mutation,query 使用一致 ledger snapshot.

slice 是降低 CPU 峰值的 duty-cycle 策略,不是严格 latency 或 memory guarantee.普通 DOM record 有 1 MiB 上限;超限完整 record 使用低分配 streaming reader 完整验证语法和安全分类.存在已安全跳过的 opaque record 时 phase 为 `partial`,而不是宣称 inventory 完全成功.

窗口失焦后的 Efficiency Mode / process priority 调整仍 deferred,当前运行与验收不得将其描述为已完成.

## Observation coverage

startup 比较上一次 collector completion/heartbeat 与当前 start time.存在 gap 时,status 记录 UTC interval.gap 代表当时没有持续观察 file event,不代表仍存在的 source 无法在后续 reconciliation 中补齐.

若 rollout 在应用首次观察前已经删除,本地 ledger 无法重建缺失 event.为了保持连续历史,应允许应用驻留 tray,并保持每 5 分钟 reconciliation 正常运行.

## Ledger and backup

```text
Default:  %LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
Override: %CODEX_USAGE_DATA_DIR%\usage.sqlite
```

override path 如果 resolve 到 protected Codex source tree 会被拒绝.复制或恢复 ledger 前退出应用,并一起处理 `usage.sqlite-wal` 与 `usage.sqlite-shm`.不要覆盖 live ledger.

旧 release ledger 的一次性迁移:

```powershell
./scripts/migrate-usage-ledger.ps1 -WhatIf
./scripts/migrate-usage-ledger.ps1
```

脚本显示 source、destination 和 backup plan,要求确认,逐文件验证 copy,并保留原 source.如果默认 destination 已存在,脚本拒绝 overwrite.

## Recovery and diagnosis

1. 查看 collector phase、retry、observation gap 和 last reconciliation.GUI 不展示 source conflict;详细信息保留在内部 diagnostic.
2. 仍为 `degraded` 时重启 watcher,并用 SQLite-compatible read-only tool 检查 `collector_diagnostics` 和 `collector_runs`.
3. 自动恢复仅接受双重稳定、metadata exact 且 semantic relation 为 `Equal` 或 `Extension` 的候选,并按 `Extension`、稳定 byte length、mtime、path 确定性选择.
4. unsafe、`Shorter`、`Diverged`、attribution 不一致、malformed 或 unstable input 保留最后有效 ledger,记录 degraded/diagnostic 并后台重试.不要编辑 Codex JSONL.
5. parser revision rebuild 逐 rollout transaction 执行;所有 required candidate 成功后才推进 revision marker.
6. `collector_diagnostics` 中的 `checkpoint-rehydrate-summary` 和 `checkpoint-inventory-io` 记录本地 checkpoint hit/miss、boundary bytes、full reconciliation bytes 与 append bytes.大量 invalidation 应先检查 source identity、mtime、boundary 和 parser revision,不要修改 JSONL.

## Install, upgrade and uninstall

生成 installer:

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.19 -AutoDetectDependencies
```

`-AutoDetectDependencies` 查找本机已有的 .NET 8 SDK、NSIS 3.x `makensis.exe` 和 7-Zip Extra 的 `7za.exe`、`7zr.exe`;脚本不会下载或安装工具.若 7-Zip Extra 位于非标准目录,追加 `-DependencySearchDirectory 'D:\tools\7-Zip'`;多个目录使用逗号数组或分号分隔.脚本生成 self-contained x64 publish,用 7-Zip LZMA2 生成并校验 payload archive,再由 NSIS 3.x 输出 `release\winui-installer\codex-usage-desktop-setup-0.3.19-x64.exe`.每次 build 使用唯一 pending EXE;只有 `makensis` 成功且 pending EXE 存在并非空后,才会在同卷原子替换正式 setup.失败不会覆盖现有正式产物.同一 workspace 的 installer build 必须串行执行.安装器检测已运行的 Codex Usage Desktop process,使用 `taskkill /F /IM` 终止同名 process,不会递归杀掉启动它的安装器,确认退出后才替换程序文件;无法确认退出时安装失败.

setup 在替换当前 WinUI payload 前结束运行中的 process,并保留或更新 HKCU Run entry 的选择.检测到更高版本时拒绝降级,相同版本可执行 repair install.安装、升级和卸载不会删除 `%LOCALAPPDATA%\Codex Usage Desktop` 下的 ledger.

卸载器删除 Program Files payload、快捷方式、HKCU Run entry 和 uninstall registration,默认不删除 `%LOCALAPPDATA%\Codex Usage Desktop` 或 ledger.需要清理数据时,应在确认不再需要审计历史且应用已退出后单独处理.

应用在启动后立即请求一次固定 GitHub Releases metadata,随后每 6 小时检查一次;用户也可点击“检查更新”并立即看到当前版本、可用更新或失败信息.自动检查只读取 metadata,不会弹窗、下载、安装或上传数据.检查严格校验 owner/repository、SemVer tag、唯一 x64 asset、下载 URL、asset size 和 GitHub SHA-256 digest.下载完成后,用户须点击“运行安装器”并在警示 dialog 中确认.NSIS 安装器会结束当前应用和 collector process;应用在 Process.Start 前重新校验 LocalAppData installer SHA-256 和 metadata generation.验证或启动失败时应用保持运行.
