import { mkdirSync } from "node:fs";
import path from "node:path";
import { DatabaseSync, SQLOutputValue } from "node:sqlite";

const SCHEMA_VERSION = 1;
const DEFAULT_BUSY_TIMEOUT_MS = 5_000;

export type StoredThreadType = "main" | "subagent" | "unknown";
export type PrefixStatus = "unknown" | "matches" | "diverged";
export type CanonicalStatus = "candidate" | "canonical" | "conflict";
export type DiagnosticSeverity = "info" | "warning" | "error";
export type CollectorRunStatus = "running" | "succeeded" | "failed";

export interface RolloutMetadataInput {
  readonly rolloutId: string;
  readonly conversationId: string;
  readonly parentThreadId: string;
  readonly threadType: StoredThreadType;
  readonly agentRole: string;
  readonly agentPath: string;
  readonly agentNickname: string;
}

export interface UsageEventInput {
  readonly tokenEventOrdinal: number;
  readonly timestampEpochMs: number;
  readonly model: string;
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningOutputTokens: number;
  readonly eventSignature: string;
}

export interface SourceFileInput {
  readonly filePath: string;
  readonly rolloutId: string | null;
  readonly sizeBytes: number;
  readonly modifiedAtEpochMs: number;
  readonly byteOffset: number;
  readonly prefixHash: string;
  readonly prefixStatus: PrefixStatus;
  readonly canonicalStatus: CanonicalStatus;
  readonly isPresent: boolean;
  readonly lastScannedAtEpochMs: number;
  readonly lastError: string | null;
}

export interface CandidateSourceInput extends Omit<SourceFileInput, "rolloutId"> {}

export interface ReplaceRolloutCandidateInput {
  readonly metadata: RolloutMetadataInput;
  readonly events: readonly UsageEventInput[];
  readonly source: CandidateSourceInput;
  readonly observedAtEpochMs: number;
}

export interface AppendRolloutSourceInput {
  readonly metadata: RolloutMetadataInput;
  readonly events: readonly UsageEventInput[];
  readonly source: CandidateSourceInput;
  readonly observedAtEpochMs: number;
}

export interface PromoteRolloutCandidateInput {
  readonly rolloutId: string;
  readonly canonicalFilePath: string;
  readonly promotedAtEpochMs: number;
}

export interface AppendEventsResult {
  readonly inserted: number;
  readonly ignoredAsDuplicate: number;
}

export interface StoredUsageEvent {
  readonly timestampUtc: string;
  readonly conversationId: string;
  readonly rolloutId: string;
  readonly parentThreadId: string;
  readonly threadType: StoredThreadType;
  readonly agentRole: string;
  readonly agentPath: string;
  readonly agentNickname: string;
  readonly model: string;
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningOutputTokens: number;
  readonly tokenEventOrdinal: number;
  readonly timestampEpochMs: number;
  readonly eventSignature: string;
}

export interface SourceFileRecord {
  readonly filePath: string;
  readonly rolloutId: string | null;
  readonly sizeBytes: number;
  readonly modifiedAtEpochMs: number;
  readonly byteOffset: number;
  readonly prefixHash: string;
  readonly prefixStatus: PrefixStatus;
  readonly canonicalStatus: CanonicalStatus;
  readonly isPresent: boolean;
  readonly lastScannedAtEpochMs: number;
  readonly lastError: string | null;
}

export interface UsageEventQuery {
  readonly startEpochMs: number;
  readonly endEpochMs: number;
  readonly models?: readonly string[];
  readonly agentRoles?: readonly string[];
  readonly threadTypes?: readonly StoredThreadType[];
  readonly pathQuery?: string;
}

export interface CollectorDiagnosticInput {
  readonly runId: string | null;
  readonly sourceFilePath: string | null;
  readonly severity: DiagnosticSeverity;
  readonly code: string;
  readonly message: string;
  readonly detailsJson: string | null;
  readonly createdAtEpochMs: number;
}

export interface SourceConflictInput {
  readonly runId: string | null;
  readonly sourceFilePath: string;
  readonly code: string;
  readonly message: string;
  readonly detailsJson: string | null;
  readonly observedAtEpochMs: number;
}

export interface CollectorRunStartInput {
  readonly runId: string;
  readonly trigger: string;
  readonly startedAtEpochMs: number;
}

export interface CollectorRunHeartbeatInput {
  readonly runId: string;
  readonly heartbeatAtEpochMs: number;
  readonly state?: Readonly<Record<string, string>>;
}

export interface CollectorRunFinishInput {
  readonly runId: string;
  readonly status: Exclude<CollectorRunStatus, "running">;
  readonly completedAtEpochMs: number;
  readonly filesScanned: number;
  readonly eventsAdded: number;
  readonly diagnosticsCount: number;
  readonly errorMessage: string | null;
}

export interface CollectorRunRecord {
  readonly runId: string;
  readonly trigger: string;
  readonly status: CollectorRunStatus;
  readonly startedAtEpochMs: number;
  readonly heartbeatAtEpochMs: number;
  readonly completedAtEpochMs: number | null;
  readonly filesScanned: number;
  readonly eventsAdded: number;
  readonly diagnosticsCount: number;
  readonly errorMessage: string | null;
}

export interface CheckpointResult {
  readonly busy: number;
  readonly logFrames: number;
  readonly checkpointedFrames: number;
}

type SqlRow = Record<string, SQLOutputValue>;

