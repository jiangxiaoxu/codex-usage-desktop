import assert from "node:assert/strict";
import { appendFile, mkdir, mkdtemp, readFile, rename, rm, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { CollectorClient } from "./collector-client";
import type { CollectorConfig } from "./collector-protocol";
import type { FilterSpec, QueryResult } from "./shared";
import { UsageStore, type CandidateSourceInput, type SourceFileRecord, type UsageEventInput } from "./usage-store";

function line(type: string, payload: Readonly<Record<string, unknown>>, timestamp = "2026-07-15T01:00:00.000Z"): string {
  return `${JSON.stringify({ timestamp, type, payload })}\n`;
}

function token(
  last: readonly [number, number, number, number, number],
  total: readonly [number, number, number, number, number],
  timestamp: string,
): string {
  const tuple = ([input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens]: readonly number[]) => ({ input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens });
  return line("event_msg", { type: "token_count", info: { last_token_usage: tuple(last), total_token_usage: tuple(total) } }, timestamp);
}

const filter: FilterSpec = {
  startUtc: "2026-07-01T00:00:00.000Z",
  endUtc: "2026-08-01T00:00:00.000Z",
  models: null,
  subjects: null,
  pathQuery: "",
};

async function query(client: CollectorClient): Promise<QueryResult> {
  return client.request("query", filter);
}

function nextUsageUpdate(client: CollectorClient): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const onUpdate = (): void => {
      clearTimeout(timeout);
      resolve();
    };
    const timeout = setTimeout(() => {
      client.off("usage-updated", onUpdate);
      reject(new Error("Timed out waiting for watcher-driven reconciliation."));
    }, 10_000);
    client.once("usage-updated", onUpdate);
  });
}

function candidateSource(record: SourceFileRecord): CandidateSourceInput {
  const { rolloutId: _rolloutId, ...source } = record;
  return source;
}

function contaminatedEvent(inputTokens: number, tokenEventOrdinal = 0): UsageEventInput {
  return {
    tokenEventOrdinal,
    timestampEpochMs: Date.parse(`2026-07-15T0${tokenEventOrdinal + 1}:00:00.000Z`),
    model: "gpt-5.6-sol",
    inputTokens,
    cachedInputTokens: 0,
    outputTokens: 1,
    reasoningOutputTokens: 0,
    eventSignature: `contaminated-${tokenEventOrdinal}-${inputTokens}`,
  };
}

function contaminateRollout(databasePath: string, rolloutId: string, events: readonly UsageEventInput[]): void {
  const ledger = new UsageStore(databasePath);
  try {
    const metadata = ledger.getRolloutMetadata(rolloutId);
    const source = ledger.listSourceFiles().find((candidate) => candidate.rolloutId === rolloutId && candidate.canonicalStatus === "canonical");
    assert.ok(metadata);
    assert.ok(source);
    ledger.replaceRolloutCandidate({ metadata, events, source: candidateSource(source), observedAtEpochMs: Date.now() });
    ledger.promoteRolloutCandidate({ rolloutId, canonicalFilePath: source.filePath, promotedAtEpochMs: Date.now() });
    ledger.setCollectorState("rollout_parser_revision", "1", Date.now());
  } finally {
    ledger.close();
  }
}

