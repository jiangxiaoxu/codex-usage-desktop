import { EventEmitter } from "node:events";
import path from "node:path";
import { Worker } from "node:worker_threads";
import type { CollectorConfig, CollectorMessage, CollectorMethod, CollectorRequest, CollectorRequestMap } from "./collector-protocol";
import type { CollectorStatus } from "./shared";

interface PendingRequest {
  readonly resolve: (value: unknown) => void;
  readonly reject: (reason: Error) => void;
  readonly timeout: NodeJS.Timeout;
}

export class CollectorClient extends EventEmitter {
  readonly #worker: Worker;
  readonly #pending = new Map<number, PendingRequest>();
  #nextRequestId = 1;
  #closed = false;

  constructor(workerDirectory: string) {
    super();
    this.#worker = new Worker(path.join(workerDirectory, "collector-worker.js"));
    this.#worker.on("message", (message: CollectorMessage) => this.#handleMessage(message));
    this.#worker.on("error", (error: Error) => this.#failAll(error));
    this.#worker.on("exit", (code: number) => {
      if (!this.#closed) this.#failAll(new Error(`Collector worker exited unexpectedly with code ${code}.`));
    });
  }

  initialize(config: CollectorConfig): Promise<CollectorStatus> {
    return this.request("initialize", config);
  }

  request<Method extends CollectorMethod>(method: Method, payload: CollectorRequestMap[Method]["input"]): Promise<CollectorRequestMap[Method]["output"]> {
    if (this.#closed) return Promise.reject(new Error("Collector worker is closed."));
    const requestId = this.#nextRequestId++;
    const message: CollectorRequest = { kind: "request", requestId, method, payload } as CollectorRequest;
    return new Promise<CollectorRequestMap[Method]["output"]>((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.#pending.delete(requestId);
        reject(new Error(`Collector request timed out: ${method}.`));
      }, method === "initialize" || method === "reconcile" ? 10 * 60_000 : 60_000);
      this.#pending.set(requestId, { resolve: (value) => resolve(value as CollectorRequestMap[Method]["output"]), reject, timeout });
      this.#worker.postMessage(message);
    });
  }

  async close(): Promise<void> {
    if (this.#closed) return;
    try {
      await this.request("shutdown", null);
    } finally {
      this.#closed = true;
      await this.#worker.terminate();
      this.#failAll(new Error("Collector worker closed."));
    }
  }

  #handleMessage(message: CollectorMessage): void {
    if (message.kind === "event") {
      this.emit(message.name, message.status);
      return;
    }
    const pending = this.#pending.get(message.requestId);
    if (pending === undefined) return;
    clearTimeout(pending.timeout);
    this.#pending.delete(message.requestId);
    if (message.ok) pending.resolve(message.result);
    else pending.reject(new Error(message.error));
  }

  #failAll(error: Error): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    this.#pending.clear();
  }
}

