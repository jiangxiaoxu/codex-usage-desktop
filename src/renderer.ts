type ThreadType = "main" | "subagent" | "unknown";
type ConfiguredAgentRole = string & { readonly __configuredAgentRole: unique symbol };
type AgentRoleCategory = "main" | "Others" | ConfiguredAgentRole;

interface SubjectFilter {
  readonly threadType: ThreadType;
  readonly agentRoleCategory: AgentRoleCategory;
}

interface FilterSpec {
  readonly startUtc: string;
  readonly endUtc: string;
  readonly models: readonly string[] | null;
  readonly subjects: readonly SubjectFilter[] | null;
  readonly pathQuery: string;
}

interface CostBreakdown {
  readonly uncachedInput: number;
  readonly cachedInput: number;
  readonly reasoningOutput: number;
  readonly otherOutput: number;
  readonly total: number;
}

interface Summary {
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly uncachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningOutputTokens: number;
  readonly otherOutputTokens: number;
  readonly canonicalTotalTokens: number;
  readonly unpricedTokens: number;
  readonly cost: CostBreakdown;
}

interface GroupRow<Key extends readonly string[]> { readonly key: Key; readonly summary: Summary; }
type ModelGroupRow = GroupRow<readonly [model: string]>;
type RoleGroupRow = GroupRow<readonly [threadType: ThreadType, agentRoleCategory: AgentRoleCategory]>;
type AgentGroupRow = GroupRow<readonly [threadType: ThreadType, agentRoleCategory: AgentRoleCategory, agentPath: string, model: string]>;
interface FacetMetrics { readonly canonicalTotalTokens: number; readonly totalCost: number; }
interface ModelFacetOption extends FacetMetrics { readonly model: string; }
interface SubjectFacetOption extends FacetMetrics { readonly subject: SubjectFilter; }
interface QueryFacets { readonly models: readonly ModelFacetOption[]; readonly subjects: readonly SubjectFacetOption[]; }
interface ScanDiagnostics { readonly filesScanned: number; readonly malformedLines: number; readonly duplicateSnapshotsSkipped: number; readonly zeroBreakdownSnapshotsSkipped: number; readonly invalidTokenRelationshipsSkipped: number; }
interface QueryResult { readonly summary: Summary; readonly byModel: readonly ModelGroupRow[]; readonly byRole: readonly RoleGroupRow[]; readonly byAgent: readonly AgentGroupRow[]; readonly facets: QueryFacets; readonly diagnostics: ScanDiagnostics; }

type CollectorPhase = "initializing" | "syncing" | "watching" | "degraded" | "stopped";
type ObservationCoverage = "baseline" | "continuous" | "gap";
interface ObservationGap { readonly startUtc: string; readonly endUtc: string; }
interface CollectorStatus { readonly phase: CollectorPhase; readonly databasePath: string; readonly runStartedUtc: string; readonly lastSuccessfulInventoryUtc: string | null; readonly lastHeartbeatUtc: string | null; readonly filesKnown: number; readonly pendingFiles: number; readonly changedFilesLastSync: number; readonly conflicts: number; readonly observationCoverage: ObservationCoverage; readonly observationGap: ObservationGap | null; readonly message: string; }
interface SyncResult { readonly status: CollectorStatus; readonly changed: boolean; }
interface UsageApi { syncNow(): Promise<SyncResult>; query(filter: FilterSpec): Promise<QueryResult>; exportCsv(filter: FilterSpec): Promise<{ readonly path: string | null; readonly count: number }>; getCollectorStatus(): Promise<CollectorStatus>; onUsageUpdated(listener: (status: CollectorStatus) => void): () => void; }

const apiWindow = window as unknown as { readonly usageApi: UsageApi };

const byId = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (element === null) throw new Error(`Missing element: ${id}`);
  return element as T;
};