test("collector remains read-only while Codex appends, archives, and deletes sources", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-collector-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions", "2026", "07", "15");
  const archived = path.join(codexHome, "archived_sessions");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  const activePath = path.join(sessions, "rollout-integration.jsonl");
  await writeFile(activePath,
    line("session_meta", { session_id: "conversation", id: "rollout-integration", thread_source: "user" })
      + line("turn_context", { turn_id: "turn-1", model: "gpt-5.6-sol" })
      + token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14], "2026-07-15T01:00:00.000Z")
      + token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14], "2026-07-15T01:00:01.000Z"),
    "utf8",
  );
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "data", "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  let client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.equal((await query(client)).summary.inputTokens, 10, "duplicate cumulative snapshots must not be billed twice");

  const partial = token([20, 4, 6, 2, 26], [30, 6, 10, 3, 40], "2026-07-15T02:00:00.000Z").trimEnd();
  await appendFile(activePath, partial, "utf8");
  await client.request("reconcile", null);
  assert.equal((await query(client)).summary.inputTokens, 10, "unterminated tail must remain provisional");
  await appendFile(activePath, "\n", "utf8");
  await client.request("reconcile", null);
  assert.equal((await query(client)).summary.inputTokens, 30, "completed append must be ingested once");
  assert.equal((await client.request("exportCsv", { filter, filePath: path.join(root, "usage.csv") })).count, 2, "CSV count uses direct event filtering");

  const archivePath = path.join(archived, path.basename(activePath));
  await rename(activePath, archivePath);
  await client.request("reconcile", null);
  assert.equal((await query(client)).summary.inputTokens, 30, "archive move must not duplicate usage");
  await rm(archivePath);
  await client.request("reconcile", null);
  assert.equal((await query(client)).summary.inputTokens, 30, "deleting an ingested archive must preserve the ledger");
  await client.close();

  client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.equal((await query(client)).summary.inputTokens, 30, "restart must restore the permanent ledger");
  await client.close();
  const database = await readFile(config.databasePath);
  assert.ok(database.byteLength > 0);
});

test("collector uses roles from actual rollout threads and ignores the read-only TOML inventory", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-agent-roles-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const agents = path.join(codexHome, "agents");
  await mkdir(sessions, { recursive: true });
  await mkdir(agents, { recursive: true });
  const rollout = (id: string, role: string | null, inputTokens: number, timestamp: string): string => {
    const source = role === null ? undefined : {
      subagent: { thread_spawn: { parent_thread_id: "parent", agent_role: role, agent_path: `/root/${role}` } },
    };
    return line("session_meta", { session_id: `conversation-${id}`, id, thread_source: role === null ? "user" : "subagent", source })
      + line("turn_context", { turn_id: `turn-${id}`, model: "gpt-5.6-sol" })
      + token([inputTokens, 0, 1, 0, inputTokens + 1], [inputTokens, 0, 1, 0, inputTokens + 1], timestamp);
  };
  await writeFile(path.join(agents, "worker.toml"), "name = \"worker\"\n", "utf8");
  await writeFile(path.join(sessions, "rollout-main.jsonl"), rollout("main", null, 40, "2026-07-15T01:00:00.000Z"), "utf8");
  await writeFile(path.join(sessions, "rollout-worker.jsonl"), rollout("worker", "worker", 30, "2026-07-15T02:00:00.000Z"), "utf8");
  await writeFile(path.join(sessions, "rollout-scout.jsonl"), rollout("scout", "scout", 20, "2026-07-15T03:00:00.000Z"), "utf8");
  await writeFile(path.join(sessions, "rollout-default.jsonl"), rollout("default", "default", 15, "2026-07-15T04:00:00.000Z"), "utf8");
  await writeFile(path.join(sessions, "rollout-unknown.jsonl"), rollout("unknown", "unknown", 10, "2026-07-15T05:00:00.000Z"), "utf8");
  await writeFile(path.join(sessions, "rollout-subagent-main.jsonl"), rollout("subagent-main", "main", 5, "2026-07-15T06:00:00.000Z"), "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  const client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });

  await client.initialize(config);
  const initial = await query(client);
  const sortedRoleKeys = (result: QueryResult): readonly (readonly string[])[] => result.byRole
    .map((row) => row.key)
    .sort((left, right) => JSON.stringify(left).localeCompare(JSON.stringify(right)));
  assert.deepEqual(sortedRoleKeys(initial), [
    ["main", "root"],
    ["subagent", "default"],
    ["subagent", "main"],
    ["subagent", "scout"],
    ["subagent", "unknown"],
    ["subagent", "worker"],
  ]);
  const mainSubject = initial.facets.subjects.find((option) => option.subject.threadType === "main" && option.subject.agentRole === "root")?.subject;
  assert.ok(mainSubject, "missing normalized root subject from the main rollout thread");
  const mainExport = await client.request("exportCsv", { filter: { ...filter, subjects: [mainSubject] }, filePath: path.join(root, "root.csv") });
  assert.equal(mainExport.count, 1, "root filter exports only the main rollout");
  assert.match(await readFile(path.join(root, "root.csv"), "utf8"), /,"main","root","\/root",/);
  for (const role of ["worker", "scout", "default", "unknown", "main"] as const) {
    const subject = initial.facets.subjects.find((option) => option.subject.threadType === "subagent" && option.subject.agentRole === role)?.subject;
    assert.ok(subject, `missing ${role} subject from its rollout thread`);
    const roleFilter: FilterSpec = { ...filter, subjects: [subject] };
    const exportResult = await client.request("exportCsv", { filter: roleFilter, filePath: path.join(root, `${role}.csv`) });
    assert.equal(exportResult.count, 1, `${role} filter exports only its own rollout`);
    assert.match(await readFile(path.join(root, `${role}.csv`), "utf8"), new RegExp(`,\\\"${role}\\\",`));
  }

  await writeFile(path.join(agents, "scout.toml"), "name = \"scout\"\n", "utf8");
  await rm(path.join(agents, "worker.toml"));
  await client.request("reconcile", null);
  const afterInventoryChange = await query(client);
  assert.deepEqual(sortedRoleKeys(afterInventoryChange), [
    ["main", "root"],
    ["subagent", "default"],
    ["subagent", "main"],
    ["subagent", "scout"],
    ["subagent", "unknown"],
    ["subagent", "worker"],
  ]);
  const workerSubject = afterInventoryChange.facets.subjects.find((option) => option.subject.threadType === "subagent" && option.subject.agentRole === "worker")?.subject;
  assert.ok(workerSubject);
  assert.equal((await client.request("exportCsv", { filter: { ...filter, subjects: [workerSubject] }, filePath: path.join(root, "worker-after-inventory-change.csv") })).count, 1);
});

