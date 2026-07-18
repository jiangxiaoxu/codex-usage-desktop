import assert from "node:assert/strict";
import test from "node:test";
import { parseRollout, parseRolloutChunk, type RolloutParserState } from "./rollout-parser";

function line(type: string, payload: Readonly<Record<string, unknown>>, timestamp = "2026-07-15T01:02:03.004Z"): string {
  return JSON.stringify({ timestamp, type, payload });
}

function token(
  last: readonly [number, number, number, number, number],
  total: readonly [number, number, number, number, number],
  timestamp = "2026-07-15T01:02:03.004Z",
): string {
  const tuple = ([input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens]: readonly number[]) => ({ input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens, total_tokens });
  return line("event_msg", { type: "token_count", info: { last_token_usage: tuple(last), total_token_usage: tuple(total) } }, timestamp);
}

function taskStarted(turnId: string, startedAtEpochSeconds?: number): string {
  return line("event_msg", {
    type: "task_started",
    turn_id: turnId,
    ...(startedAtEpochSeconds === undefined ? {} : { started_at: startedAtEpochSeconds }),
  });
}

function threadSettings(model: string): string {
  return line("event_msg", { type: "thread_settings_applied", thread_settings: { model } });
}

function triggeringAgentMessage(recipient: string, turnId?: string): string {
  return line("response_item", {
    type: "agent_message",
    recipient,
    ...(turnId === undefined ? {} : { internal_chat_message_metadata_passthrough: { turn_id: turnId } }),
  });
}

function jsonl(...lines: readonly string[]): string {
  return `${lines.join("\n")}\n`;
}

function uuidV7At(timestamp: string): string {
  const epochHex = Date.parse(timestamp).toString(16).padStart(12, "0");
  return `${epochHex.slice(0, 8)}-${epochHex.slice(8)}-7000-8000-000000000000`;
}

test("parses main metadata and keeps conversation and rollout identifiers separate", () => {
  const result = parseRollout(jsonl(
    line("session_meta", { session_id: "conversation-a", id: "rollout-a", thread_source: "user" }),
    line("turn_context", { turn_id: "turn-a", model: "gpt-main" }),
    token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
  ), "fallback");

  assert.deepEqual(result.metadata, {
    conversationId: "conversation-a",
    rolloutId: "rollout-a",
    parentThreadId: "",
    threadType: "main",
    agentRole: "main",
    agentPath: "/root",
    agentNickname: "",
  });
  assert.equal(result.events[0]?.model, "gpt-main");
  assert.equal(result.events[0]?.tokenEventOrdinal, 0);
});

test("supports nested thread_spawn metadata and legacy top-level fields", () => {
  const nested = parseRollout(jsonl(line("session_meta", {
    session_id: "parent",
    id: "child",
    source: { subagent: { thread_spawn: { parent_thread_id: "parent", agent_role: "worker", agent_path: "/root/worker", agent_nickname: "worker-a" } } },
  })), "fallback");
  assert.deepEqual(nested.metadata, {
    conversationId: "parent",
    rolloutId: "child",
    parentThreadId: "parent",
    threadType: "subagent",
    agentRole: "worker",
    agentPath: "/root/worker",
    agentNickname: "worker-a",
  });

  const legacy = parseRollout(jsonl(line("session_meta", {
    id: "legacy-child",
    parent_thread_id: "legacy-parent",
    thread_source: "subagent",
    agent_role: "reviewer",
    agent_path: "/root/reviewer",
    agent_nickname: "reviewer-a",
  })), "fallback");
  assert.equal(legacy.metadata.conversationId, "legacy-child");
  assert.equal(legacy.metadata.parentThreadId, "legacy-parent");
  assert.equal(legacy.metadata.agentRole, "reviewer");
});

test("falls back to unknown when subagent thread_spawn metadata lacks an agent role", () => {
  const result = parseRollout(jsonl(line("session_meta", {
    session_id: "parent",
    id: "child",
    thread_source: "subagent",
    source: { subagent: { thread_spawn: { parent_thread_id: "parent", agent_path: "/root/worker" } } },
  })), "fallback");

  assert.equal(result.metadata.agentRole, "unknown");
});

