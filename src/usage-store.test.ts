import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import test, { TestContext } from "node:test";
import {
  CandidateSourceInput,
  RecoverableCanonicalSourceInput,
  RolloutMetadataInput,
  UsageEventInput,
  UsageStore,
} from "./usage-store";

const metadata: RolloutMetadataInput = {
  rolloutId: "rollout-1",
  conversationId: "conversation-1",
  parentThreadId: "",
  threadType: "main",
  agentRole: "main",
  agentPath: "/root",
  agentNickname: "",
};

function usageEvent(tokenEventOrdinal: number, timestampEpochMs: number, signature = `signature-${tokenEventOrdinal}`): UsageEventInput {
  return {
    tokenEventOrdinal,
    timestampEpochMs,
    model: "gpt-5.6-sol",
    inputTokens: 100 + tokenEventOrdinal,
    cachedInputTokens: 20,
    outputTokens: 30,
    reasoningOutputTokens: 10,
    eventSignature: signature,
  };
}

function source(filePath: string, overrides: Partial<CandidateSourceInput> = {}): CandidateSourceInput {
  return {
    filePath,
    sizeBytes: 1_000,
    modifiedAtEpochMs: 2_000,
    byteOffset: 1_000,
    prefixHash: "prefix",
    prefixStatus: "matches",
    canonicalStatus: "candidate",
    isPresent: true,
    lastScannedAtEpochMs: 3_000,
    lastError: null,
    ...overrides,
  };
}

function recoverableSource(filePath: string, overrides: Partial<RecoverableCanonicalSourceInput> = {}): RecoverableCanonicalSourceInput {
  return {
    filePath,
    sizeBytes: 1_200,
    modifiedAtEpochMs: 4_000,
    byteOffset: 1_200,
    prefixHash: "recovered-prefix",
    lastScannedAtEpochMs: 5_000,
    ...overrides,
  };
}

function tempDatabase(t: TestContext): { readonly databasePath: string; readonly directory: string } {
  const directory = mkdtempSync(path.join(os.tmpdir(), "usage-store-"));
  return { databasePath: path.join(directory, "usage.sqlite3"), directory };
}

function closeAndRemove(store: UsageStore, directory: string): void {
  try {
    store.close();
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test("migrates an empty database and enables required pragmas", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const store = new UsageStore(databasePath);
  assert.equal(store.schemaVersion, 1);
  store.close();

  const database = new DatabaseSync(databasePath, { readBigInts: true });
  try {
    const tables = database.prepare(`
      SELECT name FROM sqlite_schema
      WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
      ORDER BY name
    `).all().map((row) => row.name);
    assert.deepEqual(tables, [
      "collector_diagnostics",
      "collector_runs",
      "collector_state",
      "rollouts",
      "source_files",
      "usage_events",
    ]);
    assert.equal(database.prepare("PRAGMA user_version").get()?.user_version, 1n);
    assert.equal(database.prepare("PRAGMA foreign_keys").get()?.foreign_keys, 1n);
    assert.equal(database.prepare("PRAGMA journal_mode").get()?.journal_mode, "wal");
  } finally {
    database.close();
  }
});

test("appendEvents is idempotent and rejects conflicting ordinals atomically", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const events = [usageEvent(1, 2_000), usageEvent(0, 1_000)];

  assert.deepEqual(store.appendEvents(metadata, events, 3_000), { inserted: 2, ignoredAsDuplicate: 0 });
  assert.deepEqual(store.appendEvents(metadata, events, 4_000), { inserted: 0, ignoredAsDuplicate: 2 });
  assert.throws(
    () => store.appendEvents(metadata, [usageEvent(2, 3_000), usageEvent(0, 1_000, "different")], 5_000),
    /Conflicting usage event/,
  );

  const stored = store.queryEvents({ startEpochMs: 0, endEpochMs: 10_000 });
  assert.deepEqual(stored.map((event) => event.tokenEventOrdinal), [0, 1]);
  assert.deepEqual(store.getRolloutEventSignatures(metadata.rolloutId), ["signature-0", "signature-1"]);
  assert.deepEqual(store.getRolloutEventIdentities(metadata.rolloutId), ["[1000,100,20,30,10]", "[2000,101,20,30,10]"]);
  assert.deepEqual(store.listCanonicalSourcesWithUnknownModels(), []);
});

