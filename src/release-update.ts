import type { UpdateStatus } from "./shared";

interface SemanticVersion {
  readonly major: number;
  readonly minor: number;
  readonly patch: number;
  readonly prerelease: readonly string[] | null;
}

const VERSION_PATTERN = /^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$/;

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null;
}

function parseVersion(value: string): SemanticVersion {
  const match = VERSION_PATTERN.exec(value);
  if (match === null) throw new TypeError(`Unsupported release version: ${value}`);
  const [, major, minor, patch, prerelease] = match;
  if (major === undefined || minor === undefined || patch === undefined) throw new TypeError(`Unsupported release version: ${value}`);
  return {
    major: Number(major),
    minor: Number(minor),
    patch: Number(patch),
    prerelease: prerelease === undefined ? null : prerelease.split("."),
  };
}

function compareIdentifier(left: string, right: string): number {
  const leftNumeric = /^\d+$/.test(left);
  const rightNumeric = /^\d+$/.test(right);
  if (leftNumeric && rightNumeric) return Number(left) - Number(right);
  if (leftNumeric) return -1;
  if (rightNumeric) return 1;
  return left.localeCompare(right);
}

export function compareVersions(left: string, right: string): number {
  const leftVersion = parseVersion(left);
  const rightVersion = parseVersion(right);
  for (const key of ["major", "minor", "patch"] as const) {
    const difference = leftVersion[key] - rightVersion[key];
    if (difference !== 0) return difference;
  }
  if (leftVersion.prerelease === null && rightVersion.prerelease === null) return 0;
  if (leftVersion.prerelease === null) return 1;
  if (rightVersion.prerelease === null) return -1;
  const limit = Math.min(leftVersion.prerelease.length, rightVersion.prerelease.length);
  for (let index = 0; index < limit; index += 1) {
    const leftIdentifier = leftVersion.prerelease[index];
    const rightIdentifier = rightVersion.prerelease[index];
    if (leftIdentifier === undefined || rightIdentifier === undefined) throw new Error("Invalid prerelease comparison.");
    const difference = compareIdentifier(leftIdentifier, rightIdentifier);
    if (difference !== 0) return difference;
  }
  return leftVersion.prerelease.length - rightVersion.prerelease.length;
}

function displayVersion(value: string): string {
  return value.startsWith("v") ? value.slice(1) : value;
}

export function updateStatusFromLatestRelease(currentVersion: string, payload: unknown): UpdateStatus {
  if (!isRecord(payload)) throw new TypeError("GitHub latest release payload must be an object.");
  const tagName = payload.tag_name;
  if (typeof tagName !== "string") throw new TypeError("GitHub latest release is missing tag_name.");
  if (payload.draft !== false || payload.prerelease !== false) throw new TypeError("GitHub latest release must be a published stable release.");
  const latestVersion = displayVersion(tagName);
  return {
    currentVersion,
    latestVersion,
    available: compareVersions(latestVersion, currentVersion) > 0,
  };
}
