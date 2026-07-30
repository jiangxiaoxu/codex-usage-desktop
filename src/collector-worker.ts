import { createHash, randomUUID } from "node:crypto";
import { open, opendir, readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { parentPort } from "node:worker_threads";
import { watch, type FSWatcher } from "chokidar";
import type { CollectorConfig, CollectorMessage, CollectorRequest, CollectorRequestMap, CollectorMethod } from "./collector-protocol";
import { parseRolloutChunkCooperatively, type RolloutChunkParseResult, type RolloutParseDiagnostics, type RolloutParserState } from "./rollout-parser";
import type { CollectorStatus, FilterSpec, QueryResult, ScanDiagnostics, SyncResult, UsageEvent } from "./shared";
import { csvRows, matchesFilter, query } from "./usage-core";
import { UsageStore, type CandidateSourceInput, type RolloutMetadataInput, type SourceFileRecord, type UsageEventInput } from "./usage-store";
import { assertOutsideDirectories } from "./write-boundary";

const port = parentPort;
if (port === null) throw new Error("Collector worker requires a parent port.");
const workerPort = port;

const BOUNDARY_WINDOW_BYTES = 64 * 1024;
const COOPERATIVE_SLICE_DELAY_MS = 50;
const COOPERATIVE_SLICE_ITEM_LIMIT = 32;
const COOPERATIVE_SLICE_TIME_BUDGET_MS = 8;
const FULL_INVENTORY_RUN_COUNT_STATE_KEY = "full_inventory_run_count";
const FULL_INVENTORY_YIELD_COUNT_STATE_KEY = "full_inventory_last_yield_count";
const PARSER_SLICE_BYTE_LIMIT = 256 * 1024;
const PARSER_SLICE_RECORD_LIMIT = 256;
const RECOVERY_SNAPSHOT_STABILITY_MS = 25;
const WATCHER_DRAIN_BATCH_SIZE = 16;
const WATCHER_RETRY_BASE_DELAY_MS = 250;
const WATCHER_RETRY_MAX_ATTEMPTS = 5;
const WATCHER_RETRY_MAX_DELAY_MS = 4_000;
const ROLLOUT_PARSER_REVISION = 6;
const ROLLOUT_PARSER_REVISION_STATE_KEY = "rollout_parser_revision";

// Codex rollout paths are a strict observation-only boundary. Never open them
// for writing, lock them, rename them, delete them, or attempt repairs.

interface SourceRuntime {
  readonly rolloutId: string;
  readonly byteOffset: number;
  readonly boundaryHash: string;
  readonly state: RolloutParserState;
}

interface MutableDiagnostics {
  filesScanned: number;
  malformedLines: number;
  duplicateSnapshotsSkipped: number;
  zeroBreakdownSnapshotsSkipped: number;
  invalidTokenRelationshipsSkipped: number;
}

let configuration: CollectorConfig | null = null;
let store: UsageStore | null = null;
let watcher: FSWatcher | null = null;
let inventoryTimer: NodeJS.Timeout | null = null;
let heartbeatTimer: NodeJS.Timeout | null = null;
let debounceTimer: NodeJS.Timeout | null = null;
let fullInventoryOperation: Promise<SyncResult> | null = null;
let manualTrailingInventoryOperation: Promise<SyncResult> | null = null;
let runId: string | null = null;
let runStartedEpochMs = 0;
let lastSuccessfulInventoryEpochMs: number | null = null;
let observationCoverage: CollectorStatus["observationCoverage"] = "baseline";
let observationGap: { readonly startUtc: string; readonly endUtc: string } | null = null;
let phase: CollectorStatus["phase"] = "initializing";
let statusMessage = "Starting collector";
let changedFilesLastSync = 0;
let pendingPaths = new Map<string, string>();
let watcherDrainQueuedOrRunning = false;
let watcherRetryAttempts = new Map<string, number>();
let watcherRetryTimers = new Map<string, NodeJS.Timeout>();
let runtimeByPath = new Map<string, SourceRuntime>();
let conflictsAttempted = new Set<string>();
let unknownModelsAttempted = new Set<string>();
let operationQueue: Promise<void> = Promise.resolve();
let shuttingDown = false;
const diagnostics: MutableDiagnostics = { filesScanned: 0, malformedLines: 0, duplicateSnapshotsSkipped: 0, zeroBreakdownSnapshotsSkipped: 0, invalidTokenRelationshipsSkipped: 0 };

function requireStore(): UsageStore {
  if (store === null) throw new Error("Collector store is not initialized.");
  return store;
}

function requireConfiguration(): CollectorConfig {
  if (configuration === null) throw new Error("Collector configuration is not initialized.");
  return configuration;
}

function status(): CollectorStatus {
  const activeStore = store;
  const conflicts = activeStore?.countSourceConflicts() ?? 0;
  return {
    phase: conflicts > 0 && phase === "watching" ? "degraded" : phase,
    databasePath: configuration?.databasePath ?? "",
    runStartedUtc: runStartedEpochMs === 0 ? new Date().toISOString() : new Date(runStartedEpochMs).toISOString(),
    lastSuccessfulInventoryUtc: lastSuccessfulInventoryEpochMs === null ? null : new Date(lastSuccessfulInventoryEpochMs).toISOString(),
    lastHeartbeatUtc: runId === null ? null : new Date().toISOString(),
    filesKnown: activeStore?.countPresentSources() ?? 0,
    pendingFiles: pendingPaths.size,
    changedFilesLastSync,
    conflicts,
    observationCoverage,
    observationGap,
    message: statusMessage,
  };
}

function emitUpdated(): void {
  const message: CollectorMessage = { kind: "event", name: "usage-updated", status: status() };
  workerPort.postMessage(message);
}

function scanDiagnostics(): ScanDiagnostics {
  return { ...diagnostics };
}

function addDiagnostics(value: RolloutParseDiagnostics): void {
  diagnostics.malformedLines += value.malformedLines + value.nonObjectLines;
  diagnostics.duplicateSnapshotsSkipped += value.duplicateSnapshotsSkipped;
  diagnostics.zeroBreakdownSnapshotsSkipped += value.zeroBreakdownSnapshotsSkipped;
  diagnostics.invalidTokenRelationshipsSkipped += value.invalidTokenRelationshipsSkipped;
}

function fallbackRolloutId(filePath: string): string {
  return path.basename(filePath, ".jsonl").replace(/^rollout-[^-]+-/, "") || path.basename(filePath, ".jsonl");
}

function sourceFrom(
  filePath: string,
  sourceStat: { readonly size: number; readonly mtimeMs: number },
  byteOffset: number,
  boundaryHash: string,
  canonicalStatus: CandidateSourceInput["canonicalStatus"],
  prefixStatus: CandidateSourceInput["prefixStatus"],
  lastError: string | null,
): CandidateSourceInput {
  return {
    filePath,
    sizeBytes: sourceStat.size,
    modifiedAtEpochMs: Math.trunc(sourceStat.mtimeMs),
    byteOffset,
    prefixHash: boundaryHash,
    prefixStatus,
    canonicalStatus,
    isPresent: true,
    lastScannedAtEpochMs: Date.now(),
    lastError,
  };
}

function metadataInput(result: RolloutChunkParseResult): RolloutMetadataInput {
  return { ...result.metadata };
}

function usageInputs(result: RolloutChunkParseResult): readonly UsageEventInput[] {
  return result.events.map((event) => ({
    tokenEventOrdinal: event.tokenEventOrdinal,
    timestampEpochMs: Date.parse(event.timestampUtc),
    model: event.model,
    inputTokens: event.inputTokens,
    cachedInputTokens: event.cachedInputTokens,
    outputTokens: event.outputTokens,
    reasoningOutputTokens: event.reasoningOutputTokens,
    eventSignature: event.deterministicSignature,
  }));
}

function eventIdentity(event: RolloutChunkParseResult["events"][number]): string {
  return JSON.stringify([Date.parse(event.timestampUtc), event.inputTokens, event.cachedInputTokens, event.outputTokens, event.reasoningOutputTokens]);
}

function eventSemanticSignature(event: RolloutChunkParseResult["events"][number]): string {
  return JSON.stringify([Date.parse(event.timestampUtc), event.model, event.inputTokens, event.cachedInputTokens, event.outputTokens, event.reasoningOutputTokens]);
}

function sameMetadata(left: RolloutMetadataInput | null, right: RolloutMetadataInput): boolean {
  return left !== null
    && left.rolloutId === right.rolloutId
    && left.conversationId === right.conversationId
    && left.parentThreadId === right.parentThreadId
    && left.threadType === right.threadType
    && left.agentRole === right.agentRole
    && left.agentPath === right.agentPath
    && left.agentNickname === right.agentNickname;
}

function signatureRelation(existing: readonly string[], candidate: readonly string[]): "equal" | "extension" | "shorter" | "diverged" {
  const commonLength = Math.min(existing.length, candidate.length);
  for (let index = 0; index < commonLength; index += 1) {
    if (existing[index] !== candidate[index]) return "diverged";
  }
  if (candidate.length === existing.length) return "equal";
  return candidate.length > existing.length ? "extension" : "shorter";
}

function boundaryHash(buffer: Buffer, stableByteLength: number): string {
  const start = Math.max(0, stableByteLength - BOUNDARY_WINDOW_BYTES);
  return createHash("sha256").update(buffer.subarray(start, stableByteLength)).digest("hex");
}

async function readBoundary(filePath: string, byteOffset: number): Promise<string> {
  const start = Math.max(0, byteOffset - BOUNDARY_WINDOW_BYTES);
  const length = byteOffset - start;
  if (length === 0) return boundaryHash(Buffer.alloc(0), 0);
  const handle = await open(filePath, "r");
  try {
    const buffer = Buffer.alloc(length);
    const result = await handle.read(buffer, 0, length, start);
    if (result.bytesRead !== length) throw new Error("Source boundary changed while reading.");
    return createHash("sha256").update(buffer).digest("hex");
  } finally {
    await handle.close();
  }
}

function stableStat(left: { readonly size: number; readonly mtimeMs: number }, right: { readonly size: number; readonly mtimeMs: number }): boolean {
  return left.size === right.size && Math.trunc(left.mtimeMs) === Math.trunc(right.mtimeMs);
}

function rejectInternalDamage(filePath: string, result: RolloutChunkParseResult): void {
  if (result.diagnostics.malformedLines > 0 || result.diagnostics.nonObjectLines > 0) {
    throw new Error(`Stable JSONL content is malformed: ${filePath}`);
  }
}

interface ParsedFullSourceSnapshot {
  readonly kind: "parsed";
  readonly sourceStat: { readonly size: number; readonly mtimeMs: number };
  readonly contentHash: string;
  readonly boundaryHash: string;
  readonly result: RolloutChunkParseResult;
}

interface UnsafeFullSourceSnapshot {
  readonly kind: "unsafe";
  readonly sourceStat: { readonly size: number; readonly mtimeMs: number };
  readonly contentHash: string;
  readonly boundaryHash: string;
  readonly message: string;
}

type FullSourceSnapshot = ParsedFullSourceSnapshot | UnsafeFullSourceSnapshot;

async function yieldParserControl(yields?: InventoryYieldTracker): Promise<void> {
  if (yields !== undefined) {
    yields.count += 1;
    await new Promise<void>((resolve) => setTimeout(resolve, COOPERATIVE_SLICE_DELAY_MS));
  } else await new Promise<void>((resolve) => setImmediate(resolve));
}

async function cooperativeSha256(buffer: Buffer, yields?: InventoryYieldTracker): Promise<string> {
  const hash = createHash("sha256");
  for (let offset = 0; offset < buffer.length; offset += PARSER_SLICE_BYTE_LIMIT) {
    const end = Math.min(offset + PARSER_SLICE_BYTE_LIMIT, buffer.length);
    hash.update(buffer.subarray(offset, end));
    if (end < buffer.length) await yieldParserControl(yields);
  }
  return hash.digest("hex");
}

async function readStableFullSnapshot(filePath: string, yields?: InventoryYieldTracker): Promise<FullSourceSnapshot> {
  const before = await stat(filePath);
  const buffer = await readFile(filePath);
  const after = await stat(filePath);
  if (!stableStat(before, after)) throw new Error("Source changed while reading a full snapshot.");
  const result = await parseRolloutChunkCooperatively(buffer, fallbackRolloutId(filePath), {
    maxBytesPerSlice: PARSER_SLICE_BYTE_LIMIT,
    maxRecordsPerSlice: PARSER_SLICE_RECORD_LIMIT,
    yieldControl: () => yieldParserControl(yields),
  });
  const contentHash = await cooperativeSha256(buffer, yields);
  if (result.diagnostics.malformedLines > 0 || result.diagnostics.nonObjectLines > 0) {
    return {
      kind: "unsafe",
      sourceStat: after,
      contentHash,
      boundaryHash: boundaryHash(buffer, buffer.length),
      message: `Stable JSONL content is malformed: ${filePath}`,
    };
  }
  return {
    kind: "parsed",
    sourceStat: after,
    contentHash,
    boundaryHash: boundaryHash(buffer, result.stableByteLength),
    result,
  };
}

async function recoverCanonicalSelfRewrite(filePath: string, rolloutId: string, first: ParsedFullSourceSnapshot, yields?: InventoryYieldTracker): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, RECOVERY_SNAPSHOT_STABILITY_MS));
  const second = await readStableFullSnapshot(filePath, yields);
  if (second.contentHash !== first.contentHash) throw new Error("Canonical source changed between recovery snapshots.");
  if (second.kind === "unsafe") throw new Error(second.message);
  if (second.result.metadata.rolloutId !== rolloutId) {
    throw new Error(`Canonical source rollout changed from ${rolloutId} to ${second.result.metadata.rolloutId}.`);
  }
  const observedAt = Date.now();
  requireStore().recoverDivergedCanonicalSource({
    metadata: metadataInput(second.result),
    events: usageInputs(second.result),
    source: {
      filePath,
      sizeBytes: second.sourceStat.size,
      modifiedAtEpochMs: Math.trunc(second.sourceStat.mtimeMs),
      byteOffset: second.result.stableByteLength,
      prefixHash: second.boundaryHash,
      lastScannedAtEpochMs: observedAt,
    },
    observedAtEpochMs: observedAt,
  });
  setRuntime(filePath, {
    rolloutId,
    byteOffset: second.result.stableByteLength,
    boundaryHash: second.boundaryHash,
    state: second.result.state,
  });
}

