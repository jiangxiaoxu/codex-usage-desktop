using System.Text;
using System.Text.Json;
using CodexUsage.Domain;
using Xunit;

namespace CodexUsage.Domain.Tests;

public sealed class RolloutParserTests
{
    [Fact]
    public void ParsesMainAndNestedSubagentMetadataPrecisely()
    {
        var main = RolloutParser.Parse(Jsonl(
            Line("session_meta", new { session_id = "conversation-a", id = "rollout-a", thread_source = "user" }),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-main" }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])), "fallback");
        Assert.Equal(new("conversation-a", "rollout-a", "", ThreadType.Main, "main", "/root", ""), main.Metadata);
        Assert.Equal("gpt-main", Assert.Single(main.Events).Model);

        var child = RolloutParser.Parse(Jsonl(Line("session_meta", new
        {
            session_id = "parent",
            id = "child",
            source = new { subagent = new { thread_spawn = new { parent_thread_id = "parent", agent_role = "worker", agent_path = "/root/worker", agent_nickname = "worker-a" } } },
        })), "fallback");
        Assert.Equal(new("parent", "child", "parent", ThreadType.Subagent, "worker", "/root/worker", "worker-a"), child.Metadata);
    }

    [Fact]
    public void RealtimeVoiceIsMainAndMissingSubagentRoleIsUnknown()
    {
        var voice = RolloutParser.Parse(Jsonl(Line("session_meta", new { id = "voice", thread_source = "realtime_voice" })), "fallback");
        Assert.Equal(ThreadType.Main, voice.Metadata.ThreadType);
        Assert.Equal("main", voice.Metadata.AgentRole);

        var child = RolloutParser.Parse(Jsonl(Line("session_meta", new
        {
            id = "child",
            thread_source = "subagent",
            source = new { subagent = new { thread_spawn = new { agent_path = "/root/worker" } } },
        })), "fallback");
        Assert.Equal("unknown", child.Metadata.AgentRole);
    }

    [Fact]
    public void LateTurnContextsResolveCandidatesWithoutChangingStableSignature()
    {
        var prefix = Jsonl(
            Line("event_msg", new { type = "task_started", turn_id = "turn-a" }),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]));
        var initial = RolloutParser.Parse(prefix, "rollout");
        var enriched = RolloutParser.Parse(prefix + Line("turn_context", new { turn_id = "turn-a", model = "gpt-a" }) + "\n", "rollout");
        Assert.Equal("unknown", Assert.Single(initial.Events).Model);
        Assert.Equal("gpt-a", Assert.Single(enriched.Events).Model);
        Assert.Equal(initial.Events[0].DeterministicSignature, enriched.Events[0].DeterministicSignature);
    }

    [Fact]
    public void ThreadSettingsAreSnapshotLocalButLateExactContextIsAuthoritative()
    {
        var result = RolloutParser.Parse(Jsonl(
            ThreadSettings("gpt-a"),
            TaskStarted("turn-a"),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-current" }),
            Token([5, 1, 3, 2, 8], [9, 2, 5, 3, 14]),
            ThreadSettings("gpt-b"),
            Token([2, 1, 1, 0, 3], [11, 3, 6, 3, 17])), "rollout");
        Assert.Equal(["gpt-current", "gpt-current", "gpt-b"], result.Events.Select(value => value.Model));
    }

