# Architecture

## Scope

Codex Usage Desktop 是本地 .NET 8 / WinUI 3 application.它只读观察 `%USERPROFILE%\.codex` 下的 rollout JSONL,将 normalized usage records 写入应用自己的 SQLite ledger,并显示 token 与标准 API 费用估算.应用不使用 WebView2,不加载 remote content,也不上传观测数据.

## Process and data flow

```text
Codex sessions / archived_sessions JSONL
                | read-only stat/open/read
                v
UsageCollector -> RolloutParser -> UsageStore -> usage.sqlite
      |               |              |
      |               v              v
      |          UsageEvent      query/export
      v                              |
DashboardApplicationService --------+
                |
                v
DashboardViewModel -> WinUI 3 XAML
```

这些 component 位于一个 native .NET process 中,但职责边界保持独立:

- `CodexUsage.App` 拥有 WinUI lifecycle、window、tray、HKCU Run startup、native dialogs、Efficiency Mode 和 UI dispatcher.
- `CodexUsage.Application` 编排 collector、query、export 和 platform service,并向 UI 暴露类型化 contract.
- `CodexUsage.Infrastructure` 拥有 FileSystemWatcher、collector actor、SQLite access 和 protected-path policy.
- `CodexUsage.Domain` 验证不受信任 JSONL,执行 canonicalization、filtering 和 accounting.

UI 不直接打开 rollout 或 SQLite.耗时任务不占用 UI thread;Application layer 将不可变 snapshot dispatch 回 view model.

## Lifecycle and Windows integration

应用使用 single-instance coordination.普通的第二次启动激活现有 window.HKCU Run launch 可以直接进入 tray.关闭 dashboard 只隐藏 window,tray `Exit` 才停止 collector、checkpoint ledger 并退出 process.

进程启动后以 best effort 请求 Windows Efficiency Mode:设置 process power throttling 的 execution-speed flag,并尝试使用 below-normal priority.失败不会改变 accounting correctness,但状态会向 UI 报告.

应用以 unpackaged、self-contained 的 win-x64 payload 发布,由 NSIS 提供全用户 setup、旧 Electron 0.2.6 原位升级、Program Files ownership、快捷方式、自启动迁移和卸载.安装器在覆盖程序前备份 LocalAppData ledger;应用数据不属于卸载 manifest,因此默认保留.当前 release feed 未配置,升级通过更高版本的 setup EXE 完成.

## Collection actor

`UsageCollector` 通过单 consumer channel 串行化状态变更.FileSystemWatcher callback 只规范化路径并入队,不会读取或解析完整 JSONL.重复 path event 经过 debounce 和去重,每批最多处理 16 个 path.失败 path 使用有界 retry 和 backoff;仍有 retry 或 conflict 时状态为 `degraded`.

full inventory 在 startup、manual sync 和每 5 分钟的兜底 reconciliation 运行.它不可重入;活动期间的手动同步最多排队一个 trailing run.目录采用 breadth-first 分片 enumeration,outer work 和 parser/hash work 在小片之间 cooperative yield,降低长时间占用 CPU 的峰值.这些值是 duty-cycle target,不是严格的单条 record latency 或 memory bound.

collector 记录 source size、mtime、committed byte offset 和 consumed prefix boundary hash.稳定读取使用 stat-before/read/stat-after.追加读取先验证既有 boundary;验证失败时执行完整 reparse.

## Canonicalization and recovery

每个 rollout 在 ledger 中只有一个 canonical source.active 与 archived copy 因此不会重复计费.同一路径的 canonical file 变短或 diverge 时,只有同时满足以下条件才恢复:

- 当前文件完整解析成功.
- parsed `rolloutId` 与 canonical rollout 一致.
- 两次独立 stable snapshot 完全一致.

恢复在一个 SQLite transaction 中替换该 rollout 的 event 和 source metadata.失败保留旧 ledger.Cross-path divergence、ID 变化、malformed input 或 unstable snapshot 保持 conflict.Codex JSONL 永不作为恢复步骤被修改.

source 消失时仅标记 absent,已经记账的历史 event 保留.parser revision 变化时,当前可发现 rollout 逐个 transaction rebuild;所有候选成功后才推进 revision marker.

## Parser and accounting

parser 只接受 newline-terminated JSONL record,尾部 partial line 延后处理.所有不受信任 field 在进入 aggregation 或 persistence 前经过 runtime validation.它解析 session metadata、model attribution、thread/role 和 `last_token_usage` delta.

invalid token relationship、zero-breakdown snapshot、相邻 complete cumulative duplicate 和 fork replay 被排除.`reasoning_output_tokens` 必须不大于 `output_tokens`,`cached_input_tokens` 必须不大于 `input_tokens`.

## SQLite ledger

`UsageStore` 管理 `usage.sqlite` 和 schema version.ledger 存储 rollouts、usage events、source state、collector runs、diagnostics 和 collector state.写操作使用 transaction,SQLite 启用 foreign keys、WAL 和 5 second busy timeout.clean shutdown 执行 checkpoint.

```text
Default:  %LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
Override: %CODEX_USAGE_DATA_DIR%\usage.sqlite
```

data directory 与 Program Files install location 解耦,并且不得 resolve 到受保护 Codex source tree.
