import path from "node:path";

export const UPDATER_CACHE_DIRECTORY_NAME = "codex-usage-desktop-updater";

export interface UpdateStorageInput {
  readonly localAppDataDirectory: string | undefined;
  readonly homeDirectory: string;
}

export function resolveUpdaterCacheDirectory(input: UpdateStorageInput): string {
  const cacheRoot = input.localAppDataDirectory || path.join(input.homeDirectory, "AppData", "Local");
  return path.join(cacheRoot, UPDATER_CACHE_DIRECTORY_NAME);
}