type FullParseContext =
  | { readonly kind: "inventory" }
  | { readonly kind: "canonical-prefix-rewrite"; readonly expectedRolloutId: string }
  | { readonly kind: "late-model-resolution"; readonly expectedRolloutId: string }
  | { readonly kind: "parser-revision-rebuild"; readonly expectedRolloutId: string };

function recordDeterministicCanonicalConflict(
  filePath: string,
  known: SourceFileRecord,
  snapshot: FullSourceSnapshot,
  code: string,
  message: string,
): void {
  const activeStore = requireStore();
  const observedAtEpochMs = Date.now();
  activeStore.upsertSourceFile({
    ...known,
    sizeBytes: snapshot.sourceStat.size,
    modifiedAtEpochMs: Math.trunc(snapshot.sourceStat.mtimeMs),
    byteOffset: snapshot.sourceStat.size,
    prefixHash: snapshot.boundaryHash,
    prefixStatus: "diverged",
    canonicalStatus: "conflict",
    isPresent: true,
    lastScannedAtEpochMs: observedAtEpochMs,
    lastError: message,
  });
  activeStore.recordSourceConflict({
    runId,
    sourceFilePath: filePath,
    code,
    message,
    detailsJson: JSON.stringify({ rolloutId: known.rolloutId }),
    observedAtEpochMs,
  });
  deleteRuntime(filePath);
}