test("resolves candidate models after parsing and handles model switches", () => {
  const result = parseRollout(jsonl(
    line("event_msg", { type: "task_started", turn_id: "turn-a" }),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    line("turn_context", { turn_id: "turn-a", model: "gpt-a" }),
    line("event_msg", { type: "task_started", turn_id: "turn-b" }),
    token([5, 1, 3, 2, 8], [9, 2, 5, 3, 14]),
    line("turn_context", { turn_id: "turn-b", model: "gpt-b" }),
  ), "rollout");

  assert.deepEqual(result.events.map((event) => [event.turnId, event.model]), [["turn-a", "gpt-a"], ["turn-b", "gpt-b"]]);
});

test("model enrichment does not change the stable event signature", () => {
  const prefix = jsonl(
    line("event_msg", { type: "task_started", turn_id: "turn-a" }),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
  );
  const initial = parseRollout(prefix, "rollout");
  const enriched = parseRollout(prefix + line("turn_context", { turn_id: "turn-a", model: "gpt-a" }) + "\n", "rollout");
  assert.equal(initial.events[0]?.model, "unknown");
  assert.equal(enriched.events[0]?.model, "gpt-a");
  assert.equal(initial.events[0]?.deterministicSignature, enriched.events[0]?.deterministicSignature);
});

test("skips fork replay until the addressed child turn is proven live", () => {
  const result = parseRollout(jsonl(
    line("session_meta", {
      session_id: "parent",
      id: "child",
      forked_from_id: "parent",
      source: { subagent: { thread_spawn: { parent_thread_id: "parent", agent_path: "/root/worker" } } },
    }),
    threadSettings("gpt-parent"),
    taskStarted("parent-turn"),
    line("turn_context", { turn_id: "parent-turn", model: "gpt-parent" }),
    line("inter_agent_communication_metadata", { trigger_turn: true }),
    triggeringAgentMessage("/root/worker", "different-turn"),
    token([40, 10, 6, 2, 46], [40, 10, 6, 2, 46]),
    threadSettings("gpt-child"),
    taskStarted("child-turn"),
    line("turn_context", { turn_id: "child-turn", model: "gpt-child" }),
    line("inter_agent_communication_metadata", { trigger_turn: true }),
    triggeringAgentMessage("/root/not-this-worker", "child-turn"),
    token([50, 20, 8, 3, 58], [90, 30, 14, 5, 104]),
    triggeringAgentMessage("/root/worker", "child-turn"),
    token([7, 2, 3, 1, 10], [97, 32, 17, 6, 114]),
  ), "fallback");

  assert.deepEqual(result.events.map((event) => [event.tokenEventOrdinal, event.turnId, event.model, event.inputTokens]), [
    [0, "child-turn", "gpt-child", 7],
  ]);
});

test("skips manual main fork replay and accounts usage from the first post-fork task", () => {
  const forkTimestamp = "2026-07-15T01:02:30.500Z";
  const result = parseRollout(jsonl(
    line("session_meta", {
      session_id: "child",
      id: "child",
      forked_from_id: "parent",
      thread_source: "user",
    }, forkTimestamp),
    taskStarted("replayed-turn", Date.parse("2026-07-15T01:01:00.000Z") / 1_000),
    line("turn_context", { turn_id: "replayed-turn", model: "gpt-parent" }),
    token([40, 10, 6, 2, 46], [40, 10, 6, 2, 46]),
    line("event_msg", { type: "task_complete", turn_id: "replayed-turn" }),
    threadSettings("gpt-child"),
    taskStarted("live-turn", Date.parse("2026-07-15T01:03:00.000Z") / 1_000),
    line("turn_context", { turn_id: "live-turn", model: "gpt-child" }),
    token([7, 2, 3, 1, 10], [47, 12, 9, 3, 56]),
  ), "fallback");

  assert.equal(result.metadata.threadType, "main");
  assert.deepEqual(result.events.map((event) => [event.tokenEventOrdinal, event.turnId, event.model, event.inputTokens]), [
    [0, "live-turn", "gpt-child", 7],
  ]);
});

