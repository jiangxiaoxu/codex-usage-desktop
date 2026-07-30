# Testing and smoke matrix

## Automated verification

从 repository root 运行:

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.3.0
git diff --check
```

主要 test surface:

| Project | Focus |
| --- | --- |
| `CodexUsage.Domain.Tests` | runtime JSONL validation,token relationship,cumulative deduplication,fork replay,model attribution,filters,cost and CSV |
| `CodexUsage.Infrastructure.Tests` | schema,transaction,canonical state,protected path,watch batching,reconciliation,recovery and diagnostics |
| `CodexUsage.Application.Tests` | lifecycle,query/export orchestration,collector status and platform-service behavior |

会 append、move、delete 或 rewrite source 的 test 只能使用 temporary fixture directory 和 disposable database.不得将 test root 指向真实 `%USERPROFILE%\.codex` 或 production `usage.sqlite`.

## Native desktop smoke

使用 disposable data directory 或已备份的 ledger 运行开发 build/installed application.对真实 Codex source 的 smoke 必须 observation-only.

| Area | Action | Expected result |
| --- | --- | --- |
| Single instance | 连续 launch 两次 | 只保留一个 process,第二次激活现有 window |
| Startup | enable 后退出并通过 HKCU Run launch | 状态与安装器选择一致,应用可先驻留 tray |
| Tray | close dashboard,再 Open dashboard、Sync now 和 Exit | close 不停止采集,Exit clean shutdown |
| Initial inventory | 使用 disposable ledger 启动 | watcher ready 后完成 inventory,source metadata 不变 |
| Efficiency Mode | 检查 collector health 和 Task Manager | 支持时显示 EcoQoS/priority 成功;失败时有可诊断状态 |
| Reconciliation | 等待 5 分钟并触发 manual sync | periodic run 发生,manual sync 不产生并行 inventory |
| Conflict recovery | 仅在 fixture source 中执行 same-path stable rewrite | 符合 contract 时原子替换;不稳定或 ID change 保持 conflict |
| Filters | 独立切换 model/role/thread/time/search | facet 不互相错误移除,range 使用 `[startUtc,endUtc)` |
| Cost | 检查含 reasoning output 的 event | reasoning 与 other output 对 output cost 只计一次 |
| Export | 导出到普通目录,再选择 protected directory | 前者成功,后者在创建文件前被拒绝 |
| Responsive UI | 检查 720x560 minimum、default、wide、short height 和 high DPI | command/filter reflow,无 clipping,大型 list 保持 virtualization |
| Read-only boundary | 监视真实 source metadata | 无 lock、write、rename、delete、truncate 或 repair |

不要在真实 smoke 中 append、partially write、move、delete 或 rename rollout JSONL,也不要修改 `%USERPROFILE%\.codex\agents`.这些 mutation scenario 只属于 fixture test.

## NSIS installer acceptance

1. 在 clean Windows 11 x64 环境运行 setup,确认 UAC、全用户 Program Files 安装、开始菜单/桌面选项和 launch.
2. 在已安装 Electron 0.2.6 且含真实副本 ledger 的环境运行更高版本 setup,确认原位替换、旧 Electron payload 清理和 WinUI 3 launch.
3. 确认升级前创建 `ledger-backups\preinstall-*`,升级后 ledger path、schema、event total 和自启动选择连续.
4. 分别验证旧 Startup shortcut 和现有 HKCU Run entry 的迁移,并确认 `--startup` launch 进入预期 lifecycle.
5. 验证应用运行时 installer 拒绝继续,正常退出后 upgrade/repair 能完成,较新版本存在时 downgrade 被拒绝.
6. 卸载后确认 Program Files payload、快捷方式和 HKCU Run entry 已删除,但 `%LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite` 保留.
7. 对 Authenticode-signed release 验证 publisher trust 和 SmartScreen reputation;记录 setup SHA-256 与 certificate thumbprint,保留上一个 installer 供 recovery.

当前 unsigned setup 仍可能显示 `Unknown Publisher` 或 SmartScreen,不能替代 signed release acceptance.应用内 release feed 尚未配置,自动 update discovery 不在当前验收范围;版本升级通过更高版本的 setup EXE 验证.
