import assert from "node:assert/strict";
import test from "node:test";
import { compareVersions, updateStatusFromLatestRelease } from "./release-update";

test("compares release versions including prerelease values", () => {
  assert.ok(compareVersions("1.0.1", "1.0.0") > 0);
  assert.ok(compareVersions("1.0.0", "1.0.0-beta.1") > 0);
  assert.ok(compareVersions("1.0.0-beta.2", "1.0.0-beta.10") < 0);
  assert.equal(compareVersions("v1.0.0+build.4", "1.0.0"), 0);
});

test("accepts only a newer published stable GitHub release", () => {
  assert.deepEqual(updateStatusFromLatestRelease("0.1.0", { tag_name: "v0.1.1", draft: false, prerelease: false }), {
    currentVersion: "0.1.0",
    latestVersion: "0.1.1",
    available: true,
  });
  assert.deepEqual(updateStatusFromLatestRelease("0.1.1", { tag_name: "v0.1.1", draft: false, prerelease: false }), {
    currentVersion: "0.1.1",
    latestVersion: "0.1.1",
    available: false,
  });
  assert.throws(() => updateStatusFromLatestRelease("0.1.0", { tag_name: "latest", draft: false, prerelease: false }));
  assert.throws(() => updateStatusFromLatestRelease("0.1.0", { tag_name: "v0.1.1", draft: true, prerelease: false }));
});
