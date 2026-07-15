import { access, mkdir } from "node:fs/promises";
import { constants } from "node:fs";
import path from "node:path";
import { app, BrowserWindow, dialog, ipcMain, Menu, Tray } from "electron";
import { CollectorClient } from "./collector-client";
import type { CollectorConfig } from "./collector-protocol";
import type { CollectorStatus, FilterSpec, QueryResult, SyncResult } from "./shared";
import { assertOutsideDirectories } from "./write-boundary";

const PRODUCT_NAME = "Codex Usage Desktop";
const RECONCILE_INTERVAL_MS = 10 * 60_000;
const WATCHER_DEBOUNCE_MS = 2_000;

let mainWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let collector: CollectorClient | null = null;
let collectorReady: Promise<CollectorStatus> | null = null;
let isQuitting = false;
let shutdownStarted = false;
let latestStatus: CollectorStatus | null = null;

function showWindow(): void {
  if (mainWindow === null || mainWindow.isDestroyed()) createWindow();
  mainWindow?.show();
  if (mainWindow?.isMinimized()) mainWindow.restore();
  mainWindow?.focus();
}

function createWindow(): void {
  mainWindow = new BrowserWindow({
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
  mainWindow.on("close", (event) => {
    if (isQuitting) return;
    event.preventDefault();
    mainWindow?.hide();
  });
  mainWindow.on("closed", () => { mainWindow = null; });
  mainWindow.once("ready-to-show", () => mainWindow?.show());
  void mainWindow.loadFile(path.join(__dirname, "renderer.html"));
}

async function createTray(): Promise<void> {
  const icon = await app.getFileIcon(process.execPath, { size: "small" });
  tray = new Tray(icon);
  tray.setToolTip(PRODUCT_NAME);
  tray.on("double-click", showWindow);
  updateTrayMenu();
}

function updateTrayMenu(): void {
  if (tray === null) return;
  const statusLabel = latestStatus === null ? "Collector: initializing" : `Collector: ${latestStatus.phase}`;
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: "Open dashboard", click: showWindow },
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

async function initializeApplication(): Promise<void> {
  registerIpc();
  collector = new CollectorClient(__dirname);
  collector.on("usage-updated", (status: CollectorStatus) => {
    latestStatus = status;
    updateTrayMenu();
    if (mainWindow !== null && !mainWindow.isDestroyed()) mainWindow.webContents.send("usage:updated", status);
  });
  try {
    collectorReady = collector.initialize(await collectorConfig());
    createWindow();
    await createTray();
    latestStatus = await collectorReady;
    updateTrayMenu();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    void dialog.showMessageBox({ type: "error", title: PRODUCT_NAME, message: "Collector failed to initialize.", detail: message });
  }
}

const lockAcquired = app.requestSingleInstanceLock();
if (!lockAcquired) app.quit();
else {
  app.on("second-instance", showWindow);
  app.whenReady().then(() => initializeApplication()).catch((error: unknown) => {
    const message = error instanceof Error ? error.message : String(error);
    void dialog.showErrorBox(PRODUCT_NAME, message);
    app.quit();
  });
}

app.on("activate", showWindow);
app.on("window-all-closed", () => { /* Keep the collector resident in the tray. */ });
app.on("before-quit", () => { isQuitting = true; });
app.on("will-quit", (event) => {
  if (shutdownStarted || collector === null) return;
  event.preventDefault();
  shutdownStarted = true;
  void collector.close().finally(() => app.exit(0));
});
