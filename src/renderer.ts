type ThreadType = "main" | "subagent" | "unknown";

interface SubjectFilter {
  readonly threadType: ThreadType;
  readonly agentRole: string;
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
type RoleGroupRow = GroupRow<readonly [threadType: ThreadType, agentRole: string]>;
type AgentGroupRow = GroupRow<readonly [threadType: ThreadType, agentRole: string, agentPath: string, model: string]>;
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
interface StartupSettings { readonly supported: boolean; readonly enabled: boolean; }
interface UpdateStatus { readonly currentVersion: string; readonly latestVersion: string | null; readonly available: boolean; }
interface UsageApi { syncNow(): Promise<SyncResult>; query(filter: FilterSpec): Promise<QueryResult>; exportCsv(filter: FilterSpec): Promise<{ readonly path: string | null; readonly count: number }>; getCollectorStatus(): Promise<CollectorStatus>; getStartupSettings(): Promise<StartupSettings>; setStartupEnabled(enabled: boolean): Promise<StartupSettings>; checkForUpdates(): Promise<UpdateStatus>; openLatestRelease(): Promise<void>; onUpdateStatus(listener: (status: UpdateStatus) => void): () => void; onUsageUpdated(listener: (status: CollectorStatus) => void): () => void; }

const apiWindow = window as unknown as { readonly usageApi: UsageApi };

const byId = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (element === null) throw new Error(`Missing element: ${id}`);
  return element as T;
};

const currency = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", minimumFractionDigits: 1, maximumFractionDigits: 1 });
const integer = new Intl.NumberFormat("en-US");
const RANGE_ANCHOR_HOURS = [1, 4, 12, 24, 48, 96, 168, 336] as const;
const RANGE_UNITS_PER_SEGMENT = 504;
const RANGE_MAX = (RANGE_ANCHOR_HOURS.length - 1) * RANGE_UNITS_PER_SEGMENT;
const ROLLING_REFRESH_INTERVAL_MS = 60_000;
const TIME_RANGE_STORAGE_KEY = "codex-usage-desktop.time-range.v1";
const rangeNumber = new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 2 });
type TimeRangeSelection =
  | { readonly mode: "relative"; readonly selectedDurationHours: number }
  | { readonly mode: "custom"; readonly selectedDurationHours: number };

let timeRangeSelection: TimeRangeSelection = {
  mode: "relative",
  selectedDurationHours: RANGE_ANCHOR_HOURS[RANGE_ANCHOR_HOURS.length - 1],
};
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
let rollingRefreshTimer: number | null = null;
let filtersDirty = false;
let filterRevision = 0;
let querySequence = 0;
let operationSequence = 0;
let activeOperations = 0;
let manualSyncActive = false;
let latestUpdateStatus: UpdateStatus | null = null;

interface StoredTimeRange {
  readonly selectedDurationHours: number;
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null;
}

function loadStoredTimeRange(): StoredTimeRange | null {
  try {
    const raw = window.localStorage.getItem(TIME_RANGE_STORAGE_KEY);
    if (raw === null) return null;
    const value: unknown = JSON.parse(raw);
    if (!isRecord(value) || typeof value.selectedDurationHours !== "number" || !Number.isFinite(value.selectedDurationHours)) return null;
    const minimumHours = RANGE_ANCHOR_HOURS[0];
    const maximumHours = RANGE_ANCHOR_HOURS[RANGE_ANCHOR_HOURS.length - 1];
    if (value.selectedDurationHours < minimumHours || value.selectedDurationHours > maximumHours) return null;
    return { selectedDurationHours: value.selectedDurationHours };
  } catch {
    return null;
  }
}

function persistTimeRange(): void {
  if (timeRangeSelection.mode !== "relative") return;
  try {
    const stored: StoredTimeRange = { selectedDurationHours: timeRangeSelection.selectedDurationHours };
    window.localStorage.setItem(TIME_RANGE_STORAGE_KEY, JSON.stringify(stored));
  } catch {
    // Browser storage can be unavailable; the current selection still works.
  }
}

