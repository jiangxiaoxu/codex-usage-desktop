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

- `CodexUsage.App` 拥有 WinUI lifecycle、window、tray、HKCU Run startup、native dialogs 和 UI dispatcher.
- `CodexUsage.Application` 编排 collector、query、export 和 platform service,并向 UI 暴露类型化 contract.
- `CodexUsage.Infrastructure` 拥有 FileSystemWatcher、collector actor、SQLite access 和 protected-path policy.
- `CodexUsage.Domain` 验证不受信任 JSONL,执行 canonicalization、filtering 和 accounting.

UI 不直接打开 rollout 或 SQLite.耗时任务不占用 UI thread;Application layer 将不可变 snapshot dispatch 回 view model.

## Lifecycle and Windows integration

应用使用 single-instance coordination.普通的第二次启动激活现有 window.HKCU Run launch 可以直接进入 tray.关闭 dashboard 只隐藏 window,tray `Exit` 才停止 collector、checkpoint ledger 并退出 process.

窗口失焦后的 Efficiency Mode / process priority 调整仍 deferred.当前 architecture 不把 focus-aware scheduling transition 作为已完成 contract.

应用以 unpackaged、self-contained 的 win-x64 payload 发布,由 NSIS 提供全用户 setup、旧 Electron 0.2.6 原位升级、Program Files ownership、快捷方式、自启动迁移和卸载.安装器在覆盖程序前备份 LocalAppData ledger;应用数据不属于卸载 manifest,因此默认保留.SHA-256-only GitHub Release metadata check 在启动后和每 6 小时运行;应用内 launch 需要用户确认并在 Process.Start 前复验下载文件和 metadata generation.NSIS 负责结束当前 process,且不能替代 Authenticode signing.

## Collection actor

`UsageCollector` 通过单 consumer channel 串行化状态变更.FileSystemWatcher callback 只规范化路径并入队,不会读取或解析完整 JSONL.重复 path event 经过 debounce 和去重,每批最多处理 16 个 path.失败 path 使用有界指数 backoff;watch event 不会清零失败状态,只有成功处理才会清除.相同 source/error diagnostic 按时间窗口节流.仍有 retry 或 conflict 时状态为 `degraded`.

full inventory 在 startup、manual sync 和每 5 分钟的兜底 reconciliation 运行.它不可重入;活动期间的手动同步最多排队一个 trailing run.目录采用 breadth-first 分片 enumeration,outer work 和 parser/hash work 在小片之间 cooperative yield,降低长时间占用 CPU 的峰值.这些值是 duty-cycle target,不是严格的单条 record latency 或 memory bound.

collector 记录 source size、mtime、committed byte offset 和 consumed prefix boundary hash.稳定读取使用 stat-before/read/stat-after.追加读取先验证既有 boundary;验证失败时执行完整 reparse.

schema v4 的 `rollout_checkpoints` 只为当前 canonical source 保存 restart state.它包含 checkpoint/parser revision、Windows file ID、observed stat、最后完整换行 offset、64 KiB boundary hash、完整强类型 `RolloutParserState` JSON 及 SHA-256、partial tail、oversized opaque count 和 legacy all-NUL padding count.full replace 与 append 都在 event/source/checkpoint 同一 transaction 中提交,transaction 成功后才更新进程内 runtime.

cold restart 只接受 canonical path、rollout、revision、state hash、ledger ordinal、file identity、size/mtime 和 boundary 全部匹配的 checkpoint.unchanged source 还会从 frozen stable offset 以 64 KiB 反向分块寻找最近的有效 `token_count`,在 64 MiB read budget 内比较 cumulative snapshot 与 ledger 末行的 timestamp/input/cache/output/reasoning;巨大 opaque line 按 newline boundary 跳过,不会整行累积.命中后恢复 parser state;source 已增长时直接读取 boundary+tail.任何 truncate、same-size rewrite、identity/boundary/state/revision/token-tail mismatch、conflict 或 promotion 都删除旧 checkpoint 并 fail closed 到 full reconciliation.尾部未完成 JSONL record 只更新 partial-tail checkpoint,不会进入 failure retry.

## Canonicalization and recovery

每个 rollout 在 ledger 中只有一个 canonical source.active 与 archived copy 因此不会重复计费.自动恢复只更新应用拥有的 SQLite ledger,永不修改 Codex JSONL.候选必须通过双重稳定快照,metadata 与 canonical identity exact match,并且 semantic relation 为 `Equal` 或 `Extension`.

- 安全候选按 `Extension` 优先于 `Equal`,稳定 byte length 较长优先,再按 mtime 和 path 确定性排序.
- 选定候选与 canonical rollout 在单一 SQLite transaction 中完成 event、source metadata 和精确 conflict-source demotion.
- 如果 changed path 的 rollout ID 已变化,旧 rollout 只可从安全 fallback 恢复,changed path 作为新 rollout 独立解析.
- `Shorter`、`Diverged`、attribution 不一致、malformed、unstable 或无法建立信任的候选不进入 ledger replacement.

unsafe 情况保留最后有效 ledger,内部状态保持 degraded,写入 diagnostic 并由后台 retry/reconciliation 重试.GUI contract 不暴露 source conflict banner、counter 或 table row.

source 消失时仅标记 absent,已经记账的历史 event 保留.parser revision 变化时,当前可发现 rollout 逐个 transaction rebuild;所有候选成功后才推进 revision marker.

## Parser and accounting

parser 只接受 newline-terminated JSONL record,尾部 partial line 延后处理.普通 `JsonDocument` 解析保持 1 MiB 单 record 上限.完整但超限的 record 由 `Utf8JsonReader` 遍历并验证整行 JSON 语法,只对明确白名单中的非计费、非归因 opaque record 安全跳过;`token_count`、session/context/role/model/identity、未知、歧义或 malformed 超限 record 使整个 source 保持 unsafe.安全跳过的 source 可导入其余完整 usage,但以 `partial` phase 和 typed diagnostic 明确标注,不会推进 `last_successful_inventory`.

legacy filesystem padding recovery 只接受长度大于 0 且每个 byte 都是 `0x00` 的完整物理 JSONL record.该 record 不参与 type、accounting 或 context,并以 `partial`、typed local diagnostic 和 checkpoint count 持久化.混合 NUL、whitespace+NUL 及其他 malformed record 仍 fail closed.

invalid token relationship、zero-breakdown snapshot、相邻 complete cumulative duplicate 和 fork replay 被排除.`reasoning_output_tokens` 必须不大于 `output_tokens`,`cached_input_tokens` 必须不大于 `input_tokens`.

## SQLite ledger

`UsageStore` 管理 `usage.sqlite` 和 schema version.ledger 存储 rollouts、usage events、source state、restart checkpoints、collector runs、diagnostics 和 collector state.写操作使用 transaction,SQLite 启用 foreign keys、WAL 和 5 second busy timeout.clean shutdown 执行 SQLite WAL checkpoint.

```text
Default:  %LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
Override: %CODEX_USAGE_DATA_DIR%\usage.sqlite
```

data directory 与 Program Files install location 解耦,并且不得 resolve 到受保护 Codex source tree.

## Presentation contract

最终 Figma Page 2 layout 为 node `90:2`,responsive contract 为 `90:329`.窗口最小值为 `720 x 640 DIP`;Wide 为 `>=1200`,Medium 为 `800-1199`,Compact 为 `<800`.时间、model、执行主体和路径搜索四个顶层筛选各占一行,Compact 时间控件拆为两行.model 顺序为 Sol、Terra、Luna、Others.页面只有一个纵向 scroll owner,各 table 仅在宽度不足时拥有独立横向 scroll owner.
