# Codex Usage Desktop Engineering Guide

## 项目范围

本仓库是一个本地 Electron 审计应用,用于统计 Codex token 用量并估算标准 API token 费用.应用观察 Codex rollout 数据,维护自己的 SQLite ledger,并提供离线 dashboard.

## 数据源边界

- 将 `%USERPROFILE%\\.codex\\sessions`, `%USERPROFILE%\\.codex\\archived_sessions`, `%USERPROFILE%\\.codex\\agents` 视为严格只读的观察数据源.
- 不得在 Codex 数据源目录中创建,修改,加锁,重命名,删除,截断,修复或移动任何文件.不得使用 advisory lock,exclusive open 或替换写入.
- 应用状态只能写入应用自己的数据目录.持久化 ledger 为 `codex-usage-data\\usage.sqlite`.
- 新增 output path,export path,cache 或 migration 时,必须复用或扩展 `src/write-boundary.ts`.任何解析后的写入路径只要位于受保护 Codex 目录内,就必须被拒绝.

## TypeScript 与架构

- 保持 `tsconfig.json` 的 strict 配置.模块边界和 IPC 边界使用精确的 `readonly` 数据结构,discriminated union 和明确的返回类型.
- 不引入 `any`,无类型 JSON 或宽泛类型断言.不受信任的 rollout record 必须先经过明确的 runtime validation,再进入聚合或持久化流程.
- 本地接口需要调整时,优先实施 breaking change,不要增加 compatibility shim.同一改动内同步更新全部仓库调用方和测试.
- domain type 和 renderer 可见 contract 放在 `src/shared.ts`.Electron IPC 请求应保持窄接口,可序列化,且具有明确类型.
- collector ingestion 和 SQLite access 不得进入 renderer. renderer 只能通过 preload API 查询数据.

## Electron 安全

- dashboard window 必须保持 `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`.
- `src/preload.ts` 只能通过 `contextBridge` 暴露最小化的类型化 API.不得向 renderer 暴露 Node,Electron,`ipcRenderer`,filesystem handle 或通用 invoke channel.
- 新增或变更 IPC contract 时,必须在接收端先做 runtime validation,再访问路径,查询 ledger 或写入数据.导出目标必须通过 write-boundary guard.
- 当前 technical debt: 现有 `usage:query` 和 `usage:export` handler 仍主要依赖 TypeScript 类型与下游时间范围检查,尚未对 renderer 传入的完整 `FilterSpec` 实施独立 runtime validation.不得将该缺口表述为已满足的 IPC validation.
- 应用默认离线.未经明确产品决策,不得增加 telemetry,remote content 或网络依赖.

## 文件,命名与文档

- 应用源代码位于 `src/`.测试使用相邻的 `*.test.ts` 命名.
- 新增 TypeScript,CSS,HTML 与 script 文件使用 lowercase kebab-case.使用描述性名称,不要使用日期或迭代编号作为源文件名.
- 生成物放在被忽略的 `dist/`,`release/`,`outputs/` 或 `work/` 目录.不得手工修改 `dist/`.
- 面向用户的项目文档维护在 `README.md`.`README_GUI.md` 是 Electron desktop quick guide.Legacy Python utilities 如需专用说明,应放在单独的 legacy 文档中.
- 持久化 prose,code comment 和 commit message 使用半角标点.

## 验证

开发中先运行最小相关命令.跨模块改动交付前,运行完整验证集:

```powershell
npm run typecheck
npm test
npm run package:portable
```

- 修改 UI 或采集代码时,使用 `npm start` 做开发 smoke test.验证 filter,collector status,tray behavior 和有界 agent table.
- 文档,HTML 或 CSS 改动交付前,确认 `git diff --check` 通过.
- 不自动执行 `git add` 或 `git commit`.暂存和提交由用户决定.

## 费用统计不变量

- `reasoning_output_tokens` 是 `output_tokens` 的子集,不得重复计费.
- 显示的 GPT-5.6 input pricing 始终忽略超过 272K 的 multiplier.
- GPT-5.4,GPT-5.5,GPT-5.6 之外的 model 归类为未计费的 `Others`. `source_model=unknown` 必须保留为独立 unknown attribution.
- 不得重新引入 adjacent complete cumulative snapshot,stale zero-breakdown snapshot,active-to-archive promotion 或 forked subagent replay 导致的重复计费.
