import { contextBridge, ipcRenderer } from "electron";
import type { CollectorStatus, FilterSpec, QueryResult, StartupSettings, SyncResult, UpdateStatus, UsageApi } from "./shared";

const usageApi: UsageApi = {
  syncNow: (): Promise<SyncResult> => ipcRenderer.invoke("usage:sync"),
  query: (filter: FilterSpec): Promise<QueryResult> => ipcRenderer.invoke("usage:query", filter),
  exportCsv: (filter: FilterSpec): Promise<{ readonly path: string | null; readonly count: number }> => ipcRenderer.invoke("usage:export", filter),
  getCollectorStatus: (): Promise<CollectorStatus> => ipcRenderer.invoke("usage:status"),
  getStartupSettings: (): Promise<StartupSettings> => ipcRenderer.invoke("settings:get-startup"),
  setStartupEnabled: (enabled: boolean): Promise<StartupSettings> => ipcRenderer.invoke("settings:set-startup", enabled),
  checkForUpdates: (): Promise<UpdateStatus> => ipcRenderer.invoke("updates:check"),
  downloadAndInstallUpdate: (): Promise<void> => ipcRenderer.invoke("updates:download-and-install"),
  onUpdateStatus: (listener: (status: UpdateStatus) => void): (() => void) => {
    const handler = (_event: Electron.IpcRendererEvent, status: UpdateStatus): void => listener(status);
    ipcRenderer.on("updates:status", handler);
    return () => ipcRenderer.removeListener("updates:status", handler);
  },
  onUsageUpdated: (listener: (status: CollectorStatus) => void): (() => void) => {
    const handler = (_event: Electron.IpcRendererEvent, status: CollectorStatus): void => listener(status);
    ipcRenderer.on("usage:updated", handler);
    return () => ipcRenderer.removeListener("usage:updated", handler);
  },
};

contextBridge.exposeInMainWorld("usageApi", usageApi);
