import { access, mkdir } from "node:fs/promises";
import { constants } from "node:fs";
import path from "node:path";
import { app, BrowserWindow, dialog, ipcMain, Menu, Tray } from "electron";
import { CollectorClient } from "./collector-client";
import type { CollectorConfig } from "./collector-protocol";
import { SingleInstanceWindow } from "./single-instance-window";
import type { CollectorStatus, FilterSpec, QueryResult, SyncResult } from "./shared";
import { assertOutsideDirectories } from "./write-boundary";

const PRODUCT_NAME = "Codex Usage Desktop";
const RECONCILE_INTERVAL_MS = 10 * 60_000;
const WATCHER_DEBOUNCE_MS = 2_000;

let tray: Tray | null = null;
let collector: CollectorClient | null = null;
let collectorReady: Promise<CollectorStatus> | null = null;
let isQuitting = false;
let fatalErrorHandled = false;
let shutdownPromise: Promise<void> | null = null;
let latestStatus: CollectorStatus | null = null;

interface PreparedApplication {
  readonly collectorReady: Promise<CollectorStatus>;
}

let windowReady: Promise<PreparedApplication> | null = null;

const mainWindow = new SingleInstanceWindow(() => {
  let showOnReady = true;
  const window = new BrowserWindow({
    width: 1280,
    height: 860,
    minWidth: 980,
    minHeight: 640,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  window.on("close", (event) => {
    if (isQuitting) return;
    event.preventDefault();
    showOnReady = false;
    window.hide();
  });
  window.once("ready-to-show", () => {
    if (showOnReady && !isQuitting && !window.isDestroyed()) window.show();
  });
  void window.loadFile(path.join(__dirname, "renderer.html")).catch(handleFatalError);
  return window;
});

function showWindowWhenReady(): void {
  const pendingWindow = windowReady;
  if (isQuitting || pendingWindow === null) return;
  void pendingWindow.then(() => {
    if (!isQuitting) mainWindow.show();
  }).catch(() => { /* Initialization failures are handled by handleFatalError. */ });
}

function handleFatalError(error: unknown): void {
  if (fatalErrorHandled || isQuitting) return;
  fatalErrorHandled = true;
  isQuitting = true;
  const message = error instanceof Error ? error.message : String(error);
  dialog.showErrorBox(PRODUCT_NAME, message);
  app.quit();
}

async function createTray(): Promise<void> {
  const icon = await app.getFileIcon(process.execPath, { size: "small" });
  if (isQuitting) return;
  tray = new Tray(icon);
  tray.setToolTip(PRODUCT_NAME);
  tray.on("double-click", showWindowWhenReady);
  updateTrayMenu();
}

function updateTrayMenu(): void {
  if (tray === null) return;
  const statusLabel = latestStatus === null ? "Collector: initializing" : `Collector: ${latestStatus.phase}`;
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: "Open dashboard", click: showWindowWhenReady },
    { label: "Sync now", click: () => {
      void synchronizeNow().catch((error: unknown) => {
        void dialog.showMessageBox({ type: "error", title: PRODUCT_NAME, message: "Synchronization failed.", detail: error instanceof Error ? error.message : String(error) });
      });
    } },
    { label: statusLabel, enabled: false },
    { type: "separator" },
    { label: "Exit", click: () => { isQuitting = true; app.quit(); } },
  ]));
}

function portableDataDirectory(): string {
  const override = process.env.CODEX_USAGE_DATA_DIR?.trim();
  if (override) return path.resolve(override);
  if (!app.isPackaged) return path.join(app.getPath("userData"), "codex-usage-data");
  const portableDirectory = process.env.PORTABLE_EXECUTABLE_DIR?.trim();
  const executableDirectory = portableDirectory || path.dirname(process.execPath);
  return path.join(executableDirectory, "codex-usage-data");
}

async function assertOutsideCodexSources(candidate: string, codexHome: string): Promise<void> {
  await assertOutsideDirectories(candidate, [path.join(codexHome, "sessions"), path.join(codexHome, "archived_sessions"), path.join(codexHome, "agents")]);
}

