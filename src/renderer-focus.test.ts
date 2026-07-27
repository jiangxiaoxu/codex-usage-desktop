import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const rendererSource = readFileSync(path.resolve(__dirname, "../src/renderer.ts"), "utf8");

test("filter focus restoration prevents document scrolling", () => {
  const start = rendererSource.indexOf("function restoreFilterFocus(");
  const end = rendererSource.indexOf("\nfunction renderFilterControls(", start);
  assert.ok(start >= 0 && end > start, "restoreFilterFocus must exist before renderFilterControls");

  const restoreFunctionSource = rendererSource.slice(start, end);
  assert.match(restoreFunctionSource, /\.focus\(\{\s*preventScroll:\s*true\s*\}\)/,
    "restored focus must not scroll the document");
  assert.doesNotMatch(restoreFunctionSource, /\.focus\(\s*\)/,
    "restoreFilterFocus must not use scrolling focus");
});