test("collector rejects a divergent copy without replacing canonical events", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-conflict-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const archived = path.join(codexHome, "archived_sessions");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  const meta = line("session_meta", { session_id: "conversation", id: "rollout-conflict", thread_source: "user" });
  await writeFile(path.join(sessions, "rollout-a.jsonl"), meta + token([10, 0, 2, 1, 12], [10, 0, 2, 1, 12], "2026-07-15T01:00:00.000Z"), "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  const client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await writeFile(path.join(archived, "rollout-b.jsonl"), meta + token([999, 0, 2, 1, 1001], [999, 0, 2, 1, 1001], "2026-07-15T01:00:00.000Z"), "utf8");
  await client.request("reconcile", null);
  const result = await query(client);
  assert.equal(result.summary.inputTokens, 10);
  assert.equal((await client.request("getStatus", null)).conflicts, 1);
  await rm(path.join(archived, "rollout-b.jsonl"));
  await client.request("reconcile", null);
  assert.equal((await client.request("getStatus", null)).conflicts, 0, "a removed conflict remains only as historical diagnostics");
});

test("an equal non-canonical copy cannot overwrite model attribution", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-attribution-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const archived = path.join(codexHome, "archived_sessions");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  const meta = line("session_meta", { session_id: "conversation", id: "rollout-attribution", thread_source: "user" });
  const usage = token([10, 0, 2, 1, 12], [10, 0, 2, 1, 12], "2026-07-15T01:00:00.000Z");
  await writeFile(path.join(sessions, "rollout-active.jsonl"), meta + line("turn_context", { turn_id: "turn", model: "gpt-5.6-sol" }) + usage, "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  const client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  const candidatePath = path.join(archived, "rollout-copy.jsonl");
  await writeFile(candidatePath, meta + line("turn_context", { turn_id: "turn", model: "gpt-5.6-terra" }) + usage
    + token([5, 0, 1, 0, 6], [15, 0, 3, 1, 18], "2026-07-15T02:00:00.000Z"), "utf8");
  await client.request("reconcile", null);
  assert.deepEqual((await query(client)).byModel.map((row) => row.key[0]), ["gpt-5.6-sol"]);
  assert.equal((await query(client)).summary.inputTokens, 10, "an attribution-divergent extension cannot add events");
  assert.equal((await client.request("getStatus", null)).conflicts, 1);
  await rm(path.join(sessions, "rollout-active.jsonl"));
  await client.request("reconcile", null);
  assert.deepEqual((await query(client)).byModel.map((row) => row.key[0]), ["gpt-5.6-sol"]);
  assert.equal((await query(client)).summary.inputTokens, 10, "a conflict cannot be promoted after the canonical source disappears");
  assert.equal((await client.request("getStatus", null)).conflicts, 1);
});

