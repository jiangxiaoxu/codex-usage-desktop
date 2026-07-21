import assert from "node:assert/strict";
import test from "node:test";
import { ElectronUpdateClient, type ElectronUpdater } from "./electron-update-client";

class FakeElectronUpdater implements ElectronUpdater {
  public autoDownload = true;
  public autoInstallOnAppQuit = true;
  public allowPrerelease = true;
  public allowDowngrade = true;
  public disableWebInstaller = false;
  public checkResult: { readonly isUpdateAvailable: boolean; readonly updateInfo: { readonly version: unknown; } } | null = null;
  public progressListener: ((progress: { readonly percent: number; }) => void) | null = null;
  public quitArguments: readonly [boolean | undefined, boolean | undefined] | null = null;

  public async checkForUpdates(): Promise<{ readonly isUpdateAvailable: boolean; readonly updateInfo: { readonly version: unknown; } } | null> {
    return this.checkResult;
  }

  public async downloadUpdate(): Promise<readonly string[]> {
    this.progressListener?.({ percent: 55.5 });
    return ["C:\\updater\\installer.exe"];
  }

  public quitAndInstall(isSilent?: boolean, isForceRunAfter?: boolean): void {
    this.quitArguments = [isSilent, isForceRunAfter];
  }

  public on(_event: "download-progress", listener: (progress: { readonly percent: number; }) => void): void {
    this.progressListener = listener;
  }

  public removeListener(_event: "download-progress", listener: (progress: { readonly percent: number; }) => void): void {
    if (this.progressListener === listener) this.progressListener = null;
  }
}

test("configures electron-updater for explicit full-installer downloads and silent restart", async () => {
  const updater = new FakeElectronUpdater();
  updater.checkResult = { isUpdateAvailable: true, updateInfo: { version: "0.2.5" } };
  const client = new ElectronUpdateClient(updater);

  assert.equal(updater.autoDownload, false);
  assert.equal(updater.autoInstallOnAppQuit, false);
  assert.equal(updater.allowPrerelease, false);
  assert.equal(updater.allowDowngrade, false);
  assert.equal(updater.disableWebInstaller, true);
  assert.deepEqual(await client.checkForUpdates(), { available: true, version: "0.2.5" });

  const progress: number[] = [];
  await client.downloadUpdate((item) => progress.push(item.percent));
  assert.deepEqual(progress, [55.5]);
  assert.equal(updater.progressListener, null);

  client.installAndRestart();
  assert.deepEqual(updater.quitArguments, [true, true]);
});

test("rejects malformed updater versions before they enter application state", async () => {
  const updater = new FakeElectronUpdater();
  updater.checkResult = { isUpdateAvailable: true, updateInfo: { version: 25 } };
  const client = new ElectronUpdateClient(updater);

  await assert.rejects(() => client.checkForUpdates(), /invalid version/);
});
