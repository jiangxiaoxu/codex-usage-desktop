import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { resolveLedgerDirectory } from "./ledger-directory";

test("uses an explicit ledger directory override", () => {
  assert.equal(resolveLedgerDirectory({
    overrideDirectory: "D:\\custom-ledger",
    localAppDataDirectory: "C:\\Users\\test\\AppData\\Local",
    userDataDirectory: "C:\\Users\\test\\AppData\\Roaming\\codex-usage-desktop",
    productName: "Codex Usage Desktop",
  }), path.resolve("D:\\custom-ledger"));
});

test("uses LocalAppData for the default ledger", () => {
  assert.equal(resolveLedgerDirectory({
    overrideDirectory: undefined,
    localAppDataDirectory: "C:\\Users\\test\\AppData\\Local",
    userDataDirectory: "C:\\Users\\test\\AppData\\Roaming\\codex-usage-desktop",
    productName: "Codex Usage Desktop",
  }), path.join("C:\\Users\\test\\AppData\\Local", "Codex Usage Desktop"));
});

test("falls back to Electron userData when LocalAppData is unavailable", () => {
  assert.equal(resolveLedgerDirectory({
    overrideDirectory: undefined,
    localAppDataDirectory: undefined,
    userDataDirectory: "C:\\Users\\test\\AppData\\Roaming\\codex-usage-desktop",
    productName: "Codex Usage Desktop",
  }), "C:\\Users\\test\\AppData\\Roaming\\codex-usage-desktop");
});