test("carries manual main fork replay state across incremental chunks", () => {
  const forkTimestamp = "2026-07-15T01:02:30.500Z";
  const first = parseRolloutChunk(jsonl(
    line("session_meta", {
      id: "child",
      forked_from_id: "parent",
      thread_source: "user",
    }, forkTimestamp),
    taskStarted("replayed-turn", Date.parse("2026-07-15T01:01:00.000Z") / 1_000),
    token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
  ), "fallback");
  assert.equal(first.events.length, 0);
  assert.deepEqual(first.state.forkReplay, {
    status: "awaiting_main_live_turn",
    forkBoundaryEpochMs: Date.parse(forkTimestamp),
  });

  const restoredState = JSON.parse(JSON.stringify(first.state)) as RolloutParserState;
  const second = parseRolloutChunk(jsonl(
    threadSettings("gpt-child"),
    taskStarted("live-turn", Date.parse("2026-07-15T01:03:00.000Z") / 1_000),
    line("turn_context", { turn_id: "live-turn", model: "gpt-child" }),
    token([6, 2, 2, 1, 8], [26, 7, 6, 2, 32]),
  ), "fallback", restoredState);

  assert.deepEqual(second.events.map((event) => [event.tokenEventOrdinal, event.turnId, event.model]), [
    [0, "live-turn", "gpt-child"],
  ]);
  assert.deepEqual(second.state.forkReplay, { status: "inactive" });
});

test("uses UUIDv7 time to resolve main fork tasks started in the boundary second", () => {
  const forkTimestamp = "2026-07-15T01:02:30.500Z";
  const boundarySecond = Math.floor(Date.parse(forkTimestamp) / 1_000);
  const result = parseRollout(jsonl(
    line("session_meta", {
      id: uuidV7At(forkTimestamp),
      forked_from_id: "parent",
      thread_source: "user",
    }, forkTimestamp),
    taskStarted(uuidV7At("2026-07-15T01:02:30.100Z"), boundarySecond),
    token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
    taskStarted(uuidV7At(forkTimestamp), boundarySecond),
    token([5, 1, 1, 0, 6], [25, 6, 5, 1, 30]),
    taskStarted(uuidV7At("2026-07-15T01:02:30.900Z"), boundarySecond),
    line("turn_context", { turn_id: uuidV7At("2026-07-15T01:02:30.900Z"), model: "gpt-child" }),
    token([6, 2, 2, 1, 8], [26, 7, 6, 2, 32]),
  ), "fallback");

  assert.deepEqual(result.events.map((event) => [event.tokenEventOrdinal, event.inputTokens]), [[0, 6]]);
});

test("keeps forks with unknown thread attribution unproven", () => {
  const result = parseRolloutChunk(jsonl(
    line("session_meta", {
      id: "unknown-fork",
      forked_from_id: "parent",
      thread_source: "remote",
    }, "2026-07-15T01:02:30.500Z"),
    taskStarted("apparently-live", Date.parse("2026-07-15T01:04:00.000Z") / 1_000),
    token([6, 2, 2, 1, 8], [6, 2, 2, 1, 8]),
  ), "fallback");

  assert.equal(result.events.length, 0);
  assert.deepEqual(result.state.forkReplay, { status: "unproven" });
});

test("keeps main fork replay closed when task time proofs conflict", () => {
  const forkTimestamp = "2026-07-15T01:02:30.500Z";
  const result = parseRollout(jsonl(
    line("session_meta", {
      id: uuidV7At(forkTimestamp),
      forked_from_id: "parent",
      thread_source: "user",
    }, forkTimestamp),
    taskStarted(uuidV7At("2026-07-15T01:02:00.000Z"), Date.parse("2026-07-15T01:03:00.000Z") / 1_000),
    token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
    taskStarted(uuidV7At("2026-07-15T01:04:00.000Z"), Date.parse("2026-07-15T01:04:00.000Z") / 1_000),
    line("turn_context", { turn_id: uuidV7At("2026-07-15T01:04:00.000Z"), model: "gpt-child" }),
    token([6, 2, 2, 1, 8], [26, 7, 6, 2, 32]),
  ), "fallback");

  assert.deepEqual(result.events.map((event) => [event.tokenEventOrdinal, event.inputTokens]), [[0, 6]]);
});