test("appendRolloutSource atomically appends events and updates its source", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const sourcePath = path.join(directory, "append-rollout.jsonl");
  const event = usageEvent(0, 1_000, "original");

  assert.deepEqual(store.appendRolloutSource({
    metadata,
    events: [event],
    source: source(sourcePath),
    observedAtEpochMs: 3_000,
  }), { inserted: 1, ignoredAsDuplicate: 0 });
  assert.deepEqual(store.appendRolloutSource({
    metadata,
    events: [event],
    source: source(sourcePath, { lastScannedAtEpochMs: 4_000 }),
    observedAtEpochMs: 4_000,
  }), { inserted: 0, ignoredAsDuplicate: 1 });
  assert.equal(store.listSourceFiles()[0]?.lastScannedAtEpochMs, 4_000);

  assert.throws(() => store.appendRolloutSource({
    metadata: { ...metadata, agentRole: "must-roll-back" },
    events: [usageEvent(1, 2_000), usageEvent(0, 1_000, "conflict")],
    source: source(sourcePath, { byteOffset: 900, lastScannedAtEpochMs: 5_000 }),
    observedAtEpochMs: 5_000,
  }), /Conflicting usage event/);

  assert.deepEqual(store.getRolloutEventSignatures(metadata.rolloutId), ["original"]);
  assert.equal(store.listSourceFiles()[0]?.lastScannedAtEpochMs, 4_000);
  assert.equal(store.queryEvents({ startEpochMs: 0, endEpochMs: 10_000 })[0]?.agentRole, "main");
});

test("replaceRolloutCandidate rolls back rollout, events, and source together", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const sourcePath = path.join(path.dirname(databasePath), "rollout.jsonl");
  store.replaceRolloutCandidate({
    metadata,
    events: [usageEvent(0, 1_000, "original")],
    source: source(sourcePath),
    observedAtEpochMs: 1_000,
  });

  assert.throws(
    () => store.replaceRolloutCandidate({
      metadata: { ...metadata, agentRole: "changed" },
      events: [usageEvent(0, 2_000, "new-0"), usageEvent(0, 3_000, "new-1")],
      source: source(sourcePath, { byteOffset: 900 }),
      observedAtEpochMs: 4_000,
    }),
    /UNIQUE constraint failed/,
  );

  const stored = store.queryEvents({ startEpochMs: 0, endEpochMs: 10_000 });
  assert.equal(stored.length, 1);
  assert.equal(stored[0]?.eventSignature, "original");
  assert.equal(stored[0]?.agentRole, "main");
});

test("recoverDivergedCanonicalSource atomically replaces canonical rollout data and preserves diagnostics", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const sourcePath = path.join(directory, "canonical.jsonl");
  store.replaceRolloutCandidate({
    metadata,
    events: [usageEvent(0, 1_000, "old")],
    source: source(sourcePath),
    observedAtEpochMs: 1_000,
  });
  store.promoteRolloutCandidate({ rolloutId: metadata.rolloutId, canonicalFilePath: sourcePath, promotedAtEpochMs: 2_000 });
  const diagnosticId = store.recordSourceConflict({
    runId: null,
    sourceFilePath: sourcePath,
    code: "source-diverged",
    message: "Canonical source diverged",
    detailsJson: "{\"firstDifference\":0}",
    observedAtEpochMs: 3_000,
  });

  store.recoverDivergedCanonicalSource({
    metadata: { ...metadata, agentRole: "recovered" },
    events: [usageEvent(0, 1_500, "new-0"), usageEvent(1, 2_500, "new-1")],
    source: recoverableSource(sourcePath),
    observedAtEpochMs: 5_000,
  });

  assert.equal(store.getCanonicalSourcePath(metadata.rolloutId), sourcePath);
  assert.deepEqual(store.getRolloutEventSignatures(metadata.rolloutId), ["new-0", "new-1"]);
  assert.equal(store.getRolloutMetadata(metadata.rolloutId)?.agentRole, "recovered");
  assert.deepEqual(store.listSourceFiles(), [{
    filePath: sourcePath,
    rolloutId: metadata.rolloutId,
    sizeBytes: 1_200,
    modifiedAtEpochMs: 4_000,
    byteOffset: 1_200,
    prefixHash: "recovered-prefix",
    prefixStatus: "matches",
    canonicalStatus: "canonical",
    isPresent: true,
    lastScannedAtEpochMs: 5_000,
    lastError: null,
  }]);
  assert.equal(store.countSourceConflicts(), 0);
  assert.ok(diagnosticId > 0);
  const database = new DatabaseSync(databasePath, { readBigInts: true });
  try {
    assert.equal(database.prepare("SELECT count(*) AS count FROM collector_diagnostics").get()?.count, 1n);
  } finally {
    database.close();
  }
});

