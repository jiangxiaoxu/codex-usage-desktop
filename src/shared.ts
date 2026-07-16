export type ThreadType = "main" | "subagent" | "unknown";

/** An exact thread-type and observed rollout role pair used to select usage events. */
export interface SubjectFilter {
  readonly threadType: ThreadType;
  readonly agentRole: string;
}

export interface UsageEvent {
  readonly timestampUtc: string;
  readonly tokenEventOrdinal: number;
  readonly conversationId: string;
  readonly rolloutId: string;
  readonly parentThreadId: string;
  readonly threadType: ThreadType;
  readonly agentRole: string;
  readonly agentPath: string;
  readonly agentNickname: string;
  readonly model: string;
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningOutputTokens: number;
}

export type CollectorPhase = "initializing" | "syncing" | "watching" | "degraded" | "stopped";
export type ObservationCoverage = "baseline" | "continuous" | "gap";

export interface ObservationGap {
  readonly startUtc: string;
  readonly endUtc: string;
}

export interface CollectorStatus {
  readonly phase: CollectorPhase;
  readonly databasePath: string;
  readonly runStartedUtc: string;
  readonly lastSuccessfulInventoryUtc: string | null;
  readonly lastHeartbeatUtc: string | null;
  readonly filesKnown: number;
  readonly pendingFiles: number;
  readonly changedFilesLastSync: number;
  readonly conflicts: number;
  readonly observationCoverage: ObservationCoverage;
  readonly observationGap: ObservationGap | null;
  readonly message: string;
}

export interface SyncResult {
  readonly status: CollectorStatus;
  readonly changed: boolean;
}

export interface StartupSettings {
  readonly supported: boolean;
  readonly enabled: boolean;
}

export interface UpdateStatus {
  readonly currentVersion: string;
  readonly latestVersion: string | null;
  readonly available: boolean;
}

export interface ScanDiagnostics {
  readonly filesScanned: number;
  readonly malformedLines: number;
  readonly duplicateSnapshotsSkipped: number;
  readonly zeroBreakdownSnapshotsSkipped: number;
  readonly invalidTokenRelationshipsSkipped: number;
}

export interface ScanResult {
  readonly events: readonly UsageEvent[];
  readonly diagnostics: ScanDiagnostics;
}

export interface FilterSpec {
  readonly startUtc: string;
  readonly endUtc: string;
  /** Null selects every model category; an empty array selects none. */
  readonly models: readonly string[] | null;
  /** Null selects every subject; an empty array selects none. */
  readonly subjects: readonly SubjectFilter[] | null;
  readonly pathQuery: string;
}

export interface CostBreakdown {
  readonly uncachedInput: number;
  readonly cachedInput: number;
  readonly reasoningOutput: number;
  readonly otherOutput: number;
  readonly total: number;
  readonly priced: boolean;
}

export interface Summary {
  readonly calls: number;
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

export interface GroupRow<Key extends readonly string[]> {
  readonly key: Key;
  readonly summary: Summary;
}

export type ModelGroupRow = GroupRow<readonly [model: string]>;
export type RoleGroupRow = GroupRow<readonly [threadType: ThreadType, agentRole: string]>;
export type AgentGroupRow = GroupRow<readonly [threadType: ThreadType, agentRole: string, agentPath: string, model: string]>;

export interface ModelFacetOption {
  readonly model: string;
  readonly canonicalTotalTokens: number;
  readonly totalCost: number;
}

export interface SubjectFacetOption {
  readonly subject: SubjectFilter;
  readonly canonicalTotalTokens: number;
  readonly totalCost: number;
}

/** Facets are calculated from all events in the requested date range only. */
export interface QueryFacets {
  readonly models: readonly ModelFacetOption[];
  readonly subjects: readonly SubjectFacetOption[];
}

export interface QueryResult {
  readonly summary: Summary;
  readonly byModel: readonly ModelGroupRow[];
  readonly byRole: readonly RoleGroupRow[];
  readonly byAgent: readonly AgentGroupRow[];
  readonly facets: QueryFacets;
  readonly diagnostics: ScanDiagnostics;
}

export interface UsageApi {
  syncNow(): Promise<SyncResult>;
  query(filter: FilterSpec): Promise<QueryResult>;
  exportCsv(filter: FilterSpec): Promise<{ readonly path: string | null; readonly count: number }>;
  getCollectorStatus(): Promise<CollectorStatus>;
  getStartupSettings(): Promise<StartupSettings>;
  setStartupEnabled(enabled: boolean): Promise<StartupSettings>;
  checkForUpdates(): Promise<UpdateStatus>;
  openLatestRelease(): Promise<void>;
  onUpdateStatus(listener: (status: UpdateStatus) => void): () => void;
  onUsageUpdated(listener: (status: CollectorStatus) => void): () => void;
}