test("uses thread settings as model state across model switches", () => {
  const result = parseRollout(jsonl(
    threadSettings("gpt-a"),
    taskStarted("turn-a"),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    line("event_msg", { type: "task_complete", turn_id: "turn-a" }),
    threadSettings("gpt-b"),
    taskStarted("turn-b"),
    token([5, 1, 3, 2, 8], [9, 2, 5, 3, 14]),
  ), "rollout");

  assert.deepEqual(result.events.map((event) => [event.turnId, event.model]), [
    ["turn-a", "gpt-a"],
    ["turn-b", "gpt-b"],
  ]);
});

test("attributes snapshots in one turn to the model effective when each snapshot was observed", () => {
  const result = parseRollout(jsonl(
    threadSettings("gpt-a"),
    taskStarted("turn-a"),
    line("turn_context", { turn_id: "turn-a", model: "gpt-a" }),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    threadSettings("gpt-b"),
    token([5, 1, 3, 2, 8], [9, 2, 5, 3, 14]),
  ), "rollout");

  assert.deepEqual(result.events.map((event) => [event.turnId, event.model]), [
    ["turn-a", "gpt-a"],
    ["turn-a", "gpt-b"],
  ]);
});

test("keeps turn context authoritative when it arrives after a stale thread setting", () => {
  const result = parseRollout(jsonl(
    threadSettings("gpt-stale"),
    taskStarted("turn-a"),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    line("turn_context", { turn_id: "turn-a", model: "gpt-current" }),
    token([5, 1, 3, 2, 8], [9, 2, 5, 3, 14]),
  ), "rollout");

  assert.deepEqual(result.events.map((event) => [event.turnId, event.model]), [
    ["turn-a", "gpt-current"],
    ["turn-a", "gpt-current"],
  ]);
});

test("does not carry a model-setting override into a turn selected by turn context", () => {
  const result = parseRollout(jsonl(
    line("turn_context", { turn_id: "turn-a", model: "gpt-a" }),
    threadSettings("gpt-b"),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    line("turn_context", { turn_id: "turn-c", model: "gpt-c" }),
    token([5, 1, 3, 2, 8], [9, 2, 5, 3, 14]),
  ), "rollout");

  assert.deepEqual(result.events.map((event) => [event.turnId, event.model]), [
    ["turn-a", "gpt-b"],
    ["turn-c", "gpt-c"],
  ]);
});

test("a new turn never inherits another turn model while waiting for exact metadata", () => {
  const first = parseRolloutChunk(Buffer.from(jsonl(
    line("turn_context", { turn_id: "turn-a", model: "gpt-a" }),
    line("event_msg", { type: "task_started", turn_id: "turn-b" }),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
  )), "rollout");
  assert.equal(first.events[0]?.model, "unknown");
  assert.deepEqual(first.state.unresolvedTurnIds, ["turn-b"]);
  const second = parseRolloutChunk(Buffer.from(jsonl(line("turn_context", { turn_id: "turn-b", model: "gpt-b" }))), "rollout", first.state);
  assert.deepEqual(second.state.unresolvedTurnIds, []);
  assert.deepEqual(second.state.turnModels, [["turn-a", "gpt-a"], ["turn-b", "gpt-b"]]);
});

test("deduplicates only adjacent complete cumulative snapshots", () => {
  const result = parseRollout(jsonl(
    token([10, 2, 3, 1, 13], [10, 2, 3, 1, 13]),
    token([10, 2, 3, 1, 13], [10, 2, 3, 1, 13]),
    token([10, 2, 3, 1, 13], [20, 4, 6, 2, 26]),
  ), "rollout");

  assert.equal(result.events.length, 2);
  assert.deepEqual(result.events.map((event) => event.tokenEventOrdinal), [0, 1]);
  assert.deepEqual(result.events.map((event) => event.cumulativeSnapshot), ["10:2:3:1:13", "20:4:6:2:26"]);
  assert.equal(result.diagnostics.duplicateSnapshotsSkipped, 1);
  assert.notEqual(result.events[0]?.deterministicSignature, result.events[1]?.deterministicSignature);
});