test("an unchanged matching candidate is promoted after the canonical source disappears", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-candidate-promotion-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const archived = path.join(codexHome, "archived_sessions");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  const activePath = path.join(sessions, "rollout-active.jsonl");
  const archivePath = path.join(archived, "rollout-copy.jsonl");
  const content = line("session_meta", { session_id: "conversation", id: "rollout-promotion", thread_source: "user" })
    + line("turn_context", { turn_id: "turn", model: "gpt-5.6-sol" })
    + token([10, 0, 2, 1, 12], [10, 0, 2, 1, 12], "2026-07-15T01:00:00.000Z");
  await writeFile(activePath, content, "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  const client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await writeFile(archivePath, content, "utf8");
  await client.request("reconcile", null);
  await rm(activePath);
  await client.request("reconcile", null);
  await client.close();
  const ledger = new UsageStore(config.databasePath);
  try { assert.equal(ledger.getCanonicalSourcePath("rollout-promotion"), archivePath); } finally { ledger.close(); }
});

test("late model metadata enriches canonical events without a false conflict", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-model-enrichment-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  await mkdir(sessions, { recursive: true });
  const rolloutPath = path.join(sessions, "rollout-model.jsonl");
  await writeFile(rolloutPath,
    line("session_meta", { session_id: "conversation", id: "rollout-model", thread_source: "user" })
      + line("event_msg", { type: "thread_settings_applied", thread_settings: { model: "gpt-5.6-sol" } })
      + line("event_msg", { type: "task_started", turn_id: "turn-late" })
      + token([10, 0, 2, 1, 12], [10, 0, 2, 1, 12], "2026-07-15T01:00:00.000Z"),
    "utf8",
  );
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  let client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  assert.deepEqual((await query(client)).byModel.map((row) => row.key[0]), ["gpt-5.6-sol"]);
  await appendFile(rolloutPath, line("turn_context", { turn_id: "turn-late", model: "gpt-5.6-terra" }), "utf8");
  await client.request("reconcile", null);
  const enriched = await query(client);
  assert.deepEqual(enriched.byModel.map((row) => row.key[0]), ["gpt-5.6-terra"]);
  assert.equal((await client.request("getStatus", null)).conflicts, 0);
  await client.close();
  client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.deepEqual((await query(client)).byModel.map((row) => row.key[0]), ["gpt-5.6-terra"]);
});

test("captures a model switch in an incrementally collected turn", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-model-switch-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  await mkdir(sessions, { recursive: true });
  const rolloutPath = path.join(sessions, "rollout-model-switch.jsonl");
  await writeFile(rolloutPath,
    line("session_meta", { session_id: "conversation", id: "rollout-model-switch", thread_source: "user" })
      + line("event_msg", { type: "thread_settings_applied", thread_settings: { model: "gpt-5.6-sol" } })
      + line("event_msg", { type: "task_started", turn_id: "turn-switch" })
      + line("turn_context", { turn_id: "turn-switch", model: "gpt-5.6-sol" })
      + token([10, 0, 2, 1, 12], [10, 0, 2, 1, 12], "2026-07-15T01:00:00.000Z"),
    "utf8",
  );
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  const client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  assert.deepEqual((await query(client)).byModel.map((row) => row.key[0]), ["gpt-5.6-sol"]);
  await appendFile(rolloutPath,
    line("event_msg", { type: "thread_settings_applied", thread_settings: { model: "gpt-5.6-terra" } })
      + token([20, 0, 3, 1, 23], [30, 0, 5, 2, 35], "2026-07-15T01:01:00.000Z"),
    "utf8",
  );
  await client.request("reconcile", null);
  const result = await query(client);
  assert.deepEqual(result.byModel.map((row) => [row.key[0], row.summary.inputTokens]), [
    ["gpt-5.6-sol", 10],
    ["gpt-5.6-terra", 20],
  ]);
  assert.equal(result.summary.inputTokens, 30);
});

