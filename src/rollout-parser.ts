export type RolloutThreadType = "main" | "subagent" | "unknown";

export type RolloutJsonValue = string | number | boolean | null | RolloutJsonObject | readonly RolloutJsonValue[];

export interface RolloutJsonObject {
  readonly [key: string]: RolloutJsonValue;
}

export interface RolloutMetadata {
  readonly conversationId: string;
  readonly rolloutId: string;
  readonly parentThreadId: string;
  readonly threadType: RolloutThreadType;
  readonly agentRole: string;
  readonly agentPath: string;
  readonly agentNickname: string;
}

export interface ParsedRolloutUsageEvent extends RolloutMetadata {
  readonly timestampUtc: string;
  readonly tokenEventOrdinal: number;
  readonly turnId: string;
  readonly model: string;
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningOutputTokens: number;
  readonly cumulativeSnapshot: string;
  readonly deterministicSignature: string;
}

export interface RolloutParseDiagnostics {
  readonly blankLines: number;
  readonly malformedLines: number;
  readonly nonObjectLines: number;
  readonly invalidTokenUsageLines: number;
  readonly duplicateSnapshotsSkipped: number;
  readonly zeroBreakdownSnapshotsSkipped: number;
  readonly invalidTokenRelationshipsSkipped: number;
  readonly invalidTimestampsSkipped: number;
}

export interface RolloutParseResult {
  readonly metadata: RolloutMetadata;
  readonly events: readonly ParsedRolloutUsageEvent[];
  readonly diagnostics: RolloutParseDiagnostics;
  readonly stableLineCount: number;
  readonly trailingPartialLine: boolean;
}

export type RolloutForkReplayState =
  | { readonly status: "inactive" }
  | { readonly status: "awaiting_task_started" }
  | { readonly status: "awaiting_turn_context"; readonly turnId: string }
  | { readonly status: "awaiting_trigger"; readonly turnId: string; readonly model: string | null }
  | { readonly status: "awaiting_recipient"; readonly turnId: string; readonly model: string | null };

export interface RolloutParserState {
  readonly metadataPayload: RolloutJsonObject | null;
  readonly metadata: RolloutMetadata;
  readonly turnModels: readonly (readonly [turnId: string, model: string])[];
  readonly currentTurnId: string;
  readonly currentTurnModelOverridden: boolean;
  readonly currentModel: string;
  readonly forkReplay: RolloutForkReplayState;
  readonly previousSnapshot: string | null;
  readonly nextTokenEventOrdinal: number;
  readonly unresolvedTurnIds: readonly string[];
  readonly provisionalTurnIds: readonly string[];
}

export interface RolloutChunkParseResult {
  readonly metadata: RolloutMetadata;
  readonly events: readonly ParsedRolloutUsageEvent[];
  readonly diagnostics: RolloutParseDiagnostics;
  readonly state: RolloutParserState;
  readonly stableLineCount: number;
  readonly stableByteLength: number;
  readonly trailingPartialLine: boolean;
}

interface MutableDiagnostics {
  blankLines: number;
  malformedLines: number;
  nonObjectLines: number;
  invalidTokenUsageLines: number;
  duplicateSnapshotsSkipped: number;
  zeroBreakdownSnapshotsSkipped: number;
  invalidTokenRelationshipsSkipped: number;
  invalidTimestampsSkipped: number;
}

interface TokenTuple {
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningOutputTokens: number;
  readonly totalTokens: number;
}

interface TokenCandidate {
  readonly timestamp: string;
  readonly turnId: string;
  readonly model: { readonly value: string; readonly source: "active-turn-setting" | "settings-fallback" } | null;
  readonly usage: TokenTuple;
  readonly cumulativeSnapshot: string;
}

interface StableInput {
  readonly text: string;
  readonly stableByteLength: number;
  readonly trailingPartialLine: boolean;
}

