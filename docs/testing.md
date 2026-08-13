# Testing and smoke matrix

## Automated verification

从 repository root 运行:

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
dotnet format CodexUsageDesktop.sln --verify-no-changes
$sevenZip = 'C:\Tools\7-Zip\7za.exe'
$sevenZipRuntime = 'C:\Tools\7-Zip\7zr.exe'
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.17 -SevenZipPath $sevenZip -SevenZipRuntimePath $sevenZipRuntime
git diff --check
```

主要 test surface:

| Project | Focus |
| --- | --- |
| `CodexUsage.Domain.Tests` | runtime JSONL validation,oversized streaming classification,typed parser-state checkpoint codec,token relationship,cumulative deduplication,fork replay,model attribution,filters and cost |
| `CodexUsage.Infrastructure.Tests` | schema,atomic event/source/checkpoint transaction,restart reverse-token and boundary+tail byte counts,ledger/state tamper detection,file identity invalidation,partial tail,canonical state,protected path,watch batching/backoff,reconciliation,recovery and diagnostic throttling |
| `CodexUsage.Application.Tests` | lifecycle,query orchestration,collector status and platform-service behavior |

会 append、move、delete 或 rewrite source 的 test 只能使用 temporary fixture directory 和 disposable database.不得将 test root 指向真实 `%USERPROFILE%\.codex` 或 production `usage.sqlite`.

## Native desktop smoke

使用 disposable data directory 或已备份的 ledger 运行开发 build/installed application.对真实 Codex source 的 smoke 必须 observation-only.

| Area | Action | Expected result |
| --- | --- | --- |
| Single instance | 连续 launch 两次 | 只保留一个 process,第二次激活现有 window |
| Startup | enable 后退出并通过 HKCU Run launch | 状态与安装器选择一致,应用可先驻留 tray |
| Tray | close dashboard,再 Open dashboard、查看 collector status 和 Exit | close 不停止采集,Exit clean shutdown |
| Initial inventory | 使用 disposable ledger 启动 | watcher ready 后完成 inventory,source metadata 不变 |
| Restart checkpoint | 对 200 KiB+ fixture 首次同步后重启,再 append token record | unchanged restart 读取不超过 64 KiB boundary;append 只读 boundary+tail;ledger ordinal 和 usage 连续 |
| Reconciliation | 等待 5 分钟 | periodic run 发生且不会并行 inventory |
| Conflict recovery | 仅在 fixture source 中构造 Equal、Extension、ID change 和 unsafe candidate | stable metadata-exact Equal/Extension 确定性恢复;unsafe 保留最后有效 ledger、记录内部 degraded/diagnostic 并重试;GUI 无 source conflict |
| Filters | 独立切换 model/role/main-thread/time;从下拉选择、手动输入完整 UUIDv7 session ID,再清空主线程筛选 | facet 不互相错误移除,range 使用 `[startUtc,endUtc)`;下拉仅有最近活动时间倒序的 20 项,按 `项目名 - 短 ID - 标题` 显示;项目名取自 main session `session_meta.cwd` 的目录名,标题取自 `session_index.jsonl` 的权威 `thread_name`,并以主线程 `ConversationId` 为根归集全部子代理 event |
| Cost | 检查含 reasoning output 的 event | reasoning 与 other output 对 output cost 只计一次 |
| Responsive UI | 检查 900x720 minimum、1000 DIP 阈值、short height 和 high DPI | 筛选区在一行和两行之间切换;总体费用构成独占一行并常驻显示四色占比;模型与执行主体卡在 1000 DIP 切换并排和上下堆叠;整条费用构成 hover/focus 时显示四色占比;页面单一纵向滚动且无 clipping 或横向明细表 |
| Read-only boundary | 监视真实 source metadata | 无 lock、write、rename、delete、truncate 或 repair |

不要在真实 smoke 中 append、partially write、move、delete 或 rename rollout JSONL,也不要修改 `%USERPROFILE%\.codex\agents`.这些 mutation scenario 只属于 fixture test.

## NSIS installer acceptance

1. 在 clean Windows 11 x64 环境运行 setup,确认 UAC、全用户 Program Files 安装、开始菜单/桌面选项和 launch.
2. 在已安装当前 WinUI 版本的环境运行更高版本 setup,确认 payload replacement 和正常 launch.
3. 使用 `/S /CURRENTADMIN=1` 执行 silent upgrade,确认安装器完成 payload replacement 且不修改 LocalAppData ledger.
4. 验证现有 HKCU Run entry 的保留或更新,并确认 `--startup` launch 进入预期 lifecycle.
5. 验证应用运行时 installer 强制终止同名 process 后完成 upgrade/repair;无法确认退出时拒绝替换,较新版本存在时 downgrade 被拒绝.
6. 卸载后确认 Program Files payload、快捷方式和 HKCU Run entry 已删除,但 `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite` 保留.
7. 记录 setup SHA-256,保留上一个 installer 供 recovery.

SHA-256-only update check 会在启动后和每 6 小时访问固定 GitHub Release metadata,但只会在用户点击下载时传输 installer.验收应覆盖非 owner/repository、非 SemVer、多个 asset、缺少 digest、下载 digest mismatch、安装前本地文件 rehash mismatch、首次检查、6 小时边界、取消、generation invalidation 和不重入.还应验证用户取消 confirmation、generation 变化或 Process.Start 失败时均不启动 setup 且应用保持运行.

窗口失焦后的 Efficiency Mode / process priority work 与验收仍 deferred.
