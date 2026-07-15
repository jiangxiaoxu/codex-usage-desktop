import { AgentGroupRow, AgentRoleCategory, ConfiguredAgentRole, CostBreakdown, FilterSpec, GroupRow, ModelFacetOption, ModelGroupRow, QueryFacets, QueryResult, RoleGroupRow, ScanDiagnostics, SubjectFacetOption, Summary, ThreadType, UsageEvent } from "./shared";

const MILLION = 1_000_000;
const LONG_CONTEXT_LIMIT = 272_000;
const OTHER_MODEL_CATEGORY = "Others";
const UNKNOWN_ATTRIBUTION_CATEGORY = "Unknown attribution";
const SUPPORTED_MODEL_FAMILIES = ["gpt-5.6", "gpt-5.5", "gpt-5.4"] as const;

type SupportedModelFamily = typeof SUPPORTED_MODEL_FAMILIES[number];

interface ModelRate {
  readonly input: number;
  readonly cachedInput: number;
  readonly output: number;
}

const STANDARD_RATES: Readonly<Record<string, ModelRate>> = {
  "gpt-5.6-sol": { input: 5, cachedInput: 0.5, output: 30 },
  "gpt-5.6-terra": { input: 2.5, cachedInput: 0.25, output: 15 },
  "gpt-5.6-luna": { input: 1, cachedInput: 0.1, output: 6 },
  "gpt-5.5": { input: 5, cachedInput: 0.5, output: 30 },
  "gpt-5.4": { input: 2.5, cachedInput: 0.25, output: 15 },
  "gpt-5.4-mini": { input: 0.75, cachedInput: 0.075, output: 4.5 },
  "gpt-5.4-nano": { input: 0.2, cachedInput: 0.02, output: 1.25 },
};

interface MutableSummary {
  calls: number;
  inputTokens: number;
  cachedInputTokens: number;
  uncachedInputTokens: number;
  outputTokens: number;
  reasoningOutputTokens: number;
  otherOutputTokens: number;
  canonicalTotalTokens: number;
  unpricedTokens: number;
  cost: CostBreakdown;
}

function belongsToFamily(model: string, family: SupportedModelFamily): boolean {
  return model === family || model.startsWith(`${family}-`);
}

export function modelCategory(sourceModel: string): string {
  if (sourceModel === "unknown") return UNKNOWN_ATTRIBUTION_CATEGORY;
  return SUPPORTED_MODEL_FAMILIES.some((family) => belongsToFamily(sourceModel, family)) ? sourceModel : OTHER_MODEL_CATEGORY;
}

export function agentRoleCategory(threadType: ThreadType, rawRole: string, configuredRoles: ReadonlySet<string>): AgentRoleCategory {
  if (threadType === "main") return "main";
  return configuredRoles.has(rawRole) && rawRole !== "main" && rawRole !== "Others" ? rawRole as ConfiguredAgentRole : "Others";
}

export function costFor(event: UsageEvent): CostBreakdown {
  const category = modelCategory(event.model);
  if (category === OTHER_MODEL_CATEGORY) {
    return { uncachedInput: 0, cachedInput: 0, reasoningOutput: 0, otherOutput: 0, total: 0, priced: true };
  }
  if (category === UNKNOWN_ATTRIBUTION_CATEGORY) {
    return { uncachedInput: 0, cachedInput: 0, reasoningOutput: 0, otherOutput: 0, total: 0, priced: false };
  }
  const rate = STANDARD_RATES[event.model];
  if (rate === undefined) return { uncachedInput: 0, cachedInput: 0, reasoningOutput: 0, otherOutput: 0, total: 0, priced: false };
  const longContext = event.inputTokens > LONG_CONTEXT_LIMIT;
  const applyMultiplier = longContext && !belongsToFamily(event.model, "gpt-5.6");
  const inputMultiplier = applyMultiplier ? 2 : 1;
  const outputMultiplier = applyMultiplier ? 1.5 : 1;
  const uncachedInput = event.inputTokens - event.cachedInputTokens;
  const reasoningOutput = event.reasoningOutputTokens;
  const otherOutput = event.outputTokens - reasoningOutput;
  const uncached = uncachedInput * rate.input * inputMultiplier / MILLION;
  const cached = event.cachedInputTokens * rate.cachedInput * inputMultiplier / MILLION;
  const reasoning = reasoningOutput * rate.output * outputMultiplier / MILLION;
  const other = otherOutput * rate.output * outputMultiplier / MILLION;
  return { uncachedInput: uncached, cachedInput: cached, reasoningOutput: reasoning, otherOutput: other, total: uncached + cached + reasoning + other, priced: true };
}