const currency = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", minimumFractionDigits: 1, maximumFractionDigits: 1 });
const integer = new Intl.NumberFormat("en-US");
const EMPTY_FACET_METRICS: FacetMetrics = { canonicalTotalTokens: 0, totalCost: 0 };
const QUICK_RANGE_HOURS = [0.5, 1, 2, 4, 8, 12, 24, 48, 72] as const;
const selectedModels = new Set<string>();
const selectedSubjectKeys = new Set<string>();
const subjectsByKey = new Map<string, SubjectFilter>();
let availableModelNames: readonly string[] = [];
let availableSubjectKeys: readonly string[] = [];
let selectAllModelsMode = true;
let selectAllSubjectsMode = true;
let latestResult: QueryResult | null = null;
let refreshTimer: number | null = null;
let liveFilterTimer: number | null = null;
let filtersDirty = false;
let filterRevision = 0;
let querySequence = 0;
let operationSequence = 0;
let manualSyncActive = false;

function subjectKey(subject: SubjectFilter): string {
  return JSON.stringify([subject.threadType, subject.agentRoleCategory]);
}

function subjectLabel(subject: SubjectFilter): string {
  if (subject.threadType === "main") return "主线程";
  if (subject.threadType === "subagent") return `子代理 · ${subject.agentRoleCategory}`;
  return `未知线程 · ${subject.agentRoleCategory}`;
}

function singaporeDate(value: Date): string {
  const parts = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Singapore", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(value);
  const part = (type: string): string => parts.find((item) => item.type === type)?.value ?? "00";
  return `${part("year")}-${part("month")}-${part("day")}T${part("hour")}:${part("minute")}`;
}

