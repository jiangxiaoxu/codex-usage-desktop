import type { CheckedUpdate, UpdateClient, UpdateDownloadProgress } from "./update-manager";

interface ElectronUpdaterCheckResult {
  readonly isUpdateAvailable: boolean;
  readonly updateInfo: {
    readonly version: unknown;
  };
}

interface ElectronUpdaterProgress {
  readonly percent: number;
}

export interface ElectronUpdater {
  autoDownload: boolean;
  autoInstallOnAppQuit: boolean;
  allowPrerelease: boolean;
  allowDowngrade: boolean;
  disableWebInstaller: boolean;
  checkForUpdates(): Promise<ElectronUpdaterCheckResult | null>;
  downloadUpdate(): Promise<readonly string[]>;
  quitAndInstall(isSilent?: boolean, isForceRunAfter?: boolean): void;
  on(event: "download-progress", listener: (progress: ElectronUpdaterProgress) => void): void;
  removeListener(event: "download-progress", listener: (progress: ElectronUpdaterProgress) => void): void;
}

export class ElectronUpdateClient implements UpdateClient {
  public constructor(private readonly updater: ElectronUpdater) {
    updater.autoDownload = false;
    updater.autoInstallOnAppQuit = false;
    updater.allowPrerelease = false;
    updater.allowDowngrade = false;
    updater.disableWebInstaller = true;
  }

  public async checkForUpdates(): Promise<CheckedUpdate> {
    const result = await this.updater.checkForUpdates();
    if (result === null || !result.isUpdateAvailable) return { available: false, version: null };
    const version = result.updateInfo.version;
    if (typeof version !== "string" || version.trim().length === 0) throw new TypeError("Updater returned an invalid version.");
    return { available: true, version: version.trim() };
  }

  public async downloadUpdate(onProgress: (progress: UpdateDownloadProgress) => void): Promise<void> {
    const listener = (progress: ElectronUpdaterProgress): void => onProgress({ percent: progress.percent });
    this.updater.on("download-progress", listener);
    try {
      await this.updater.downloadUpdate();
    } finally {
      this.updater.removeListener("download-progress", listener);
    }
  }

  public installAndRestart(): void {
    this.updater.quitAndInstall(true, true);
  }
}