function emptySummary(): MutableSummary {
  return { calls: 0, inputTokens: 0, cachedInputTokens: 0, uncachedInputTokens: 0, outputTokens: 0, reasoningOutputTokens: 0, otherOutputTokens: 0, canonicalTotalTokens: 0, unpricedTokens: 0, cost: { uncachedInput: 0, cachedInput: 0, reasoningOutput: 0, otherOutput: 0, total: 0, priced: true } };
}

export function summarize(events: Iterable<UsageEvent>): Summary {
  const summary = emptySummary();
  for (const event of events) {
    const cost = costFor(event);
    const canonicalTotalTokens = event.inputTokens + event.outputTokens;
    summary.calls += 1;
    summary.inputTokens += event.inputTokens;
    summary.cachedInputTokens += event.cachedInputTokens;
    summary.uncachedInputTokens += event.inputTokens - event.cachedInputTokens;
    summary.outputTokens += event.outputTokens;
    summary.reasoningOutputTokens += event.reasoningOutputTokens;
    summary.otherOutputTokens += event.outputTokens - event.reasoningOutputTokens;
    summary.canonicalTotalTokens += canonicalTotalTokens;
    if (!cost.priced) summary.unpricedTokens += canonicalTotalTokens;
    summary.cost = { uncachedInput: summary.cost.uncachedInput + cost.uncachedInput, cachedInput: summary.cost.cachedInput + cost.cachedInput, reasoningOutput: summary.cost.reasoningOutput + cost.reasoningOutput, otherOutput: summary.cost.otherOutput + cost.otherOutput, total: summary.cost.total + cost.total, priced: true };
  }
  return summary;
}

export function matchesFilter(event: UsageEvent, filter: FilterSpec, configuredRoles: ReadonlySet<string>): boolean {
  const time = Date.parse(event.timestampUtc);
  if (time < Date.parse(filter.startUtc) || time >= Date.parse(filter.endUtc)) return false;
  if (filter.models !== null && !filter.models.includes(modelCategory(event.model))) return false;
  const roleCategory = agentRoleCategory(event.threadType, event.agentRole, configuredRoles);
  if (filter.subjects !== null && !filter.subjects.some((subject) => subject.threadType === event.threadType && subject.agentRoleCategory === roleCategory)) return false;
  const query = filter.pathQuery.trim().toLocaleLowerCase();
  return !query || [event.agentPath, event.agentNickname, event.rolloutId, event.conversationId].join(" ").toLocaleLowerCase().includes(query);
}

function inDateScope(event: UsageEvent, filter: FilterSpec): boolean {
  const time = Date.parse(event.timestampUtc);
  return time >= Date.parse(filter.startUtc) && time < Date.parse(filter.endUtc);
}

function facets(events: readonly UsageEvent[], configuredRoles: ReadonlySet<string>): QueryFacets {
  const models = new Map<string, Omit<ModelFacetOption, "model">>();
  const subjects = new Map<string, SubjectFacetOption>();
  for (const event of events) {
    const category = modelCategory(event.model);
    const canonicalTotalTokens = event.inputTokens + event.outputTokens;
    const totalCost = costFor(event).total;
    const currentModel = models.get(category);
    models.set(category, {
      canonicalTotalTokens: (currentModel?.canonicalTotalTokens ?? 0) + canonicalTotalTokens,
      totalCost: (currentModel?.totalCost ?? 0) + totalCost,
    });
    const subject = { threadType: event.threadType, agentRoleCategory: agentRoleCategory(event.threadType, event.agentRole, configuredRoles) };
    const key = JSON.stringify([subject.threadType, subject.agentRoleCategory]);
    const current = subjects.get(key);
    subjects.set(key, {
      subject,
      canonicalTotalTokens: (current?.canonicalTotalTokens ?? 0) + canonicalTotalTokens,
      totalCost: (current?.totalCost ?? 0) + totalCost,
    });
  }
  const modelOptions: ModelFacetOption[] = [...models.entries()]
    .map(([model, metrics]) => ({ model, ...metrics }))
    .sort((left, right) => left.model.localeCompare(right.model));
  const subjectOptions = [...subjects.values()].sort((left, right) =>
    left.subject.threadType.localeCompare(right.subject.threadType) || left.subject.agentRoleCategory.localeCompare(right.subject.agentRoleCategory),
  );
  return { models: modelOptions, subjects: subjectOptions };
}

