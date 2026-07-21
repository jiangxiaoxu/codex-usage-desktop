import assert from "node:assert/strict";
import test from "node:test";
import type { UpdateStatus } from "./shared";
import { UpdateManager, type CheckedUpdate, type UpdateClient, type UpdateDownloadProgress } from "./update-manager";

class FakeUpdateClient implements UpdateClient {
  public checkResult: CheckedUpdate = { available: false, version: null };
  public checkAction: () => Promise<CheckedUpdate> = async () => this.checkResult;
  public downloadAction: (onProgress: (progress: UpdateDownloadProgress) => void) => Promise<void> = async () => {};
  public checks = 0;
  public downloads = 0;
  public installs = 0;

  public async checkForUpdates(): Promise<CheckedUpdate> {
    this.checks += 1;
    return this.checkAction();
  }

  public async downloadUpdate(onProgress: (progress: UpdateDownloadProgress) => void): Promise<void> {
    this.downloads += 1;
    await this.downloadAction(onProgress);
  }

  public installAndRestart(): void {
    this.installs += 1;
  }
}

function createManager(client: UpdateClient | null, supported = client !== null, prepareForInstall: () => Promise<void> = async () => {}): { readonly manager: UpdateManager; readonly statuses: UpdateStatus[] } {
  const statuses: UpdateStatus[] = [];
  const manager = new UpdateManager({
    currentVersion: "0.2.4",
    supported,
    client,
    prepareForInstall,
    publish: (status) => statuses.push(status),
  });
  return { manager, statuses };
}

test("checks manually and exposes an available version without downloading it", async () => {
  const client = new FakeUpdateClient();
  client.checkResult = { available: true, version: "0.2.5" };
  const { manager, statuses } = createManager(client);

  const status = await manager.checkForUpdates();

  assert.deepEqual(status, { currentVersion: "0.2.4", phase: "available", latestVersion: "0.2.5" });
  assert.deepEqual(statuses.map((item) => item.phase), ["checking", "available"]);
  assert.equal(client.downloads, 0);
});

test("downloads once, reports progress, and silently installs after the download completes", async () => {
  const client = new FakeUpdateClient();
  client.checkResult = { available: true, version: "0.2.5" };
  client.downloadAction = async (onProgress) => {
    onProgress({ percent: 12.4 });
    onProgress({ percent: 87.8 });
  };
  const { manager, statuses } = createManager(client);
  await manager.checkForUpdates();

  await manager.downloadAndInstall();

  assert.equal(client.downloads, 1);
  assert.equal(client.installs, 1);
  assert.deepEqual(statuses.map((item) => [item.phase, item.phase === "downloading" ? item.downloadPercent : null]), [
    ["checking", null],
    ["available", null],
    ["downloading", 0],
    ["downloading", 12],
    ["downloading", 88],
    ["downloaded", null],
    ["installing", null],
  ]);
});

test("keeps a failed download retryable", async () => {
  const client = new FakeUpdateClient();
  client.checkResult = { available: true, version: "0.2.5" };
  client.downloadAction = async () => { throw new Error("network unavailable"); };
  const { manager } = createManager(client);
  await manager.checkForUpdates();

  await assert.rejects(() => manager.downloadAndInstall(), /network unavailable/);

  assert.deepEqual(manager.currentStatus(), {
    currentVersion: "0.2.4",
    phase: "error",
    operation: "download",
    latestVersion: "0.2.5",
    error: "network unavailable",
  });
  client.downloadAction = async () => {};
  await manager.downloadAndInstall();
  assert.equal(client.downloads, 2);
  assert.equal(client.installs, 1);
});

test("keeps a previously available update actionable when a later check fails", async () => {
  const client = new FakeUpdateClient();
  client.checkResult = { available: true, version: "0.2.5" };
  const { manager, statuses } = createManager(client);
  await manager.checkForUpdates();
  client.checkAction = async () => { throw new Error("GitHub unavailable"); };

  await assert.rejects(() => manager.checkForUpdates(), /GitHub unavailable/);

  assert.deepEqual(manager.currentStatus(), { currentVersion: "0.2.4", phase: "available", latestVersion: "0.2.5" });
  assert.deepEqual(statuses.map((item) => item.phase), ["checking", "available", "available"]);
});

test("waits for an in-flight recheck before downloading the version it reports", async () => {
  const client = new FakeUpdateClient();
  client.checkResult = { available: true, version: "0.2.5" };
  const { manager, statuses } = createManager(client);
  await manager.checkForUpdates();

  const checkGate: { resolve: ((value: CheckedUpdate) => void) | null } = { resolve: null };
  client.checkAction = () => new Promise<CheckedUpdate>((resolve) => { checkGate.resolve = resolve; });
  const recheck = manager.checkForUpdates();
  const update = manager.downloadAndInstall();
  assert.equal(client.downloads, 0);
  const resolveRecheck = checkGate.resolve;
  if (resolveRecheck === null) throw new Error("Expected update recheck to be pending.");
  resolveRecheck({ available: true, version: "0.2.6" });

  await Promise.all([recheck, update]);

  assert.equal(client.downloads, 1);
  assert.equal(client.installs, 1);
  assert.ok(statuses.some((status) => status.phase === "downloading" && status.latestVersion === "0.2.6"));
});

test("waits for application shutdown before starting the installer", async () => {
  const client = new FakeUpdateClient();
  client.checkResult = { available: true, version: "0.2.5" };
  const shutdownGate: { resolve: (() => void) | null } = { resolve: null };
  const shutdown = new Promise<void>((resolve) => { shutdownGate.resolve = resolve; });
  const { manager } = createManager(client, true, async () => shutdown);
  await manager.checkForUpdates();

  const update = manager.downloadAndInstall();
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(client.installs, 0);
  const releaseShutdown = shutdownGate.resolve;
  if (releaseShutdown === null) throw new Error("Expected update shutdown to be pending.");
  releaseShutdown();
  await update;

  assert.equal(client.installs, 1);
});

test("does not allow unsupported packages to start an update", async () => {
  const { manager, statuses } = createManager(null, false);

  assert.deepEqual(await manager.checkForUpdates(), { currentVersion: "0.2.4", phase: "unsupported" });
  await assert.rejects(() => manager.downloadAndInstall(), /仅适用于/);
  assert.deepEqual(statuses, []);
});