test("parser revision rebuild replaces present canonical rollouts and preserves missing history", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-parser-revision-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  await mkdir(sessions, { recursive: true });
  const presentPath = path.join(sessions, "rollout-present.jsonl");
  const missingPath = path.join(sessions, "rollout-missing.jsonl");
  const presentContent = line("session_meta", { session_id: "conversation-present", id: "rollout-present", thread_source: "user" })
    + token([10, 0, 1, 0, 11], [10, 0, 1, 0, 11], "2026-07-15T01:00:00.000Z");
  const missingContent = line("session_meta", { session_id: "conversation-missing", id: "rollout-missing", thread_source: "user" })
    + token([20, 0, 1, 0, 21], [20, 0, 1, 0, 21], "2026-07-15T02:00:00.000Z");
  await writeFile(presentPath, presentContent, "utf8");
  await writeFile(missingPath, missingContent, "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  let client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await client.close();

  contaminateRollout(config.databasePath, "rollout-present", [contaminatedEvent(10), contaminatedEvent(1_000, 1)]);
  contaminateRollout(config.databasePath, "rollout-missing", [contaminatedEvent(777)]);
  await rm(missingPath);

  client = new CollectorClient(__dirname);
  await client.initialize(config);
  const rebuilt = await query(client);
  assert.equal(rebuilt.summary.inputTokens, 787, "the present source is reparsed while deleted-source history remains permanent");
  await client.close();

  const ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCollectorState("rollout_parser_revision"), "4");
    const stored = ledger.queryEvents({ startEpochMs: Date.parse(filter.startUtc), endEpochMs: Date.parse(filter.endUtc) });
    assert.deepEqual(stored.map((event) => [event.rolloutId, event.inputTokens]), [
      ["rollout-missing", 777],
      ["rollout-present", 10],
    ]);
    assert.equal(ledger.listSourceFiles().find((source) => source.rolloutId === "rollout-missing")?.isPresent, false);
  } finally {
    ledger.close();
  }
});

test("parser revision rebuild promotes a present archived candidate when canonical is missing", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-parser-candidate-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const archived = path.join(codexHome, "archived_sessions");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  const canonicalPath = path.join(sessions, "rollout-active.jsonl");
  const candidatePath = path.join(archived, "rollout-archived.jsonl");
  const content = line("session_meta", { session_id: "conversation-candidate", id: "rollout-candidate", thread_source: "user" })
    + token([10, 0, 1, 0, 11], [10, 0, 1, 0, 11], "2026-07-15T01:00:00.000Z");
  await writeFile(canonicalPath, content, "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  let client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await client.close();

  await writeFile(candidatePath, content, "utf8");
  client = new CollectorClient(__dirname);
  await client.initialize(config);
  await client.close();
  let ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCanonicalSourcePath("rollout-candidate"), canonicalPath);
    assert.equal(ledger.listSourceFiles().find((source) => source.filePath === candidatePath)?.canonicalStatus, "candidate");
  } finally {
    ledger.close();
  }

  contaminateRollout(config.databasePath, "rollout-candidate", [contaminatedEvent(999)]);
  await rm(canonicalPath);
  client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.equal((await query(client)).summary.inputTokens, 10);
  await client.close();

  ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCollectorState("rollout_parser_revision"), "4");
    assert.equal(ledger.getCanonicalSourcePath("rollout-candidate"), candidatePath);
    assert.equal(ledger.listSourceFiles().find((source) => source.filePath === canonicalPath)?.isPresent, false);
    assert.equal(ledger.listSourceFiles().find((source) => source.filePath === candidatePath)?.canonicalStatus, "canonical");
  } finally {
    ledger.close();
  }
});

