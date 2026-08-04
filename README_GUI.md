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

需要 NSIS 3.x,并确保 `makensis.exe` 位于 PATH、标准安装目录或现有 electron-builder cache;同时准备 7-Zip Extra 的 `7za.exe` 和 `7zr.exe`.生成 unpackaged、self-contained 的 x64 WinUI 3 应用和全用户 installer:

```powershell
$sevenZip = 'C:\Tools\7-Zip\7za.exe'
$sevenZipRuntime = 'C:\Tools\7-Zip\7zr.exe'
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.7 -SevenZipPath $sevenZip -SevenZipRuntimePath $sevenZipRuntime
```

setup 输出位于 `release\winui-installer\codex-usage-desktop-setup-0.3.7-x64.exe`.构建会先用 7-Zip 生成并校验 payload archive,再生成唯一 pending EXE;仅在 `makensis` 成功且 pending EXE 存在并非空时,再同卷原子发布正式 setup;失败不会覆盖已有 setup.同一 workspace 的 installer build 必须串行执行.它将 self-contained payload 安装到 `%ProgramFiles%\Codex Usage Desktop`,目标计算机不需要预装 .NET 或 Windows App SDK runtime.安装范围为全用户,安装、升级和卸载需要 UAC.

setup 可识别旧 Electron 0.2.6 安装并原位替换为 WinUI 3.它会检测并强制终止仍在运行的 Codex Usage Desktop process,调用旧 Electron uninstaller 后覆盖 WinUI payload,不再创建 ledger 备份;旧 Electron uninstaller 可能删除 LocalAppData ledger.卸载 WinUI 3 默认不删除 LocalAppData ledger.当前 EXE 未签名,仍可能触发 `Unknown Publisher` 或 SmartScreen.用户可显式检查 GitHub Release,实验通道在下载时校验 SHA-256;运行前需要在警示 dialog 中确认并再次校验文件,随后 NSIS 结束当前应用和 collector process.

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
- source conflict 恢复只写应用 ledger.稳定候选必须 metadata exact,且 semantic relation 为 `Equal` 或 `Extension`;多个安全候选按确定性规则选择.
- unsafe、`Shorter`、`Diverged`、attribution 不一致或不稳定候选保留最后有效 ledger,记录内部 degraded/diagnostic 并后台重试.GUI 不显示 source conflict.

这些策略降低峰值和长期后台消耗,但不承诺固定 CPU 百分比.首次建库、文件数量、单条 JSON 大小、磁盘 cache 和手动同步仍会影响瞬时负载.

窗口失焦后的 Efficiency Mode / process priority 调整仍 deferred,不属于当前交付能力.

## 原生界面与 lifecycle

- Native title bar 和 CommandBar 提供同步、导出、更新和 startup control.
- Native title bar 显示当前软件版本,版本号来自程序集 metadata.
- Collector health 展示 watcher、offline gap、retry、reconciliation 和 ledger 状态,不展示 source conflict.
- 已确认的 Figma Page 2 节点为 `90:2`,responsive contract 为 `90:329`.
- 最小窗口为 `720 x 640 DIP`.Wide 为 `>=1200`,Medium 为 `800-1199`,Compact 为 `<800`.
- 时间、model、执行主体和路径搜索四个顶层筛选各占一行;Compact 的时间控件拆为两行.model 顺序固定为 Sol、Terra、Luna、codex-auto-review、Others.
- 页面只允许一个纵向滚动容器;每个 table 在宽度不足时拥有独立横向滚动,不得引入嵌套纵向滚动.
- 查询由 Application layer 执行,结果通过 UI dispatcher 更新.
- 模型与执行主体 table 是有界聚合结果,使用无内部纵向滚动的 `ItemsControl`;页面根容器负责唯一纵向滚动.
- unpackaged 应用通过 HKCU Run entry 管理开机自启动;启动后可直接驻留 tray.
- 关闭 dashboard 可保持后台采集,通过 tray `Exit` 执行 clean shutdown.
- 更新在启动后立即检查一次固定 GitHub Release metadata,随后每 6 小时检查一次;手动检查仍可用.自动检查不会下载或安装;下载后用户必须在警示 dialog 中确认,应用再复验 SHA-256 后启动未签名 setup.NSIS 会结束当前应用和 collector process.当前 SHA-256 实验通道不能替代 Authenticode signing.

## 交付验证

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
dotnet format CodexUsageDesktop.sln --verify-no-changes
$sevenZip = 'C:\Tools\7-Zip\7za.exe'
$sevenZipRuntime = 'C:\Tools\7-Zip\7zr.exe'
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.7 -SevenZipPath $sevenZip -SevenZipRuntimePath $sevenZipRuntime
git diff --check
```

release 还应验证 UAC install、旧 Electron 0.2.6 原位升级、HKCU Run startup、tray lifecycle、collector shutdown、uninstall 和 ledger continuity.正式分发前还需要对 setup EXE 进行 Authenticode 签名.Efficiency Mode / process priority 验收 deferred.
