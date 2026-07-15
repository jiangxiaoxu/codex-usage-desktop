import assert from "node:assert/strict";
import test from "node:test";
import type { FilterSpec, UsageEvent } from "./shared";
import { agentRoleCategory, costFor, csvRows, modelCategory, query, summarize } from "./usage-core";

const event: UsageEvent = {
  timestampUtc: "2026-07-15T01:00:00.000Z",
  tokenEventOrdinal: 0,
  conversationId: "conversation",
  rolloutId: "rollout",
  parentThreadId: "",
  threadType: "main",
  agentRole: "main",
  agentPath: "/root",
  agentNickname: "",
  model: "gpt-5.6-sol",
  inputTokens: 1_000_000,
  cachedInputTokens: 800_000,
  outputTokens: 100_000,
  reasoningOutputTokens: 70_000,
};

const filter: FilterSpec = {
  startUtc: "2026-07-15T00:00:00.000Z",
  endUtc: "2026-07-16T00:00:00.000Z",
  models: null,
  subjects: null,
  pathQuery: "",
};

const configuredRoles: ReadonlySet<string> = new Set(["awaiter", "bounded_worker", "explorer", "reviewer", "scout", "worker"]);
const workerRole = agentRoleCategory("subagent", "worker", configuredRoles);

test("reasoning output remains a subset of output cost and GPT-5.6 always ignores the long-context multiplier", () => {
  const cost = costFor(event);
  assert.equal(cost.uncachedInput, 1);
  assert.equal(cost.cachedInput, 0.4);
  assert.equal(cost.reasoningOutput, 2.1);
  assert.equal(cost.otherOutput, 0.9);
  assert.equal(cost.total, 4.4);
  assert.equal(summarize([event]).canonicalTotalTokens, 1_100_000);

  const gpt54Cost = costFor({ ...event, model: "gpt-5.4", cachedInputTokens: 0 });
  assert.equal(gpt54Cost.uncachedInput, 5, "non-GPT-5.6 long contexts retain the input multiplier");
  assert.equal(gpt54Cost.reasoningOutput, 1.575, "non-GPT-5.6 long contexts retain the output multiplier");
});

test("CSV exposes model category and raw source model while formatting cost and SGT", () => {
  const otherEvent = { ...event, rolloutId: "other", tokenEventOrdinal: 1, model: "o3" };
  const csv = csvRows([event, otherEvent], filter, configuredRoles);
  assert.match(csv, /^\uFEFFtimestamp_sgt,conversation_id,rollout_id,thread_type,agent_role,agent_path,model_category,source_model,/);
  assert.match(csv, /2026-07-15T09:00:00\+08:00/);
  assert.match(csv, /,"gpt-5\.6-sol","gpt-5\.6-sol",/);
  assert.match(csv, /,"Others","o3",/);
  assert.match(csv, /,"4\.4"\r?\n/);
  assert.doesNotMatch(csv, /4\.400000000000001/);

  const unknownCsv = csvRows([{ ...event, model: "unknown" }], filter, configuredRoles);
  assert.match(unknownCsv, /,"Unknown attribution","unknown",/);
});

test("model categories retain supported families, isolate unknown attribution, and aggregate other source models", () => {
  const events: readonly UsageEvent[] = [
    event,
    { ...event, rolloutId: "exact-family", tokenEventOrdinal: 1, model: "gpt-5.6" },
    { ...event, rolloutId: "supported-variant", tokenEventOrdinal: 2, model: "gpt-5.6-preview" },
    { ...event, rolloutId: "other-o3", tokenEventOrdinal: 3, model: "o3" },
    { ...event, rolloutId: "other-unknown", tokenEventOrdinal: 4, model: "unknown" },
  ];
  const diagnostics = { filesScanned: 0, malformedLines: 0, duplicateSnapshotsSkipped: 0, zeroBreakdownSnapshotsSkipped: 0, invalidTokenRelationshipsSkipped: 0 };
  const result = query(events, diagnostics, filter, configuredRoles);

  assert.equal(modelCategory("gpt-5.4-mini"), "gpt-5.4-mini");
  assert.equal(modelCategory("gpt-5.5"), "gpt-5.5");
  assert.equal(modelCategory("gpt-5.6-preview"), "gpt-5.6-preview");
  assert.equal(modelCategory("unknown"), "Unknown attribution");
  assert.equal(modelCategory("Unknown"), "Others", "unknown attribution recognition remains exact and case-sensitive");
  assert.equal(modelCategory("gpt-5.60-preview"), "Others", "family recognition requires an exact name or hyphen separator");
  assert.equal(modelCategory("GPT-5.6-sol"), "Others", "classification remains case-sensitive");

  assert.deepEqual(new Map(result.facets.models.map((option) => [option.model, { canonicalTotalTokens: option.canonicalTotalTokens, totalCost: option.totalCost }])), new Map([
    ["gpt-5.6-sol", { canonicalTotalTokens: 1_100_000, totalCost: 4.4 }],
    ["gpt-5.6", { canonicalTotalTokens: 1_100_000, totalCost: 0 }],
    ["gpt-5.6-preview", { canonicalTotalTokens: 1_100_000, totalCost: 0 }],
    ["Others", { canonicalTotalTokens: 1_100_000, totalCost: 0 }],
    ["Unknown attribution", { canonicalTotalTokens: 1_100_000, totalCost: 0 }],
  ]));
  assert.equal(result.byModel.find((row) => row.key[0] === "Others")?.summary.calls, 1);
  assert.equal(result.byModel.find((row) => row.key[0] === "Unknown attribution")?.summary.unpricedTokens, 1_100_000);
  assert.deepEqual(result.byAgent.filter((row) => row.key[3] === "Others").map((row) => row.key[3]), ["Others"]);
  assert.ok(result.byModel.some((row) => row.key[0] === "gpt-5.6"));
  assert.ok(result.byModel.some((row) => row.key[0] === "gpt-5.6-preview"), "supported variants remain distinct categories");
});

