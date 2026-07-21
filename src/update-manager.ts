import type { UpdateStatus } from "./shared";

export interface CheckedUpdate {
  readonly available: boolean;
  readonly version: string | null;
}

export interface UpdateDownloadProgress {
  readonly percent: number;
}

export interface UpdateClient {
  checkForUpdates(): Promise<CheckedUpdate>;
  downloadUpdate(onProgress: (progress: UpdateDownloadProgress) => void): Promise<void>;
  installAndRestart(): void;
}

export interface UpdateManagerOptions {
  readonly currentVersion: string;
  readonly supported: boolean;
  readonly client: UpdateClient | null;
  readonly prepareForInstall: () => Promise<void>;
  readonly publish: (status: UpdateStatus) => void;
}

type UpdateOperation = "check" | "download" | "install";

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) return error.message;
  return "更新失败.";
}

function boundedPercent(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.min(100, Math.max(0, Math.round(value)));
}

function statusVersion(status: UpdateStatus): string | null {
  switch (status.phase) {
    case "available":
    case "downloading":
    case "downloaded":
    case "installing":
      return status.latestVersion;
    case "error":
      return status.operation === "check" ? null : status.latestVersion;
    case "unsupported":
    case "idle":
    case "checking":
      return null;
  }
}

export class UpdateManager {
  private readonly currentVersion: string;
  private readonly client: UpdateClient | null;
  private readonly prepareForInstall: () => Promise<void>;
  private readonly publish: (status: UpdateStatus) => void;
  private status: UpdateStatus;
  private activeOperation: UpdateOperation | null = null;
  private checkPromise: Promise<UpdateStatus> | null = null;
  private downloadPromise: Promise<void> | null = null;

  public constructor(options: UpdateManagerOptions) {
    if (options.currentVersion.trim().length === 0) throw new TypeError("Current application version must not be empty.");
    if (options.supported && options.client === null) throw new TypeError("A supported updater requires an update client.");
    this.currentVersion = options.currentVersion;
    this.client = options.client;
    this.prepareForInstall = options.prepareForInstall;
    this.publish = options.publish;
    this.status = options.supported
      ? { currentVersion: this.currentVersion, phase: "idle" }
      : { currentVersion: this.currentVersion, phase: "unsupported" };
  }

  public currentStatus(): UpdateStatus {
    return this.status;
  }

  public checkForUpdates(): Promise<UpdateStatus> {
    const client = this.client;
    if (client === null) return Promise.resolve(this.status);
    if (this.downloadPromise !== null) return Promise.resolve(this.status);
    if (this.checkPromise !== null) return this.checkPromise;

    const retainedVersion = statusVersion(this.status);
    this.activeOperation = "check";
    if (retainedVersion === null) this.setStatus({ currentVersion: this.currentVersion, phase: "checking" });
    const checking = client.checkForUpdates()
      .then((result) => {
        if (!result.available) {
          this.setStatus({ currentVersion: this.currentVersion, phase: "idle" });
          return this.status;
        }
        const version = result.version?.trim();
        if (version === undefined || version.length === 0) throw new TypeError("Update service returned an invalid available version.");
        this.setStatus({ currentVersion: this.currentVersion, phase: "available", latestVersion: version });
        return this.status;
      })
      .catch((error: unknown) => {
        this.reportFailure(error);
        throw error;
      });
    this.checkPromise = checking;
    void checking.then(
      () => this.completeCheck(checking),
      () => this.completeCheck(checking),
    );
    return checking;
  }

  public downloadAndInstall(): Promise<void> {
    const client = this.client;
    if (client === null) return Promise.reject(new Error("自动更新仅适用于通过安装程序安装的 Windows 应用."));
    const checking = this.checkPromise;
    if (checking !== null) return checking.then(() => this.downloadAndInstall());
    if (this.downloadPromise !== null) return this.downloadPromise;
    if (this.status.phase === "downloaded" || this.status.phase === "installing") return Promise.resolve();
    const version = statusVersion(this.status);
    if (version === null) return Promise.reject(new Error("No newer update is available."));

    this.activeOperation = "download";
    this.setStatus({ currentVersion: this.currentVersion, phase: "downloading", latestVersion: version, downloadPercent: 0 });
    const downloading = client.downloadUpdate((progress) => {
      if (this.status.phase !== "downloading") return;
      const percent = Math.max(this.status.downloadPercent, boundedPercent(progress.percent));
      this.setStatus({ currentVersion: this.currentVersion, phase: "downloading", latestVersion: version, downloadPercent: percent });
    })
      .then(async () => {
        this.setStatus({ currentVersion: this.currentVersion, phase: "downloaded", latestVersion: version });
        this.activeOperation = "install";
        this.setStatus({ currentVersion: this.currentVersion, phase: "installing", latestVersion: version });
        await this.prepareForInstall();
        client.installAndRestart();
      })
      .catch((error: unknown) => {
        this.reportFailure(error);
        throw error;
      });
    this.downloadPromise = downloading;
    void downloading.then(
      () => { if (this.downloadPromise === downloading) this.downloadPromise = null; },
      () => this.completeDownload(downloading),
    );
    return downloading;
  }

  public reportFailure(error: unknown): void {
    const message = errorMessage(error);
    const operation = this.failureOperation();
    const version = statusVersion(this.status);
    if (operation === "check" && version !== null) {
      this.setStatus(this.status);
      return;
    }
    if (this.status.phase === "error" && this.status.operation === operation && this.status.error === message) return;
    if (operation === "download" || operation === "install") {
      if (version !== null) {
        this.setStatus({ currentVersion: this.currentVersion, phase: "error", operation, latestVersion: version, error: message });
        return;
      }
    }
    this.setStatus({ currentVersion: this.currentVersion, phase: "error", operation: "check", error: message });
  }

  private completeCheck(checking: Promise<UpdateStatus>): void {
    if (this.checkPromise === checking) this.checkPromise = null;
    if (this.activeOperation === "check") this.activeOperation = null;
  }

  private completeDownload(downloading: Promise<void>): void {
    if (this.downloadPromise === downloading) this.downloadPromise = null;
    this.activeOperation = null;
  }

  private failureOperation(): UpdateOperation {
    if (this.activeOperation !== null) return this.activeOperation;
    if (this.status.phase === "error") return this.status.operation;
    if (this.status.phase === "downloading") return "download";
    if (this.status.phase === "downloaded" || this.status.phase === "installing") return "install";
    return "check";
  }

  private setStatus(status: UpdateStatus): void {
    this.status = status;
    this.publish(status);
  }
}
