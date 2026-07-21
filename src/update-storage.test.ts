import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { resolveUpdaterCacheDirectory } from "./update-storage";
import { assertOutsideDirectories } from "./write-boundary";

test("uses LocalAppData for the electron-updater cache and preserves the source write boundary", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-update-storage-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const protectedDirectory = path.join(root, "codex", "sessions");
  const localAppDataDirectory = path.join(root, "AppData", "Local");
  await mkdir(protectedDirectory, { recursive: true });
  await mkdir(localAppDataDirectory, { recursive: true });

  const cacheDirectory = resolveUpdaterCacheDirectory({ localAppDataDirectory, homeDirectory: root });
  assert.equal(cacheDirectory, path.join(localAppDataDirectory, "codex-usage-desktop-updater"));
  await assert.doesNotReject(() => assertOutsideDirectories(cacheDirectory, [protectedDirectory]));
  await assert.rejects(
    () => assertOutsideDirectories(resolveUpdaterCacheDirectory({ localAppDataDirectory: protectedDirectory, homeDirectory: root }), [protectedDirectory]),
    /read-only observation sources/,
  );
  const linkedLocalAppDataDirectory = path.join(root, "linked-local-app-data");
  await symlink(protectedDirectory, linkedLocalAppDataDirectory, process.platform === "win32" ? "junction" : "dir");
  await assert.rejects(
    () => assertOutsideDirectories(resolveUpdaterCacheDirectory({ localAppDataDirectory: linkedLocalAppDataDirectory, homeDirectory: root }), [protectedDirectory]),
    /read-only observation sources/,
  );
});

test("falls back to the standard Windows Local AppData path for the updater cache", () => {
  assert.equal(
    resolveUpdaterCacheDirectory({ localAppDataDirectory: undefined, homeDirectory: "C:\\Users\\test" }),
    path.join("C:\\Users\\test", "AppData", "Local", "codex-usage-desktop-updater"),
  );
});

test("matches electron-updater raw environment-variable path behavior", () => {
  assert.equal(
    resolveUpdaterCacheDirectory({ localAppDataDirectory: " relative cache ", homeDirectory: "C:\\Users\\test" }),
    path.join(" relative cache ", "codex-usage-desktop-updater"),
  );
  assert.equal(
    resolveUpdaterCacheDirectory({ localAppDataDirectory: "", homeDirectory: "C:\\Users\\test" }),
    path.join("C:\\Users\\test", "AppData", "Local", "codex-usage-desktop-updater"),
  );
});
