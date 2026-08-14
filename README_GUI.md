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
dotnet format CodexUsageDesktop.sln --verify-no-changes
```

## Native installer

使用 `-AutoDetectDependencies` 可查找本机已有的 .NET 8 SDK、NSIS 3.x `makensis.exe` 和 7-Zip Extra 的 `7za.exe`、`7zr.exe`;脚本不会下载或安装构建工具.生成 unpackaged、self-contained 的 x64 WinUI 3 应用和全用户 installer:

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.16 -AutoDetectDependencies
```

7-Zip Extra 位于非标准目录时,追加 `-DependencySearchDirectory 'D:\tools\7-Zip'`;多个目录使用逗号数组或分号分隔.setup 输出位于 `release\winui-installer\codex-usage-desktop-setup-0.3.16-x64.exe`.构建会先用 7-Zip 生成并校验 payload archive,再生成唯一 pending EXE;仅在 `makensis` 成功且 pending EXE 存在并非空时,再同卷原子发布正式 setup;失败不会覆盖已有 setup.同一 workspace 的 installer build 必须串行执行.它将 self-contained payload 安装到 `%ProgramFiles%\Codex Usage Desktop`,目标计算机不需要预装 .NET 或 Windows App SDK runtime.安装范围为全用户,安装、升级和卸载需要 UAC.

setup 会在替换当前 WinUI payload 前检测并强制终止正在运行的 Codex Usage Desktop process.卸载 WinUI 3 默认不删除 LocalAppData ledger.用户可显式检查 GitHub Release,实验通道在下载时校验 SHA-256;运行前需要在警示 dialog 中确认并再次校验文件,随后 NSIS 结束当前应用和 collector process.

## 数据边界

应用只读观察 `%USERPROFILE%\.codex\sessions` 和 `%USERPROFILE%\.codex\archived_sessions`.`%USERPROFILE%\.codex\agents` 不参与 collector inventory,但同样禁止写入、锁定、重命名、删除、截断、修复或移动.

```text
Default:  %LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
Override: %CODEX_USAGE_DATA_DIR%\usage.sqlite
```

override 必须是受保护 Codex tree 以外的绝对可写目录.更新下载和迁移路径经过同一类 resolved-path validation.

## 后台采集与 CPU 策略

- FileSystemWatcher callback 仅规范化路径并入队.
- 重复事件经过 debounce 和路径去重,每轮只处理有界 batch.
- 兜底 inventory reconciliation 每 5 分钟运行一次,采用分片 enumeration、分片 parsing、cooperative cancellation 和主动 yield.
- source conflict 恢复只写应用 ledger.稳定候选必须 metadata exact,且 semantic relation 为 `Equal` 或 `Extension`;多个安全候选按确定性规则选择.
- unsafe、`Shorter`、`Diverged`、attribution 不一致或不稳定候选保留最后有效 ledger,记录内部 degraded/diagnostic 并后台重试.GUI 不显示 source conflict.

这些策略降低峰值和长期后台消耗,但不承诺固定 CPU 百分比.首次建库、文件数量、单条 JSON 大小和磁盘 cache 仍会影响瞬时负载.

窗口失焦后的 Efficiency Mode / process priority 调整仍 deferred,不属于当前交付能力.

## 原生界面与 lifecycle

- Native title bar 和 CommandBar 提供更新和 startup control.
- Native title bar 显示当前软件版本,版本号来自程序集 metadata.
- Collector health 展示 watcher、offline gap、retry、reconciliation 和 ledger 状态,不展示 source conflict.
- 最小窗口为 `900 x 720 DIP`.Wide 为 `>=1280`,Medium 为 `1000-1279`,Compact 为 `<1000`;两张明细表在 `>=1440` 时并排显示.
- 时间、model、执行主体和主线程四个顶层筛选各占一行.主线程使用 `AutoSuggestBox`,最多显示最近活动时间倒序的 20 个选项,格式为 `项目名 - 短 ID - 标题`:项目名取自 main session `session_meta.cwd` 的目录名,标题取自 `session_index.jsonl` 的权威 `thread_name`.可手动输入完整 UUIDv7 session ID,使用清空按钮取消筛选.合法输入会规范化;非空非法输入显示红色验证状态并保留已应用的筛选.筛选以精确的主线程 `ConversationId` 为根,归集全部子代理 event.model 顺序固定为 Sol、Terra、Luna、codex-auto-review、Others.
- 页面只允许一个纵向滚动容器;每个 table 在宽度不足时拥有独立横向滚动,不得引入嵌套纵向滚动.
- 查询由 Application layer 执行,结果通过 UI dispatcher 更新.
- 模型与执行主体 table 是有界聚合结果,使用无内部纵向滚动的 `ItemsControl`;页面根容器负责唯一纵向滚动.
- unpackaged 应用通过 HKCU Run entry 管理开机自启动;启动后可直接驻留 tray.
- 关闭 dashboard 可保持后台采集,通过 tray `Exit` 执行 clean shutdown.
- 更新在启动后立即检查一次固定 GitHub Release metadata,随后每 6 小时检查一次;手动检查会明确显示当前版本、可用更新或失败信息.自动检查不会弹窗、下载或安装;下载后用户必须在警示 dialog 中确认,应用再复验 SHA-256 后启动 setup.NSIS 会结束当前应用和 collector process.

## 交付验证

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
dotnet format CodexUsageDesktop.sln --verify-no-changes
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.16 -AutoDetectDependencies
git diff --check
```

release 还应验证 UAC install、当前 WinUI payload replacement、HKCU Run startup、tray lifecycle、collector shutdown、uninstall 和 ledger continuity.Efficiency Mode / process priority 验收 deferred.
