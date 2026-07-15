# Codex Usage Desktop

`Codex Usage Desktop` 是一个离线 Windows Electron 应用,用于审计本地 Codex token 用量并估算标准 API token 费用.它持续观察 `%USERPROFILE%\\.codex` 下的 rollout JSONL.后台自动持久化仅写入应用自己的 SQLite ledger;只有用户明确执行 export 时才写入所选 CSV 路径.应用不会上传源数据.

## 主要能力

- 后台 tray collector,结合 watcher 驱动的增量读取和周期性 reconciliation.
- 只读观察 active session,archived session 与 agent configuration file.
- 按 model,role category 以及单个主线程或 subagent thread 汇总 token 和预估费用.
- 分别展示 input,cached input,output 和 reasoning output token. reasoning output 是 output 的子集,不会重复计费.
- 支持 time range,自定义 Singapore time range,model,execution subject 和 thread search 的实时筛选.
- 从 `%USERPROFILE%\\.codex\\agents\\*.toml` 只读发现 role.没有配置的 role 显示为 `Others`.
- 将当前筛选条件匹配的 usage event 导出为 CSV,保存到用户选择且不位于受保护 Codex 目录内的位置.
- 提供固定高度,独立滚动且具有 sticky header 的 thread audit table.
- 估算 GPT-5.4,GPT-5.5,GPT-5.6 的费用.其他 model 归入未计费的 `Others`.GPT-5.6 始终忽略 input 超过 272K token 的 multiplier.

## 目录结构

```text
src/                    Electron main process,preload bridge,collector,parser,ledger,renderer,test
scripts/copy-static.mjs 构建时复制 HTML 与 CSS static asset
dist/                   生成的 TypeScript 与 static build output,已忽略
release/                Portable EXE output,已忽略
launch_codex_usage_gui.vbs
                        双击启动最新的 Portable EXE
README_GUI.md           Electron desktop quick guide
AGENTS.md               贡献者和 agent 的工程约束
```

## 快速启动

安装依赖并启动开发版应用:

```powershell
npm install
npm start
```

collector 初始化完成后会显示 dashboard.关闭窗口只会隐藏到 notification area,collector 会继续运行.可通过 tray menu 重新打开 dashboard,立即同步或退出应用.

## VBS 启动器

构建 Portable 包后,双击 [launch_codex_usage_gui.vbs](launch_codex_usage_gui.vbs).启动器会查找 `release` 目录下最新的 Portable executable,并在不显示 console window 的情况下启动它.

如果尚未有 package,先运行 `npm run package:portable`.

## 构建,测试与打包

```powershell
npm run typecheck
npm test
npm run build
npm run package:portable
```

`npm run package:portable` 会在 `release/` 生成 Windows Portable executable.package 使用 `dist/` 中的生成文件.应修改 `src/` 后重新构建,不要编辑生成 output.

## 数据目录与安全边界

应用仅以观察模式读取下列 Codex 目录:

```text
%USERPROFILE%\\.codex\\sessions
%USERPROFILE%\\.codex\\archived_sessions
%USERPROFILE%\\.codex\\agents
```

应用不会对这些 source file 加锁,写入,重命名,删除,截断或修复. collector 的 SQLite ledger 由应用自身拥有,位置如下:

```text
Development: %APPDATA%\\codex-usage-desktop\\codex-usage-data\\usage.sqlite
Portable:    <directory containing the running Portable EXE>\\codex-usage-data\\usage.sqlite
Override:    %CODEX_USAGE_DATA_DIR%\\usage.sqlite
```

SQLite lock 仅限于应用自己的 ledger.应用会拒绝任何解析后落在受保护 Codex 目录内的 output 或 export path.

## 文档索引

- [Engineering guide](AGENTS.md): 数据源边界,TypeScript,Electron security,验证和费用统计不变量.
- [Electron quick guide](README_GUI.md): Electron desktop app 的启动,功能与日常使用说明.
- [Architecture](docs/architecture.md): process boundary,watcher owner,IPC 与 ledger design.
- [Cost model](docs/cost-model.md): token accounting,pricing,CSV field 与 price share 定义.
- [Data safety](docs/data-safety.md): read-only source boundary,ledger 和 export safety.
- [Migration to G project](docs/migration-g-project.md): 本次完整跨盘移动,验证,cutover 和 rollback.
- [Operations](docs/operations.md): collector lifecycle,watcher delay,backup,recovery 与 export.
- [Testing](docs/testing.md): automated verification,read-only desktop smoke 与 release acceptance.
- [Package scripts](package.json): command 与 Portable packaging configuration 的权威定义.

Legacy Python utilities remain as `codex_token_usage_gui.py` and related scripts. They are separate from the Electron desktop application and are not the current product launch path.

## 验证要求

共享代码改动前,运行相关验证并保持 working tree 可检查:

```powershell
npm run typecheck
npm test
git diff --check
```

修改 packaging 时运行 `npm run package:portable`.修改 collector,tray 或 renderer 时,还要完成一次真实 Electron smoke test.