function subjectKey(subject: SubjectFilter): string {
  return JSON.stringify([subject.threadType, subject.agentRole]);
}

function subjectLabel(subject: SubjectFilter): string {
  if (subject.threadType === "main") return "主线程 · root";
  if (subject.threadType === "subagent") return `子代理 · ${subject.agentRole}`;
  return `未知线程 · ${subject.agentRole}`;
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
    if (manualSyncActive || activeOperations > 0) {
      scheduleFilterQuery(100, false);
      return;
    }
    void apply();
  }, delayMs);
}

function resetRollingRefreshTimer(): void {
  if (rollingRefreshTimer !== null) {
    window.clearTimeout(rollingRefreshTimer);
    rollingRefreshTimer = null;
  }
  if (document.visibilityState !== "visible" || latestResult === null || timeRangeSelection.mode !== "relative") return;
  rollingRefreshTimer = window.setTimeout(() => {
    rollingRefreshTimer = null;
    const queryCanStart = !manualSyncActive
      && liveFilterTimer === null
      && refreshTimer === null
      && activeOperations === 0;
    if (document.visibilityState === "visible" && latestResult !== null && timeRangeSelection.mode === "relative" && queryCanStart) {
      void apply();
    }
    resetRollingRefreshTimer();
  }, ROLLING_REFRESH_INTERVAL_MS);
}

function hoursForSliderValue(sliderValue: number): number {
  if (!Number.isFinite(sliderValue)) throw new RangeError(`Invalid range slider value: ${sliderValue}`);
  const boundedValue = Math.min(RANGE_MAX, Math.max(0, sliderValue));
  const segmentIndex = Math.min(Math.floor(boundedValue / RANGE_UNITS_PER_SEGMENT), RANGE_ANCHOR_HOURS.length - 2);
  const startHours = RANGE_ANCHOR_HOURS[segmentIndex];
  const endHours = RANGE_ANCHOR_HOURS[segmentIndex + 1];
  if (startHours === undefined || endHours === undefined) throw new RangeError(`Unknown range slider segment: ${segmentIndex}`);
  const progress = (boundedValue - segmentIndex * RANGE_UNITS_PER_SEGMENT) / RANGE_UNITS_PER_SEGMENT;
  return startHours + (endHours - startHours) * progress;
}

function sliderValueForHours(hours: number): number {
  if (!Number.isFinite(hours)) throw new RangeError(`Invalid range hours: ${hours}`);
  const boundedHours = Math.min(RANGE_ANCHOR_HOURS[RANGE_ANCHOR_HOURS.length - 1], Math.max(RANGE_ANCHOR_HOURS[0], hours));
  const segmentIndex = Math.min(
    RANGE_ANCHOR_HOURS.findIndex((anchorHours) => boundedHours <= anchorHours) - 1,
    RANGE_ANCHOR_HOURS.length - 2,
  );
  const boundedSegmentIndex = Math.max(0, segmentIndex);
  const startHours = RANGE_ANCHOR_HOURS[boundedSegmentIndex];
  const endHours = RANGE_ANCHOR_HOURS[boundedSegmentIndex + 1];
  if (startHours === undefined || endHours === undefined) throw new RangeError(`Unknown range hours segment: ${boundedSegmentIndex}`);
  const progress = (boundedHours - startHours) / (endHours - startHours);
  return (boundedSegmentIndex + progress) * RANGE_UNITS_PER_SEGMENT;
}

function formatRangeHours(hours: number): string {
  return hours < 24 ? `${rangeNumber.format(hours)}小时` : `${rangeNumber.format(hours / 24)}天`;
}

