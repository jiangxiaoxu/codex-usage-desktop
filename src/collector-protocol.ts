import type { CollectorStatus, FilterSpec, QueryResult, SyncResult } from "./shared";

export interface CollectorConfig {
  readonly codexHome: string;
  readonly databasePath: string;
  readonly reconcileIntervalMs: number;
  readonly watcherDebounceMs: number;
}

export interface ExportRequest {
  readonly filter: FilterSpec;
  readonly filePath: string;
}

export interface ExportResult {
  readonly count: number;
}

export interface CollectorRequestMap {
  readonly initialize: { readonly input: CollectorConfig; readonly output: CollectorStatus };
  readonly reconcile: { readonly input: null; readonly output: SyncResult };
  readonly query: { readonly input: FilterSpec; readonly output: QueryResult };
  readonly exportCsv: { readonly input: ExportRequest; readonly output: ExportResult };
  readonly getStatus: { readonly input: null; readonly output: CollectorStatus };
  readonly shutdown: { readonly input: null; readonly output: null };
}

export type CollectorMethod = keyof CollectorRequestMap;

export type CollectorRequest = {
  readonly [Method in CollectorMethod]: {
    readonly kind: "request";
    readonly requestId: number;
    readonly method: Method;
    readonly payload: CollectorRequestMap[Method]["input"];
  }
}[CollectorMethod];

export interface CollectorSuccessResponse {
  readonly kind: "response";
  readonly requestId: number;
  readonly ok: true;
  readonly result: unknown;
}

export interface CollectorErrorResponse {
  readonly kind: "response";
  readonly requestId: number;
  readonly ok: false;
  readonly error: string;
}

export interface CollectorUpdatedEvent {
  readonly kind: "event";
  readonly name: "usage-updated";
  readonly status: CollectorStatus;
}

export type CollectorMessage = CollectorSuccessResponse | CollectorErrorResponse | CollectorUpdatedEvent;