test("recoverDivergedCanonicalSource rolls back metadata, events, and source on replacement failure", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const sourcePath = path.join(directory, "canonical.jsonl");
  store.replaceRolloutCandidate({
    metadata,
    events: [usageEvent(0, 1_000, "old")],
    source: source(sourcePath, { prefixStatus: "diverged", canonicalStatus: "conflict", lastError: "diverged" }),
    observedAtEpochMs: 1_000,
  });
  store.promoteRolloutCandidate({ rolloutId: metadata.rolloutId, canonicalFilePath: sourcePath, promotedAtEpochMs: 2_000 });

  assert.throws(() => store.recoverDivergedCanonicalSource({
    metadata: { ...metadata, agentRole: "must-roll-back" },
    events: [usageEvent(0, 2_000, "duplicate"), usageEvent(1, 3_000, "duplicate")],
    source: recoverableSource(sourcePath),
    observedAtEpochMs: 5_000,
  }), /UNIQUE constraint failed/);

  assert.equal(store.getRolloutMetadata(metadata.rolloutId)?.agentRole, "main");
  assert.deepEqual(store.getRolloutEventSignatures(metadata.rolloutId), ["old"]);
  assert.deepEqual(store.listSourceFiles()[0], {
    filePath: sourcePath,
    rolloutId: metadata.rolloutId,
    sizeBytes: 1_000,
    modifiedAtEpochMs: 2_000,
    byteOffset: 1_000,
    prefixHash: "prefix",
    prefixStatus: "diverged",
    canonicalStatus: "canonical",
    isPresent: true,
    lastScannedAtEpochMs: 2_000,
    lastError: "diverged",
  });
  assert.equal(store.getCanonicalSourcePath(metadata.rolloutId), sourcePath);
});

test("recoverDivergedCanonicalSource enforces source identity while allowing a missing canonical source to reappear", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const sourcePath = path.join(directory, "canonical.jsonl");
  const recovery = {
    metadata,
    events: [usageEvent(0, 2_000, "recovered")],
    source: recoverableSource(sourcePath),
    observedAtEpochMs: 5_000,
  } as const;

  assert.throws(() => store.recoverDivergedCanonicalSource(recovery), /Unknown rollout/);

  store.replaceRolloutCandidate({
    metadata,
    events: [usageEvent(0, 1_000, "old")],
    source: source(sourcePath),
    observedAtEpochMs: 1_000,
  });
  assert.throws(() => store.recoverDivergedCanonicalSource(recovery), /not the rollout's canonical source/);

  store.promoteRolloutCandidate({ rolloutId: metadata.rolloutId, canonicalFilePath: sourcePath, promotedAtEpochMs: 2_000 });
  store.markSourceMissing(sourcePath, 3_000);
  store.recoverDivergedCanonicalSource(recovery);
  assert.deepEqual(store.getRolloutEventSignatures(metadata.rolloutId), ["recovered"]);
  assert.deepEqual(store.listSourceFiles()[0], {
    filePath: sourcePath,
    rolloutId: metadata.rolloutId,
    sizeBytes: 1_200,
    modifiedAtEpochMs: 4_000,
    byteOffset: 1_200,
    prefixHash: "recovered-prefix",
    prefixStatus: "matches",
    canonicalStatus: "canonical",
    isPresent: true,
    lastScannedAtEpochMs: 5_000,
    lastError: null,
  });

  const otherMetadata: RolloutMetadataInput = {
    ...metadata,
    rolloutId: "rollout-2",
    conversationId: "conversation-2",
  };
  store.appendEvents(otherMetadata, [], 3_500);
  store.upsertSourceFile({ ...source(sourcePath), rolloutId: otherMetadata.rolloutId });
  assert.throws(() => store.recoverDivergedCanonicalSource(recovery), /belongs to a different rollout/);

  const database = new DatabaseSync(databasePath, { readBigInts: true });
  try {
    database.prepare("DELETE FROM source_files WHERE file_path = ?").run(sourcePath);
  } finally {
    database.close();
  }
  assert.throws(() => store.recoverDivergedCanonicalSource(recovery), /does not exist in the ledger/);

  assert.equal(store.getRolloutMetadata(metadata.rolloutId)?.agentRole, "main");
  assert.deepEqual(store.getRolloutEventSignatures(metadata.rolloutId), ["recovered"]);
  assert.equal(store.getCanonicalSourcePath(metadata.rolloutId), sourcePath);
});