function setContinuousRange(sliderValue: number, applyImmediately: boolean): void {
  const hours = hoursForSliderValue(sliderValue);
  timeRangeSelection = { mode: "relative", selectedDurationHours: hours };
  resetRollingRefreshTimer();
  const end = new Date(Date.now());
  byId<HTMLInputElement>("end").value = singaporeDate(end);
  byId<HTMLInputElement>("start").value = singaporeDate(new Date(end.getTime() - hours * 60 * 60 * 1000));
  const slider = byId<HTMLInputElement>("range-slider");
  const label = formatRangeHours(hours);
  slider.value = String(sliderValue);
  slider.setAttribute("aria-valuetext", label);
  byId<HTMLOutputElement>("range-output").value = label;
  persistTimeRange();
  if (applyImmediately) scheduleFilterQuery(120);
}

function setCustomDateMode(enabled: boolean, applyImmediately: boolean): void {
  const recentRange = byId<HTMLElement>("recent-range");
  const dateRange = byId<HTMLElement>("date-range");
  const toggle = byId<HTMLButtonElement>("custom-date-toggle");
  recentRange.hidden = enabled;
  dateRange.hidden = !enabled;
  toggle.setAttribute("aria-expanded", String(enabled));
  toggle.textContent = enabled ? "使用近期时间范围" : "使用自定义日期";
  if (enabled) {
    timeRangeSelection = { mode: "custom", selectedDurationHours: timeRangeSelection.selectedDurationHours };
    resetRollingRefreshTimer();
    byId<HTMLOutputElement>("range-output").value = "自定义";
    byId<HTMLInputElement>("range-slider").setAttribute("aria-valuetext", "自定义时间范围");
    persistTimeRange();
    if (applyImmediately) scheduleFilterQuery(0);
    return;
  }
  setContinuousRange(Number(byId<HTMLInputElement>("range-slider").value), applyImmediately);
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
  let startUtc: Date;
  let endUtc: Date;
  if (timeRangeSelection.mode === "relative") {
    endUtc = new Date(Date.now());
    startUtc = new Date(endUtc.getTime() - timeRangeSelection.selectedDurationHours * 60 * 60 * 1000);
    byId<HTMLInputElement>("end").value = singaporeDate(endUtc);
    byId<HTMLInputElement>("start").value = singaporeDate(startUtc);
  } else {
    const start = byId<HTMLInputElement>("start").value;
    const end = byId<HTMLInputElement>("end").value;
    if (!start || !end) throw new Error("请选择开始和结束时间.");
    startUtc = new Date(`${start}:00+08:00`);
    endUtc = new Date(`${end}:00+08:00`);
    if (Number.isNaN(startUtc.valueOf()) || Number.isNaN(endUtc.valueOf()) || startUtc >= endUtc) throw new Error("时间范围无效.");
  }
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

function inlineOption(label: string, checked: boolean, unavailable: boolean, focusKey: string, onChange: (checked: boolean) => void): HTMLLabelElement {
  const wrapper = document.createElement("label");
  wrapper.className = `inline-option${unavailable ? " unavailable" : ""}`;
  const checkbox = document.createElement("input"); checkbox.type = "checkbox"; checkbox.checked = checked;
  checkbox.dataset.filterKey = focusKey;
  checkbox.addEventListener("change", () => onChange(checkbox.checked));
  const name = document.createElement("span"); name.className = "inline-option-name"; name.textContent = label;
  wrapper.append(checkbox, name);
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
  const availableModels = new Set((facets?.models ?? []).map((option) => option.model));
  availableModelNames = [...availableModels].sort();
  const visibleModels = new Set(availableModels);
  if (selectAllModelsMode) {
    selectedModels.clear();
    for (const model of availableModelNames) selectedModels.add(model);
  } else {
    for (const model of selectedModels) visibleModels.add(model);
  }
  byId<HTMLElement>("model-options").replaceChildren(...[...visibleModels].sort().map((model) =>
    inlineOption(model, selectedModels.has(model), !availableModels.has(model), `model:${model}`, (checked) => {
      if (checked) selectedModels.add(model); else selectedModels.delete(model);
      selectAllModelsMode = availableModelNames.length > 0 && availableModelNames.every((name) => selectedModels.has(name));
      renderFilterControls(latestResult?.facets ?? null);
      restoreFilterFocus(`model:${model}`);
      scheduleFilterQuery(0);
    }),
  ));

  const availableSubjects = new Set<string>();
  for (const option of facets?.subjects ?? []) {
    const key = subjectKey(option.subject);
    subjectsByKey.set(key, option.subject);
    availableSubjects.add(key);
  }
  availableSubjectKeys = [...availableSubjects];
  const visibleSubjects = new Set(availableSubjects);
  if (selectAllSubjectsMode) {
    selectedSubjectKeys.clear();
    for (const key of availableSubjectKeys) selectedSubjectKeys.add(key);
  } else {
    for (const key of selectedSubjectKeys) visibleSubjects.add(key);
  }
  const orderedSubjects = [...visibleSubjects].sort((left, right) => {
    const leftSubject = subjectsByKey.get(left);
    const rightSubject = subjectsByKey.get(right);
    if (leftSubject === undefined || rightSubject === undefined) return left.localeCompare(right);
    const rank = (subject: SubjectFilter): number => subject.threadType === "main" ? 0 : subject.threadType === "subagent" ? 1 : 2;
    return rank(leftSubject) - rank(rightSubject) || leftSubject.agentRole.localeCompare(rightSubject.agentRole);
  });
  byId<HTMLElement>("subject-options").replaceChildren(...orderedSubjects.flatMap((key) => {
    const subject = subjectsByKey.get(key);
    if (subject === undefined) return [];
    return [inlineOption(subjectLabel(subject), selectedSubjectKeys.has(key), !availableSubjects.has(key), `subject:${key}`, (checked) => {
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

function displayedRole(threadType: ThreadType, agentRole: string): string {
  return threadType === "main" ? "root" : agentRole;
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
  resetRollingRefreshTimer();
}

async function scan(): Promise<void> {
  const operation = ++operationSequence;
  const requestRevision = filterRevision;
  activeOperations += 1;
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
    activeOperations -= 1;
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
  activeOperations += 1;
  try {
    const requestSequence = ++querySequence;
    const result = await apiWindow.usageApi.query(filterSpec());
    if (operation !== operationSequence || requestRevision !== filterRevision || requestSequence !== querySequence) return;
    render(result);
    filtersDirty = false;
  } catch (error) {
    if (operation === operationSequence && requestRevision === filterRevision) setStatus(error instanceof Error ? error.message : "筛选失败.");
  } finally {
    activeOperations -= 1;
  }
}

async function exportCsv(): Promise<void> {
  try { const result = await apiWindow.usageApi.exportCsv(filterSpec()); setStatus(result.path === null ? "已取消导出." : `已导出当前 token 与费用明细: ${result.path}`); } catch (error) { setStatus(error instanceof Error ? error.message : "导出失败."); }
}

const storedTimeRange = loadStoredTimeRange();
setContinuousRange(sliderValueForHours(storedTimeRange?.selectedDurationHours ?? RANGE_ANCHOR_HOURS[RANGE_ANCHOR_HOURS.length - 1]), false);
byId<HTMLInputElement>("range-slider").addEventListener("input", (event) => {
  const target = event.currentTarget;
  if (!(target instanceof HTMLInputElement)) return;
  setContinuousRange(Number(target.value), true);
});
for (const tick of document.querySelectorAll<HTMLButtonElement>(".range-tick")) {
  tick.addEventListener("click", () => {
    const hours = Number(tick.dataset.hours);
    if (!Number.isFinite(hours)) throw new RangeError(`Invalid range tick hours: ${tick.dataset.hours ?? "missing"}`);
    setContinuousRange(sliderValueForHours(hours), true);
  });
}
byId<HTMLButtonElement>("custom-date-toggle").addEventListener("click", (event) => {
  const toggle = event.currentTarget;
  if (!(toggle instanceof HTMLButtonElement)) return;
  setCustomDateMode(toggle.getAttribute("aria-expanded") !== "true", true);
});
for (const id of ["start", "end"] as const) byId<HTMLInputElement>(id).addEventListener("change", () => {
  if (timeRangeSelection.mode !== "custom") setCustomDateMode(true, false);
  persistTimeRange();
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

function renderUpdateStatus(status: UpdateStatus | null): void {
  const button = byId<HTMLButtonElement>("update-button");
  button.disabled = false;
  if (status?.available && status.latestVersion !== null) {
    button.textContent = `下载 v${status.latestVersion}`;
    button.setAttribute("aria-label", `发现 v${status.latestVersion},打开下载页面`);
    return;
  }
  button.textContent = "检查更新";
  button.setAttribute("aria-label", "检查 GitHub Release 更新");
}

async function checkForUpdates(showResult: boolean): Promise<void> {
  const button = byId<HTMLButtonElement>("update-button");
  button.disabled = true;
  button.textContent = "正在检查";
  try {
    const status = await apiWindow.usageApi.checkForUpdates();
    latestUpdateStatus = status;
    renderUpdateStatus(status);
    if (status.available && status.latestVersion !== null) setStatus(`发现新版本 v${status.latestVersion},可点击“下载 v${status.latestVersion}”前往 GitHub Release。`);
    else if (showResult) setStatus("当前已是最新版本。");
  } catch (error) {
    latestUpdateStatus = null;
    renderUpdateStatus(null);
    if (showResult) setStatus(error instanceof Error ? `检查更新失败: ${error.message}` : "检查更新失败。");
  }
}

byId<HTMLButtonElement>("update-button").addEventListener("click", () => {
  if (latestUpdateStatus?.available) {
    void apiWindow.usageApi.openLatestRelease().catch((error: unknown) => setStatus(error instanceof Error ? error.message : "无法打开下载页面。"));
    return;
  }
  void checkForUpdates(true);
});

apiWindow.usageApi.onUpdateStatus((status) => {
  latestUpdateStatus = status;
  renderUpdateStatus(status);
  if (status.available && status.latestVersion !== null) setStatus(`发现新版本 v${status.latestVersion},可点击“下载 v${status.latestVersion}”前往 GitHub Release。`);
});

function renderStartupSettings(settings: StartupSettings): void {
  const off = byId<HTMLButtonElement>("startup-off");
  const on = byId<HTMLButtonElement>("startup-on");
  off.disabled = !settings.supported;
  on.disabled = !settings.supported;
  off.setAttribute("aria-pressed", String(!settings.enabled));
  on.setAttribute("aria-pressed", String(settings.enabled));
}

function updateStartupSetting(enabled: boolean): void {
  renderStartupSettings({ supported: false, enabled });
  void apiWindow.usageApi.setStartupEnabled(enabled)
    .then(renderStartupSettings)
    .catch((error: unknown) => { void apiWindow.usageApi.getStartupSettings().then(renderStartupSettings); setStatus(error instanceof Error ? error.message : "更新开机自启动失败."); });
}

byId<HTMLButtonElement>("startup-off").addEventListener("click", () => updateStartupSetting(false));
byId<HTMLButtonElement>("startup-on").addEventListener("click", () => updateStartupSetting(true));

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

document.addEventListener("visibilitychange", () => {
  resetRollingRefreshTimer();
  if (document.visibilityState !== "visible" || latestResult === null) return;
  cancelRefreshTimer();
  if (manualSyncActive || activeOperations > 0) {
    if (liveFilterTimer === null) scheduleFilterQuery(100, false);
    return;
  }
  if (filtersDirty || liveFilterTimer !== null) {
    if (liveFilterTimer === null) scheduleFilterQuery(0, false);
    return;
  }
  void apply();
});

async function initialize(): Promise<void> {
  const operation = ++operationSequence;
  const requestRevision = filterRevision;
  activeOperations += 1;
  try {
    const settings = await apiWindow.usageApi.getStartupSettings();
    renderStartupSettings(settings);
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
  } finally {
    activeOperations -= 1;
  }
}

void initialize().finally(() => { void checkForUpdates(false); });