async function confirmStableSnapshot(filePath: string, first: FullSourceSnapshot, yields?: InventoryYieldTracker): Promise<FullSourceSnapshot> {
  await new Promise<void>((resolve) => setTimeout(resolve, RECOVERY_SNAPSHOT_STABILITY_MS));
  const second = await readStableFullSnapshot(filePath, yields);
  if (second.contentHash !== first.contentHash) throw new Error("Canonical source changed between recovery snapshots.");
  return second;
}

async function processFullFile(filePath: string, context: FullParseContext = { kind: "inventory" }, yields?: InventoryYieldTracker): Promise<boolean> {
  const activeStore = requireStore();
  const snapshot = await readStableFullSnapshot(filePath, yields);
  const knownSource = activeStore.listSourceFiles().find((source) => normalizedWatcherPath(source.filePath).key === normalizedWatcherPath(filePath).key);
  const knownCanonicalPath = knownSource?.rolloutId === null || knownSource?.rolloutId === undefined
    ? null
    : activeStore.getCanonicalSourcePath(knownSource.rolloutId);
  const isKnownCanonical = knownSource !== undefined && knownCanonicalPath !== null
    && normalizedWatcherPath(knownCanonicalPath).key === normalizedWatcherPath(knownSource.filePath).key;
  if (snapshot.kind === "unsafe") {
    const confirmed = await confirmStableSnapshot(filePath, snapshot, yields);
    if (confirmed.kind !== "unsafe") throw new Error("Canonical source safety classification changed between recovery snapshots.");
    if (isKnownCanonical) {
      recordDeterministicCanonicalConflict(filePath, knownSource, confirmed, "canonical-source-malformed", confirmed.message);
      return false;
    }
    throw new Error(confirmed.message);
  }
  const { result, sourceStat: after } = snapshot;
  addDiagnostics(result.diagnostics);
  const candidateIdentities = result.events.map(eventIdentity);
  const existingIdentities = activeStore.getRolloutEventIdentities(result.metadata.rolloutId);
  const relation = signatureRelation(existingIdentities, candidateIdentities);
  const observedAt = Date.now();
  const hash = snapshot.boundaryHash;
  if (knownSource?.rolloutId !== null && knownSource?.rolloutId !== undefined
    && knownSource.rolloutId !== result.metadata.rolloutId
    && isKnownCanonical) {
    const confirmed = await confirmStableSnapshot(filePath, snapshot, yields);
    if (confirmed.kind !== "parsed" || confirmed.result.metadata.rolloutId !== result.metadata.rolloutId) {
      throw new Error("Canonical source identity changed between recovery snapshots.");
    }
    const message = `Canonical source rollout changed from ${knownSource.rolloutId} to ${result.metadata.rolloutId}.`;
    recordDeterministicCanonicalConflict(filePath, knownSource, confirmed, "canonical-source-rollout-changed", message);
    return false;
  }
  if (context.kind === "parser-revision-rebuild") {
    if (result.metadata.rolloutId !== context.expectedRolloutId) {
      throw new Error(`Canonical source rollout changed from ${context.expectedRolloutId} to ${result.metadata.rolloutId}.`);
    }
    const source = sourceFrom(filePath, after, result.stableByteLength, hash, "canonical", "matches", null);
    activeStore.replaceRolloutCandidate({ metadata: metadataInput(result), events: usageInputs(result), source, observedAtEpochMs: observedAt });
    activeStore.promoteRolloutCandidate({ rolloutId: context.expectedRolloutId, canonicalFilePath: filePath, promotedAtEpochMs: observedAt });
    setRuntime(filePath, { rolloutId: context.expectedRolloutId, byteOffset: result.stableByteLength, boundaryHash: hash, state: result.state });
    return true;
  }
  const canonicalPath = activeStore.getCanonicalSourcePath(result.metadata.rolloutId);
  const presentPaths = new Set(activeStore.listSourceFiles().filter((source) => source.isPresent).map((source) => normalizedWatcherPath(source.filePath).key));
  const isCurrentCanonical = canonicalPath !== null
    && normalizedWatcherPath(canonicalPath).key === normalizedWatcherPath(filePath).key;
  const candidateSemantic = result.events.map(eventSemanticSignature);
  const canonicalSemantic = activeStore.getRolloutSemanticSignatures(result.metadata.rolloutId);
  const semanticRelation = signatureRelation(canonicalSemantic, candidateSemantic);
  const metadataMatches = sameMetadata(activeStore.getRolloutMetadata(result.metadata.rolloutId), metadataInput(result));
  const canonicalRewriteDetected = isCurrentCanonical && (
    context.kind === "canonical-prefix-rewrite"
    || relation === "shorter"
    || relation === "diverged"
    || !metadataMatches
    || semanticRelation === "shorter"
    || semanticRelation === "diverged"
  );
  if (canonicalRewriteDetected) {
    await recoverCanonicalSelfRewrite(filePath, result.metadata.rolloutId, snapshot, yields);
    return true;
  }
  if (relation === "diverged") {
    activeStore.upsertSourceFile({ ...sourceFrom(filePath, after, result.stableByteLength, hash, "conflict", "diverged", "Candidate diverges from the canonical event prefix."), rolloutId: result.metadata.rolloutId });
    activeStore.recordSourceConflict({ runId, sourceFilePath: filePath, code: "source-diverged", message: "Rollout source diverges from the canonical event prefix.", detailsJson: JSON.stringify({ rolloutId: result.metadata.rolloutId }), observedAtEpochMs: observedAt });
    deleteRuntime(filePath);
    return false;
  }
  if (relation === "shorter") {
    activeStore.upsertSourceFile({ ...sourceFrom(filePath, after, result.stableByteLength, hash, "candidate", "matches", null), rolloutId: result.metadata.rolloutId });
    deleteRuntime(filePath);
    return false;
  }
  const attributionMatches = metadataMatches
    && (semanticRelation === "equal" || semanticRelation === "extension");
  if (existingIdentities.length > 0 && !isCurrentCanonical && !attributionMatches) {
    const conflictSource = sourceFrom(filePath, after, result.stableByteLength, hash, "conflict", "diverged", "Candidate metadata or model attribution differs from the canonical rollout.");
    activeStore.upsertSourceFile({ ...conflictSource, rolloutId: result.metadata.rolloutId });
    activeStore.recordSourceConflict({ runId, sourceFilePath: filePath, code: "source-attribution-diverged", message: "Rollout candidate metadata or model attribution differs from the canonical rollout.", detailsJson: JSON.stringify({ rolloutId: result.metadata.rolloutId }), observedAtEpochMs: observedAt });
    deleteRuntime(filePath);
    return false;
  }
  const shouldPromote = relation === "extension" || canonicalPath === null
    || !presentPaths.has(normalizedWatcherPath(canonicalPath).key) || isCurrentCanonical;
  const source = sourceFrom(filePath, after, result.stableByteLength, hash, shouldPromote ? "canonical" : "candidate", "matches", null);
  if (!shouldPromote) {
    activeStore.upsertSourceFile({ ...source, rolloutId: result.metadata.rolloutId });
    deleteRuntime(filePath);
    return false;
  }
  activeStore.replaceRolloutCandidate({ metadata: metadataInput(result), events: usageInputs(result), source, observedAtEpochMs: observedAt });
  activeStore.promoteRolloutCandidate({ rolloutId: result.metadata.rolloutId, canonicalFilePath: filePath, promotedAtEpochMs: observedAt });
  setRuntime(filePath, { rolloutId: result.metadata.rolloutId, byteOffset: result.stableByteLength, boundaryHash: hash, state: result.state });
  return relation === "extension" || existingIdentities.length === 0;
}