function record(value: unknown): RolloutJsonObject | null {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? value as RolloutJsonObject
    : null;
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function stringOr(value: unknown, fallback: string): string {
  return nonEmptyString(value) ?? fallback;
}

function nonNegativeSafeInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

function tokenTuple(value: unknown): TokenTuple | null {
  const source = record(value);
  if (source === null) return null;
  const inputTokens = nonNegativeSafeInteger(source.input_tokens);
  const cachedInputTokens = nonNegativeSafeInteger(source.cached_input_tokens);
  const outputTokens = nonNegativeSafeInteger(source.output_tokens);
  const reasoningOutputTokens = nonNegativeSafeInteger(source.reasoning_output_tokens);
  const totalTokens = nonNegativeSafeInteger(source.total_tokens);
  if (inputTokens === null || cachedInputTokens === null || outputTokens === null || reasoningOutputTokens === null || totalTokens === null) return null;
  return { inputTokens, cachedInputTokens, outputTokens, reasoningOutputTokens, totalTokens };
}

function snapshot(tuple: TokenTuple): string {
  return [tuple.inputTokens, tuple.cachedInputTokens, tuple.outputTokens, tuple.reasoningOutputTokens, tuple.totalTokens].join(":");
}

const TIMESTAMP_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(?:Z|([+-])(\d{2}):(\d{2}))$/;

function leapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

function validTimestamp(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const match = TIMESTAMP_PATTERN.exec(value);
  if (match === null) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  const offsetHour = match[8] === undefined ? 0 : Number(match[8]);
  const offsetMinute = match[9] === undefined ? 0 : Number(match[9]);
  const daysPerMonth = [31, leapYear(year) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31] as const;
  return month >= 1 && month <= 12
    && day >= 1 && day <= daysPerMonth[month - 1]!
    && hour <= 23 && minute <= 59 && second <= 59
    && offsetHour <= 23 && offsetMinute <= 59
    && !Number.isNaN(Date.parse(value));
}

function metadataFrom(payload: RolloutJsonObject | null, fallbackRolloutId: string): RolloutMetadata {
  const source = record(payload?.source);
  const subagent = record(source?.subagent);
  const spawn = record(subagent?.thread_spawn);
  const threadSource = nonEmptyString(payload?.thread_source);
  const isSubagent = threadSource === "subagent" || spawn !== null;
  const threadType: RolloutThreadType = isSubagent
    ? "subagent"
    : threadSource === null || threadSource === "user" ? "main" : "unknown";
  const rolloutId = stringOr(payload?.id, fallbackRolloutId);
  return {
    conversationId: stringOr(payload?.session_id, rolloutId),
    rolloutId,
    parentThreadId: stringOr(spawn?.parent_thread_id, stringOr(payload?.parent_thread_id, "")),
    threadType,
    agentRole: threadType === "main" ? "main" : stringOr(spawn?.agent_role, stringOr(payload?.agent_role, "unknown")),
    agentPath: threadType === "main" ? "/root" : stringOr(spawn?.agent_path, stringOr(payload?.agent_path, "/root")),
    agentNickname: stringOr(spawn?.agent_nickname, stringOr(payload?.agent_nickname, "")),
  };
}

function forkReplayFrom(payload: RolloutJsonObject): RolloutForkReplayState {
  return nonEmptyString(payload.forked_from_id) === null
    ? { status: "inactive" }
    : { status: "awaiting_task_started" };
}

function emptyDiagnostics(): MutableDiagnostics {
  return {
    blankLines: 0,
    malformedLines: 0,
    nonObjectLines: 0,
    invalidTokenUsageLines: 0,
    duplicateSnapshotsSkipped: 0,
    zeroBreakdownSnapshotsSkipped: 0,
    invalidTokenRelationshipsSkipped: 0,
    invalidTimestampsSkipped: 0,
  };
}

function stableInput(input: string | Buffer): StableInput {
  if (typeof input === "string") {
    const trailingPartialLine = input.length > 0 && !input.endsWith("\n");
    const stableCharacterLength = trailingPartialLine ? input.lastIndexOf("\n") + 1 : input.length;
    const text = input.slice(0, stableCharacterLength);
    return { text, stableByteLength: Buffer.byteLength(text, "utf8"), trailingPartialLine };
  }
  const trailingPartialLine = input.length > 0 && input[input.length - 1] !== 0x0a;
  const finalNewline = trailingPartialLine ? input.lastIndexOf(0x0a) : input.length - 1;
  const stableByteLength = trailingPartialLine ? finalNewline + 1 : input.length;
  return {
    text: input.subarray(0, stableByteLength).toString("utf8"),
    stableByteLength,
    trailingPartialLine,
  };
}

/** Parse newline-terminated records from one chunk without mutating the input or prior state. */
export function parseRolloutChunk(input: string | Buffer, fallbackRolloutId: string, priorState?: RolloutParserState): RolloutChunkParseResult {
  const stable = stableInput(input);
  const diagnostics = emptyDiagnostics();
  const turnModels = new Map<string, string>(priorState?.turnModels);
  const candidates: TokenCandidate[] = [];
  let metadataPayload = priorState?.metadataPayload ?? null;
  let metadataDiscovered = false;
  let metadata = priorState?.metadata ?? metadataFrom(metadataPayload, fallbackRolloutId);
  let currentTurnId = priorState?.currentTurnId ?? "";
  let currentTurnModelOverridden = priorState?.currentTurnModelOverridden ?? false;
  let currentModel = priorState?.currentModel ?? "unknown";
  let forkReplay: RolloutForkReplayState = priorState?.forkReplay ?? { status: "inactive" };
  let stableLineCount = 0;
  let lineStart = 0;
  let previousSnapshot = priorState?.previousSnapshot ?? null;

  while (lineStart < stable.text.length) {
    const newline = stable.text.indexOf("\n", lineStart);
    const lineEnd = newline < 0 ? stable.text.length : newline;
    const rawLine = stable.text.slice(lineStart, lineEnd).replace(/\r$/, "");
    lineStart = lineEnd + 1;
    stableLineCount += 1;
    if (rawLine.trim().length === 0) {
      diagnostics.blankLines += 1;
      continue;
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(rawLine) as unknown;
    } catch {
      diagnostics.malformedLines += 1;
      continue;
    }
    const event = record(parsed);
    if (event === null) {
      diagnostics.nonObjectLines += 1;
      continue;
    }
    const payload = record(event.payload);
    if (event.type === "session_meta" && metadataPayload === null && payload !== null) {
      metadataPayload = payload;
      metadataDiscovered = true;
      metadata = metadataFrom(metadataPayload, fallbackRolloutId);
      forkReplay = forkReplayFrom(payload);
      continue;
    }
    if (event.type === "turn_context" && payload !== null) {
      const turnId = nonEmptyString(payload.turn_id);
      const model = nonEmptyString(payload.model);
      if (forkReplay.status === "awaiting_turn_context") {
        if (turnId === forkReplay.turnId) {
          forkReplay = { status: "awaiting_trigger", turnId: forkReplay.turnId, model };
        }
        continue;
      }
      if (forkReplay.status !== "inactive") continue;
      if (turnId !== null) {
        if (turnId !== currentTurnId) currentTurnModelOverridden = false;
        currentTurnId = turnId;
      }
      if (turnId !== null && model !== null) turnModels.set(turnId, model);
      continue;
    }
    if (event.type === "inter_agent_communication_metadata" && payload?.trigger_turn === true) {
      if (forkReplay.status === "awaiting_trigger") {
        forkReplay = { status: "awaiting_recipient", turnId: forkReplay.turnId, model: forkReplay.model };
      }
      continue;
    }
    if (event.type === "response_item" && payload?.type === "agent_message" && forkReplay.status === "awaiting_recipient") {
      const internalMetadata = record(payload.internal_chat_message_metadata_passthrough);
      const internalTurnId = nonEmptyString(internalMetadata?.turn_id);
      if (payload.recipient === metadata.agentPath && (internalTurnId === null || internalTurnId === forkReplay.turnId)) {
        currentTurnId = forkReplay.turnId;
        if (forkReplay.model !== null) {
          turnModels.set(forkReplay.turnId, forkReplay.model);
        }
        forkReplay = { status: "inactive" };
      }
      continue;
    }
    if (event.type !== "event_msg" || payload === null) continue;
    if (payload.type === "thread_settings_applied") {
      const model = nonEmptyString(record(payload.thread_settings)?.model);
      if (model !== null) {
        currentModel = model;
        if (currentTurnId.length > 0) currentTurnModelOverridden = true;
      }
      continue;
    }
    if (payload.type === "task_started") {
      const turnId = nonEmptyString(payload.turn_id);
      if (forkReplay.status !== "inactive") {
        if (turnId !== null) forkReplay = { status: "awaiting_turn_context", turnId };
        continue;
      }
      currentTurnId = turnId ?? currentTurnId;
      currentTurnModelOverridden = false;
      continue;
    }
    if (payload.type === "task_complete") {
      const turnId = nonEmptyString(payload.turn_id);
      if (turnId === null || turnId === currentTurnId) {
        currentTurnId = "";
        currentTurnModelOverridden = false;
      }
      continue;
    }
    if (payload.type !== "token_count") continue;
    if (forkReplay.status !== "inactive") continue;

    const info = record(payload.info);
    const usage = tokenTuple(info?.last_token_usage);
    const total = tokenTuple(info?.total_token_usage);
    if (usage === null || total === null) {
      diagnostics.invalidTokenUsageLines += 1;
      continue;
    }
    const cumulativeSnapshot = snapshot(total);
    if (usage.inputTokens === 0 && usage.cachedInputTokens === 0 && usage.outputTokens === 0 && usage.reasoningOutputTokens === 0) {
      diagnostics.zeroBreakdownSnapshotsSkipped += 1;
      continue;
    }
    if (usage.cachedInputTokens > usage.inputTokens || usage.reasoningOutputTokens > usage.outputTokens) {
      diagnostics.invalidTokenRelationshipsSkipped += 1;
      continue;
    }
    if (!validTimestamp(event.timestamp)) {
      diagnostics.invalidTimestampsSkipped += 1;
      continue;
    }
    if (cumulativeSnapshot === previousSnapshot) {
      diagnostics.duplicateSnapshotsSkipped += 1;
      continue;
    }
    previousSnapshot = cumulativeSnapshot;
    const candidateTurnId = nonEmptyString(payload.turn_id) ?? currentTurnId;
    const activeTurnSetting = currentTurnModelOverridden && candidateTurnId === currentTurnId && currentModel !== "unknown";
    const settingsFallback = !activeTurnSetting && !turnModels.has(candidateTurnId) && currentModel !== "unknown";
    candidates.push({
      timestamp: event.timestamp,
      turnId: candidateTurnId,
      model: activeTurnSetting
        ? { value: currentModel, source: "active-turn-setting" }
        : settingsFallback ? { value: currentModel, source: "settings-fallback" } : null,
      usage,
      cumulativeSnapshot,
    });
  }

  if (!metadataDiscovered && priorState === undefined) metadata = metadataFrom(metadataPayload, fallbackRolloutId);
  const firstTokenEventOrdinal = priorState?.nextTokenEventOrdinal ?? 0;
  const events = candidates.map((candidate, candidateIndex): ParsedRolloutUsageEvent => {
    const tokenEventOrdinal = firstTokenEventOrdinal + candidateIndex;
    const activeTurnSetting = candidate.model?.source === "active-turn-setting" ? candidate.model.value : null;
    const settingsFallback = candidate.model?.source === "settings-fallback" ? candidate.model.value : null;
    const model = activeTurnSetting ?? turnModels.get(candidate.turnId) ?? settingsFallback ?? "unknown";
    const deterministicSignature = JSON.stringify([
      candidate.timestamp,
      candidate.turnId,
      candidate.usage.inputTokens,
      candidate.usage.cachedInputTokens,
      candidate.usage.outputTokens,
      candidate.usage.reasoningOutputTokens,
      candidate.cumulativeSnapshot,
    ]);
    return {
      ...metadata,
      timestampUtc: candidate.timestamp,
      tokenEventOrdinal,
      turnId: candidate.turnId,
      model,
      inputTokens: candidate.usage.inputTokens,
      cachedInputTokens: candidate.usage.cachedInputTokens,
      outputTokens: candidate.usage.outputTokens,
      reasoningOutputTokens: candidate.usage.reasoningOutputTokens,
      cumulativeSnapshot: candidate.cumulativeSnapshot,
      deterministicSignature,
    };
  });

  const unresolvedTurnIds = new Set(priorState?.unresolvedTurnIds ?? []);
  const provisionalTurnIds = new Set(priorState?.provisionalTurnIds ?? []);
  for (const event of events) {
    if (event.model === "unknown") unresolvedTurnIds.add(event.turnId);
  }
  for (const candidate of candidates) {
    if (candidate.model?.source === "settings-fallback" && candidate.turnId.length > 0 && !turnModels.has(candidate.turnId)) {
      provisionalTurnIds.add(candidate.turnId);
    }
  }
  for (const turnId of turnModels.keys()) unresolvedTurnIds.delete(turnId);
  for (const turnId of turnModels.keys()) provisionalTurnIds.delete(turnId);

  const state: RolloutParserState = {
    metadataPayload,
    metadata,
    turnModels: [...turnModels.entries()],
    currentTurnId,
    currentTurnModelOverridden,
    currentModel,
    forkReplay,
    previousSnapshot,
    nextTokenEventOrdinal: firstTokenEventOrdinal + events.length,
    unresolvedTurnIds: [...unresolvedTurnIds].sort(),
    provisionalTurnIds: [...provisionalTurnIds].sort(),
  };
  return {
    metadata,
    events,
    diagnostics,
    state,
    stableLineCount,
    stableByteLength: stable.stableByteLength,
    trailingPartialLine: stable.trailingPartialLine,
  };
}

/** Parse only newline-terminated JSONL records. The input is read without mutation. */
export function parseRollout(input: string | Buffer, fallbackRolloutId: string): RolloutParseResult {
  const result = parseRolloutChunk(input, fallbackRolloutId);
  return {
    metadata: result.metadata,
    events: result.events,
    diagnostics: result.diagnostics,
    stableLineCount: result.stableLineCount,
    trailingPartialLine: result.trailingPartialLine,
  };
}