test("model filter distinguishes all, none, Others, unknown attribution, and supported categories", () => {
  const events: readonly UsageEvent[] = [
    event,
    { ...event, rolloutId: "other-o3", tokenEventOrdinal: 1, model: "o3" },
    { ...event, rolloutId: "other-unknown", tokenEventOrdinal: 2, model: "unknown" },
    { ...event, rolloutId: "variant", tokenEventOrdinal: 3, model: "gpt-5.6-preview" },
  ];
  const diagnostics = { filesScanned: 0, malformedLines: 0, duplicateSnapshotsSkipped: 0, zeroBreakdownSnapshotsSkipped: 0, invalidTokenRelationshipsSkipped: 0 };

  assert.equal(query(events, diagnostics, { ...filter, models: null }, configuredRoles).summary.calls, 4);
  assert.equal(query(events, diagnostics, { ...filter, models: [] }, configuredRoles).summary.calls, 0);
  assert.equal(query(events, diagnostics, { ...filter, models: ["Others"] }, configuredRoles).summary.calls, 1);
  assert.equal(query(events, diagnostics, { ...filter, models: ["Unknown attribution"] }, configuredRoles).summary.calls, 1);
  assert.equal(query(events, diagnostics, { ...filter, models: ["gpt-5.6-preview"] }, configuredRoles).summary.calls, 1);
});

test("Others is priced at zero while unknown attribution and unpriced supported variants accumulate unpriced tokens", () => {
  const otherEvent = { ...event, model: "o3" };
  const unknownEvent = { ...event, model: "unknown" };
  const supportedUnpricedEvent = { ...event, model: "gpt-5.6-preview" };

  assert.deepEqual(costFor(otherEvent), { uncachedInput: 0, cachedInput: 0, reasoningOutput: 0, otherOutput: 0, total: 0, priced: true });
  assert.equal(summarize([otherEvent]).unpricedTokens, 0);
  assert.deepEqual(costFor(unknownEvent), { uncachedInput: 0, cachedInput: 0, reasoningOutput: 0, otherOutput: 0, total: 0, priced: false });
  assert.equal(summarize([unknownEvent]).unpricedTokens, 1_100_000);
  assert.equal(costFor(supportedUnpricedEvent).priced, false);
  assert.equal(summarize([supportedUnpricedEvent]).unpricedTokens, 1_100_000);
  assert.equal(summarize([otherEvent, unknownEvent, supportedUnpricedEvent]).unpricedTokens, 2_200_000);
});