test("recoverDivergedCanonicalSource does not alter sibling sources for the rollout", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const canonicalPath = path.join(directory, "canonical.jsonl");
  const siblingPath = path.join(directory, "archived.jsonl");
  store.replaceRolloutCandidate({
    metadata,
    events: [usageEvent(0, 1_000, "old")],
    source: source(canonicalPath),
    observedAtEpochMs: 1_000,
  });
  store.upsertSourceFile({
    ...source(siblingPath, {
      prefixHash: "sibling-prefix",
      prefixStatus: "diverged",
      canonicalStatus: "conflict",
      lastScannedAtEpochMs: 2_500,
      lastError: "sibling conflict",
    }),
    rolloutId: metadata.rolloutId,
  });
  store.promoteRolloutCandidate({ rolloutId: metadata.rolloutId, canonicalFilePath: canonicalPath, promotedAtEpochMs: 3_000 });
  const siblingBefore = store.listSourceFiles().find((entry) => entry.filePath === siblingPath);

  store.recoverDivergedCanonicalSource({
    metadata,
    events: [usageEvent(0, 2_000, "new")],
    source: recoverableSource(canonicalPath),
    observedAtEpochMs: 5_000,
  });

  assert.deepEqual(store.listSourceFiles().find((entry) => entry.filePath === siblingPath), siblingBefore);
  assert.equal(store.listSourceFiles().find((entry) => entry.filePath === canonicalPath)?.canonicalStatus, "canonical");
  assert.equal(store.countSourceConflicts(), 1);
  assert.equal(store.getCanonicalSourcePath(metadata.rolloutId), canonicalPath);
});

test("marking a source missing does not delete permanent usage", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  const sourcePath = path.join(path.dirname(databasePath), "rollout.jsonl");
  store.replaceRolloutCandidate({
    metadata,
    events: [usageEvent(0, 1_000)],
    source: source(sourcePath),
    observedAtEpochMs: 1_000,
  });

  assert.equal(store.countPresentSources(), 1);
  assert.equal(store.countSourceConflicts(), 0);
  assert.deepEqual(store.listSourceFiles(), [{
    filePath: sourcePath,
    rolloutId: metadata.rolloutId,
    sizeBytes: 1_000,
    modifiedAtEpochMs: 2_000,
    byteOffset: 1_000,
    prefixHash: "prefix",
    prefixStatus: "matches",
    canonicalStatus: "candidate",
    isPresent: true,
    lastScannedAtEpochMs: 3_000,
    lastError: null,
  }]);
  assert.equal(store.getCanonicalSourcePath(metadata.rolloutId), null);
  store.promoteRolloutCandidate({ rolloutId: metadata.rolloutId, canonicalFilePath: sourcePath, promotedAtEpochMs: 4_000 });
  assert.equal(store.getCanonicalSourcePath(metadata.rolloutId), sourcePath);

  assert.equal(store.markSourceMissing(sourcePath, 5_000), true);
  assert.equal(store.countPresentSources(), 0);
  assert.equal(store.queryEvents({ startEpochMs: 0, endEpochMs: 10_000 }).length, 1);
  store.recordSourceConflict({
    runId: null,
    sourceFilePath: sourcePath,
    code: "prefix-diverged",
    message: "Source prefix changed",
    detailsJson: null,
    observedAtEpochMs: 6_000,
  });
  assert.equal(store.countSourceConflicts(), 0, "missing conflicts remain historical only");
  assert.equal(store.listSourceFiles()[0]?.canonicalStatus, "conflict");
  store.upsertSourceFile({ ...store.listSourceFiles()[0]!, isPresent: true, lastScannedAtEpochMs: 7_000 });
  assert.equal(store.countSourceConflicts(), 1);
  store.markSourceMissing(sourcePath, 8_000);
  assert.equal(store.countSourceConflicts(), 0);
});

