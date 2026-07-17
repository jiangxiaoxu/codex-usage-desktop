import { createHash, randomUUID } from "node:crypto";
import { open, readFile, readdir, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { parentPort } from "node:worker_threads";
import { watch, type FSWatcher } from "chokidar";
import type { CollectorConfig, CollectorMessage, CollectorRequest, CollectorRequestMap, CollectorMethod } from "./collector-protocol";
import { parseRolloutChunk, type RolloutChunkParseResult, type RolloutParseDiagnostics, type RolloutParserState } from "./rollout-parser";
import type { CollectorStatus, FilterSpec, QueryResult, ScanDiagnostics, SyncResult, UsageEvent } from "./shared";
import { csvRows, matchesFilter, query } from "./usage-core";
import { UsageStore, type CandidateSourceInput, type RolloutMetadataInput, type SourceFileRecord, type UsageEventInput } from "./usage-store";
import { assertOutsideDirectories } from "./write-boundary";

const port = parentPort;
if (port === null) throw new Error("Collector worker requires a parent port.");
const workerPort = port;

const BOUNDARY_WINDOW_BYTES = 64 * 1024;
const ROLLOUT_PARSER_REVISION = 4;
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
let runId: string | null = null;
let runStartedEpochMs = 0;
let lastSuccessfulInventoryEpochMs: number | null = null;
let observationCoverage: CollectorStatus["observationCoverage"] = "baseline";
let observationGap: { readonly startUtc: string; readonly endUtc: string } | null = null;
let phase: CollectorStatus["phase"] = "initializing";
let statusMessage = "Starting collector";
let changedFilesLastSync = 0;
let pendingPaths = new Set<string>();
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

async function processFullFile(filePath: string, forceCanonicalRolloutId: string | null = null): Promise<boolean> {
  const activeStore = requireStore();
  const before = await stat(filePath);
  const buffer = await readFile(filePath);
  const after = await stat(filePath);
  if (!stableStat(before, after)) throw new Error("Source changed while being parsed.");
  const result = parseRolloutChunk(buffer, fallbackRolloutId(filePath));
  rejectInternalDamage(filePath, result);
  addDiagnostics(result.diagnostics);
  const candidateIdentities = result.events.map(eventIdentity);
  const existingIdentities = activeStore.getRolloutEventIdentities(result.metadata.rolloutId);
  const relation = signatureRelation(existingIdentities, candidateIdentities);
  const observedAt = Date.now();
  const hash = boundaryHash(buffer, result.stableByteLength);
  if (forceCanonicalRolloutId !== null) {
    if (result.metadata.rolloutId !== forceCanonicalRolloutId) {
      throw new Error(`Canonical source rollout changed from ${forceCanonicalRolloutId} to ${result.metadata.rolloutId}.`);
    }
    const source = sourceFrom(filePath, after, result.stableByteLength, hash, "canonical", "matches", null);
    activeStore.replaceRolloutCandidate({ metadata: metadataInput(result), events: usageInputs(result), source, observedAtEpochMs: observedAt });
    activeStore.promoteRolloutCandidate({ rolloutId: forceCanonicalRolloutId, canonicalFilePath: filePath, promotedAtEpochMs: observedAt });
    runtimeByPath.set(filePath, { rolloutId: forceCanonicalRolloutId, byteOffset: result.stableByteLength, boundaryHash: hash, state: result.state });
    return true;
  }
  const canonicalPath = activeStore.getCanonicalSourcePath(result.metadata.rolloutId);
  const presentPaths = new Set(activeStore.listSourceFiles().filter((source) => source.isPresent).map((source) => source.filePath));
  if (relation === "diverged") {
    activeStore.upsertSourceFile({ ...sourceFrom(filePath, after, result.stableByteLength, hash, "conflict", "diverged", "Candidate diverges from the canonical event prefix."), rolloutId: result.metadata.rolloutId });
    activeStore.recordSourceConflict({ runId, sourceFilePath: filePath, code: "source-diverged", message: "Rollout source diverges from the canonical event prefix.", detailsJson: JSON.stringify({ rolloutId: result.metadata.rolloutId }), observedAtEpochMs: observedAt });
    runtimeByPath.delete(filePath);
    return false;
  }
  if (relation === "shorter") {
    activeStore.upsertSourceFile({ ...sourceFrom(filePath, after, result.stableByteLength, hash, "candidate", "matches", null), rolloutId: result.metadata.rolloutId });
    runtimeByPath.delete(filePath);
    return false;
  }
  const isCurrentCanonical = canonicalPath === filePath;
  const candidateSemantic = result.events.map(eventSemanticSignature);
  const canonicalSemantic = activeStore.getRolloutSemanticSignatures(result.metadata.rolloutId);
  const semanticRelation = signatureRelation(canonicalSemantic, candidateSemantic);
  const attributionMatches = sameMetadata(activeStore.getRolloutMetadata(result.metadata.rolloutId), metadataInput(result))
    && (semanticRelation === "equal" || semanticRelation === "extension");
  if (existingIdentities.length > 0 && !isCurrentCanonical && !attributionMatches) {
    const conflictSource = sourceFrom(filePath, after, result.stableByteLength, hash, "conflict", "diverged", "Candidate metadata or model attribution differs from the canonical rollout.");
    activeStore.upsertSourceFile({ ...conflictSource, rolloutId: result.metadata.rolloutId });
    activeStore.recordSourceConflict({ runId, sourceFilePath: filePath, code: "source-attribution-diverged", message: "Rollout candidate metadata or model attribution differs from the canonical rollout.", detailsJson: JSON.stringify({ rolloutId: result.metadata.rolloutId }), observedAtEpochMs: observedAt });
    runtimeByPath.delete(filePath);
    return false;
  }
  const shouldPromote = relation === "extension" || canonicalPath === null || !presentPaths.has(canonicalPath) || isCurrentCanonical;
  const source = sourceFrom(filePath, after, result.stableByteLength, hash, shouldPromote ? "canonical" : "candidate", "matches", null);
  if (!shouldPromote) {
    activeStore.upsertSourceFile({ ...source, rolloutId: result.metadata.rolloutId });
    runtimeByPath.delete(filePath);
    return false;
  }
  activeStore.replaceRolloutCandidate({ metadata: metadataInput(result), events: usageInputs(result), source, observedAtEpochMs: observedAt });
  activeStore.promoteRolloutCandidate({ rolloutId: result.metadata.rolloutId, canonicalFilePath: filePath, promotedAtEpochMs: observedAt });
  runtimeByPath.set(filePath, { rolloutId: result.metadata.rolloutId, byteOffset: result.stableByteLength, boundaryHash: hash, state: result.state });
  return relation === "extension" || existingIdentities.length === 0;
}

async function processIncrementalFile(filePath: string, runtime: SourceRuntime): Promise<boolean> {
  const activeStore = requireStore();
  if (activeStore.getCanonicalSourcePath(runtime.rolloutId) !== filePath) return processFullFile(filePath);
  const before = await stat(filePath);
  if (before.size < runtime.byteOffset) return processFullFile(filePath);
  if (await readBoundary(filePath, runtime.byteOffset) !== runtime.boundaryHash) return processFullFile(filePath);
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
  const result = parseRolloutChunk(buffer, runtime.rolloutId, runtime.state);
  rejectInternalDamage(filePath, result);
  const resolvedTurns = new Set(result.state.turnModels.map(([turnId]) => turnId));
  const resolvedPreviouslyUnattributed = [...runtime.state.unresolvedTurnIds, ...runtime.state.provisionalTurnIds]
    .some((turnId) => resolvedTurns.has(turnId));
  if (resolvedPreviouslyUnattributed) return processFullFile(filePath);
  addDiagnostics(result.diagnostics);
  if (result.stableByteLength === 0) return false;
  const newOffset = runtime.byteOffset + result.stableByteLength;
  const hash = await readBoundary(filePath, newOffset);
  const source = sourceFrom(filePath, after, newOffset, hash, "canonical", "matches", null);
  const appended = activeStore.appendRolloutSource({ metadata: metadataInput(result), events: usageInputs(result), source, observedAtEpochMs: Date.now() });
  runtimeByPath.set(filePath, { rolloutId: runtime.rolloutId, byteOffset: newOffset, boundaryHash: hash, state: result.state });
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

async function discoverRevisionSource(filePath: string, known: SourceFileRecord | undefined): Promise<RevisionSourceCandidate> {
  const before = await stat(filePath);
  const buffer = await readFile(filePath);
  const after = await stat(filePath);
  if (!stableStat(before, after)) throw new Error("Source changed while being discovered for parser revision rebuild.");
  const result = parseRolloutChunk(buffer, fallbackRolloutId(filePath));
  rejectInternalDamage(filePath, result);
  if (known?.rolloutId !== null && known?.rolloutId !== undefined && known.rolloutId !== result.metadata.rolloutId) {
    throw new Error(`Known source rollout changed from ${known.rolloutId} to ${result.metadata.rolloutId}.`);
  }
  return {
    filePath,
    rolloutId: result.metadata.rolloutId,
    byteOffset: result.stableByteLength,
    sizeBytes: after.size,
    modifiedAtEpochMs: Math.trunc(after.mtimeMs),
    viable: known?.canonicalStatus !== "conflict",
  };
}

function recordSourceFailure(filePath: string, error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  const activeStore = requireStore();
  const known = activeStore.listSourceFiles().find((source) => source.filePath === filePath);
  if (known !== undefined) activeStore.upsertSourceFile({ ...known, isPresent: true, lastScannedAtEpochMs: Date.now(), lastError: message });
  activeStore.addDiagnostic({ runId, sourceFilePath: filePath, severity: "warning", code: "source-read-retry", message, detailsJson: null, createdAtEpochMs: Date.now() });
}

async function processFile(filePath: string, forceCanonicalRolloutId: string | null = null): Promise<ProcessFileResult> {
  conflictsAttempted.add(filePath);
  unknownModelsAttempted.add(filePath);
  const runtime = runtimeByPath.get(filePath);
  try {
    const changed = forceCanonicalRolloutId === null
      ? runtime === undefined ? await processFullFile(filePath) : await processIncrementalFile(filePath, runtime)
      : await processFullFile(filePath, forceCanonicalRolloutId);
    diagnostics.filesScanned += 1;
    return { changed, succeeded: true };
  } catch (error) {
    recordSourceFailure(filePath, error);
    return { changed: false, succeeded: false };
  }
}

async function listRollouts(root: string): Promise<readonly string[]> {
  const result: string[] = [];
  async function visit(directory: string): Promise<void> {
    let entries;
    try { entries = await readdir(directory, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const target = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(target);
      else if (entry.isFile() && entry.name.startsWith("rollout-") && entry.name.endsWith(".jsonl")) result.push(path.resolve(target));
    }
  }
  await visit(path.join(root, "sessions"));
  await visit(path.join(root, "archived_sessions"));
  return result.sort();
}

async function reconcile(): Promise<SyncResult> {
  const activeStore = requireStore();
  const config = requireConfiguration();
  phase = "syncing";
  statusMessage = "Reconciling local rollouts";
  emitUpdated();
  const paths = await listRollouts(config.codexHome);
  const present = new Set(paths);
  const knownSources = new Map(activeStore.listSourceFiles().map((source) => [source.filePath, source] as const));
  const sourcesWithUnknownModels = new Set(activeStore.listCanonicalSourcesWithUnknownModels());
  let changedFiles = 0;
  let usageChanged = false;
  for (const source of knownSources.values()) {
    if (source.isPresent && !present.has(source.filePath)) {
      activeStore.markSourceMissing(source.filePath, Date.now());
      runtimeByPath.delete(source.filePath);
      changedFiles += 1;
    }
  }
  const storedParserRevision = activeStore.getCollectorState(ROLLOUT_PARSER_REVISION_STATE_KEY);
  const revisionAttemptedPaths = new Set<string>();
  if (storedParserRevision !== String(ROLLOUT_PARSER_REVISION)) {
    let revisionRebuildSucceeded = true;
    const revisionSourcesByRollout = new Map<string, RevisionSourceCandidate[]>();
    const discoveredRollouts = new Set<string>();
    for (const filePath of paths) {
      try {
        const source = await discoverRevisionSource(filePath, knownSources.get(filePath));
        discoveredRollouts.add(source.rolloutId);
        if (!source.viable) continue;
        const candidates = revisionSourcesByRollout.get(source.rolloutId) ?? [];
        candidates.push(source);
        revisionSourcesByRollout.set(source.rolloutId, candidates);
      } catch (error) {
        recordSourceFailure(filePath, error);
        revisionRebuildSucceeded = false;
      }
    }
    for (const rolloutId of discoveredRollouts) {
      const sources = revisionSourcesByRollout.get(rolloutId) ?? [];
      if (sources.length === 0) {
        revisionRebuildSucceeded = false;
        continue;
      }
      const canonicalPath = activeStore.getCanonicalSourcePath(rolloutId);
      const source = sources.find((candidate) => candidate.filePath === canonicalPath)
        ?? [...sources].sort((left, right) => right.byteOffset - left.byteOffset
          || right.sizeBytes - left.sizeBytes
          || right.modifiedAtEpochMs - left.modifiedAtEpochMs
          || left.filePath.localeCompare(right.filePath))[0];
      if (source === undefined) continue;
      revisionAttemptedPaths.add(source.filePath);
      changedFiles += 1;
      const result = await processFile(source.filePath, rolloutId);
      usageChanged = result.changed || usageChanged;
      revisionRebuildSucceeded = result.succeeded && revisionRebuildSucceeded;
    }
    if (revisionRebuildSucceeded) {
      const completedAtEpochMs = Date.now();
      activeStore.setCollectorState(ROLLOUT_PARSER_REVISION_STATE_KEY, String(ROLLOUT_PARSER_REVISION), completedAtEpochMs);
    }
  }
  for (const filePath of paths) {
    if (revisionAttemptedPaths.has(filePath)) continue;
    let sourceStat;
    try { sourceStat = await stat(filePath); } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") {
        const vanished = knownSources.get(filePath);
        if (vanished?.isPresent) activeStore.markSourceMissing(filePath, Date.now());
        runtimeByPath.delete(filePath);
        continue;
      }
      throw error;
    }
    const known = knownSources.get(filePath);
    const canonicalPath = known?.rolloutId ? activeStore.getCanonicalSourcePath(known.rolloutId) : null;
    const canonicalUnavailable = canonicalPath !== null && !present.has(canonicalPath);
    const changed = known === undefined || !known.isPresent || known.sizeBytes !== sourceStat.size || known.modifiedAtEpochMs !== Math.trunc(sourceStat.mtimeMs) || known.byteOffset < sourceStat.size || (known.canonicalStatus === "conflict" && !conflictsAttempted.has(filePath)) || (sourcesWithUnknownModels.has(filePath) && !unknownModelsAttempted.has(filePath)) || (known.canonicalStatus === "candidate" && canonicalUnavailable);
    if (!changed) continue;
    changedFiles += 1;
    const result = await processFile(filePath);
    usageChanged = result.changed || usageChanged;
  }
  changedFilesLastSync = changedFiles;
  lastSuccessfulInventoryEpochMs = Date.now();
  activeStore.setCollectorState("last_successful_inventory_epoch_ms", String(lastSuccessfulInventoryEpochMs), lastSuccessfulInventoryEpochMs);
  phase = activeStore.countSourceConflicts() > 0 ? "degraded" : "watching";
  statusMessage = changedFiles === 0 ? "Inventory is current" : `Processed ${changedFiles} changed sources`;
  const currentStatus = status();
  emitUpdated();
  return { status: currentStatus, changed: usageChanged };
}

function enqueueOperation<T>(operation: () => Promise<T>): Promise<T> {
  const result = operationQueue.then(operation, operation);
  operationQueue = result.then(() => undefined, () => undefined);
  return result;
}

function scheduleWatcherReconcile(filePath: string): void {
  if (shuttingDown) return;
  pendingPaths.add(path.resolve(filePath));
  if (debounceTimer !== null) clearTimeout(debounceTimer);
  debounceTimer = setTimeout(() => {
    debounceTimer = null;
    pendingPaths = new Set<string>();
    void enqueueOperation(reconcile).catch((error: unknown) => {
      phase = "degraded";
      statusMessage = error instanceof Error ? error.message : String(error);
      emitUpdated();
    });
  }, requireConfiguration().watcherDebounceMs);
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
  activeWatcher.on("add", scheduleWatcherReconcile).on("change", scheduleWatcherReconcile).on("unlink", scheduleWatcherReconcile).on("error", (error: unknown) => {
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
    void enqueueOperation(reconcile).catch((error: unknown) => {
      phase = "degraded";
      statusMessage = error instanceof Error ? error.message : String(error);
      emitUpdated();
    });
  }, config.reconcileIntervalMs);
  heartbeatTimer = setInterval(() => {
    if (store !== null && runId !== null) store.heartbeatCollector({ runId, heartbeatAtEpochMs: Date.now(), state: { phase } });
  }, 60_000);
  phase = store.countSourceConflicts() > 0 ? "degraded" : "watching";
  statusMessage = "Watching Codex rollout history";
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
  void enqueueOperation(() => handle(request.method, request.payload)).then((result) => {
    const response: CollectorMessage = { kind: "response", requestId: request.requestId, ok: true, result };
    workerPort.postMessage(response);
  }).catch((error: unknown) => {
    const response: CollectorMessage = { kind: "response", requestId: request.requestId, ok: false, error: error instanceof Error ? error.message : String(error) };
    workerPort.postMessage(response);
  });
});