function group<Key extends readonly string[]>(events: readonly UsageEvent[], getKey: (event: UsageEvent) => Key): GroupRow<Key>[] {
  const buckets = new Map<string, { readonly key: Key; readonly events: UsageEvent[] }>();
  for (const event of events) {
    const key = getKey(event);
    const id = JSON.stringify(key);
    const bucket = buckets.get(id) ?? { key, events: [] };
    bucket.events.push(event);
    buckets.set(id, bucket);
  }
  return [...buckets.values()].map((bucket) => ({ key: bucket.key, summary: summarize(bucket.events) })).sort((left, right) => right.summary.cost.total - left.summary.cost.total);
}

export function query(events: readonly UsageEvent[], diagnostics: ScanDiagnostics, filter: FilterSpec, configuredRoles: ReadonlySet<string>): QueryResult {
  const dateScoped = events.filter((event) => inDateScope(event, filter));
  const selected = dateScoped.filter((event) => matchesFilter(event, filter, configuredRoles));
  return {
    summary: summarize(selected),
    byModel: group(selected, (event): ModelGroupRow["key"] => [modelCategory(event.model)]),
    byRole: group(selected, (event): RoleGroupRow["key"] => [event.threadType, agentRoleCategory(event.threadType, event.agentRole, configuredRoles)]),
    byAgent: group(selected, (event): AgentGroupRow["key"] => [event.threadType, agentRoleCategory(event.threadType, event.agentRole, configuredRoles), event.agentPath, modelCategory(event.model)]),
    facets: facets(dateScoped, configuredRoles),
    diagnostics,
  };
}

export function csvRows(events: readonly UsageEvent[], filter: FilterSpec, configuredRoles: ReadonlySet<string>): string {
  const headers = ["timestamp_sgt", "conversation_id", "rollout_id", "thread_type", "agent_role", "agent_path", "model_category", "source_model", "input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens", "other_output_tokens", "total_cost_usd"];
  const quote = (value: string | number): string => {
    const raw = String(value);
    const safe = /^[=+\-@\t\r]/.test(raw) ? `'${raw}` : raw;
    return `"${safe.replaceAll("\"", "\"\"")}"`;
  };
  const singaporeIso = (timestampUtc: string): string => {
    const parts = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Singapore", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" }).formatToParts(new Date(timestampUtc));
    const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((item) => item.type === type)?.value ?? "00";
    return `${part("year")}-${part("month")}-${part("day")}T${part("hour")}:${part("minute")}:${part("second")}+08:00`;
  };
  const rows = events.filter((event) => matchesFilter(event, filter, configuredRoles)).sort((left, right) => Date.parse(left.timestampUtc) - Date.parse(right.timestampUtc) || left.rolloutId.localeCompare(right.rolloutId) || left.tokenEventOrdinal - right.tokenEventOrdinal).map((event) => {
    const cost = costFor(event);
    const costText = cost.total.toFixed(12).replace(/\.?0+$/, "") || "0";
    return [singaporeIso(event.timestampUtc), event.conversationId, event.rolloutId, event.threadType, event.agentRole, event.agentPath, modelCategory(event.model), event.model, event.inputTokens, event.cachedInputTokens, event.outputTokens, event.reasoningOutputTokens, event.outputTokens - event.reasoningOutputTokens, costText].map(quote).join(",");
  });
  return `\uFEFF${headers.join(",")}\n${rows.join("\n")}\n`;
}
