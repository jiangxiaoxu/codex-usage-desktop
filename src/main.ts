import { access, mkdir, rm } from "node:fs/promises";
import { constants, existsSync } from "node:fs";
import path from "node:path";
import { app, BrowserWindow, dialog, ipcMain, Menu, shell, Tray } from "electron";
import { CollectorClient } from "./collector-client";
import type { CollectorConfig } from "./collector-protocol";
import { resolveLedgerDirectory } from "./ledger-directory";
import { updateStatusFromLatestRelease } from "./release-update";
import { SingleInstanceWindow } from "./single-instance-window";
import type { CollectorStatus, FilterSpec, QueryResult, StartupSettings, SyncResult, UpdateStatus } from "./shared";
import { assertOutsideDirectories } from "./write-boundary";

const PRODUCT_NAME = "Codex Usage Desktop";
const RECONCILE_INTERVAL_MS = 10 * 60_000;
const WATCHER_DEBOUNCE_MS = 2_000;
const RESTART_REQUEST_PREFIX = "--shutdown-for-restart=";
const RESTART_DATA_DIRECTORY_PREFIX = "--shutdown-for-data-directory=";
const GITHUB_LATEST_RELEASE_API = "https://api.github.com/repos/jiangxiaoxu/codex-usage-desktop/releases/latest";
const GITHUB_RELEASES_PAGE = "https://github.com/jiangxiaoxu/codex-usage-desktop/releases/latest";
const UPDATE_REQUEST_TIMEOUT_MS = 8_000;
const UPDATE_CHECK_INTERVAL_MS = 4 * 60 * 60_000;

let tray: Tray | null = null;
let collector: CollectorClient | null = null;
let collectorReady: Promise<CollectorStatus> | null = null;
let isQuitting = false;
let fatalErrorHandled = false;
let shutdownPromise: Promise<void> | null = null;
let latestStatus: CollectorStatus | null = null;
let latestUpdateStatus: UpdateStatus | null = null;
let updateCheckTimer: NodeJS.Timeout | null = null;

interface PreparedApplication {
  readonly collectorReady: Promise<CollectorStatus>;
}

let windowReady: Promise<PreparedApplication> | null = null;

