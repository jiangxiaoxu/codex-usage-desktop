import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { isStartupLaunch, STARTUP_LAUNCH_ARGUMENT } from "./startup-launch";

test("recognizes the explicit Windows Startup shortcut argument", () => {
  assert.equal(isStartupLaunch(["Codex Usage Desktop.exe", STARTUP_LAUNCH_ARGUMENT]), true);
});

test("does not treat an ordinary or similarly named argument as a startup launch", () => {
  assert.equal(isStartupLaunch(["Codex Usage Desktop.exe"]), false);
  assert.equal(isStartupLaunch(["Codex Usage Desktop.exe", "--startup=1"]), false);
});

test("the NSIS installer writes the Windows Startup argument", () => {
  const installerScript = readFileSync(path.join(__dirname, "..", "build", "custom-installer.nsh"), "utf8");
  const startupShortcutCreations = installerScript
    .split(/\r?\n/)
    .filter((line) => line.includes("CreateShortCut \"$APPDATA\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\Codex Usage Desktop.lnk\""));
  assert.equal(startupShortcutCreations.length, 2);
  for (const shortcutCreation of startupShortcutCreations) {
    assert.ok(shortcutCreation.includes(`\"${STARTUP_LAUNCH_ARGUMENT}\"`));
  }
});