test("events and collector state persist across restart", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  let store = new UsageStore(databasePath);
  store.appendEvents(metadata, [usageEvent(0, 1_000)], 1_000);
  store.setCollectorState("last-observation", "complete", 2_000);
  store.beginCollectorRun({ runId: "run-1", trigger: "startup", startedAtEpochMs: 2_000 });
  store.heartbeatCollector({ runId: "run-1", heartbeatAtEpochMs: 3_000, state: { phase: "reconcile" } });
  store.finishCollectorRun({
    runId: "run-1",
    status: "succeeded",
    completedAtEpochMs: 4_000,
    filesScanned: 1,
    eventsAdded: 1,
    diagnosticsCount: 0,
    errorMessage: null,
  });
  store.beginCollectorRun({ runId: "run-2", trigger: "watcher", startedAtEpochMs: 5_000 });
  store.close();

  store = new UsageStore(databasePath);
  try {
    assert.equal(store.queryEvents({ startEpochMs: 0, endEpochMs: 10_000 }).length, 1);
    assert.equal(store.getCollectorState("last-observation"), "complete");
    assert.equal(store.getCollectorState("phase"), "reconcile");
    assert.deepEqual(store.getCollectorRun("run-1"), {
      runId: "run-1",
      trigger: "startup",
      status: "succeeded",
      startedAtEpochMs: 2_000,
      heartbeatAtEpochMs: 4_000,
      completedAtEpochMs: 4_000,
      filesScanned: 1,
      eventsAdded: 1,
      diagnosticsCount: 0,
      errorMessage: null,
    });
    assert.deepEqual(store.getLatestCollectorRun(), {
      runId: "run-2",
      trigger: "watcher",
      status: "running",
      startedAtEpochMs: 5_000,
      heartbeatAtEpochMs: 5_000,
      completedAtEpochMs: null,
      filesScanned: 0,
      eventsAdded: 0,
      diagnosticsCount: 0,
      errorMessage: null,
    });
  } finally {
    store.close();
  }
});

test("queryEvents uses a half-open UTC epoch interval", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  const store = new UsageStore(databasePath);
  t.after(() => closeAndRemove(store, directory));
  store.appendEvents(metadata, [
    usageEvent(0, 1_000),
    usageEvent(1, 2_000),
    usageEvent(2, 3_000),
  ], 3_000);

  const stored = store.queryEvents({ startEpochMs: 1_000, endEpochMs: 3_000 });
  assert.deepEqual(stored.map((event) => event.timestampEpochMs), [1_000, 2_000]);
  assert.deepEqual(stored.map((event) => event.timestampUtc), [
    "1970-01-01T00:00:01.000Z",
    "1970-01-01T00:00:02.000Z",
  ]);
});

test("rejects database integers outside the JavaScript safe range", (t) => {
  const { databasePath, directory } = tempDatabase(t);
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  let store = new UsageStore(databasePath);
  store.appendEvents(metadata, [usageEvent(0, 1_000)], 1_000);
  store.close();

  const database = new DatabaseSync(databasePath, { readBigInts: true });
  try {
    database.prepare("UPDATE usage_events SET input_tokens = ? WHERE rollout_id = ?").run(9_007_199_254_740_992n, metadata.rolloutId);
  } finally {
    database.close();
  }

  store = new UsageStore(databasePath);
  try {
    assert.throws(
      () => store.queryEvents({ startEpochMs: 0, endEpochMs: 10_000 }),
      /exceeds the JavaScript safe integer range/,
    );
  } finally {
    store.close();
  }
});
