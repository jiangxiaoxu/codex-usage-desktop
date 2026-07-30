# Codex Usage Desktop Native GUI

GUI 是纯 WinUI 3 desktop application.XAML、WinUI controls 和类型化 C# view model 运行在同一 .NET 8 process 中.项目不使用 WebView2,不加载 remote content,collector 和 SQLite access 不进入 UI thread.

## 开发运行

前置环境:

- Windows 11.
- .NET 8 SDK.
- Visual Studio 2022 或更新版本,包含 .NET desktop、C++ desktop 和 Windows SDK build tools.
- 可由 NuGet restore 的 Microsoft Windows App SDK.

打开 `CodexUsageDesktop.sln`,选择 x64 并将 `CodexUsage.App` 设为 startup project.命令行验证:

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Debug --no-restore
dotnet test CodexUsageDesktop.sln -c Debug --no-build
```

## Native installer

需要 NSIS 3.x,并确保 `makensis.exe` 位于 PATH、标准安装目录或现有 electron-builder cache.生成 unpackaged、self-contained 的 x64 WinUI 3 应用和全用户 installer:

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.0
```

setup 输出位于 `release\winui-installer\codex-usage-desktop-setup-0.3.0-x64.exe`.它将 self-contained payload 安装到 `%ProgramFiles%\Codex Usage Desktop`,目标计算机不需要预装 .NET 或 Windows App SDK runtime.安装范围为全用户,安装、升级和卸载需要 UAC.

setup 可识别旧 Electron 0.2.6 安装并原位替换为 WinUI 3.覆盖前会备份 `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite` 及其 WAL/SHM,保留 ledger 和自启动选择,并清理旧 Electron payload.卸载 WinUI 3 默认不删除 LocalAppData ledger.当前 EXE 未签名,仍可能触发 `Unknown Publisher` 或 SmartScreen;release feed 尚未配置,更新按钮保持不可用,升级通过运行更高版本的 setup EXE 完成.

## 数据边界

应用只读观察 `%USERPROFILE%\.codex\sessions` 和 `%USERPROFILE%\.codex\archived_sessions`.`%USERPROFILE%\.codex\agents` 不参与 collector inventory,但同样禁止写入、锁定、重命名、删除、截断、修复或移动.

```text
Default:  %LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
Override: %CODEX_USAGE_DATA_DIR%\usage.sqlite
```

override 必须是受保护 Codex tree 以外的绝对可写目录.CSV export 和迁移路径经过同一类 resolved-path validation.

## 后台采集与 CPU 策略

- FileSystemWatcher callback 仅规范化路径并入队.
- 重复事件经过 debounce 和路径去重,每轮只处理有界 batch.
- 兜底 inventory reconciliation 每 5 分钟运行一次,采用分片 enumeration、分片 parsing、cooperative cancellation 和主动 yield.
- 手动同步不会启动并行 inventory;活动 inventory 结束后最多追加一个 trailing run.
- 启动后以 best effort 启用 Windows Efficiency Mode,使用 EcoQoS 与 below-normal process priority.
- canonical rewrite 只有在同路径、同 `rolloutId`、完整解析和双重稳定快照一致时才原子替换 ledger;否则显示 conflict.

这些策略降低峰值和长期后台消耗,但不承诺固定 CPU 百分比.首次建库、文件数量、单条 JSON 大小、磁盘 cache 和手动同步仍会影响瞬时负载.

## 原生界面与 lifecycle

- Native title bar 和 CommandBar 提供同步、导出、更新和 startup control.
- Collector health 展示 watcher、offline gap、conflict、retry、reconciliation、Efficiency Mode 和 ledger 状态.
- 宽窗口使用持久 filter pane;紧凑窗口折叠筛选并将次要 command 放入 overflow.
- 查询由 Application layer 执行,结果通过 UI dispatcher 更新.
- 大型明细使用 WinUI virtualizing controls.
- unpackaged 应用通过 HKCU Run entry 管理开机自启动;启动后可直接驻留 tray.
- 关闭 dashboard 可保持后台采集,通过 tray `Exit` 执行 clean shutdown.
- 当前 release feed 尚未配置;版本升级通过更高版本的 NSIS setup EXE 完成.

## 交付验证

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.0
git diff --check
```

release 还应验证 UAC install、旧 Electron 0.2.6 原位升级、HKCU Run startup、Efficiency Mode、tray lifecycle、collector shutdown、uninstall 和 ledger continuity.正式分发前还需要对 setup EXE 进行 Authenticode 签名.