async function collectorConfig(): Promise<CollectorConfig> {
  const dataDirectory = portableDataDirectory();
  const codexHome = path.join(process.env.USERPROFILE ?? "", ".codex");
  await assertOutsideCodexSources(dataDirectory, codexHome);
  await mkdir(dataDirectory, { recursive: true });
  await assertOutsideCodexSources(dataDirectory, codexHome);
  await access(dataDirectory, constants.R_OK | constants.W_OK);
  return {
    codexHome,
    databasePath: path.join(dataDirectory, "usage.sqlite"),
    reconcileIntervalMs: RECONCILE_INTERVAL_MS,
    watcherDebounceMs: WATCHER_DEBOUNCE_MS,
  };
}

async function getCollector(): Promise<CollectorClient> {
  if (collector === null || collectorReady === null) throw new Error("Collector is not available.");
  await collectorReady;
  return collector;
}

async function synchronizeNow(): Promise<SyncResult> {
  const activeCollector = await getCollector();
  return activeCollector.request("reconcile", null);
}

function registerIpc(): void {
  ipcMain.handle("usage:sync", (): Promise<SyncResult> => synchronizeNow());
  ipcMain.handle("usage:query", async (_event, filter: FilterSpec): Promise<QueryResult> => (await getCollector()).request("query", filter));
  ipcMain.handle("usage:status", async (): Promise<CollectorStatus> => (await getCollector()).request("getStatus", null));
  ipcMain.handle("usage:export", async (_event, filter: FilterSpec): Promise<{ readonly path: string | null; readonly count: number }> => {
    const result = await dialog.showSaveDialog({
      title: "Export filtered Codex usage",
      defaultPath: "codex-usage-export.csv",
      filters: [{ name: "CSV", extensions: ["csv"] }],
    });
    if (result.canceled || result.filePath === undefined) return { path: null, count: 0 };
    await assertOutsideCodexSources(result.filePath, path.join(process.env.USERPROFILE ?? "", ".codex"));
    const exported = await (await getCollector()).request("exportCsv", { filter, filePath: result.filePath });
    return { path: result.filePath, count: exported.count };
  });
}

async function prepareApplication(): Promise<PreparedApplication> {
  registerIpc();
  const activeCollector = new CollectorClient(__dirname);
  collector = activeCollector;
  activeCollector.on("usage-updated", (status: CollectorStatus) => {
    latestStatus = status;
    updateTrayMenu();
    mainWindow.current()?.webContents.send("usage:updated", status);
  });
  const config = await collectorConfig();
  if (isQuitting) throw new Error("Application is shutting down.");
  const ready = activeCollector.initialize(config);
  collectorReady = ready;
  mainWindow.getOrCreate();
  return { collectorReady: ready };
}

async function finishApplicationInitialization(prepared: PreparedApplication): Promise<void> {
  const [status] = await Promise.all([prepared.collectorReady, createTray()]);
  latestStatus = status;
  updateTrayMenu();
}

const lockAcquired = app.requestSingleInstanceLock();
if (!lockAcquired) app.quit();
else {
  windowReady = app.whenReady().then(() => prepareApplication());
  void windowReady.then((prepared) => finishApplicationInitialization(prepared)).catch(handleFatalError);
  app.on("second-instance", showWindowWhenReady);
  app.on("activate", showWindowWhenReady);
  app.on("window-all-closed", () => { /* Keep the collector resident in the tray. */ });
  app.on("before-quit", () => { isQuitting = true; });
  app.on("will-quit", (event) => {
    if (collector === null) return;
    event.preventDefault();
    if (shutdownPromise !== null) return;
    shutdownPromise = collector.close()
      .catch(() => { /* Exit after the worker has been terminated even if shutdown reporting fails. */ })
      .finally(() => app.exit(fatalErrorHandled ? 1 : 0));
  });
}
