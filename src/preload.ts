import { contextBridge, ipcRenderer } from "electron";
import type { CollectorStatus, FilterSpec, QueryResult, SyncResult, UsageApi } from "./shared";

const usageApi: UsageApi = {
  syncNow: (): Promise<SyncResult> => ipcRenderer.invoke("usage:sync"),
  query: (filter: FilterSpec): Promise<QueryResult> => ipcRenderer.invoke("usage:query", filter),
  exportCsv: (filter: FilterSpec): Promise<{ readonly path: string | null; readonly count: number }> => ipcRenderer.invoke("usage:export", filter),
  getCollectorStatus: (): Promise<CollectorStatus> => ipcRenderer.invoke("usage:status"),
  onUsageUpdated: (listener: (status: CollectorStatus) => void): (() => void) => {
    const handler = (_event: Electron.IpcRendererEvent, status: CollectorStatus): void => listener(status);
    ipcRenderer.on("usage:updated", handler);
    return () => ipcRenderer.removeListener("usage:updated", handler);
  },
};

contextBridge.exposeInMainWorld("usageApi", usageApi);