test("rejects missing, negative, fractional, unsafe, and invalid relationship values", () => {
  const validTotal = { input_tokens: 1, cached_input_tokens: 0, output_tokens: 1, reasoning_output_tokens: 0, total_tokens: 2 };
  const badLast = (last_token_usage: Readonly<Record<string, unknown>>) => line("event_msg", { type: "token_count", info: { last_token_usage, total_token_usage: validTotal } });
  const result = parseRollout(jsonl(
    badLast({ input_tokens: 1, cached_input_tokens: 0, output_tokens: 1, total_tokens: 2 }),
    badLast({ input_tokens: -1, cached_input_tokens: 0, output_tokens: 1, reasoning_output_tokens: 0, total_tokens: 0 }),
    badLast({ input_tokens: 1.5, cached_input_tokens: 0, output_tokens: 1, reasoning_output_tokens: 0, total_tokens: 2 }),
    badLast({ input_tokens: Number.MAX_SAFE_INTEGER + 1, cached_input_tokens: 0, output_tokens: 1, reasoning_output_tokens: 0, total_tokens: 2 }),
    token([1, 2, 1, 0, 2], [2, 0, 2, 0, 4]),
    token([1, 0, 1, 2, 2], [3, 0, 3, 0, 6]),
  ), "rollout");

  assert.equal(result.events.length, 0);
  assert.equal(result.diagnostics.invalidTokenUsageLines, 4);
  assert.equal(result.diagnostics.invalidTokenRelationshipsSkipped, 2);
});

test("skips zero breakdowns and ignores an unterminated trailing line", () => {
  const stable = jsonl(token([0, 0, 0, 0, 0], [0, 0, 0, 0, 0]));
  const partial = token([2, 0, 1, 0, 3], [2, 0, 1, 0, 3]).slice(0, -2);
  const input = Buffer.from(stable + partial);
  const before = Buffer.from(input);
  const result = parseRollout(input, "rollout");

  assert.deepEqual(input, before);
  assert.equal(result.stableLineCount, 1);
  assert.equal(result.trailingPartialLine, true);
  assert.equal(result.events.length, 0);
  assert.equal(result.diagnostics.zeroBreakdownSnapshotsSkipped, 1);
});

test("requires an explicit timestamp timezone and accepts offsets", () => {
  const result = parseRollout(jsonl(
    token([1, 0, 1, 0, 2], [1, 0, 1, 0, 2], "2026-07-15T01:02:03"),
    token([1, 0, 1, 0, 2], [1, 0, 1, 0, 2], "2026-07-15T09:02:03+08:00"),
  ), "rollout");

  assert.equal(result.diagnostics.invalidTimestampsSkipped, 1);
  assert.equal(result.events.length, 1);
  assert.equal(result.events[0]?.timestampUtc, "2026-07-15T09:02:03+08:00");
});

test("reports stable buffer offsets in UTF-8 bytes", () => {
  const stableLine = line("event_msg", { type: "agent_message", message: "中文内容" });
  const partialLine = line("event_msg", { type: "agent_message", message: "未完成" }).slice(0, -3);
  const input = Buffer.from(`${stableLine}\n${partialLine}`, "utf8");
  const result = parseRolloutChunk(input, "rollout");

  assert.equal(result.stableLineCount, 1);
  assert.equal(result.stableByteLength, Buffer.byteLength(`${stableLine}\n`, "utf8"));
  assert.equal(result.trailingPartialLine, true);
});