async function processIncrementalFile(filePath: string, runtime: SourceRuntime, yields?: InventoryYieldTracker): Promise<boolean> {
  const activeStore = requireStore();
  const canonicalPath = activeStore.getCanonicalSourcePath(runtime.rolloutId);
  if (canonicalPath === null || normalizedWatcherPath(canonicalPath).key !== normalizedWatcherPath(filePath).key) return processFullFile(filePath, { kind: "inventory" }, yields);
  const before = await stat(filePath);
  if (before.size < runtime.byteOffset) {
    return processFullFile(filePath, { kind: "canonical-prefix-rewrite", expectedRolloutId: runtime.rolloutId }, yields);
  }
  if (await readBoundary(filePath, runtime.byteOffset) !== runtime.boundaryHash) {
    return processFullFile(filePath, { kind: "canonical-prefix-rewrite", expectedRolloutId: runtime.rolloutId }, yields);
  }
  const length = before.size - runtime.byteOffset;
  if (length === 0) return false;
  const handle = await open(filePath, "r");
  let buffer: Buffer;
  try {
    buffer = Buffer.alloc(length);
    const read = await handle.read(buffer, 0, length, runtime.byteOffset);
    if (read.bytesRead !== length) throw new Error("Source changed while reading appended bytes.");
  } finally {
    await handle.close();
  }
  const after = await stat(filePath);
  if (!stableStat(before, after)) throw new Error("Source changed while parsing appended bytes.");
  const result = await parseRolloutChunkCooperatively(buffer, runtime.rolloutId, {
    maxBytesPerSlice: PARSER_SLICE_BYTE_LIMIT,
    maxRecordsPerSlice: PARSER_SLICE_RECORD_LIMIT,
    yieldControl: () => yieldParserControl(yields),
  }, runtime.state);
  rejectInternalDamage(filePath, result);
  const resolvedTurns = new Set(result.state.turnModels.map(([turnId]) => turnId));
  const resolvedPreviouslyUnattributed = [...runtime.state.unresolvedTurnIds, ...runtime.state.provisionalTurnIds]
    .some((turnId) => resolvedTurns.has(turnId));
  if (resolvedPreviouslyUnattributed) {
    return processFullFile(filePath, { kind: "late-model-resolution", expectedRolloutId: runtime.rolloutId }, yields);
  }
  addDiagnostics(result.diagnostics);
  if (result.stableByteLength === 0) return false;
  const newOffset = runtime.byteOffset + result.stableByteLength;
  const hash = await readBoundary(filePath, newOffset);
  const source = sourceFrom(filePath, after, newOffset, hash, "canonical", "matches", null);
  const appended = activeStore.appendRolloutSource({ metadata: metadataInput(result), events: usageInputs(result), source, observedAtEpochMs: Date.now() });
  setRuntime(filePath, { rolloutId: runtime.rolloutId, byteOffset: newOffset, boundaryHash: hash, state: result.state });
  return appended.inserted > 0;
}

