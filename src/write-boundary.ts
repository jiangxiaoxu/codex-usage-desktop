import { realpath } from "node:fs/promises";
import path from "node:path";

function comparable(value: string): string {
  const normalized = path.normalize(value);
  return process.platform === "win32" ? normalized.toLocaleLowerCase("en-US") : normalized;
}

function isWithin(directory: string, candidate: string): boolean {
  const relative = path.relative(comparable(directory), comparable(candidate));
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

async function resolveThroughExistingAncestor(candidate: string): Promise<string> {
  let cursor = path.resolve(candidate);
  const missingSegments: string[] = [];
  while (true) {
    try {
      const resolved = await realpath(cursor);
      return path.join(resolved, ...missingSegments);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      const parent = path.dirname(cursor);
      if (parent === cursor) return path.resolve(candidate);
      missingSegments.unshift(path.basename(cursor));
      cursor = parent;
    }
  }
}

export async function assertOutsideDirectories(candidate: string, protectedDirectories: readonly string[]): Promise<void> {
  const resolvedCandidate = await resolveThroughExistingAncestor(candidate);
  for (const directory of protectedDirectories) {
    const resolvedDirectory = await resolveThroughExistingAncestor(directory);
    if (isWithin(resolvedDirectory, resolvedCandidate)) throw new Error("The Codex source directories are read-only observation sources and cannot be used for application output.");
  }
}