test("continues turn models, snapshot deduplication, and ordinals across chunks", () => {
  const first = parseRolloutChunk(jsonl(
    line("session_meta", { session_id: "conversation", id: "rollout", thread_source: "user" }),
    line("event_msg", { type: "task_started", turn_id: "turn-a" }),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    line("turn_context", { turn_id: "turn-a", model: "gpt-a" }),
  ), "fallback");
  const restoredState = JSON.parse(JSON.stringify(first.state)) as RolloutParserState;
  const second = parseRolloutChunk(jsonl(
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
    token([3, 1, 2, 0, 5], [7, 2, 4, 1, 11]),
    line("event_msg", { type: "task_started", turn_id: "turn-b" }),
    token([5, 2, 3, 1, 8], [12, 4, 7, 2, 19]),
    line("turn_context", { turn_id: "turn-b", model: "gpt-b" }),
  ), "fallback", restoredState);

  assert.deepEqual(first.events.map((event) => [event.tokenEventOrdinal, event.turnId, event.model]), [[0, "turn-a", "gpt-a"]]);
  assert.deepEqual(second.events.map((event) => [event.tokenEventOrdinal, event.turnId, event.model]), [
    [1, "turn-a", "gpt-a"],
    [2, "turn-b", "gpt-b"],
  ]);
  assert.equal(second.diagnostics.duplicateSnapshotsSkipped, 1);
  assert.equal(second.state.nextTokenEventOrdinal, 3);
  assert.equal(second.state.previousSnapshot, "12:4:7:2:19");
  assert.deepEqual(second.state.metadata, first.state.metadata);
});

test("carries an incomplete fork replay proof across incremental chunks", () => {
  const first = parseRolloutChunk(jsonl(
    line("session_meta", {
      id: "child",
      forked_from_id: "parent",
      source: { subagent: { thread_spawn: { parent_thread_id: "parent", agent_path: "/root/worker" } } },
    }),
    taskStarted("replayed-turn"),
    token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
    threadSettings("gpt-child"),
    taskStarted("child-turn"),
    line("turn_context", { turn_id: "child-turn", model: "gpt-child" }),
  ), "fallback");
  assert.equal(first.events.length, 0);
  assert.deepEqual(first.state.forkReplay, {
    status: "awaiting_trigger",
    turnId: "child-turn",
    model: "gpt-child",
  });

  const restoredState = JSON.parse(JSON.stringify(first.state)) as RolloutParserState;
  const second = parseRolloutChunk(jsonl(
    line("inter_agent_communication_metadata", { trigger_turn: true }),
    triggeringAgentMessage("/root/worker", "child-turn"),
    token([6, 2, 2, 1, 8], [26, 7, 6, 2, 32]),
  ), "fallback", restoredState);

  assert.deepEqual(second.events.map((event) => [event.tokenEventOrdinal, event.turnId, event.model]), [
    [0, "child-turn", "gpt-child"],
  ]);
  assert.deepEqual(second.state.forkReplay, { status: "inactive" });
  assert.equal(second.state.currentModel, "gpt-child");
});

test("advances a stable prefix but not its partial tail", () => {
  const initial = parseRolloutChunk(jsonl(
    line("event_msg", { type: "task_started", turn_id: "turn-a" }),
    line("turn_context", { turn_id: "turn-a", model: "gpt-a" }),
    token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
  ), "rollout");
  const partial = `${line("event_msg", { type: "task_started", turn_id: "turn-b" })}\n${token([5, 1, 3, 1, 8], [9, 2, 5, 2, 14])}`;
  const stablePrefixLength = Buffer.byteLength(partial.slice(0, partial.indexOf("\n") + 1), "utf8");
  const result = parseRolloutChunk(partial, "rollout", initial.state);

  assert.equal(result.stableByteLength, stablePrefixLength);
  assert.equal(result.trailingPartialLine, true);
  assert.equal(result.events.length, 0);
  assert.equal(result.state.currentTurnId, "turn-b");
  assert.equal(result.state.previousSnapshot, initial.state.previousSnapshot);
  assert.equal(result.state.nextTokenEventOrdinal, initial.state.nextTokenEventOrdinal);

  const partialOnly = parseRolloutChunk(token([5, 1, 3, 1, 8], [9, 2, 5, 2, 14]), "rollout", initial.state);
  assert.equal(partialOnly.stableByteLength, 0);
  assert.equal(partialOnly.stableLineCount, 0);
  assert.deepEqual(partialOnly.state, initial.state);
});