function requireInputString(value: string, name: string, allowEmpty = false): string {
  if (typeof value !== "string" || (!allowEmpty && value.length === 0)) throw new TypeError(`${name} must be a ${allowEmpty ? "string" : "non-empty string"}`);
  return value;
}

function requireInputInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${name} must be a non-negative safe integer`);
  return value;
}

function requireEnum<T extends string>(value: string, values: readonly T[], name: string): T {
  if (!(values as readonly string[]).includes(value)) throw new TypeError(`${name} has an invalid value`);
  return value as T;
}

function rowString(row: SqlRow, column: string): string {
  const value = row[column];
  if (typeof value !== "string") throw new TypeError(`Database column ${column} is not a string`);
  return value;
}

function rowNullableString(row: SqlRow, column: string): string | null {
  const value = row[column];
  if (value === null) return null;
  if (typeof value !== "string") throw new TypeError(`Database column ${column} is not a nullable string`);
  return value;
}

function safeInteger(value: SQLOutputValue | number | bigint, name: string): number {
  if (typeof value === "bigint") {
    if (value < BigInt(Number.MIN_SAFE_INTEGER) || value > BigInt(Number.MAX_SAFE_INTEGER)) throw new RangeError(`Database integer ${name} exceeds the JavaScript safe integer range`);
    return Number(value);
  }
  if (typeof value !== "number" || !Number.isSafeInteger(value)) throw new TypeError(`Database value ${name} is not a safe integer`);
  return value;
}

function rowInteger(row: SqlRow, column: string): number {
  const value = row[column];
  if (value === null || typeof value === "string" || value instanceof Uint8Array) throw new TypeError(`Database column ${column} is not an integer`);
  return safeInteger(value, column);
}

function rowNullableInteger(row: SqlRow, column: string): number | null {
  if (row[column] === null) return null;
  return rowInteger(row, column);
}

function validateMetadata(metadata: RolloutMetadataInput): void {
  requireInputString(metadata.rolloutId, "metadata.rolloutId");
  requireInputString(metadata.conversationId, "metadata.conversationId");
  requireInputString(metadata.parentThreadId, "metadata.parentThreadId", true);
  requireEnum(metadata.threadType, ["main", "subagent", "unknown"], "metadata.threadType");
  requireInputString(metadata.agentRole, "metadata.agentRole");
  requireInputString(metadata.agentPath, "metadata.agentPath", true);
  requireInputString(metadata.agentNickname, "metadata.agentNickname", true);
}

function validateEvent(event: UsageEventInput): void {
  requireInputInteger(event.tokenEventOrdinal, "event.tokenEventOrdinal");
  requireInputInteger(event.timestampEpochMs, "event.timestampEpochMs");
  requireInputString(event.model, "event.model");
  requireInputInteger(event.inputTokens, "event.inputTokens");
  requireInputInteger(event.cachedInputTokens, "event.cachedInputTokens");
  requireInputInteger(event.outputTokens, "event.outputTokens");
  requireInputInteger(event.reasoningOutputTokens, "event.reasoningOutputTokens");
  requireInputString(event.eventSignature, "event.eventSignature");
  if (event.cachedInputTokens > event.inputTokens) throw new RangeError("event.cachedInputTokens cannot exceed event.inputTokens");
  if (event.reasoningOutputTokens > event.outputTokens) throw new RangeError("event.reasoningOutputTokens cannot exceed event.outputTokens");
}

function validateSource(source: SourceFileInput): void {
  requireInputString(source.filePath, "source.filePath");
  if (source.rolloutId !== null) requireInputString(source.rolloutId, "source.rolloutId");
  requireInputInteger(source.sizeBytes, "source.sizeBytes");
  requireInputInteger(source.modifiedAtEpochMs, "source.modifiedAtEpochMs");
  requireInputInteger(source.byteOffset, "source.byteOffset");
  if (source.byteOffset > source.sizeBytes) throw new RangeError("source.byteOffset cannot exceed source.sizeBytes");
  requireInputString(source.prefixHash, "source.prefixHash", true);
  requireEnum(source.prefixStatus, ["unknown", "matches", "diverged"], "source.prefixStatus");
  requireEnum(source.canonicalStatus, ["candidate", "canonical", "conflict"], "source.canonicalStatus");
  if (typeof source.isPresent !== "boolean") throw new TypeError("source.isPresent must be a boolean");
  requireInputInteger(source.lastScannedAtEpochMs, "source.lastScannedAtEpochMs");
  if (source.lastError !== null) requireInputString(source.lastError, "source.lastError", true);
}

export class UsageStore {
  readonly databasePath: string;
  private readonly database: DatabaseSync;
  private closed = false;

  constructor(databasePath: string, options: { readonly busyTimeoutMs?: number } = {}) {
    this.databasePath = requireInputString(databasePath, "databasePath");
    const busyTimeoutMs = requireInputInteger(options.busyTimeoutMs ?? DEFAULT_BUSY_TIMEOUT_MS, "options.busyTimeoutMs");
    if (databasePath !== ":memory:") mkdirSync(path.dirname(path.resolve(databasePath)), { recursive: true });
    this.database = new DatabaseSync(databasePath, {
      enableForeignKeyConstraints: true,
      enableDoubleQuotedStringLiterals: false,
      timeout: busyTimeoutMs,
      readBigInts: true,
      returnArrays: false,
      allowBareNamedParameters: false,
      allowUnknownNamedParameters: false,
    });
    try {
      this.database.exec("PRAGMA foreign_keys = ON");
      this.database.exec(`PRAGMA busy_timeout = ${busyTimeoutMs}`);
      this.database.exec("PRAGMA journal_mode = WAL");
      this.migrate();
    } catch (error) {
      this.database.close();
      this.closed = true;
      throw error;
    }
  }

  get schemaVersion(): number {
    this.assertOpen();
    const row = this.database.prepare("PRAGMA user_version").get();
    if (row === undefined) throw new Error("PRAGMA user_version returned no row");
    return rowInteger(row, "user_version");
  }

  appendEvents(metadata: RolloutMetadataInput, events: readonly UsageEventInput[], observedAtEpochMs: number): AppendEventsResult {
    this.assertOpen();
    validateMetadata(metadata);
    requireInputInteger(observedAtEpochMs, "observedAtEpochMs");
    for (const event of events) validateEvent(event);
    return this.writeTransaction(() => this.appendWithinTransaction(metadata, events, observedAtEpochMs));
  }

  appendRolloutSource(input: AppendRolloutSourceInput): AppendEventsResult {
    this.assertOpen();
    validateMetadata(input.metadata);
    requireInputInteger(input.observedAtEpochMs, "input.observedAtEpochMs");
    for (const event of input.events) validateEvent(event);
    const source: SourceFileInput = { ...input.source, rolloutId: input.metadata.rolloutId };
    validateSource(source);
    return this.writeTransaction(() => {
      const result = this.appendWithinTransaction(input.metadata, input.events, input.observedAtEpochMs);
      this.upsertSourceFileWithinTransaction(source);
      return result;
    });
  }

  replaceRolloutCandidate(input: ReplaceRolloutCandidateInput): void {
    this.assertOpen();
    validateMetadata(input.metadata);
    requireInputInteger(input.observedAtEpochMs, "input.observedAtEpochMs");
    for (const event of input.events) validateEvent(event);
    const source: SourceFileInput = { ...input.source, rolloutId: input.metadata.rolloutId };
    validateSource(source);
    this.writeTransaction(() => {
      this.upsertRollout(input.metadata, input.observedAtEpochMs);
      this.database.prepare("DELETE FROM usage_events WHERE rollout_id = ?").run(input.metadata.rolloutId);
      const insert = this.database.prepare(`
        INSERT INTO usage_events (
          rollout_id, token_event_ordinal, timestamp_epoch_ms, model,
          input_tokens, cached_input_tokens, output_tokens,
          reasoning_output_tokens, event_signature
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
      `);
      for (const event of input.events) {
        insert.run(input.metadata.rolloutId, event.tokenEventOrdinal, event.timestampEpochMs, event.model, event.inputTokens, event.cachedInputTokens, event.outputTokens, event.reasoningOutputTokens, event.eventSignature);
      }
      this.upsertSourceFileWithinTransaction(source);
    });
  }

  promoteRolloutCandidate(input: PromoteRolloutCandidateInput): void {
    this.assertOpen();
    requireInputString(input.rolloutId, "input.rolloutId");
    requireInputString(input.canonicalFilePath, "input.canonicalFilePath");
    requireInputInteger(input.promotedAtEpochMs, "input.promotedAtEpochMs");
    this.writeTransaction(() => {
      const source = this.database.prepare("SELECT rollout_id FROM source_files WHERE file_path = ? AND is_present = 1").get(input.canonicalFilePath);
      if (source === undefined || rowNullableString(source, "rollout_id") !== input.rolloutId) throw new Error("Canonical source is not a present candidate for the rollout");
      const rolloutUpdate = this.database.prepare(`
        UPDATE rollouts SET canonical_source_path = ?, updated_at_epoch_ms = ?
        WHERE rollout_id = ?
      `).run(input.canonicalFilePath, input.promotedAtEpochMs, input.rolloutId);
      if (safeInteger(rolloutUpdate.changes, "rollout promotion changes") !== 1) throw new Error(`Unknown rollout: ${input.rolloutId}`);
      this.database.prepare(`
        UPDATE source_files
        SET canonical_status = CASE
          WHEN file_path = ? THEN 'canonical'
          WHEN canonical_status = 'canonical' THEN 'candidate'
          ELSE canonical_status
        END,
        last_scanned_at_epoch_ms = CASE WHEN file_path = ? THEN ? ELSE last_scanned_at_epoch_ms END
        WHERE rollout_id = ?
      `).run(input.canonicalFilePath, input.canonicalFilePath, input.promotedAtEpochMs, input.rolloutId);
    });
  }

  upsertSourceFile(source: SourceFileInput): void {
    this.assertOpen();
    validateSource(source);
    this.writeTransaction(() => this.upsertSourceFileWithinTransaction(source));
  }

  markSourceMissing(filePath: string, lastScannedAtEpochMs: number): boolean {
    this.assertOpen();
    requireInputString(filePath, "filePath");
    requireInputInteger(lastScannedAtEpochMs, "lastScannedAtEpochMs");
    return this.writeTransaction(() => {
      const result = this.database.prepare(`
        UPDATE source_files
        SET is_present = 0, last_scanned_at_epoch_ms = ?
        WHERE file_path = ?
      `).run(lastScannedAtEpochMs, filePath);
      return safeInteger(result.changes, "mark source missing changes") === 1;
    });
  }

  listSourceFiles(): readonly SourceFileRecord[] {
    this.assertOpen();
    const rows = this.database.prepare(`
      SELECT file_path, rollout_id, size_bytes, modified_at_epoch_ms,
             byte_offset, prefix_hash, prefix_status, canonical_status,
             is_present, last_scanned_at_epoch_ms, last_error
      FROM source_files
      ORDER BY file_path
    `).all();
    return rows.map((row) => this.mapSourceFile(row));
  }

  getRolloutEventSignatures(rolloutId: string): readonly string[] {
    this.assertOpen();
    requireInputString(rolloutId, "rolloutId");
    return this.database.prepare(`
      SELECT event_signature FROM usage_events
      WHERE rollout_id = ?
      ORDER BY token_event_ordinal
    `).all(rolloutId).map((row) => rowString(row, "event_signature"));
  }

  getRolloutEventIdentities(rolloutId: string): readonly string[] {
    this.assertOpen();
    requireInputString(rolloutId, "rolloutId");
    return this.database.prepare(`
      SELECT timestamp_epoch_ms, input_tokens, cached_input_tokens,
             output_tokens, reasoning_output_tokens
      FROM usage_events
      WHERE rollout_id = ?
      ORDER BY token_event_ordinal
    `).all(rolloutId).map((row) => JSON.stringify([
      rowInteger(row, "timestamp_epoch_ms"),
      rowInteger(row, "input_tokens"),
      rowInteger(row, "cached_input_tokens"),
      rowInteger(row, "output_tokens"),
      rowInteger(row, "reasoning_output_tokens"),
    ]));
  }

  getRolloutSemanticSignatures(rolloutId: string): readonly string[] {
    this.assertOpen();
    requireInputString(rolloutId, "rolloutId");
    return this.database.prepare(`
      SELECT timestamp_epoch_ms, model, input_tokens, cached_input_tokens,
             output_tokens, reasoning_output_tokens
      FROM usage_events
      WHERE rollout_id = ?
      ORDER BY token_event_ordinal
    `).all(rolloutId).map((row) => JSON.stringify([
      rowInteger(row, "timestamp_epoch_ms"),
      rowString(row, "model"),
      rowInteger(row, "input_tokens"),
      rowInteger(row, "cached_input_tokens"),
      rowInteger(row, "output_tokens"),
      rowInteger(row, "reasoning_output_tokens"),
    ]));
  }

  getRolloutMetadata(rolloutId: string): RolloutMetadataInput | null {
    this.assertOpen();
    requireInputString(rolloutId, "rolloutId");
    const row = this.database.prepare(`
      SELECT rollout_id, conversation_id, parent_thread_id, thread_type,
             agent_role, agent_path, agent_nickname
      FROM rollouts WHERE rollout_id = ?
    `).get(rolloutId);
    if (row === undefined) return null;
    return {
      rolloutId: rowString(row, "rollout_id"),
      conversationId: rowString(row, "conversation_id"),
      parentThreadId: rowString(row, "parent_thread_id"),
      threadType: requireEnum(rowString(row, "thread_type"), ["main", "subagent", "unknown"], "rollouts.thread_type"),
      agentRole: rowString(row, "agent_role"),
      agentPath: rowString(row, "agent_path"),
      agentNickname: rowString(row, "agent_nickname"),
    };
  }

  getCanonicalSourcePath(rolloutId: string): string | null {
    this.assertOpen();
    requireInputString(rolloutId, "rolloutId");
    const row = this.database.prepare("SELECT canonical_source_path FROM rollouts WHERE rollout_id = ?").get(rolloutId);
    return row === undefined ? null : rowNullableString(row, "canonical_source_path");
  }

  listCanonicalSourcesWithUnknownModels(): readonly string[] {
    this.assertOpen();
    return this.database.prepare(`
      SELECT DISTINCT r.canonical_source_path AS file_path
      FROM rollouts AS r
      JOIN usage_events AS e ON e.rollout_id = r.rollout_id
      WHERE e.model = 'unknown' AND r.canonical_source_path IS NOT NULL
      ORDER BY r.canonical_source_path
    `).all().map((row) => rowString(row, "file_path"));
  }

  countSourceConflicts(): number {
    this.assertOpen();
    const row = this.database.prepare("SELECT count(*) AS count FROM source_files WHERE canonical_status = 'conflict' AND is_present = 1").get();
    if (row === undefined) throw new Error("Source conflict count returned no row");
    return rowInteger(row, "count");
  }

  countPresentSources(): number {
    this.assertOpen();
    const row = this.database.prepare("SELECT count(*) AS count FROM source_files WHERE is_present = 1").get();
    if (row === undefined) throw new Error("Present source count returned no row");
    return rowInteger(row, "count");
  }

  recordSourceConflict(input: SourceConflictInput): number {
    this.assertOpen();
    requireInputString(input.sourceFilePath, "input.sourceFilePath");
    if (input.runId !== null) requireInputString(input.runId, "input.runId");
    requireInputString(input.code, "input.code");
    requireInputString(input.message, "input.message");
    if (input.detailsJson !== null) requireInputString(input.detailsJson, "input.detailsJson", true);
    requireInputInteger(input.observedAtEpochMs, "input.observedAtEpochMs");
    return this.writeTransaction(() => {
      this.database.prepare(`
        UPDATE source_files
        SET canonical_status = 'conflict', last_error = ?, last_scanned_at_epoch_ms = ?
        WHERE file_path = ?
      `).run(input.message, input.observedAtEpochMs, input.sourceFilePath);
      return this.insertDiagnostic({
        runId: input.runId,
        sourceFilePath: input.sourceFilePath,
        severity: "error",
        code: input.code,
        message: input.message,
        detailsJson: input.detailsJson,
        createdAtEpochMs: input.observedAtEpochMs,
      });
    });
  }

  addDiagnostic(input: CollectorDiagnosticInput): number {
    this.assertOpen();
    this.validateDiagnostic(input);
    return this.writeTransaction(() => this.insertDiagnostic(input));
  }

  queryEvents(filter: UsageEventQuery): readonly StoredUsageEvent[] {
    this.assertOpen();
    requireInputInteger(filter.startEpochMs, "filter.startEpochMs");
    requireInputInteger(filter.endEpochMs, "filter.endEpochMs");
    if (filter.endEpochMs < filter.startEpochMs) throw new RangeError("filter.endEpochMs cannot precede filter.startEpochMs");
    const conditions = ["e.timestamp_epoch_ms >= ?", "e.timestamp_epoch_ms < ?"];
    const parameters: Array<string | number> = [filter.startEpochMs, filter.endEpochMs];
    this.addListFilter(conditions, parameters, "e.model", filter.models, "filter.models");
    this.addListFilter(conditions, parameters, "r.agent_role", filter.agentRoles, "filter.agentRoles");
    if (filter.threadTypes !== undefined) {
      for (const value of filter.threadTypes) requireEnum(value, ["main", "subagent", "unknown"], "filter.threadTypes entry");
      this.addListFilter(conditions, parameters, "r.thread_type", filter.threadTypes, "filter.threadTypes");
    }
    const pathQuery = filter.pathQuery?.trim() ?? "";
    if (pathQuery.length > 0) {
      conditions.push("instr(lower(r.agent_path || ' ' || r.agent_nickname || ' ' || r.rollout_id || ' ' || r.conversation_id), lower(?)) > 0");
      parameters.push(pathQuery);
    }
    const rows = this.database.prepare(`
      SELECT
        e.timestamp_epoch_ms, e.token_event_ordinal, e.event_signature,
        e.model, e.input_tokens, e.cached_input_tokens, e.output_tokens,
        e.reasoning_output_tokens, r.conversation_id, r.rollout_id,
        r.parent_thread_id, r.thread_type, r.agent_role, r.agent_path,
        r.agent_nickname
      FROM usage_events AS e
      JOIN rollouts AS r ON r.rollout_id = e.rollout_id
      WHERE ${conditions.join(" AND ")}
      ORDER BY e.timestamp_epoch_ms, r.rollout_id, e.token_event_ordinal
    `).all(...parameters);
    return rows.map((row) => this.mapUsageEvent(row));
  }

  beginCollectorRun(input: CollectorRunStartInput): void {
    this.assertOpen();
    requireInputString(input.runId, "input.runId");
    requireInputString(input.trigger, "input.trigger");
    requireInputInteger(input.startedAtEpochMs, "input.startedAtEpochMs");
    this.writeTransaction(() => {
      this.database.prepare(`
        INSERT INTO collector_runs (
          run_id, trigger, status, started_at_epoch_ms, heartbeat_at_epoch_ms,
          completed_at_epoch_ms, files_scanned, events_added,
          diagnostics_count, error_message
        ) VALUES (?, ?, 'running', ?, ?, NULL, 0, 0, 0, NULL)
      `).run(input.runId, input.trigger, input.startedAtEpochMs, input.startedAtEpochMs);
    });
  }

  heartbeatCollector(input: CollectorRunHeartbeatInput): void {
    this.assertOpen();
    requireInputString(input.runId, "input.runId");
    requireInputInteger(input.heartbeatAtEpochMs, "input.heartbeatAtEpochMs");
    if (input.state !== undefined) {
      for (const [key, value] of Object.entries(input.state)) {
        requireInputString(key, "input.state key");
        requireInputString(value, `input.state.${key}`, true);
      }
    }
    this.writeTransaction(() => {
      const result = this.database.prepare(`
        UPDATE collector_runs SET heartbeat_at_epoch_ms = ?
        WHERE run_id = ? AND status = 'running'
      `).run(input.heartbeatAtEpochMs, input.runId);
      if (safeInteger(result.changes, "collector heartbeat changes") !== 1) throw new Error(`Unknown or completed collector run: ${input.runId}`);
      if (input.state !== undefined) {
        for (const [key, value] of Object.entries(input.state)) this.setCollectorStateWithinTransaction(key, value, input.heartbeatAtEpochMs);
      }
    });
  }

  finishCollectorRun(input: CollectorRunFinishInput): void {
    this.assertOpen();
    requireInputString(input.runId, "input.runId");
    requireEnum(input.status, ["succeeded", "failed"], "input.status");
    requireInputInteger(input.completedAtEpochMs, "input.completedAtEpochMs");
    requireInputInteger(input.filesScanned, "input.filesScanned");
    requireInputInteger(input.eventsAdded, "input.eventsAdded");
    requireInputInteger(input.diagnosticsCount, "input.diagnosticsCount");
    if (input.errorMessage !== null) requireInputString(input.errorMessage, "input.errorMessage", true);
    this.writeTransaction(() => {
      const result = this.database.prepare(`
        UPDATE collector_runs
        SET status = ?, heartbeat_at_epoch_ms = ?, completed_at_epoch_ms = ?,
            files_scanned = ?, events_added = ?, diagnostics_count = ?, error_message = ?
        WHERE run_id = ? AND status = 'running'
      `).run(input.status, input.completedAtEpochMs, input.completedAtEpochMs, input.filesScanned, input.eventsAdded, input.diagnosticsCount, input.errorMessage, input.runId);
      if (safeInteger(result.changes, "finish collector run changes") !== 1) throw new Error(`Unknown or completed collector run: ${input.runId}`);
    });
  }

  getCollectorRun(runId: string): CollectorRunRecord | null {
    this.assertOpen();
    requireInputString(runId, "runId");
    const row = this.database.prepare(`
      SELECT run_id, trigger, status, started_at_epoch_ms, heartbeat_at_epoch_ms,
             completed_at_epoch_ms, files_scanned, events_added,
             diagnostics_count, error_message
      FROM collector_runs WHERE run_id = ?
    `).get(runId);
    return row === undefined ? null : this.mapCollectorRun(row);
  }

  getLatestCollectorRun(): CollectorRunRecord | null {
    this.assertOpen();
    const row = this.database.prepare(`
      SELECT run_id, trigger, status, started_at_epoch_ms, heartbeat_at_epoch_ms,
             completed_at_epoch_ms, files_scanned, events_added,
             diagnostics_count, error_message
      FROM collector_runs
      ORDER BY started_at_epoch_ms DESC, run_id DESC
      LIMIT 1
    `).get();
    return row === undefined ? null : this.mapCollectorRun(row);
  }

  setCollectorState(key: string, value: string, updatedAtEpochMs: number): void {
    this.assertOpen();
    requireInputString(key, "key");
    requireInputString(value, "value", true);
    requireInputInteger(updatedAtEpochMs, "updatedAtEpochMs");
    this.writeTransaction(() => this.setCollectorStateWithinTransaction(key, value, updatedAtEpochMs));
  }

  getCollectorState(key: string): string | null {
    this.assertOpen();
    requireInputString(key, "key");
    const row = this.database.prepare("SELECT value FROM collector_state WHERE key = ?").get(key);
    return row === undefined ? null : rowString(row, "value");
  }

  close(): CheckpointResult {
    this.assertOpen();
    const row = this.database.prepare("PRAGMA wal_checkpoint(TRUNCATE)").get();
    if (row === undefined) throw new Error("WAL checkpoint returned no row");
    const result = {
      busy: rowInteger(row, "busy"),
      logFrames: rowInteger(row, "log"),
      checkpointedFrames: rowInteger(row, "checkpointed"),
    };
    this.database.close();
    this.closed = true;
    return result;
  }

  private migrate(): void {
    const currentVersion = this.schemaVersion;
    if (currentVersion > SCHEMA_VERSION) throw new Error(`Database schema version ${currentVersion} is newer than supported version ${SCHEMA_VERSION}`);
    if (currentVersion === SCHEMA_VERSION) return;
    this.writeTransaction(() => {
      if (currentVersion === 0) {
        this.database.exec(`
          CREATE TABLE rollouts (
            rollout_id TEXT PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            parent_thread_id TEXT NOT NULL,
            thread_type TEXT NOT NULL CHECK (thread_type IN ('main', 'subagent', 'unknown')),
            agent_role TEXT NOT NULL,
            agent_path TEXT NOT NULL,
            agent_nickname TEXT NOT NULL,
            canonical_source_path TEXT,
            created_at_epoch_ms INTEGER NOT NULL CHECK (created_at_epoch_ms >= 0),
            updated_at_epoch_ms INTEGER NOT NULL CHECK (updated_at_epoch_ms >= 0)
          ) STRICT;

          CREATE TABLE usage_events (
            rollout_id TEXT NOT NULL REFERENCES rollouts(rollout_id) ON DELETE CASCADE,
            token_event_ordinal INTEGER NOT NULL CHECK (token_event_ordinal >= 0),
            timestamp_epoch_ms INTEGER NOT NULL CHECK (timestamp_epoch_ms >= 0),
            model TEXT NOT NULL,
            input_tokens INTEGER NOT NULL CHECK (input_tokens >= 0),
            cached_input_tokens INTEGER NOT NULL CHECK (cached_input_tokens >= 0 AND cached_input_tokens <= input_tokens),
            output_tokens INTEGER NOT NULL CHECK (output_tokens >= 0),
            reasoning_output_tokens INTEGER NOT NULL CHECK (reasoning_output_tokens >= 0 AND reasoning_output_tokens <= output_tokens),
            event_signature TEXT NOT NULL,
            PRIMARY KEY (rollout_id, token_event_ordinal),
            UNIQUE (rollout_id, event_signature)
          ) WITHOUT ROWID, STRICT;

          CREATE INDEX usage_events_timestamp_idx ON usage_events(timestamp_epoch_ms);
          CREATE INDEX usage_events_model_timestamp_idx ON usage_events(model, timestamp_epoch_ms);

          CREATE TABLE source_files (
            file_path TEXT PRIMARY KEY,
            rollout_id TEXT REFERENCES rollouts(rollout_id) ON DELETE SET NULL,
            size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
            modified_at_epoch_ms INTEGER NOT NULL CHECK (modified_at_epoch_ms >= 0),
            byte_offset INTEGER NOT NULL CHECK (byte_offset >= 0 AND byte_offset <= size_bytes),
            prefix_hash TEXT NOT NULL,
            prefix_status TEXT NOT NULL CHECK (prefix_status IN ('unknown', 'matches', 'diverged')),
            canonical_status TEXT NOT NULL CHECK (canonical_status IN ('candidate', 'canonical', 'conflict')),
            is_present INTEGER NOT NULL CHECK (is_present IN (0, 1)),
            last_scanned_at_epoch_ms INTEGER NOT NULL CHECK (last_scanned_at_epoch_ms >= 0),
            last_error TEXT
          ) STRICT;

          CREATE INDEX source_files_rollout_idx ON source_files(rollout_id);

          CREATE TABLE collector_runs (
            run_id TEXT PRIMARY KEY,
            trigger TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('running', 'succeeded', 'failed')),
            started_at_epoch_ms INTEGER NOT NULL CHECK (started_at_epoch_ms >= 0),
            heartbeat_at_epoch_ms INTEGER NOT NULL CHECK (heartbeat_at_epoch_ms >= 0),
            completed_at_epoch_ms INTEGER CHECK (completed_at_epoch_ms IS NULL OR completed_at_epoch_ms >= 0),
            files_scanned INTEGER NOT NULL CHECK (files_scanned >= 0),
            events_added INTEGER NOT NULL CHECK (events_added >= 0),
            diagnostics_count INTEGER NOT NULL CHECK (diagnostics_count >= 0),
            error_message TEXT
          ) STRICT;

          CREATE TABLE collector_diagnostics (
            diagnostic_id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT REFERENCES collector_runs(run_id) ON DELETE SET NULL,
            source_file_path TEXT,
            severity TEXT NOT NULL CHECK (severity IN ('info', 'warning', 'error')),
            code TEXT NOT NULL,
            message TEXT NOT NULL,
            details_json TEXT,
            created_at_epoch_ms INTEGER NOT NULL CHECK (created_at_epoch_ms >= 0)
          ) STRICT;

          CREATE INDEX collector_diagnostics_run_idx ON collector_diagnostics(run_id, created_at_epoch_ms);

          CREATE TABLE collector_state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at_epoch_ms INTEGER NOT NULL CHECK (updated_at_epoch_ms >= 0)
          ) STRICT;
        `);
      }
      this.database.exec(`PRAGMA user_version = ${SCHEMA_VERSION}`);
    });
  }

  private upsertRollout(metadata: RolloutMetadataInput, observedAtEpochMs: number): void {
    this.database.prepare(`
      INSERT INTO rollouts (
        rollout_id, conversation_id, parent_thread_id, thread_type,
        agent_role, agent_path, agent_nickname, canonical_source_path,
        created_at_epoch_ms, updated_at_epoch_ms
      ) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, ?, ?)
      ON CONFLICT(rollout_id) DO UPDATE SET
        conversation_id = excluded.conversation_id,
        parent_thread_id = excluded.parent_thread_id,
        thread_type = excluded.thread_type,
        agent_role = excluded.agent_role,
        agent_path = excluded.agent_path,
        agent_nickname = excluded.agent_nickname,
        updated_at_epoch_ms = excluded.updated_at_epoch_ms
    `).run(metadata.rolloutId, metadata.conversationId, metadata.parentThreadId, metadata.threadType, metadata.agentRole, metadata.agentPath, metadata.agentNickname, observedAtEpochMs, observedAtEpochMs);
  }

  private appendWithinTransaction(metadata: RolloutMetadataInput, events: readonly UsageEventInput[], observedAtEpochMs: number): AppendEventsResult {
    this.upsertRollout(metadata, observedAtEpochMs);
    const insert = this.database.prepare(`
      INSERT INTO usage_events (
        rollout_id, token_event_ordinal, timestamp_epoch_ms, model,
        input_tokens, cached_input_tokens, output_tokens,
        reasoning_output_tokens, event_signature
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(rollout_id, token_event_ordinal) DO NOTHING
    `);
    const lookup = this.database.prepare(`
      SELECT event_signature FROM usage_events
      WHERE rollout_id = ? AND token_event_ordinal = ?
    `);
    let inserted = 0;
    for (const event of events) {
      const result = insert.run(metadata.rolloutId, event.tokenEventOrdinal, event.timestampEpochMs, event.model, event.inputTokens, event.cachedInputTokens, event.outputTokens, event.reasoningOutputTokens, event.eventSignature);
      const changes = safeInteger(result.changes, "append changes");
      if (changes === 1) {
        inserted += 1;
        continue;
      }
      if (changes !== 0) throw new Error(`Unexpected append change count: ${changes}`);
      const existing = lookup.get(metadata.rolloutId, event.tokenEventOrdinal);
      if (existing === undefined || rowString(existing, "event_signature") !== event.eventSignature) {
        throw new Error(`Conflicting usage event at ${metadata.rolloutId}:${event.tokenEventOrdinal}`);
      }
    }
    return { inserted, ignoredAsDuplicate: events.length - inserted };
  }

  private upsertSourceFileWithinTransaction(source: SourceFileInput): void {
    this.database.prepare(`
      INSERT INTO source_files (
        file_path, rollout_id, size_bytes, modified_at_epoch_ms, byte_offset,
        prefix_hash, prefix_status, canonical_status, is_present,
        last_scanned_at_epoch_ms, last_error
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(file_path) DO UPDATE SET
        rollout_id = excluded.rollout_id,
        size_bytes = excluded.size_bytes,
        modified_at_epoch_ms = excluded.modified_at_epoch_ms,
        byte_offset = excluded.byte_offset,
        prefix_hash = excluded.prefix_hash,
        prefix_status = excluded.prefix_status,
        canonical_status = excluded.canonical_status,
        is_present = excluded.is_present,
        last_scanned_at_epoch_ms = excluded.last_scanned_at_epoch_ms,
        last_error = excluded.last_error
    `).run(source.filePath, source.rolloutId, source.sizeBytes, source.modifiedAtEpochMs, source.byteOffset, source.prefixHash, source.prefixStatus, source.canonicalStatus, source.isPresent ? 1 : 0, source.lastScannedAtEpochMs, source.lastError);
  }

  private validateDiagnostic(input: CollectorDiagnosticInput): void {
    if (input.runId !== null) requireInputString(input.runId, "input.runId");
    if (input.sourceFilePath !== null) requireInputString(input.sourceFilePath, "input.sourceFilePath");
    requireEnum(input.severity, ["info", "warning", "error"], "input.severity");
    requireInputString(input.code, "input.code");
    requireInputString(input.message, "input.message");
    if (input.detailsJson !== null) requireInputString(input.detailsJson, "input.detailsJson", true);
    requireInputInteger(input.createdAtEpochMs, "input.createdAtEpochMs");
  }

  private insertDiagnostic(input: CollectorDiagnosticInput): number {
    this.validateDiagnostic(input);
    const result = this.database.prepare(`
      INSERT INTO collector_diagnostics (
        run_id, source_file_path, severity, code, message, details_json,
        created_at_epoch_ms
      ) VALUES (?, ?, ?, ?, ?, ?, ?)
    `).run(input.runId, input.sourceFilePath, input.severity, input.code, input.message, input.detailsJson, input.createdAtEpochMs);
    return safeInteger(result.lastInsertRowid, "diagnostic id");
  }

  private setCollectorStateWithinTransaction(key: string, value: string, updatedAtEpochMs: number): void {
    this.database.prepare(`
      INSERT INTO collector_state (key, value, updated_at_epoch_ms)
      VALUES (?, ?, ?)
      ON CONFLICT(key) DO UPDATE SET
        value = excluded.value,
        updated_at_epoch_ms = excluded.updated_at_epoch_ms
    `).run(key, value, updatedAtEpochMs);
  }

  private addListFilter(
    conditions: string[],
    parameters: Array<string | number>,
    column: string,
    values: readonly string[] | undefined,
    name: string,
  ): void {
    if (values === undefined || values.length === 0) return;
    for (const value of values) requireInputString(value, `${name} entry`);
    conditions.push(`${column} IN (${values.map(() => "?").join(", ")})`);
    parameters.push(...values);
  }

  private mapUsageEvent(row: SqlRow): StoredUsageEvent {
    const timestampEpochMs = rowInteger(row, "timestamp_epoch_ms");
    const timestampUtc = new Date(timestampEpochMs).toISOString();
    const threadType = requireEnum(rowString(row, "thread_type"), ["main", "subagent", "unknown"], "rollouts.thread_type");
    const tokenEventOrdinal = rowInteger(row, "token_event_ordinal");
    return {
      timestampUtc,
      conversationId: rowString(row, "conversation_id"),
      rolloutId: rowString(row, "rollout_id"),
      parentThreadId: rowString(row, "parent_thread_id"),
      threadType,
      agentRole: rowString(row, "agent_role"),
      agentPath: rowString(row, "agent_path"),
      agentNickname: rowString(row, "agent_nickname"),
      model: rowString(row, "model"),
      inputTokens: rowInteger(row, "input_tokens"),
      cachedInputTokens: rowInteger(row, "cached_input_tokens"),
      outputTokens: rowInteger(row, "output_tokens"),
      reasoningOutputTokens: rowInteger(row, "reasoning_output_tokens"),
      tokenEventOrdinal,
      timestampEpochMs,
      eventSignature: rowString(row, "event_signature"),
    };
  }

  private mapSourceFile(row: SqlRow): SourceFileRecord {
    const isPresent = rowInteger(row, "is_present");
    if (isPresent !== 0 && isPresent !== 1) throw new TypeError("Database column is_present is not a boolean integer");
    return {
      filePath: rowString(row, "file_path"),
      rolloutId: rowNullableString(row, "rollout_id"),
      sizeBytes: rowInteger(row, "size_bytes"),
      modifiedAtEpochMs: rowInteger(row, "modified_at_epoch_ms"),
      byteOffset: rowInteger(row, "byte_offset"),
      prefixHash: rowString(row, "prefix_hash"),
      prefixStatus: requireEnum(rowString(row, "prefix_status"), ["unknown", "matches", "diverged"], "source_files.prefix_status"),
      canonicalStatus: requireEnum(rowString(row, "canonical_status"), ["candidate", "canonical", "conflict"], "source_files.canonical_status"),
      isPresent: isPresent === 1,
      lastScannedAtEpochMs: rowInteger(row, "last_scanned_at_epoch_ms"),
      lastError: rowNullableString(row, "last_error"),
    };
  }

  private mapCollectorRun(row: SqlRow): CollectorRunRecord {
    return {
      runId: rowString(row, "run_id"),
      trigger: rowString(row, "trigger"),
      status: requireEnum(rowString(row, "status"), ["running", "succeeded", "failed"], "collector_runs.status"),
      startedAtEpochMs: rowInteger(row, "started_at_epoch_ms"),
      heartbeatAtEpochMs: rowInteger(row, "heartbeat_at_epoch_ms"),
      completedAtEpochMs: rowNullableInteger(row, "completed_at_epoch_ms"),
      filesScanned: rowInteger(row, "files_scanned"),
      eventsAdded: rowInteger(row, "events_added"),
      diagnosticsCount: rowInteger(row, "diagnostics_count"),
      errorMessage: rowNullableString(row, "error_message"),
    };
  }

  private writeTransaction<T>(operation: () => T): T {
    this.assertOpen();
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const result = operation();
      this.database.exec("COMMIT");
      return result;
    } catch (error) {
      try {
        this.database.exec("ROLLBACK");
      } catch {
        // Preserve the error that caused the transaction to fail.
      }
      throw error;
    }
  }

  private assertOpen(): void {
    if (this.closed) throw new Error("UsageStore is closed");
  }
}
