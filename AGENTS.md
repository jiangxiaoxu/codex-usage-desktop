# Codex Usage Desktop Engineering Guide

## 项目范围

本仓库是一个本地 .NET 8 / WinUI 3 审计应用,用于统计 Codex token 用量并估算标准 API token 费用.应用只读观察 Codex rollout 数据,维护自己的 SQLite ledger,并提供离线 dashboard.

## 数据源边界

- 将 `%USERPROFILE%\\.codex\\sessions`, `%USERPROFILE%\\.codex\\archived_sessions`, `%USERPROFILE%\\.codex\\agents` 视为严格只读的观察数据源.
- 不得在 Codex 数据源目录中创建,修改,加锁,重命名,删除,截断,修复或移动任何文件.不得使用 advisory lock,exclusive open 或替换写入.
- 应用状态只能写入应用自己的数据目录.默认 ledger 为 `%LOCALAPPDATA%\\Codex Usage Desktop\\usage.sqlite`.
- 新增 ledger,export,update download cache 或 migration staging 路径时,必须复用或扩展 `ProtectedPathPolicy`.任何 resolve 到受保护 Codex 目录内的写入路径都必须被拒绝.

## .NET 与架构

- 保持 solution 的 nullable 和 implicit-using 配置.公共 API,模块边界和 collection 使用精确 C# 类型、`readonly` 数据结构与明确的 nullable contract.
- 不引入 `dynamic`、无验证的 JSON 或宽泛类型断言.不受信任的 rollout record 必须先经过明确的 runtime validation,再进入聚合或持久化流程.
- 本地 contract 需要调整时,优先实施 breaking change,不要增加 compatibility shim.同一改动内同步更新所有调用方和测试.
- `CodexUsage.Domain` 保存解析、filtering 和 accounting contract;`CodexUsage.Application` 负责编排 query 与 lifecycle;`CodexUsage.Infrastructure` 拥有 collector 和 SQLite;`CodexUsage.App` 只拥有 WinUI 和 Windows integration.
- collector ingestion 和 SQLite access 不得进入 ViewModel 或 XAML code-behind.ViewModel 只能通过 Application contract 查询和更新 UI.

## WinUI 与安全

- dashboard 必须保持 native WinUI 3:不使用 WebView2,不加载 remote content,也不暴露 filesystem 或 SQLite handle 给 UI.
- 新增 UI 输入时,在 Application boundary 验证并以窄、类型化 request 传递,再访问 ledger 或路径.
- 应用默认不上传 rollout 数据.未经明确产品决策,不得增加 telemetry 或新的远端 API;现有固定 GitHub Release 更新检查不构成扩展 remote-data 权限.

## 主线程筛选

- 主线程筛选只接受完整的 main `ConversationId` UUIDv7,不得恢复 agent path,nickname 或 rollout ID 的模糊搜索.
- 下拉项最多显示最近活动时间倒序的 20 个主线程.项目名取自 main session `session_meta.cwd` 的目录名,标题取自 `session_index.jsonl` 的权威 `thread_name`,显示格式为 `项目名 - 短 ID - 标题`.
- 用户可直接输入完整 session ID.匹配以 main `ConversationId` 为根,并归集其全部后代 event;清空输入必须取消该筛选.

## 文件,命名与文档

- 应用源代码位于 `src/`,测试位于 `tests/` 并按对应 project 组织.
- 新增 C#,XAML,PowerShell 和 script 文件使用 lowercase kebab-case.使用描述性名称,不要使用日期或迭代编号作为源文件名.
- 生成物放在被忽略的 `bin/`,`obj/`,`release/` 或 `work/` 目录.不得手工修改生成物.
- 面向用户的项目文档维护在 `README.md` 和 `README_GUI.md`;installer 说明必须与实际 WinUI 安装、升级和卸载行为一致.
- 持久化 prose,code comment 和 commit message 使用半角标点.

## 验证

开发中先运行最小相关命令.跨模块改动交付前,运行完整验证集:

```powershell
dotnet restore CodexUsageDesktop.sln
dotnet build CodexUsageDesktop.sln -c Release --no-restore
dotnet test CodexUsageDesktop.sln -c Release --no-build
dotnet format CodexUsageDesktop.sln --verify-no-changes
```

当本次交付包含 release packaging 时,在已准备 NSIS 3.x、`7za.exe` 和 `7zr.exe` 的环境中运行 `pwsh -NoProfile -File .\scripts\build-installer.ps1 ...`.文档,XAML 或 CSS 改动交付前,确认 `git diff --check` 通过.不自动执行 `git add` 或 `git commit`;暂存和提交由用户决定.

## 费用统计不变量

- `reasoning_output_tokens` 是 `output_tokens` 的子集,不得重复计费.
- 显示的 GPT-5.6 input pricing 始终忽略超过 272K 的 multiplier.
- GPT-5.4,GPT-5.5,GPT-5.6 之外的 model 归类为未计费的 `Others`. `source_model=unknown` 必须保留为独立 unknown attribution.
- 不得重新引入 adjacent complete cumulative snapshot,stale zero-breakdown snapshot,active-to-archive promotion 或 forked subagent replay 导致的重复计费.