const mainWindow = new SingleInstanceWindow(() => {
  let showOnReady = true;
  const window = new BrowserWindow({
    title: `${PRODUCT_NAME} v${app.getVersion()}`,
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
  window.webContents.on("page-title-updated", (event) => {
    event.preventDefault();
    window.setTitle(`${PRODUCT_NAME} v${app.getVersion()}`);
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

function isRestartRequestForCurrentExecutable(commandLine: readonly string[]): boolean {
  const portableDirectory = process.env.PORTABLE_EXECUTABLE_DIR?.trim();
  const executableDirectory = path.resolve(portableDirectory || path.dirname(process.execPath)).toLowerCase();
  const targetDirectories = commandLine
    .filter((argument) => argument.startsWith(RESTART_REQUEST_PREFIX) || argument.startsWith(RESTART_DATA_DIRECTORY_PREFIX))
    .map((argument) => argument.startsWith(RESTART_REQUEST_PREFIX)
      ? argument.slice(RESTART_REQUEST_PREFIX.length).trim()
      : argument.slice(RESTART_DATA_DIRECTORY_PREFIX.length).trim())
    .filter((directory) => directory.length > 0)
    .map((directory) => path.resolve(directory).toLowerCase());
  if (targetDirectories.includes(executableDirectory)) return true;
  const normalizedDataDirectory = path.resolve(ledgerDataDirectory()).toLowerCase();
  return targetDirectories.includes(normalizedDataDirectory);
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

function ledgerDataDirectory(): string {
  return resolveLedgerDirectory({
    overrideDirectory: process.env.CODEX_USAGE_DATA_DIR,
    localAppDataDirectory: process.env.LOCALAPPDATA,
    userDataDirectory: app.getPath("userData"),
    productName: PRODUCT_NAME,
  });
}

function startupSettings(): StartupSettings {
  if (process.platform !== "win32" || !app.isPackaged || process.env.PORTABLE_EXECUTABLE_DIR?.trim()) {
    return { supported: false, enabled: false };
  }
  return { supported: true, enabled: existsSync(startupShortcutPath()) };
}

function startupShortcutPath(): string {
  return path.join(app.getPath("appData"), "Microsoft", "Windows", "Start Menu", "Programs", "Startup", `${PRODUCT_NAME}.lnk`);
}

async function setStartupEnabled(value: unknown): Promise<StartupSettings> {
  if (typeof value !== "boolean") throw new TypeError("Startup setting must be a boolean.");
  const enabled = value;
  const settings = startupSettings();
  if (!settings.supported) throw new Error("Startup settings are available only for the installed Windows application.");
  const shortcutPath = startupShortcutPath();
  if (enabled) {
    const written = shell.writeShortcutLink(shortcutPath, "create", {
      target: process.execPath,
      cwd: path.dirname(process.execPath),
      description: PRODUCT_NAME,
    });
    if (!written) throw new Error("Unable to create the startup shortcut.");
  } else {
    await rm(shortcutPath, { force: true });
  }
  return startupSettings();
}

async function checkForUpdates(): Promise<UpdateStatus> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), UPDATE_REQUEST_TIMEOUT_MS);
  try {
    const response = await fetch(GITHUB_LATEST_RELEASE_API, {
      headers: {
        Accept: "application/vnd.github+json",
        "User-Agent": `${PRODUCT_NAME}/${app.getVersion()}`,
        "X-GitHub-Api-Version": "2022-11-28",
      },
      signal: controller.signal,
    });
    const responseUrl = new URL(response.url);
    if (responseUrl.origin !== "https://api.github.com") throw new Error("Unexpected update response origin.");
    if (!response.ok) throw new Error(`GitHub update check failed with HTTP ${response.status}.`);
    const payload: unknown = await response.json();
    const status = updateStatusFromLatestRelease(app.getVersion(), payload);
    latestUpdateStatus = status;
    mainWindow.current()?.webContents.send("updates:status", status);
    return status;
  } catch (error) {
    latestUpdateStatus = null;
    if (error instanceof Error && error.name === "AbortError") throw new Error("GitHub update check timed out.");
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

async function openLatestRelease(): Promise<void> {
  if (latestUpdateStatus === null || !latestUpdateStatus.available) throw new Error("No newer release is available.");
  await shell.openExternal(GITHUB_RELEASES_PAGE);
}

function startPeriodicUpdateChecks(): void {
  if (updateCheckTimer !== null) return;
  updateCheckTimer = setInterval(() => {
    void checkForUpdates().catch(() => { /* A later interval or manual check can retry. */ });
  }, UPDATE_CHECK_INTERVAL_MS);
}

async function assertOutsideCodexSources(candidate: string, codexHome: string): Promise<void> {
  await assertOutsideDirectories(candidate, [path.join(codexHome, "sessions"), path.join(codexHome, "archived_sessions"), path.join(codexHome, "agents")]);
}

async function collectorConfig(): Promise<CollectorConfig> {
  const dataDirectory = ledgerDataDirectory();
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
  ipcMain.handle("settings:get-startup", (): StartupSettings => startupSettings());
  ipcMain.handle("settings:set-startup", (_event, enabled: unknown): Promise<StartupSettings> => setStartupEnabled(enabled));
  ipcMain.handle("updates:check", (): Promise<UpdateStatus> => checkForUpdates());
  ipcMain.handle("updates:open-latest-release", (): Promise<void> => openLatestRelease());
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
  startPeriodicUpdateChecks();
}

const lockAcquired = app.requestSingleInstanceLock();
if (!lockAcquired || isRestartRequestForCurrentExecutable(process.argv)) app.quit();
else {
  windowReady = app.whenReady().then(() => prepareApplication());
  void windowReady.then((prepared) => finishApplicationInitialization(prepared)).catch(handleFatalError);
  app.on("second-instance", (_event, commandLine) => {
    if (isRestartRequestForCurrentExecutable(commandLine)) {
      app.quit();
      return;
    }
    showWindowWhenReady();
  });
  app.on("activate", showWindowWhenReady);
  app.on("window-all-closed", () => { /* Keep the collector resident in the tray. */ });
  app.on("before-quit", () => {
    isQuitting = true;
    if (updateCheckTimer !== null) clearInterval(updateCheckTimer);
  });
  app.on("will-quit", (event) => {
    if (collector === null) return;
    event.preventDefault();
    if (shutdownPromise !== null) return;
    shutdownPromise = collector.close()
      .catch(() => { /* Exit after the worker has been terminated even if shutdown reporting fails. */ })
      .finally(() => app.exit(fatalErrorHandled ? 1 : 0));
  });
}