    [Fact]
    public void NewTurnNeverInheritsPreviousExactModelAcrossChunks()
    {
        var first = RolloutParser.ParseChunk(Jsonl(
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-a" }),
            TaskStarted("turn-b"),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6])), "rollout");
        Assert.Equal("unknown", Assert.Single(first.Events).Model);
        Assert.Contains("turn-b", first.State.UnresolvedTurnIds);
        var second = RolloutParser.ParseChunk(Jsonl(Line("turn_context", new { turn_id = "turn-b", model = "gpt-b" })), "rollout", first.State);
        Assert.Empty(second.State.UnresolvedTurnIds);
        Assert.Equal("gpt-b", second.State.TurnModels["turn-b"]);
    }

    [Fact]
    public void DeduplicatesOnlyAdjacentCompleteSnapshotsAndMaintainsOrdinalsAcrossChunks()
    {
        var first = RolloutParser.ParseChunk(Jsonl(
            Token([10, 2, 3, 1, 13], [10, 2, 3, 1, 13]),
            Token([10, 2, 3, 1, 13], [10, 2, 3, 1, 13]),
            Token([10, 2, 3, 1, 13], [20, 4, 6, 2, 26])), "rollout");
        Assert.Equal(2, first.Events.Length);
        Assert.Equal(1, first.Diagnostics.DuplicateSnapshotsSkipped);
        var second = RolloutParser.ParseChunk(Jsonl(
            Token([1, 0, 1, 0, 2], [20, 4, 6, 2, 26]),
            Token([2, 0, 1, 0, 3], [22, 4, 7, 2, 29])), "rollout", first.State);
        Assert.Equal(1, second.Diagnostics.DuplicateSnapshotsSkipped);
        Assert.Equal(2L, Assert.Single(second.Events).TokenEventOrdinal);
    }

    [Fact]
    public void RejectsInvalidRuntimeJsonAndTokenRelationships()
    {
        var validTotal = new { input_tokens = 1, cached_input_tokens = 0, output_tokens = 1, reasoning_output_tokens = 0, total_tokens = 2 };
        var missing = Line("event_msg", new { type = "token_count", info = new { last_token_usage = new { input_tokens = 1, cached_input_tokens = 0, output_tokens = 1, total_tokens = 2 }, total_token_usage = validTotal } });
        var negative = Token([-1, 0, 1, 0, 0], [1, 0, 1, 0, 2]);
        var fractional = "{\"timestamp\":\"2026-07-15T01:02:03.004Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1.5,\"cached_input_tokens\":0,\"output_tokens\":1,\"reasoning_output_tokens\":0,\"total_tokens\":2},\"total_token_usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1,\"reasoning_output_tokens\":0,\"total_tokens\":2}}}}";
        var result = RolloutParser.Parse(Jsonl("", "not-json", "[]", missing, negative, fractional,
            Token([1, 2, 1, 0, 2], [2, 0, 2, 0, 4]),
            Token([1, 0, 1, 2, 2], [3, 0, 3, 0, 6])), "rollout");
        Assert.Empty(result.Events);
        Assert.Equal(1, result.Diagnostics.BlankLines);
        Assert.Equal(1, result.Diagnostics.MalformedLines);
        Assert.Equal(1, result.Diagnostics.NonObjectLines);
        Assert.Equal(3, result.Diagnostics.InvalidTokenUsageLines);
        Assert.Equal(2, result.Diagnostics.InvalidTokenRelationshipsSkipped);
    }

    [Fact]
    public void IgnoresPartialTailAndTracksStableUtf8ByteOffset()
    {
        var stable = Line("event_msg", new { type = "agent_message", message = "中文内容" }) + "\n";
        var partial = Line("event_msg", new { type = "agent_message", message = "未完成" })[..^3];
        var bytes = Encoding.UTF8.GetBytes(stable + partial);
        var result = RolloutParser.ParseChunk(bytes, "rollout");
        Assert.True(result.TrailingPartialLine);
        Assert.Equal(1, result.StableLineCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(stable), result.StableByteLength);
    }

    [Fact]
    public void RequiresTimezoneAndAcceptsOffsetTimestamp()
    {
        var result = RolloutParser.Parse(Jsonl(
            Token([1, 0, 1, 0, 2], [1, 0, 1, 0, 2], "2026-07-15T01:02:03"),
            Token([1, 0, 1, 0, 2], [2, 0, 2, 0, 4], "2026-07-15T09:02:03+08:00")), "rollout");
        Assert.Equal(1, result.Diagnostics.InvalidTimestampsSkipped);
        Assert.Equal("2026-07-15T09:02:03+08:00", Assert.Single(result.Events).TimestampUtc);
    }

    [Fact]
    public void SubagentForkReplayStaysClosedUntilAddressedChildTurnIsProven()
    {
        var first = RolloutParser.ParseChunk(Jsonl(
            Line("session_meta", new { id = "child", forked_from_id = "parent", source = new { subagent = new { thread_spawn = new { agent_path = "/root/worker" } } } }),
            TaskStarted("replayed"),
            Token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
            ThreadSettings("gpt-child"),
            TaskStarted("child-turn"),
            Line("turn_context", new { turn_id = "child-turn", model = "gpt-child" })), "fallback");
        Assert.Empty(first.Events);
        Assert.Equal(ForkReplayStatus.AwaitingTrigger, first.State.ForkReplay.Status);

        var second = RolloutParser.ParseChunk(Jsonl(
            Line("inter_agent_communication_metadata", new { trigger_turn = true }),
            Line("response_item", new { type = "agent_message", recipient = "/root/worker", internal_chat_message_metadata_passthrough = new { turn_id = "child-turn" } }),
            Token([6, 2, 2, 1, 8], [26, 7, 6, 2, 32])), "fallback", first.State);
        var usageEvent = Assert.Single(second.Events);
        Assert.Equal("child-turn", usageEvent.TurnId);
        Assert.Equal("gpt-child", usageEvent.Model);
        Assert.Equal(ForkReplayStatus.Inactive, second.State.ForkReplay.Status);
    }

    [Fact]
    public void MainForkSkipsReplayAndOpensOnlyForTimeProvenLiveTask()
    {
        var forkTimestamp = "2026-07-15T01:02:30.500Z";
        var result = RolloutParser.Parse(Jsonl(
            Line("session_meta", new { id = "child", forked_from_id = "parent", thread_source = "user" }, forkTimestamp),
            TaskStarted("replayed", DateTimeOffset.Parse("2026-07-15T01:01:00Z").ToUnixTimeSeconds()),
            Token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
            TaskStarted("live", DateTimeOffset.Parse("2026-07-15T01:03:00Z").ToUnixTimeSeconds()),
            Line("turn_context", new { turn_id = "live", model = "gpt-child" }),
            Token([6, 2, 2, 1, 8], [26, 7, 6, 2, 32])), "fallback");
        var usageEvent = Assert.Single(result.Events);
        Assert.Equal("live", usageEvent.TurnId);
        Assert.Equal(6, usageEvent.InputTokens);
    }

    [Fact]
    public void MainForkUsesUuidV7WithinBoundarySecondAndRejectsConflictingProofs()
    {
        const string forkTimestamp = "2026-07-15T01:02:30.500Z";
        var boundarySeconds = DateTimeOffset.Parse(forkTimestamp).ToUnixTimeSeconds();
        var result = RolloutParser.Parse(Jsonl(
            Line("session_meta", new { id = UuidV7At(forkTimestamp), forked_from_id = "parent", thread_source = "user" }, forkTimestamp),
            TaskStarted(UuidV7At("2026-07-15T01:02:30.100Z"), boundarySeconds),
            Token([20, 5, 4, 1, 24], [20, 5, 4, 1, 24]),
            TaskStarted(UuidV7At("2026-07-15T01:02:00.000Z"), boundarySeconds + 30),
            Token([5, 1, 1, 0, 6], [25, 6, 5, 1, 30]),
            TaskStarted(UuidV7At("2026-07-15T01:02:30.900Z"), boundarySeconds),
            Line("turn_context", new { turn_id = UuidV7At("2026-07-15T01:02:30.900Z"), model = "gpt-child" }),
            Token([6, 2, 2, 1, 8], [31, 8, 7, 2, 38])), "fallback");
        Assert.Equal(6, Assert.Single(result.Events).InputTokens);
    }

    [Fact]
    public void UnknownForkAttributionRemainsPermanentlyUnproven()
    {
        var result = RolloutParser.ParseChunk(Jsonl(
            Line("session_meta", new { id = "unknown-fork", forked_from_id = "parent", thread_source = "remote" }),
            TaskStarted("apparently-live", DateTimeOffset.Parse("2026-07-15T01:04:00Z").ToUnixTimeSeconds()),
            Token([6, 2, 2, 1, 8], [6, 2, 2, 1, 8])), "fallback");
        Assert.Empty(result.Events);
        Assert.Equal(ForkReplayStatus.Unproven, result.State.ForkReplay.Status);
    }

    [Fact]
    public async Task CooperativeParserYieldsByByteAndRecordBudgetWithoutChangingResult()
    {
        var ignored = Enumerable.Range(0, 200).Select(index => Line("event_msg", new { type = "ignored", index, padding = new string('x', 64) }));
        var lines = new[] { Line("session_meta", new { id = "cooperative", thread_source = "user" }), TaskStarted("late-turn"), Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]) }
            .Concat(ignored).Append(Line("turn_context", new { turn_id = "late-turn", model = "gpt-late" }));
        var input = Encoding.UTF8.GetBytes(Jsonl(lines.ToArray()));
        var expected = RolloutParser.ParseChunk(input, "fallback");
        var yields = 0;
        var actual = await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
            512, 10, TimeSpan.FromMilliseconds(8), RolloutParser.CooperativeHardMaximumRecordBytes, _ =>
        {
            yields++;
            return ValueTask.CompletedTask;
        }));
        Assert.True(yields > 10);
        Assert.Equal(expected.Metadata, actual.Metadata);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        Assert.Equal("gpt-late", Assert.Single(actual.Events).Model);
    }

    [Fact]
    public async Task CooperativeParserSkipsRecordsBeyondTheHardParseBoundAndContinues()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(
            Line("event_msg", new { type = "ignored", padding = new string('x', 2 * 1024) }),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6])));
        var yields = 0;
        var result = await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
            512, 20, TimeSpan.FromMilliseconds(8), 1024, _ =>
        {
            yields++;
            return ValueTask.CompletedTask;
        }));
        Assert.True(yields > 2);
        Assert.Single(result.Events);
        Assert.Equal(1, result.Diagnostics.OversizedRecordsSkipped);
    }

    [Fact]
    public async Task CooperativeParserCancelsBetweenProcessedRecords()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Enumerable.Range(0, 100)
            .Select(index => Line("event_msg", new { type = "ignored", index })).ToArray()));
        using var cancellation = new CancellationTokenSource();
        var yields = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
                64 * 1024, 3, TimeSpan.FromMinutes(1), RolloutParser.CooperativeHardMaximumRecordBytes, _ =>
                {
                    yields++;
                    if (yields == 2) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }), cancellationToken: cancellation.Token));
        Assert.Equal(2, yields);
    }

    [Fact]
    public async Task CooperativeParserUsesTimeBudgetEvenWhenByteAndRecordBudgetsAreNotReached()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Enumerable.Range(0, 20)
            .Select(index => Line("event_msg", new { type = "ignored", index })).ToArray()));
        var yields = 0;
        await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
            64 * 1024, 10_000, TimeSpan.FromTicks(1), RolloutParser.CooperativeHardMaximumRecordBytes, _ =>
            {
                yields++;
                return ValueTask.CompletedTask;
            }));
        Assert.True(yields > 1);
    }

    [Fact]
    public async Task CooperativeParserRejectsAConfiguredRecordLimitAboveTheHardBound()
    {
        var options = new CooperativeParseOptions(
            1024, 10, TimeSpan.FromMilliseconds(8), RolloutParser.CooperativeHardMaximumRecordBytes + 1,
            _ => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await RolloutParser.ParseChunkCooperativelyAsync(ReadOnlyMemory<byte>.Empty, "fallback", options));
    }

    private static string Line(string type, object payload, string timestamp = "2026-07-15T01:02:03.004Z") =>
        JsonSerializer.Serialize(new { timestamp, type, payload });

    private static string Token(long[] last, long[] total, string timestamp = "2026-07-15T01:02:03.004Z") =>
        Line("event_msg", new
        {
            type = "token_count",
            info = new
            {
                last_token_usage = Tuple(last),
                total_token_usage = Tuple(total),
            },
        }, timestamp);

    private static object Tuple(long[] values) => new
    {
        input_tokens = values[0],
        cached_input_tokens = values[1],
        output_tokens = values[2],
        reasoning_output_tokens = values[3],
        total_tokens = values[4],
    };

    private static string TaskStarted(string turnId, long? startedAt = null) => startedAt is null
        ? Line("event_msg", new { type = "task_started", turn_id = turnId })
        : Line("event_msg", new { type = "task_started", turn_id = turnId, started_at = startedAt });

    private static string ThreadSettings(string model) =>
        Line("event_msg", new { type = "thread_settings_applied", thread_settings = new { model } });

    private static string UuidV7At(string timestamp)
    {
        var epochHex = DateTimeOffset.Parse(timestamp).ToUnixTimeMilliseconds().ToString("x12");
        return $"{epochHex[..8]}-{epochHex[8..]}-7000-8000-000000000000";
    }

    private static string Jsonl(params string[] lines) => string.Join('\n', lines) + "\n";
}