test("parser revision discovers an offline archive move before publishing the upgrade", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-parser-offline-archive-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const archived = path.join(codexHome, "archived_sessions");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  const activePath = path.join(sessions, "rollout-active.jsonl");
  const archivedPath = path.join(archived, "rollout-archived.jsonl");
  const content = line("session_meta", { session_id: "conversation-offline", id: "rollout-offline", thread_source: "user" })
    + token([10, 0, 1, 0, 11], [10, 0, 1, 0, 11], "2026-07-15T01:00:00.000Z");
  await writeFile(activePath, content, "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  let client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await client.close();

  contaminateRollout(config.databasePath, "rollout-offline", [contaminatedEvent(10), contaminatedEvent(999, 1)]);
  await rename(activePath, archivedPath);
  let ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCollectorState("rollout_parser_revision"), "1");
    assert.equal(ledger.listSourceFiles().some((source) => source.filePath === archivedPath), false, "the archive path was not inventoried before shutdown");
  } finally {
    ledger.close();
  }

  client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.equal((await query(client)).summary.inputTokens, 10, "the newly discovered archive bypasses the contaminated shorter prefix");
  await client.close();

  ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCollectorState("rollout_parser_revision"), "4");
    assert.equal(ledger.getCanonicalSourcePath("rollout-offline"), archivedPath);
    assert.equal(ledger.listSourceFiles().find((source) => source.filePath === activePath)?.isPresent, false);
    assert.equal(ledger.listSourceFiles().find((source) => source.filePath === archivedPath)?.canonicalStatus, "canonical");
  } finally {
    ledger.close();
  }
});

test("an interrupted parser revision rebuild keeps the old revision and retries", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-parser-retry-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  await mkdir(sessions, { recursive: true });
  const firstPath = path.join(sessions, "rollout-a.jsonl");
  const secondPath = path.join(sessions, "rollout-b.jsonl");
  const firstContent = line("session_meta", { session_id: "conversation-a", id: "rollout-a", thread_source: "user" })
    + token([10, 0, 1, 0, 11], [10, 0, 1, 0, 11], "2026-07-15T01:00:00.000Z");
  const secondContent = line("session_meta", { session_id: "conversation-b", id: "rollout-b", thread_source: "user" })
    + token([20, 0, 1, 0, 21], [20, 0, 1, 0, 21], "2026-07-15T02:00:00.000Z");
  await writeFile(firstPath, firstContent, "utf8");
  await writeFile(secondPath, secondContent, "utf8");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  let client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await client.close();

  contaminateRollout(config.databasePath, "rollout-a", [contaminatedEvent(111)]);
  contaminateRollout(config.databasePath, "rollout-b", [contaminatedEvent(222)]);
  await writeFile(secondPath, "not-json\n", "utf8");

  client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.equal((await query(client)).summary.inputTokens, 232, "successful files may commit before a later source interrupts the rebuild");
  await client.close();
  let ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCollectorState("rollout_parser_revision"), "1", "a partial rebuild must not publish the new revision");
  } finally {
    ledger.close();
  }

  await writeFile(secondPath, secondContent, "utf8");
  client = new CollectorClient(__dirname);
  await client.initialize(config);
  assert.equal((await query(client)).summary.inputTokens, 30, "retry reparses the entire canonical set and completes the rebuild");
  await client.close();
  ledger = new UsageStore(config.databasePath);
  try {
    assert.equal(ledger.getCollectorState("rollout_parser_revision"), "4");
  } finally {
    ledger.close();
  }
});

test("worker revalidates export paths against junctions into Codex sources", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-export-boundary-"));
  const codexHome = path.join(root, ".codex");
  const sessions = path.join(codexHome, "sessions");
  const archived = path.join(codexHome, "archived_sessions");
  const agents = path.join(codexHome, "agents");
  const outside = path.join(root, "outside");
  await mkdir(sessions, { recursive: true });
  await mkdir(archived, { recursive: true });
  await mkdir(agents, { recursive: true });
  await mkdir(outside, { recursive: true });
  const junction = path.join(outside, "source-link");
  await symlink(agents, junction, process.platform === "win32" ? "junction" : "dir");
  const config: CollectorConfig = { codexHome, databasePath: path.join(root, "usage.sqlite"), reconcileIntervalMs: 60 * 60_000, watcherDebounceMs: 50 };
  const client = new CollectorClient(__dirname);
  t.after(async () => { await client.close(); await rm(root, { recursive: true, force: true }); });
  await client.initialize(config);
  await assert.rejects(() => client.request("exportCsv", { filter, filePath: path.join(agents, "blocked.csv") }), /read-only observation sources/);
  await assert.rejects(() => client.request("exportCsv", { filter, filePath: path.join(junction, "blocked.csv") }), /read-only observation sources/);
});