function compactTokens(value: number): string {
  if (Math.abs(value) >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
  if (Math.abs(value) >= 1_000) {
    const thousands = Number((value / 1_000).toFixed(1));
    if (Math.abs(thousands) >= 1_000) return `${(value / 1_000_000).toFixed(1)}M`;
    return `${thousands.toFixed(1)}K`;
  }
  return integer.format(value);
}

function cancelRefreshTimer(): void {
  if (refreshTimer === null) return;
  window.clearTimeout(refreshTimer);
  refreshTimer = null;
}

function scheduleFilterQuery(delayMs: number, recordChange = true): void {
  if (recordChange) filterRevision += 1;
  filtersDirty = true;
  cancelRefreshTimer();
  if (liveFilterTimer !== null) window.clearTimeout(liveFilterTimer);
  liveFilterTimer = window.setTimeout(() => {
    liveFilterTimer = null;
    if (manualSyncActive) {
      scheduleFilterQuery(100, false);
      return;
    }
    void apply();
  }, delayMs);
}

function setQuickRange(index: number, applyImmediately: boolean): void {
  const hours = QUICK_RANGE_HOURS[index];
  if (hours === undefined) throw new RangeError(`Unknown quick range index: ${index}`);
  const end = new Date();
  byId<HTMLInputElement>("end").value = singaporeDate(end);
  byId<HTMLInputElement>("start").value = singaporeDate(new Date(end.getTime() - hours * 60 * 60 * 1000));
  const slider = byId<HTMLInputElement>("range-slider");
  const label = `${hours}h`;
  slider.value = String(index);
  slider.setAttribute("aria-valuetext", `${hours} 小时`);
  byId<HTMLOutputElement>("range-output").value = label;
  if (applyImmediately) scheduleFilterQuery(120);
}

function selectedSubjects(): readonly SubjectFilter[] {
  const result: SubjectFilter[] = [];
  for (const key of selectedSubjectKeys) {
    const subject = subjectsByKey.get(key);
    if (subject !== undefined) result.push(subject);
  }
  return result.sort((left, right) => subjectKey(left).localeCompare(subjectKey(right)));
}

function filterSpec(): FilterSpec {
  const start = byId<HTMLInputElement>("start").value;
  const end = byId<HTMLInputElement>("end").value;
  if (!start || !end) throw new Error("请选择开始和结束时间.");
  const startUtc = new Date(`${start}:00+08:00`);
  const endUtc = new Date(`${end}:00+08:00`);
  if (Number.isNaN(startUtc.valueOf()) || Number.isNaN(endUtc.valueOf()) || startUtc >= endUtc) throw new Error("时间范围无效.");
  return {
    startUtc: startUtc.toISOString(),
    endUtc: endUtc.toISOString(),
    models: selectAllModelsMode ? null : [...selectedModels].sort(),
    subjects: selectAllSubjectsMode ? null : selectedSubjects(),
    pathQuery: byId<HTMLInputElement>("path-query").value,
  };
}

function setStatus(message: string): void { byId<HTMLElement>("status").textContent = message; }

function collectorItem(label: string, value: string, className = ""): HTMLElement {
  const item = document.createElement("div"); item.className = "collector-item";
  const caption = document.createElement("span"); caption.textContent = label;
  const content = document.createElement("strong"); content.textContent = value; content.title = value; content.className = className;
  item.append(caption, content); return item;
}

function formatInstant(value: string | null): string {
  if (value === null) return "尚未完成";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : new Intl.DateTimeFormat("zh-CN", { timeZone: "Asia/Singapore", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" }).format(date);
}

function renderCollectorStatus(status: CollectorStatus): void {
  const phaseText: Readonly<Record<CollectorPhase, string>> = { initializing: "初始化", syncing: "同步中", watching: "监听中", degraded: "异常", stopped: "已停止" };
  const phaseClass = status.phase === "watching" ? "good" : status.phase === "degraded" || status.phase === "stopped" ? "bad" : "warn";
  const coverage = status.observationCoverage === "baseline"
    ? "首次基线已建立"
    : status.observationGap === null ? "连续观测" : `${formatInstant(status.observationGap.startUtc)} - ${formatInstant(status.observationGap.endUtc)} 未观测`;
  byId<HTMLElement>("collector-status").replaceChildren(
    collectorItem("采集状态", `${phaseText[status.phase]} · ${status.message}`, phaseClass),
    collectorItem("最后对账", formatInstant(status.lastSuccessfulInventoryUtc)),
    collectorItem("文件 / 冲突", `${integer.format(status.filesKnown)} / ${integer.format(status.conflicts)}`, status.conflicts === 0 ? "good" : "bad"),
    collectorItem("观测覆盖", coverage, status.observationCoverage === "continuous" ? "good" : "warn"),
    collectorItem("SQLite", status.databasePath),
  );
}

function inlineOption(label: string, metrics: FacetMetrics, checked: boolean, unavailable: boolean, focusKey: string, onChange: (checked: boolean) => void): HTMLLabelElement {
  const wrapper = document.createElement("label");
  wrapper.className = `inline-option${unavailable ? " unavailable" : ""}`;
  const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.checked = checked;
  checkbox.dataset.filterKey = focusKey;
  checkbox.addEventListener("change", () => onChange(checkbox.checked));
  const name = document.createElement("span"); name.className = "inline-option-name"; name.textContent = label;
  const metricElement = document.createElement("span"); metricElement.className = "inline-option-metrics";
  const tokens = document.createElement("span"); tokens.textContent = `${compactTokens(metrics.canonicalTotalTokens)} tok`;
  const cost = document.createElement("span"); cost.textContent = currency.format(metrics.totalCost);
  metricElement.title = `${integer.format(metrics.canonicalTotalTokens)} total tokens · ${currency.format(metrics.totalCost)}`;
  metricElement.append(tokens, cost);
  wrapper.append(checkbox, name, metricElement);
  return wrapper;
}

function focusedFilterKey(): string | null {
  const active = document.activeElement;
  return active instanceof HTMLInputElement ? active.dataset.filterKey ?? null : null;
}

function restoreFilterFocus(key: string | null): void {
  if (key === null) return;
  document.querySelectorAll<HTMLInputElement>("input[data-filter-key]").forEach((input) => {
    if (input.dataset.filterKey === key) input.focus();
  });
}

function renderFilterControls(facets: QueryFacets | null): void {
  const modelMetrics = new Map<string, FacetMetrics>((facets?.models ?? []).map((option) => [option.model, option]));
  availableModelNames = [...modelMetrics.keys()].sort();
  if (selectAllModelsMode) {
    selectedModels.clear();
    for (const model of availableModelNames) selectedModels.add(model);
  } else {
    for (const model of selectedModels) if (!modelMetrics.has(model)) modelMetrics.set(model, EMPTY_FACET_METRICS);
  }
  byId<HTMLElement>("model-options").replaceChildren(...[...modelMetrics.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([model, metrics]) =>
    inlineOption(model, metrics, selectedModels.has(model), metrics.canonicalTotalTokens === 0, `model:${model}`, (checked) => {
      if (checked) selectedModels.add(model); else selectedModels.delete(model);
      selectAllModelsMode = availableModelNames.length > 0 && availableModelNames.every((name) => selectedModels.has(name));
      renderFilterControls(latestResult?.facets ?? null);
      restoreFilterFocus(`model:${model}`);
      scheduleFilterQuery(0);
    }),
  ));

  const subjectMetrics = new Map<string, FacetMetrics>();
  for (const option of facets?.subjects ?? []) {
    const key = subjectKey(option.subject);
    subjectsByKey.set(key, option.subject);
    subjectMetrics.set(key, option);
  }
  availableSubjectKeys = [...subjectMetrics.keys()];
  if (selectAllSubjectsMode) {
    selectedSubjectKeys.clear();
    for (const key of availableSubjectKeys) selectedSubjectKeys.add(key);
  } else {
    for (const key of selectedSubjectKeys) if (!subjectMetrics.has(key)) subjectMetrics.set(key, EMPTY_FACET_METRICS);
  }
  const orderedSubjects = [...subjectMetrics.entries()].sort(([left], [right]) => {
    const leftSubject = subjectsByKey.get(left);
    const rightSubject = subjectsByKey.get(right);
    if (leftSubject === undefined || rightSubject === undefined) return left.localeCompare(right);
    const rank = (subject: SubjectFilter): number => subject.threadType === "main" ? 0 : subject.threadType === "subagent" ? 1 : 2;
    return rank(leftSubject) - rank(rightSubject) || leftSubject.agentRoleCategory.localeCompare(rightSubject.agentRoleCategory);
  });
  byId<HTMLElement>("subject-options").replaceChildren(...orderedSubjects.flatMap(([key, metrics]) => {
    const subject = subjectsByKey.get(key);
    if (subject === undefined) return [];
    return [inlineOption(subjectLabel(subject), metrics, selectedSubjectKeys.has(key), metrics.canonicalTotalTokens === 0, `subject:${key}`, (checked) => {
      if (checked) selectedSubjectKeys.add(key); else selectedSubjectKeys.delete(key);
      selectAllSubjectsMode = availableSubjectKeys.length > 0 && availableSubjectKeys.every((availableKey) => selectedSubjectKeys.has(availableKey));
      renderFilterControls(latestResult?.facets ?? null);
      restoreFilterFocus(`subject:${key}`);
      scheduleFilterQuery(0);
    })];
  }));
}

function card(label: string, value: string): HTMLElement {
  const element = document.createElement("article"); element.className = "metric";
  element.innerHTML = `<span>${label}</span><strong>${value}</strong>`; return element;
}

function renderSummary(summary: Summary): void {
  byId<HTMLElement>("summary").replaceChildren(
    card("总 tokens", compactTokens(summary.canonicalTotalTokens)), card("输入 tokens", compactTokens(summary.inputTokens)), card("输出 tokens", compactTokens(summary.outputTokens)), card("未定价 tokens", compactTokens(summary.unpricedTokens)), card("模型 token 费用", currency.format(summary.cost.total)),
  );
  const categories = [["无缓存输入", summary.cost.uncachedInput, "uncached"], ["缓存输入", summary.cost.cachedInput, "cached"], ["思考输出", summary.cost.reasoningOutput, "reasoning"], ["其他输出", summary.cost.otherOutput, "other"]] as const;
  byId<HTMLElement>("cost-bars").replaceChildren(...categories.map(([name, cost, className]) => {
    const row = document.createElement("div"); row.className = "cost-row";
    const percent = summary.cost.total === 0 ? 0 : cost / summary.cost.total * 100;
    row.innerHTML = `<span>${name}</span><div class="track"><i class="${className}" style="width:${percent}%"></i></div><b>${currency.format(cost)} · ${percent.toFixed(1)}%</b>`;
    return row;
  }));
}

function table(tableId: string, headers: readonly string[], rows: readonly (readonly string[])[]): void {
  const tableElement = byId<HTMLTableElement>(tableId); tableElement.replaceChildren();
  const head = document.createElement("thead"); const headerRow = document.createElement("tr");
  headers.forEach((header) => { const cell = document.createElement("th"); cell.textContent = header; headerRow.append(cell); }); head.append(headerRow);
  const body = document.createElement("tbody");
  rows.forEach((row) => { const tr = document.createElement("tr"); row.forEach((value) => { const cell = document.createElement("td"); cell.textContent = value; tr.append(cell); }); body.append(tr); });
  tableElement.append(head, body);
}

function percentage(cost: number, total: number): string { return total === 0 ? "-" : `${(cost / total * 100).toFixed(1)}%`; }

function threadTypeLabel(threadType: ThreadType): string {
  return threadType === "main" ? "主线程" : threadType === "subagent" ? "子代理" : "未知线程";
}

function displayedRole(threadType: ThreadType, roleCategory: AgentRoleCategory): string {
  return threadType === "main" ? "主线程" : roleCategory;
}

function summaryCells(summary: Summary, total: number): readonly string[] {
  return [compactTokens(summary.canonicalTotalTokens), compactTokens(summary.uncachedInputTokens), compactTokens(summary.cachedInputTokens), compactTokens(summary.outputTokens), compactTokens(summary.reasoningOutputTokens), currency.format(summary.cost.total), percentage(summary.cost.total, total)];
}

function modelRows(groups: readonly ModelGroupRow[], total: number): string[][] {
  return groups.map(({ key, summary }) => [key[0], ...summaryCells(summary, total)]);
}

function roleRows(groups: readonly RoleGroupRow[], total: number): string[][] {
  return groups.map(({ key, summary }) => [threadTypeLabel(key[0]), displayedRole(key[0], key[1]), ...summaryCells(summary, total)]);
}

function agentRows(groups: readonly AgentGroupRow[], total: number): string[][] {
  return groups.map(({ key, summary }) => [threadTypeLabel(key[0]), displayedRole(key[0], key[1]), key[2], key[3], ...summaryCells(summary, total)]);
}

function render(result: QueryResult): void {
  const filterFocus = focusedFilterKey();
  latestResult = result;
  renderFilterControls(result.facets);
  restoreFilterFocus(filterFocus);
  renderSummary(result.summary);
  const total = result.summary.cost.total;
  table("model-table", ["模型", "总 tokens", "无缓存输入", "缓存输入", "输出", "思考输出", "费用", "价格占比"], modelRows(result.byModel, total));
  table("role-table", ["类型", "角色", "总 tokens", "无缓存输入", "缓存输入", "输出", "思考输出", "费用", "价格占比"], roleRows(result.byRole, total));
  table("agent-table", ["类型", "角色", "agent path", "模型", "总 tokens", "无缓存输入", "缓存输入", "输出", "思考输出", "费用", "价格占比"], agentRows(result.byAgent, total));
  const d = result.diagnostics;
  setStatus(`本次运行处理 ${d.filesScanned} 个源文件批次. 跳过重复累计快照 ${d.duplicateSnapshotsSkipped}, 无拆分快照 ${d.zeroBreakdownSnapshotsSkipped}, 关系无效 ${d.invalidTokenRelationshipsSkipped}.`);
}

async function scan(): Promise<void> {
  const operation = ++operationSequence;
  const requestRevision = filterRevision;
  manualSyncActive = true;
  setStatus("正在同步变化的 Codex JSONL...");
  byId<HTMLButtonElement>("scan-button").disabled = true;
  try {
    const synced = await apiWindow.usageApi.syncNow();
    if (operation !== operationSequence) return;
    renderCollectorStatus(synced.status);
    const requestSequence = ++querySequence;
    const result = await apiWindow.usageApi.query(filterSpec());
    if (operation !== operationSequence || requestRevision !== filterRevision || requestSequence !== querySequence) return;
    render(result);
    filtersDirty = false;
  } catch (error) {
    if (operation === operationSequence) setStatus(error instanceof Error ? error.message : "同步失败.");
  } finally {
    manualSyncActive = false;
    byId<HTMLButtonElement>("scan-button").disabled = false;
    if (filtersDirty && liveFilterTimer === null) scheduleFilterQuery(0, false);
  }
}

async function apply(): Promise<void> {
  if (manualSyncActive) {
    scheduleFilterQuery(100, false);
    return;
  }
  if (latestResult === null) return scan();
  const operation = ++operationSequence;
  const requestRevision = filterRevision;
  try {
    const requestSequence = ++querySequence;
    const result = await apiWindow.usageApi.query(filterSpec());
    if (operation !== operationSequence || requestRevision !== filterRevision || requestSequence !== querySequence) return;
    render(result);
    filtersDirty = false;
  } catch (error) {
    if (operation === operationSequence && requestRevision === filterRevision) setStatus(error instanceof Error ? error.message : "筛选失败.");
  }
}

async function exportCsv(): Promise<void> {
  try { const result = await apiWindow.usageApi.exportCsv(filterSpec()); setStatus(result.path === null ? "已取消导出." : `已导出当前 token 与费用明细: ${result.path}`); } catch (error) { setStatus(error instanceof Error ? error.message : "导出失败."); }
}

setQuickRange(QUICK_RANGE_HOURS.length - 1, false);
byId<HTMLInputElement>("range-slider").addEventListener("input", (event) => {
  const target = event.currentTarget;
  if (!(target instanceof HTMLInputElement)) return;
  setQuickRange(Number(target.value), true);
});
for (const id of ["start", "end"] as const) byId<HTMLInputElement>(id).addEventListener("change", () => {
  byId<HTMLOutputElement>("range-output").value = "自定义";
  scheduleFilterQuery(0);
});
byId<HTMLInputElement>("path-query").addEventListener("input", () => scheduleFilterQuery(250));
byId<HTMLInputElement>("path-query").addEventListener("keydown", (event) => { if (event.key === "Enter") scheduleFilterQuery(0); });
byId<HTMLButtonElement>("clear-models").addEventListener("click", () => {
  selectAllModelsMode = true;
  selectedModels.clear();
  for (const model of availableModelNames) selectedModels.add(model);
  renderFilterControls(latestResult?.facets ?? null);
  scheduleFilterQuery(0);
});
byId<HTMLButtonElement>("clear-subjects").addEventListener("click", () => {
  selectAllSubjectsMode = true;
  selectedSubjectKeys.clear();
  for (const key of availableSubjectKeys) selectedSubjectKeys.add(key);
  renderFilterControls(latestResult?.facets ?? null);
  scheduleFilterQuery(0);
});
byId<HTMLButtonElement>("clear-filters").addEventListener("click", () => {
  selectAllModelsMode = true;
  selectAllSubjectsMode = true;
  selectedModels.clear();
  selectedSubjectKeys.clear();
  byId<HTMLInputElement>("path-query").value = "";
  renderFilterControls(latestResult?.facets ?? null);
  scheduleFilterQuery(0);
});
byId<HTMLButtonElement>("scan-button").addEventListener("click", () => void scan());
byId<HTMLButtonElement>("export-button").addEventListener("click", () => void exportCsv());

apiWindow.usageApi.onUsageUpdated((collectorStatus) => {
  renderCollectorStatus(collectorStatus);
  const stablePhase = collectorStatus.phase === "watching" || collectorStatus.phase === "degraded";
  if (!stablePhase || manualSyncActive) {
    cancelRefreshTimer();
    return;
  }
  if (document.visibilityState !== "visible" || latestResult === null || filtersDirty) return;
  cancelRefreshTimer();
  refreshTimer = window.setTimeout(() => {
    refreshTimer = null;
    if (document.visibilityState !== "visible" || latestResult === null || filtersDirty) return;
    void apply();
  }, 500);
});

async function initialize(): Promise<void> {
  const operation = ++operationSequence;
  const requestRevision = filterRevision;
  try {
    const collectorStatus = await apiWindow.usageApi.getCollectorStatus();
    if (operation !== operationSequence) return;
    renderCollectorStatus(collectorStatus);
    const requestSequence = ++querySequence;
    const result = await apiWindow.usageApi.query(filterSpec());
    if (operation !== operationSequence || requestRevision !== filterRevision || requestSequence !== querySequence) return;
    render(result);
    filtersDirty = false;
  } catch (error) {
    if (operation === operationSequence && requestRevision === filterRevision) setStatus(error instanceof Error ? error.message : "采集器初始化失败.");
  }
}

void initialize();