interface ProcessFileResult {
  readonly changed: boolean;
  readonly succeeded: boolean;
}

interface RevisionSourceCandidate {
  readonly filePath: string;
  readonly rolloutId: string;
  readonly byteOffset: number;
  readonly sizeBytes: number;
  readonly modifiedAtEpochMs: number;
  readonly viable: boolean;
}

async function discoverRevisionSource(filePath: string, known: SourceFileRecord | undefined, yields?: InventoryYieldTracker): Promise<RevisionSourceCandidate> {
  const snapshot = await readStableFullSnapshot(filePath, yields);
  if (snapshot.kind === "unsafe") throw new Error(snapshot.message);
  const { result, sourceStat: after } = snapshot;
  if (known?.rolloutId !== null && known?.rolloutId !== undefined && known.rolloutId !== result.metadata.rolloutId) {
    throw new Error(`Known source rollout changed from ${known.rolloutId} to ${result.metadata.rolloutId}.`);
  }
  const knownCanonicalPath = known?.rolloutId === null || known?.rolloutId === undefined
    ? null
    : requireStore().getCanonicalSourcePath(known.rolloutId);
  return {
    filePath: known?.filePath ?? filePath,
    rolloutId: result.metadata.rolloutId,
    byteOffset: result.stableByteLength,
    sizeBytes: after.size,
    modifiedAtEpochMs: Math.trunc(after.mtimeMs),
    viable: known?.canonicalStatus !== "conflict"
      || (known !== undefined && knownCanonicalPath !== null
        && normalizedWatcherPath(knownCanonicalPath).key === normalizedWatcherPath(known.filePath).key),
  };
}

function recordSourceFailure(filePath: string, error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  const activeStore = requireStore();
  const known = activeStore.listSourceFiles().find((source) => source.filePath === filePath);
  if (known !== undefined) activeStore.upsertSourceFile({ ...known, isPresent: true, lastScannedAtEpochMs: Date.now(), lastError: message });
  activeStore.addDiagnostic({ runId, sourceFilePath: filePath, severity: "warning", code: "source-read-retry", message, detailsJson: null, createdAtEpochMs: Date.now() });
}

async function processFile(filePath: string, context: FullParseContext = { kind: "inventory" }, yields?: InventoryYieldTracker): Promise<ProcessFileResult> {
  const fileKey = normalizedWatcherPath(filePath).key;
  conflictsAttempted.add(fileKey);
  unknownModelsAttempted.add(fileKey);
  const runtime = runtimeByPath.get(fileKey);
  try {
    const changed = context.kind === "parser-revision-rebuild"
      ? await processFullFile(filePath, context, yields)
      : runtime === undefined ? await processFullFile(filePath, context, yields) : await processIncrementalFile(filePath, runtime, yields);
    diagnostics.filesScanned += 1;
    return { changed, succeeded: true };
  } catch (error) {
    recordSourceFailure(filePath, error);
    return { changed: false, succeeded: false };
  }
}

class CooperativeSlice {
  #items = 0;
  #startedAtEpochMs = Date.now();

  constructor(private readonly recordYield: () => void) {}

