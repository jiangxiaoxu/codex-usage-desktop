import assert from "node:assert/strict";
import test from "node:test";
import { SingleInstanceWindow, type ManagedWindow } from "./single-instance-window";

class FakeWindow implements ManagedWindow {
  destroyed = false;
  minimized = false;
  showCount = 0;
  restoreCount = 0;
  focusCount = 0;
  readonly calls: string[] = [];
  private readonly closedListeners: Array<() => void> = [];

  isDestroyed(): boolean {
    return this.destroyed;
  }

  isMinimized(): boolean {
    return this.minimized;
  }

  show(): void {
    this.showCount += 1;
    this.calls.push("show");
  }

  restore(): void {
    this.restoreCount += 1;
    this.minimized = false;
    this.calls.push("restore");
  }

  focus(): void {
    this.focusCount += 1;
    this.calls.push("focus");
  }

  once(event: "closed", listener: () => void): this {
    assert.equal(event, "closed");
    this.closedListeners.push(listener);
    return this;
  }

  close(): void {
    this.destroyed = true;
    for (const listener of this.closedListeners.splice(0)) listener();
  }
}

test("reuses and focuses the single live window", () => {
  const createdWindows: FakeWindow[] = [];
  const controller = new SingleInstanceWindow(() => {
    const window = new FakeWindow();
    createdWindows.push(window);
    return window;
  });

  const first = controller.show();
  const second = controller.show();

  assert.equal(first, second);
  assert.equal(createdWindows.length, 1);
  assert.equal(first.showCount, 2);
  assert.equal(first.focusCount, 2);
  assert.equal(first.restoreCount, 0);
});

test("restores a minimized window before focusing it", () => {
  const window = new FakeWindow();
  window.minimized = true;
  const controller = new SingleInstanceWindow(() => window);

  controller.show();

  assert.equal(window.showCount, 1);
  assert.equal(window.restoreCount, 1);
  assert.equal(window.focusCount, 1);
  assert.equal(window.minimized, false);
  assert.deepEqual(window.calls, ["restore", "show", "focus"]);
});

test("creates a replacement only after the previous window is destroyed", () => {
  const createdWindows: FakeWindow[] = [];
  const controller = new SingleInstanceWindow(() => {
    const window = new FakeWindow();
    createdWindows.push(window);
    return window;
  });

  const first = controller.getOrCreate();
  first.close();
  const second = controller.getOrCreate();

  assert.notEqual(first, second);
  assert.equal(createdWindows.length, 2);
  assert.equal(controller.current(), second);
});

test("does not let a stale closed event clear a replacement window", () => {
  const createdWindows: FakeWindow[] = [];
  const controller = new SingleInstanceWindow(() => {
    const window = new FakeWindow();
    createdWindows.push(window);
    return window;
  });

  const first = controller.getOrCreate();
  first.destroyed = true;
  const second = controller.getOrCreate();
  first.close();

  assert.equal(controller.current(), second);
  assert.equal(createdWindows.length, 2);
});