test("subject filter uses query-time role categories while facets ignore active selectors", () => {
  const events: readonly UsageEvent[] = [
    event,
    { ...event, rolloutId: "worker", tokenEventOrdinal: 1, threadType: "subagent", agentRole: "worker", model: "gpt-5.6-terra" },
    { ...event, rolloutId: "cross-product-role", tokenEventOrdinal: 2, threadType: "main", agentRole: "worker", model: "gpt-5.6-terra" },
    { ...event, rolloutId: "cross-product-thread", tokenEventOrdinal: 3, threadType: "subagent", agentRole: "main", model: "gpt-5.6-luna" },
    { ...event, rolloutId: "outside-date", tokenEventOrdinal: 4, timestampUtc: "2026-07-16T00:00:00.000Z", model: "gpt-5.4" },
  ];
  const diagnostics = { filesScanned: 0, malformedLines: 0, duplicateSnapshotsSkipped: 0, zeroBreakdownSnapshotsSkipped: 0, invalidTokenRelationshipsSkipped: 0 };
  const subjects = [{ threadType: "main", agentRoleCategory: "main" }, { threadType: "subagent", agentRoleCategory: workerRole }] as const;
  assert.equal(query(events, diagnostics, { ...filter, subjects: null }, configuredRoles).summary.calls, 4, "null selects every subject in range");
  assert.equal(query(events, diagnostics, { ...filter, subjects: [] }, configuredRoles).summary.calls, 0, "an empty subject list selects none");
  assert.equal(query(events, diagnostics, { ...filter, subjects }, configuredRoles).summary.calls, 3, "all raw main-thread roles share the main category");

  const result = query(events, diagnostics, {
    ...filter,
    models: ["gpt-5.6-sol"],
    subjects,
  }, configuredRoles);

  assert.equal(result.summary.calls, 1, "model filter applies after exact subject union");
  assert.deepEqual(result.facets.models, [
    { model: "gpt-5.6-luna", canonicalTotalTokens: 1_100_000, totalCost: costFor(events[3]).total },
    { model: "gpt-5.6-sol", canonicalTotalTokens: 1_100_000, totalCost: costFor(events[0]).total },
    { model: "gpt-5.6-terra", canonicalTotalTokens: 2_200_000, totalCost: costFor(events[1]).total + costFor(events[2]).total },
  ]);
  assert.deepEqual(result.facets.subjects, [
    { subject: { threadType: "main", agentRoleCategory: "main" }, canonicalTotalTokens: 2_200_000, totalCost: costFor(events[0]).total + costFor(events[2]).total },
    { subject: { threadType: "subagent", agentRoleCategory: "Others" }, canonicalTotalTokens: 1_100_000, totalCost: costFor(events[3]).total },
    { subject: { threadType: "subagent", agentRoleCategory: workerRole }, canonicalTotalTokens: 1_100_000, totalCost: costFor(events[1]).total },
  ]);
});

test("agent roles are categorized from the configured-role set and unsupported raw roles merge into Others", () => {
  const diagnostics = { filesScanned: 0, malformedLines: 0, duplicateSnapshotsSkipped: 0, zeroBreakdownSnapshotsSkipped: 0, invalidTokenRelationshipsSkipped: 0 };
  const events: readonly UsageEvent[] = [
    event,
    { ...event, rolloutId: "configured", tokenEventOrdinal: 1, threadType: "subagent", agentRole: "worker", agentPath: "/root/configured" },
    { ...event, rolloutId: "default", tokenEventOrdinal: 2, threadType: "subagent", agentRole: "default", agentPath: "/root/default" },
    { ...event, rolloutId: "unknown", tokenEventOrdinal: 3, threadType: "subagent", agentRole: "unknown", agentPath: "/root/unknown" },
    { ...event, rolloutId: "heavy", tokenEventOrdinal: 4, threadType: "subagent", agentRole: "worker_heavy", agentPath: "/root/heavy" },
  ];

  assert.equal(agentRoleCategory("main", "worker_heavy", configuredRoles), "main");
  assert.equal(agentRoleCategory("subagent", "worker", configuredRoles), workerRole);
  assert.equal(agentRoleCategory("subagent", "default", configuredRoles), "Others");

  const result = query(events, diagnostics, filter, configuredRoles);
  assert.deepEqual(result.facets.subjects.map((option) => [option.subject.threadType, option.subject.agentRoleCategory, option.canonicalTotalTokens]), [
    ["main", "main", 1_100_000],
    ["subagent", "Others", 3_300_000],
    ["subagent", "worker", 1_100_000],
  ]);
  assert.equal(result.byRole.find((row) => row.key[1] === "Others")?.summary.calls, 3);
  assert.equal(result.byAgent.filter((row) => row.key[1] === "Others").length, 3, "agent paths remain distinct while each role category is Others");
  assert.equal(query(events, diagnostics, { ...filter, subjects: [{ threadType: "subagent", agentRoleCategory: "Others" }] }, configuredRoles).summary.calls, 3);

  const csv = csvRows(events, filter, configuredRoles);
  assert.match(csv, /,"subagent","default","\/root\/default",/);
  assert.match(csv, /,"subagent","worker_heavy","\/root\/heavy",/);
});