  async itemProcessed(): Promise<void> {
    this.#items += 1;
    if (this.#items < COOPERATIVE_SLICE_ITEM_LIMIT
      && Date.now() - this.#startedAtEpochMs < COOPERATIVE_SLICE_TIME_BUDGET_MS) return;
    this.recordYield();
    await new Promise<void>((resolve) => setTimeout(resolve, COOPERATIVE_SLICE_DELAY_MS));
    this.#items = 0;
    this.#startedAtEpochMs = Date.now();
  }
}

interface InventoryYieldTracker {
  count: number;
}

async function listRollouts(root: string, yields: InventoryYieldTracker): Promise<readonly string[]> {
  const result: string[] = [];
  const directories = [path.join(root, "sessions"), path.join(root, "archived_sessions")];
  let directoryIndex = 0;
  const slice = new CooperativeSlice(() => { yields.count += 1; });
  while (directoryIndex < directories.length) {
    const directory = directories[directoryIndex];
    directoryIndex += 1;
    try {
      const entries = await opendir(directory);
      for await (const entry of entries) {
        const target = path.join(directory, entry.name);
        if (entry.isDirectory()) directories.push(target);
        else if (entry.isFile() && entry.name.startsWith("rollout-") && entry.name.endsWith(".jsonl")) result.push(path.resolve(target));
        await slice.itemProcessed();
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") continue;
      throw error;
    }
  }
  return result.sort();
}

async function reconcile(): Promise<SyncResult> {
  const activeStore = requireStore();
  const config = requireConfiguration();
  phase = "syncing";
  statusMessage = "Reconciling local rollouts";
  emitUpdated();
  const storedInventoryRunCount = Number(activeStore.getCollectorState(FULL_INVENTORY_RUN_COUNT_STATE_KEY) ?? "0");
  const inventoryRunCount = Number.isSafeInteger(storedInventoryRunCount) && storedInventoryRunCount >= 0
    ? storedInventoryRunCount + 1
    : 1;
  activeStore.setCollectorState(FULL_INVENTORY_RUN_COUNT_STATE_KEY, String(inventoryRunCount), Date.now());
  const yields: InventoryYieldTracker = { count: 0 };
  const paths = await listRollouts(config.codexHome, yields);
  const slice = new CooperativeSlice(() => { yields.count += 1; });
  const present = new Set(paths.map((filePath) => normalizedWatcherPath(filePath).key));
  const knownSources = new Map(activeStore.listSourceFiles().map((source) => [normalizedWatcherPath(source.filePath).key, source] as const));
  const sourcesWithUnknownModels = new Set(activeStore.listCanonicalSourcesWithUnknownModels().map((filePath) => normalizedWatcherPath(filePath).key));
  let changedFiles = 0;
  let usageChanged = false;
  let inventorySucceeded = true;
  for (const source of knownSources.values()) {
    if (source.isPresent && !present.has(normalizedWatcherPath(source.filePath).key)) {
      activeStore.markSourceMissing(source.filePath, Date.now());
      deleteRuntime(source.filePath);
      changedFiles += 1;
    }
    await slice.itemProcessed();
  }
  const storedParserRevision = activeStore.getCollectorState(ROLLOUT_PARSER_REVISION_STATE_KEY);
  const revisionAttemptedPaths = new Set<string>();
  if (storedParserRevision !== String(ROLLOUT_PARSER_REVISION)) {
    let revisionRebuildSucceeded = true;
    const revisionSourcesByRollout = new Map<string, RevisionSourceCandidate[]>();
    const discoveredRollouts = new Set<string>();
    for (const filePath of paths) {
      try {
        const source = await discoverRevisionSource(filePath, knownSources.get(normalizedWatcherPath(filePath).key), yields);
        discoveredRollouts.add(source.rolloutId);
        if (source.viable) {
          const candidates = revisionSourcesByRollout.get(source.rolloutId) ?? [];
          candidates.push(source);
          revisionSourcesByRollout.set(source.rolloutId, candidates);
        }
      } catch (error) {
        recordSourceFailure(filePath, error);
        revisionRebuildSucceeded = false;
        inventorySucceeded = false;
      }
      await slice.itemProcessed();
    }
    for (const rolloutId of discoveredRollouts) {
      const sources = revisionSourcesByRollout.get(rolloutId) ?? [];
      if (sources.length === 0) {
        revisionRebuildSucceeded = false;
      } else {
        const canonicalPath = activeStore.getCanonicalSourcePath(rolloutId);
        const source = sources.find((candidate) => canonicalPath !== null
          && normalizedWatcherPath(candidate.filePath).key === normalizedWatcherPath(canonicalPath).key)
          ?? [...sources].sort((left, right) => right.byteOffset - left.byteOffset
            || right.sizeBytes - left.sizeBytes
            || right.modifiedAtEpochMs - left.modifiedAtEpochMs
            || left.filePath.localeCompare(right.filePath))[0];
        if (source !== undefined) {
          revisionAttemptedPaths.add(normalizedWatcherPath(source.filePath).key);
          changedFiles += 1;
          const result = await processFile(source.filePath, { kind: "parser-revision-rebuild", expectedRolloutId: rolloutId }, yields);
          usageChanged = result.changed || usageChanged;
          revisionRebuildSucceeded = result.succeeded && revisionRebuildSucceeded;
          inventorySucceeded = result.succeeded && inventorySucceeded;
          if (result.succeeded) clearWatcherRetry(source.filePath);
        }
      }
      await slice.itemProcessed();
    }
    if (revisionRebuildSucceeded) {
      const completedAtEpochMs = Date.now();
      activeStore.setCollectorState(ROLLOUT_PARSER_REVISION_STATE_KEY, String(ROLLOUT_PARSER_REVISION), completedAtEpochMs);
    }
  }
  for (const filePath of paths) {
    const fileKey = normalizedWatcherPath(filePath).key;
    if (revisionAttemptedPaths.has(fileKey)) {
      await slice.itemProcessed();
      continue;
    }
    let sourceStat;
    try { sourceStat = await stat(filePath); } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") {
        const vanished = knownSources.get(fileKey);
        if (vanished?.isPresent) activeStore.markSourceMissing(vanished.filePath, Date.now());
        deleteRuntime(vanished?.filePath ?? filePath);
        await slice.itemProcessed();
        continue;
      }
      throw error;
    }
    const known = knownSources.get(fileKey);
    const canonicalPath = known?.rolloutId ? activeStore.getCanonicalSourcePath(known.rolloutId) : null;
    const canonicalUnavailable = canonicalPath !== null && !present.has(normalizedWatcherPath(canonicalPath).key);
    const changed = known === undefined || !known.isPresent || known.sizeBytes !== sourceStat.size || known.modifiedAtEpochMs !== Math.trunc(sourceStat.mtimeMs) || known.byteOffset < sourceStat.size || (known.canonicalStatus === "conflict" && !conflictsAttempted.has(fileKey)) || (sourcesWithUnknownModels.has(fileKey) && !unknownModelsAttempted.has(fileKey)) || (known.canonicalStatus === "candidate" && canonicalUnavailable);
    if (changed) {
      changedFiles += 1;
      const result = await processFile(known?.filePath ?? filePath, { kind: "inventory" }, yields);
      usageChanged = result.changed || usageChanged;
      inventorySucceeded = result.succeeded && inventorySucceeded;
      if (result.succeeded) clearWatcherRetry(known?.filePath ?? filePath);
    }
    await slice.itemProcessed();
  }
  changedFilesLastSync = changedFiles;
  if (inventorySucceeded) {
    lastSuccessfulInventoryEpochMs = Date.now();
    activeStore.setCollectorState("last_successful_inventory_epoch_ms", String(lastSuccessfulInventoryEpochMs), lastSuccessfulInventoryEpochMs);
    activeStore.setCollectorState(FULL_INVENTORY_YIELD_COUNT_STATE_KEY, String(yields.count), lastSuccessfulInventoryEpochMs);
  }
  phase = !inventorySucceeded || activeStore.countSourceConflicts() > 0 || watcherRetryAttempts.size > 0 ? "degraded" : "watching";
  statusMessage = !inventorySucceeded
    ? `Inventory incomplete after processing ${changedFiles} changed sources`
    : changedFiles === 0 ? "Inventory is current" : `Processed ${changedFiles} changed sources`;
  const currentStatus = status();
  emitUpdated();
  return { status: currentStatus, changed: usageChanged };
}

function enqueueOperation<T>(operation: () => Promise<T>): Promise<T> {
  const result = operationQueue.then(operation, operation);
  operationQueue = result.then(() => undefined, () => undefined);
  return result;
}

function startFullInventory(): Promise<SyncResult> {
  const operation = enqueueOperation(reconcile).catch((error: unknown) => {
    phase = "degraded";
    statusMessage = error instanceof Error ? error.message : String(error);
    emitUpdated();
    throw error;
  });
  fullInventoryOperation = operation;
  void operation.then(
    () => { if (fullInventoryOperation === operation) fullInventoryOperation = null; },
    () => { if (fullInventoryOperation === operation) fullInventoryOperation = null; },
  );
  return operation;
}

