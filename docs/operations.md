# Operations

## Start and lifecycle

开发运行从 `CodexUsageDesktop.sln` 启动 `CodexUsage.App`.正式用户运行 NSIS setup EXE,将 unpackaged、self-contained 的 WinUI 3 应用安装到 `%ProgramFiles%\Codex Usage Desktop`.这是全用户安装,安装、升级和卸载需要 UAC.

应用维持单一 instance.普通的第二次启动激活现有 dashboard.HKCU Run launch 可以直接进入 notification area.关闭 dashboard 会隐藏 window 并保留 tray collector;使用 tray `Exit` 执行 clean shutdown.常用 tray command 包括 `Open dashboard`、`Sync now`、collector status 和 `Exit`.

开机自启动由当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry 管理.应用和安装器可启用或禁用同一 entry;开机启动不会使用独立 collector path.

## Collector schedule

startup sequence 先打开应用 ledger,注册 source watcher,确认 watcher ready,再运行 full inventory,避免 initial inventory 与 watch registration 之间出现 observation gap.

运行期间:

- FileSystemWatcher callback 只规范化 source path 并写入 channel.
- event 使用 2 second debounce、path deduplication 和最多 16 path 的 bounded batch.
- failed path 最多进行五次 backoff retry;retry 或 conflict 未清空时状态保持 `degraded`.
- full inventory 每 5 分钟兜底运行.
- directory enumeration、source processing、JSONL parsing 和 hashing 被切成小片,在片之间 cooperative yield.
- startup 和 `Sync now` 使用相同 inventory path;已有 inventory 时,手动同步最多追加一次 trailing run.
- actor 串行化 collector state mutation,query 使用一致 ledger snapshot.

slice 是降低 CPU 峰值的 duty-cycle 策略,不是严格 latency 或 memory guarantee.极大的单条 JSON record 仍可能产生不可抢占的短暂 parse work.

进程 startup 以 best effort 开启 Windows Efficiency Mode,包括 EcoQoS 和 below-normal priority.启用结果显示在 collector health.失败不会停止采集,也不会改变数据正确性.

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

1. 查看 collector phase、conflict、retry、observation gap、Efficiency Mode 和 last reconciliation.
2. 使用 `Sync now` 请求一次 full inventory.
3. 仍为 `degraded` 时重启 watcher,并用 SQLite-compatible read-only tool 检查 `collector_diagnostics` 和 `collector_runs`.
4. 同路径 rewrite 只有在 `rolloutId` 不变、完整 parse 和两次 stable snapshot 一致时自动恢复.
5. Cross-path divergence、ID change、malformed input 和 unstable snapshot 保持 conflict.保留 source 与 ledger 供诊断,不要编辑 Codex JSONL.
6. parser revision rebuild 逐 rollout transaction 执行;所有 required candidate 成功后才推进 revision marker.

CSV export 是明确 user action.选定 snapshot 只会写入通过 protected-path check 的目标,formula-leading text 会被 neutralize.

## Install, upgrade and uninstall

生成 installer:

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.0
```

脚本生成 self-contained x64 publish,再由 NSIS 3.x 输出 `release\winui-installer\codex-usage-desktop-setup-0.3.0-x64.exe`.安装或升级前必须通过 tray `Exit` 正常退出旧版或新版;安装器不会强制结束应用.

setup 支持从旧 Electron 0.2.6 原位升级.覆盖前会把 `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite` 及存在的 WAL/SHM 备份到 `ledger-backups\preinstall-*`,然后移除旧 Electron payload.旧 Startup shortcut 会迁移为 HKCU Run entry,安装页允许保留或改变该选择.检测到更高版本时拒绝降级,相同版本可执行 repair install.

卸载器删除 Program Files payload、快捷方式、HKCU Run entry 和 uninstall registration,默认不删除 `%LOCALAPPDATA%\Codex Usage Desktop` 或 ledger.需要清理数据时,应在确认不再需要审计历史且应用已退出后单独处理.

当前 setup EXE 尚未 Authenticode 签名,Windows 可能显示 `Unknown Publisher` 或 SmartScreen.正式发布前需要可信 code-signing certificate.应用内 release feed 尚未配置,不会联网检查或自动下载更新;当前升级方式是运行版本号更高的 setup EXE.
