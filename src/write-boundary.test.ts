import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { assertOutsideDirectories } from "./write-boundary";

test("rejects direct and junction-mediated output inside every protected Codex source directory", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-write-boundary-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const sessions = path.join(root, "codex", "sessions");
  const agents = path.join(root, "codex", "agents");
  const outside = path.join(root, "outside");
  await mkdir(sessions, { recursive: true });
  await mkdir(agents, { recursive: true });
  await mkdir(outside, { recursive: true });
  const protectedDirectories = [sessions, agents];
  await assert.rejects(() => assertOutsideDirectories(path.join(sessions, "rollout.jsonl"), protectedDirectories), /read-only observation sources/);
  await assert.rejects(() => assertOutsideDirectories(path.join(agents, "worker.toml"), protectedDirectories), /read-only observation sources/);
  await assert.doesNotReject(() => assertOutsideDirectories(path.join(outside, "export.csv"), protectedDirectories));
  const junction = path.join(outside, "agents-link");
  await symlink(agents, junction, process.platform === "win32" ? "junction" : "dir");
  await assert.rejects(() => assertOutsideDirectories(path.join(junction, "blocked.csv"), protectedDirectories), /read-only observation sources/);
});