function requestTimerInventory(): Promise<SyncResult> {
  return fullInventoryOperation ?? startFullInventory();
}

function requestManualInventory(): Promise<SyncResult> {
  if (fullInventoryOperation === null) return startFullInventory();
  if (manualTrailingInventoryOperation !== null) return manualTrailingInventoryOperation;
  const active = fullInventoryOperation;
  const startTrailing = (): Promise<SyncResult> => {
    manualTrailingInventoryOperation = null;
    return startFullInventory();
  };
  const trailing = active.then(startTrailing, startTrailing);
  manualTrailingInventoryOperation = trailing;
  return trailing;
}

function normalizedWatcherPath(filePath: string): { readonly key: string; readonly filePath: string } {
  const resolved = path.resolve(path.normalize(filePath));
  return { key: process.platform === "win32" ? resolved.toLowerCase() : resolved, filePath: resolved };
}

function setRuntime(filePath: string, runtime: SourceRuntime): void {
  runtimeByPath.set(normalizedWatcherPath(filePath).key, runtime);
}

function deleteRuntime(filePath: string): void {
  runtimeByPath.delete(normalizedWatcherPath(filePath).key);
}

function addPendingWatcherPath(filePath: string): void {
  if (!path.basename(filePath).startsWith("rollout-") || !filePath.endsWith(".jsonl")) return;
  const normalized = normalizedWatcherPath(filePath);
  pendingPaths.set(normalized.key, normalized.filePath);
}

function clearWatcherRetry(filePath: string): void {
  const key = normalizedWatcherPath(filePath).key;
  const timer = watcherRetryTimers.get(key);
  if (timer !== undefined) clearTimeout(timer);
  watcherRetryTimers.delete(key);
  watcherRetryAttempts.delete(key);
}

function scheduleWatcherRetry(filePath: string): void {
  const normalized = normalizedWatcherPath(filePath);
  if (watcherRetryTimers.has(normalized.key)) return;
  const attempt = (watcherRetryAttempts.get(normalized.key) ?? 0) + 1;
  watcherRetryAttempts.set(normalized.key, attempt);
  if (attempt > WATCHER_RETRY_MAX_ATTEMPTS) return;
  const delayMs = Math.min(WATCHER_RETRY_BASE_DELAY_MS * 2 ** (attempt - 1), WATCHER_RETRY_MAX_DELAY_MS);
  const timer = setTimeout(() => {
    watcherRetryTimers.delete(normalized.key);
    if (shuttingDown) return;
    addPendingWatcherPath(normalized.filePath);
    ensureWatcherDrainScheduled();
  }, delayMs);
  watcherRetryTimers.set(normalized.key, timer);
}

interface WatcherPathObservation {
  readonly filePath: string;
  readonly isPresent: boolean;
}

async function drainWatcherPaths(): Promise<void> {
  const activeStore = requireStore();
  let processedPaths = 0;
  let usageChanged = false;
  let drainSucceeded = true;
  while (pendingPaths.size > 0) {
    const batch = [...pendingPaths.entries()].slice(0, WATCHER_DRAIN_BATCH_SIZE);
    for (const [key] of batch) pendingPaths.delete(key);
    const observations: WatcherPathObservation[] = [];
    for (const [, filePath] of batch) {
      try {
        await stat(filePath);
        observations.push({ filePath, isPresent: true });
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === "ENOENT") observations.push({ filePath, isPresent: false });
        else {
          recordSourceFailure(filePath, error);
          scheduleWatcherRetry(filePath);
          drainSucceeded = false;
        }
      }
    }

    const knownSources = new Map(activeStore.listSourceFiles().map((source) => [normalizedWatcherPath(source.filePath).key, source] as const));
    for (const observation of observations) {
      if (observation.isPresent) continue;
      const known = knownSources.get(normalizedWatcherPath(observation.filePath).key);
      if (known?.isPresent) {
        activeStore.markSourceMissing(known.filePath, Date.now());
        processedPaths += 1;
        const canonicalPath = known.rolloutId === null ? null : activeStore.getCanonicalSourcePath(known.rolloutId);
        if (known.rolloutId !== null && canonicalPath !== null
          && normalizedWatcherPath(canonicalPath).key === normalizedWatcherPath(known.filePath).key) {
          for (const candidate of activeStore.listSourceFiles()) {
            if (candidate.rolloutId === known.rolloutId && candidate.isPresent
              && normalizedWatcherPath(candidate.filePath).key !== normalizedWatcherPath(known.filePath).key) {
              addPendingWatcherPath(candidate.filePath);
            }
          }
        }
      }
      deleteRuntime(known?.filePath ?? observation.filePath);
    }
    for (const observation of observations) {
      if (!observation.isPresent) continue;
      const known = knownSources.get(normalizedWatcherPath(observation.filePath).key);
      const result = await processFile(known?.filePath ?? observation.filePath);
      usageChanged = result.changed || usageChanged;
      if (result.succeeded) clearWatcherRetry(known?.filePath ?? observation.filePath);
      else {
        scheduleWatcherRetry(known?.filePath ?? observation.filePath);
        drainSucceeded = false;
      }
      processedPaths += 1;
    }
    if (pendingPaths.size > 0) {
      await new Promise<void>((resolve) => setTimeout(resolve, COOPERATIVE_SLICE_DELAY_MS));
    }
  }
  changedFilesLastSync = processedPaths;
  phase = !drainSucceeded || activeStore.countSourceConflicts() > 0 || watcherRetryAttempts.size > 0 ? "degraded" : "watching";
  statusMessage = !drainSucceeded
    ? `Watcher retry scheduled after processing ${processedPaths} paths`
    : processedPaths === 0 ? "Watcher changes are current" : `Processed ${processedPaths} watcher paths`;
  if (processedPaths > 0 || usageChanged || !drainSucceeded) emitUpdated();
}

function ensureWatcherDrainScheduled(): void {
  if (pendingPaths.size === 0 || watcherDrainQueuedOrRunning || debounceTimer !== null) return;
  debounceTimer = setTimeout(() => {
    debounceTimer = null;
    if (shuttingDown || watcherDrainQueuedOrRunning || pendingPaths.size === 0) return;
    watcherDrainQueuedOrRunning = true;
    void enqueueOperation(drainWatcherPaths).catch((error: unknown) => {
      phase = "degraded";
      statusMessage = error instanceof Error ? error.message : String(error);
      emitUpdated();
    }).finally(() => {
      watcherDrainQueuedOrRunning = false;
      if (pendingPaths.size > 0 && !shuttingDown) ensureWatcherDrainScheduled();
    });
  }, requireConfiguration().watcherDebounceMs);
}

