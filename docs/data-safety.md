# Data safety and read-only boundary

## Protected Codex directories

以下路径构成严格 no-write boundary:

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`
- `%USERPROFILE%\.codex\agents`

collector 只读观察 `sessions` 和 `archived_sessions`.`agents` 不被读取或 watch,但受相同保护.应用不得在这些路径内写入、创建、锁定、重命名、删除、截断、修复或移动任何文件.

## Enforcement

- `ProtectedPathPolicy` 对 ledger、export 和其他 output path 执行 absolute-path normalization,解析现有 reparse point 和 ancestor,拒绝位于 protected tree 内的 candidate.
- rollout source 只以 read access 打开.FileSystemWatcher 只订阅 notification,不会改变 source.
- `UsageStore` 只写应用拥有的 SQLite ledger.
- CSV save path 在打开 output stream 前验证.
- 测试中的 source mutation 仅作用于 disposable fixture directory,绝不指向用户真实 `.codex`.

source 在 stat/read/stat window 中变化时,当前 snapshot 不会被接受.collector 记录 retry diagnostic 并等待后续 event 或 reconciliation,不会修复 source.

## Consistency and recovery

watch event 经过 debounce、deduplication 和有界 batch.不可重入 full inventory 在 startup、manual sync 和每 5 分钟运行.目录、解析和 hashing work 被切片,在片之间 cooperative yield,以降低后台 CPU 峰值.

append-only source 可以从 verified byte offset 继续读取.如果 source 变短、prefix boundary 改变或 canonicalization 需要,collector 重新解析当前 stable snapshot.

同路径 canonical rewrite 只有在 `rolloutId` 不变、完整 parse 成功且两次 stable snapshot 一致时才原子替换 ledger.任何失败都保留旧 ledger.Cross-path divergence、ID 变化、malformed input 和 unstable snapshot 保持 conflict.恢复过程不修改 Codex source.

## Local data

```text
Default:  %LOCALAPPDATA%\Codex Usage Desktop\usage.sqlite
Override: %CODEX_USAGE_DATA_DIR%\usage.sqlite
```

ledger 可包含 token event、source metadata、collector diagnostics、agent path、nickname、conversation ID 和 rollout ID.CSV 与 log 也可能包含本地 usage data,应使用相同访问控制.

source 被 archive 或删除不会删除已采集 event.ledger 保留历史以维持 accounting continuity;source deletion 不是 ledger deletion request.

## Operational safeguards

- data directory 必须位于三个 protected directory 以外.
- 复制或恢复 `usage.sqlite` 前退出应用,并一起处理可能存在的 `-wal` 与 `-shm` companion.
- 不要在不明确支持 SQLite WAL 的 network-synced location 中运行 live ledger.
- conflict 或 degraded 状态出现时先保留 ledger 和 source,再使用 `Sync now` 或重启重试.不要编辑 rollout JSONL 以强制 parse.
- 旧 ledger 迁移脚本只复制、校验和备份,不会删除 source.
- 应用默认 offline,没有 remote audit API.