function scheduleWatcherDrain(filePath: string): void {
  if (shuttingDown) return;
  clearWatcherRetry(filePath);
  addPendingWatcherPath(filePath);
  ensureWatcherDrainScheduled();
}

async function initialize(config: CollectorConfig): Promise<CollectorStatus> {
  if (store !== null) throw new Error("Collector is already initialized.");
  configuration = config;
  store = new UsageStore(config.databasePath);
  runStartedEpochMs = Date.now();
  const previousRun = store.getLatestCollectorRun();
  if (previousRun !== null) {
    const gapStart = previousRun.completedAtEpochMs ?? previousRun.heartbeatAtEpochMs;
    if (gapStart < runStartedEpochMs) {
      observationCoverage = "gap";
      observationGap = { startUtc: new Date(gapStart).toISOString(), endUtc: new Date(runStartedEpochMs).toISOString() };
    } else observationCoverage = "continuous";
  }
  const storedInventory = store.getCollectorState("last_successful_inventory_epoch_ms");
  lastSuccessfulInventoryEpochMs = storedInventory === null ? null : Number(storedInventory);
  runId = randomUUID();
  store.beginCollectorRun({ runId, trigger: "application-session", startedAtEpochMs: runStartedEpochMs });
  const activeWatcher = watch([path.join(config.codexHome, "sessions"), path.join(config.codexHome, "archived_sessions")], { ignoreInitial: true, awaitWriteFinish: { stabilityThreshold: config.watcherDebounceMs, pollInterval: 250 } });
  watcher = activeWatcher;
  activeWatcher.on("add", scheduleWatcherDrain).on("change", scheduleWatcherDrain).on("unlink", scheduleWatcherDrain).on("error", (error: unknown) => {
    phase = "degraded";
    statusMessage = `Watcher error: ${error instanceof Error ? error.message : String(error)}`;
    emitUpdated();
  });
  await new Promise<void>((resolve, reject) => {
    const onReady = (): void => {
      activeWatcher.off("error", onInitialError);
      resolve();
    };
    const onInitialError = (error: unknown): void => {
      activeWatcher.off("ready", onReady);
      reject(error);
    };
    activeWatcher.once("ready", onReady).once("error", onInitialError);
  });
  await reconcile();
  inventoryTimer = setInterval(() => {
    void requestTimerInventory().catch((error: unknown) => {
      phase = "degraded";
      statusMessage = error instanceof Error ? error.message : String(error);
      emitUpdated();
    });
  }, config.reconcileIntervalMs);
  heartbeatTimer = setInterval(() => {
    if (store !== null && runId !== null) store.heartbeatCollector({ runId, heartbeatAtEpochMs: Date.now(), state: { phase } });
  }, 60_000);
  if (phase !== "degraded" && store.countSourceConflicts() === 0) {
    phase = "watching";
    statusMessage = "Watching Codex rollout history";
  } else phase = "degraded";
  emitUpdated();
  return status();
}

function eventsForFilter(filter: FilterSpec): readonly UsageEvent[] {
  const startEpochMs = Date.parse(filter.startUtc);
  const endEpochMs = Date.parse(filter.endUtc);
  if (!Number.isSafeInteger(startEpochMs) || !Number.isSafeInteger(endEpochMs) || startEpochMs >= endEpochMs) throw new Error("Invalid query time range.");
  return requireStore().queryEvents({ startEpochMs, endEpochMs });
}

async function handle<Method extends CollectorMethod>(method: Method, payload: CollectorRequestMap[Method]["input"]): Promise<CollectorRequestMap[Method]["output"]> {
  switch (method) {
    case "initialize": return await initialize(payload as CollectorRequestMap["initialize"]["input"]) as CollectorRequestMap[Method]["output"];
    case "reconcile": return await reconcile() as CollectorRequestMap[Method]["output"];
    case "query": {
      const filter = payload as FilterSpec;
      return query(eventsForFilter(filter), scanDiagnostics(), filter) as CollectorRequestMap[Method]["output"];
    }
    case "exportCsv": {
      const request = payload as CollectorRequestMap["exportCsv"]["input"];
      const events = eventsForFilter(request.filter);
      const selected = events.filter((event) => matchesFilter(event, request.filter));
      const config = requireConfiguration();
      await assertOutsideDirectories(request.filePath, [path.join(config.codexHome, "sessions"), path.join(config.codexHome, "archived_sessions"), path.join(config.codexHome, "agents")]);
      await writeFile(request.filePath, csvRows(events, request.filter), "utf8");
      return { count: selected.length } as CollectorRequestMap[Method]["output"];
    }
    case "getStatus": return status() as CollectorRequestMap[Method]["output"];
    case "shutdown": {
      shuttingDown = true;
      if (debounceTimer !== null) clearTimeout(debounceTimer);
      pendingPaths.clear();
      for (const timer of watcherRetryTimers.values()) clearTimeout(timer);
      watcherRetryTimers.clear();
      watcherRetryAttempts.clear();
      if (inventoryTimer !== null) clearInterval(inventoryTimer);
      if (heartbeatTimer !== null) clearInterval(heartbeatTimer);
      await watcher?.close();
      if (store !== null && runId !== null) store.finishCollectorRun({ runId, status: "succeeded", completedAtEpochMs: Date.now(), filesScanned: diagnostics.filesScanned, eventsAdded: 0, diagnosticsCount: diagnostics.malformedLines + diagnostics.invalidTokenRelationshipsSkipped, errorMessage: null });
      phase = "stopped";
      statusMessage = "Collector stopped";
      store?.close();
      store = null;
      return null as CollectorRequestMap[Method]["output"];
    }
  }
}

workerPort.on("message", (request: CollectorRequest) => {
  if (request.kind !== "request") return;
  const operation: Promise<unknown> = request.method === "reconcile"
    ? requestManualInventory()
    : enqueueOperation(() => handle(request.method, request.payload));
  void operation.then((result) => {
    const response: CollectorMessage = { kind: "response", requestId: request.requestId, ok: true, result };
    workerPort.postMessage(response);
  }).catch((error: unknown) => {
    const response: CollectorMessage = { kind: "response", requestId: request.requestId, ok: false, error: error instanceof Error ? error.message : String(error) };
    workerPort.postMessage(response);
  });
});
