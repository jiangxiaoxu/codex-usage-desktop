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
        Assert.Equal(new(
            "conversation-a", "rollout-a", "", ThreadType.Main, "main", "/root", "", false, "Codex", "",
            DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds()), main.Metadata);
        Assert.Equal("gpt-main", Assert.Single(main.Events).Model);

        var child = RolloutParser.Parse(Jsonl(Line("session_meta", new
        {
            session_id = "parent",
            id = "child",
            source = new { subagent = new { thread_spawn = new { parent_thread_id = "parent", agent_role = "worker", agent_path = "/root/worker", agent_nickname = "worker-a" } } },
        })), "fallback");
        Assert.Equal(new(
            "parent", "child", "parent", ThreadType.Subagent, "worker", "/root/worker", "worker-a", false, "Codex", "",
            DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds()), child.Metadata);
    }

    [Fact]
    public void RealtimeVoiceIsMainAndMissingSubagentRoleIsUnknown()
    {
        var voice = RolloutParser.Parse(Jsonl(
            Line("session_meta", new { id = "voice", thread_source = "realtime_voice" }),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6])), "fallback");
        Assert.Equal(ThreadType.Main, voice.Metadata.ThreadType);
        Assert.Equal("main", voice.Metadata.AgentRole);
        Assert.True(voice.Metadata.IsRealtimeVoice);
        Assert.Empty(voice.Events);

        var child = RolloutParser.Parse(Jsonl(Line("session_meta", new
        {
            id = "child",
            thread_source = "subagent",
            source = new { subagent = new { thread_spawn = new { agent_path = "/root/worker" } } },
        })), "fallback");
        Assert.Equal("unknown", child.Metadata.AgentRole);
    }

    [Fact]
    public void GuardianReviewUsesItsExactThreadSourceAndGuardianRole()
    {
        var guardian = RolloutParser.Parse(Jsonl(Line("session_meta", new
        {
            session_id = "parent",
            id = "guardian",
            parent_thread_id = "parent-rollout",
            thread_source = "guardian_review",
            source = new { subagent = new { other = "guardian" } },
        })), "fallback");
        var unrelatedModel = RolloutParser.Parse(Jsonl(Line("session_meta", new
        {
            id = "not-guardian",
            thread_source = "user",
        }), ThreadSettings("codex-auto-review")), "fallback");

        Assert.Equal(ThreadType.GuardianReview, guardian.Metadata.ThreadType);
        Assert.Equal("guardian", guardian.Metadata.AgentRole);
        Assert.Equal("/root", guardian.Metadata.AgentPath);
        Assert.Equal(ThreadType.Main, unrelatedModel.Metadata.ThreadType);
    }

    [Fact]
    public void AgentCreatedThreadIsMainRoot()
    {
        var result = RolloutParser.Parse(Jsonl(
            Line("session_meta", new
            {
                session_id = "conversation-agent-created",
                id = "rollout-agent-created",
                thread_source = "agent_created_thread",
            })), "fallback");

        Assert.Equal(ThreadType.Main, result.Metadata.ThreadType);
        Assert.Equal("main", result.Metadata.AgentRole);
        Assert.Equal("/root", result.Metadata.AgentPath);
        Assert.False(result.Metadata.IsRealtimeVoice);
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
    public void DoesNotTreatUserMessagesAsThreadTitlesAndTracksLatestAcceptedActivity()
    {
        var result = RolloutParser.Parse(Jsonl(
            Line("session_meta", new { session_id = "conversation", id = "rollout", thread_source = "user" }, "2026-07-15T01:00:00Z"),
            Line("event_msg", new { type = "user_message", message = "  first\n  request  " }, "2026-07-15T01:01:00Z"),
            Line("event_msg", new { type = "user_message", message = "later request" }, "2026-07-15T01:02:00Z"),
            Line("response_item", new { type = "message", content = "later response" }, "2026-07-15T01:03:00Z")), "fallback");

        Assert.Empty(result.Metadata.ThreadTitle);
        Assert.Equal(DateTimeOffset.Parse("2026-07-15T01:03:00Z").ToUnixTimeMilliseconds(), result.Metadata.LastActivityEpochMs);
    }

    [Fact]
    public void SessionMetadataCwdProvidesProjectNameFallback()
    {
        var result = RolloutParser.Parse(Jsonl(
            Line("session_meta", new
            {
                session_id = "conversation",
                id = "rollout",
                thread_source = "user",
                cwd = @"E:\\Project\\codex-usage-desktop\\",
            })), "fallback");

        Assert.Equal("codex-usage-desktop", result.Metadata.ProjectName);
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
        var oversized = Assert.Single(result.Diagnostics.OversizedRecords);
        Assert.Equal(OversizedRecordDisposition.UnsafeUnclassified, oversized.Disposition);
    }

    [Fact]
    public async Task OversizedOpaqueResponseItemIsFullyValidatedAndAccountingContinues()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(
            Line("session_meta", new { id = "oversized-safe", thread_source = "user" }),
            Line("turn_context", new { turn_id = "turn-safe", model = "gpt-5.6-sol" }),
            Line("response_item", new { type = "custom_tool_call_output", output = new string('x', 2 * 1024) }),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6])));

        var result = await ParseWithRecordLimitAsync(input, 1024);

        var usage = Assert.Single(result.Events);
        Assert.Equal("gpt-5.6-sol", usage.Model);
        var oversized = Assert.Single(result.Diagnostics.OversizedRecords);
        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped, oversized.Disposition);
        Assert.Equal(OversizedRecordKind.ResponseItemOpaque, oversized.Kind);
    }

    [Theory]
    [InlineData("session_meta", "ignored", OversizedRecordKind.SessionMetadata)]
    [InlineData("turn_context", "ignored", OversizedRecordKind.TurnContext)]
    [InlineData("inter_agent_communication_metadata", "ignored", OversizedRecordKind.InterAgentCommunicationMetadata)]
    [InlineData("event_msg", "token_count", OversizedRecordKind.TokenCount)]
    [InlineData("event_msg", "thread_settings_applied", OversizedRecordKind.EventMessageContext)]
    [InlineData("event_msg", "task_started", OversizedRecordKind.EventMessageContext)]
    [InlineData("event_msg", "task_complete", OversizedRecordKind.EventMessageContext)]
    [InlineData("response_item", "agent_message", OversizedRecordKind.ResponseItemAgentMessage)]
    public async Task OversizedAccountingOrAttributionRecordsAreUnsafe(
        string eventType,
        string payloadType,
        OversizedRecordKind expectedKind)
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Line(eventType, new
        {
            type = payloadType,
            padding = new string('x', 2 * 1024),
        })));

        var result = await ParseWithRecordLimitAsync(input, 1024);

        var oversized = Assert.Single(result.Diagnostics.OversizedRecords);
        Assert.Equal(OversizedRecordDisposition.UnsafeCritical, oversized.Disposition);
        Assert.Equal(expectedKind, oversized.Kind);
    }

    [Fact]
    public async Task OversizedUnknownAndMalformedRecordsRemainUnsafe()
    {
        var unknown = Encoding.UTF8.GetBytes(Jsonl(Line("future_record", new
        {
            type = "future_payload",
            padding = new string('x', 2 * 1024),
        })));
        var malformed = Encoding.UTF8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\""
            + new string('x', 2 * 1024) + "\"}\n");

        var unknownResult = await ParseWithRecordLimitAsync(unknown, 1024);
        var malformedResult = await ParseWithRecordLimitAsync(malformed, 1024);

        Assert.Equal(OversizedRecordDisposition.UnsafeUnclassified,
            Assert.Single(unknownResult.Diagnostics.OversizedRecords).Disposition);
        Assert.Equal(OversizedRecordDisposition.Malformed,
            Assert.Single(malformedResult.Diagnostics.OversizedRecords).Disposition);
    }

    [Theory]
    [InlineData("compacted", "ignored", OversizedRecordKind.Compacted)]
    [InlineData("event_msg", "image_generation_end", OversizedRecordKind.ImageGenerationEnd)]
    [InlineData("event_msg", "mcp_tool_call_end", OversizedRecordKind.McpToolCallEnd)]
    public async Task KnownOpaqueOversizedRecordsAreSafe(
        string eventType,
        string payloadType,
        OversizedRecordKind expectedKind)
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Line(eventType, new
        {
            type = payloadType,
            padding = new string('x', 2 * 1024),
        })));

        var result = await ParseWithRecordLimitAsync(input, 1024);

        var oversized = Assert.Single(result.Diagnostics.OversizedRecords);
        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped, oversized.Disposition);
        Assert.Equal(expectedKind, oversized.Kind);
    }

    [Fact]
    public async Task OversizedMcpToolCallEndIsFullyValidatedAndAccountingContinues()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(
            Line("session_meta", new { id = "mcp-safe", thread_source = "user" }),
            Line("turn_context", new { turn_id = "turn-mcp", model = "gpt-5.6-sol" }),
            Line("event_msg", new { type = "mcp_tool_call_end", result = new string('x', 1024 * 1024 + 1) }),
            Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6])));

        var result = await ParseWithRecordLimitAsync(input, 1024);

        var usage = Assert.Single(result.Events);
        Assert.Equal("gpt-5.6-sol", usage.Model);
        var oversized = Assert.Single(result.Diagnostics.OversizedRecords);
        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped, oversized.Disposition);
        Assert.Equal(OversizedRecordKind.McpToolCallEnd, oversized.Kind);
    }

    [Fact]
    public async Task ExactNullPaddingRecordIsSkippedButMixedNullContentRemainsMalformed()
    {
        var prefix = Encoding.UTF8.GetBytes(Jsonl(
            Line("session_meta", new { id = "null-padding", thread_source = "user" }),
            Line("turn_context", new { turn_id = "turn-null", model = "gpt-5.6-sol" })));
        var token = Encoding.UTF8.GetBytes(Token([4, 1, 2, 1, 6], [4, 1, 2, 1, 6]) + "\n");
        var safe = prefix.Concat(new byte[3411]).Append((byte)'\n').Concat(token).ToArray();
        var mixed = prefix.Concat(new byte[3411]).Append((byte)' ').Append((byte)'\n').Concat(token).ToArray();

        var safeResult = await ParseWithRecordLimitAsync(safe, 1024);
        var mixedResult = await ParseWithRecordLimitAsync(mixed, 1024);

        Assert.Equal(1, safeResult.Diagnostics.SafeNullPaddingRecordsSkipped);
        Assert.Equal(0, safeResult.Diagnostics.MalformedLines);
        Assert.Equal("gpt-5.6-sol", Assert.Single(safeResult.Events).Model);
        Assert.Equal(0, mixedResult.Diagnostics.SafeNullPaddingRecordsSkipped);
        Assert.Equal(OversizedRecordDisposition.Malformed,
            Assert.Single(mixedResult.Diagnostics.OversizedRecords).Disposition);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("reasoning")]
    [InlineData("local_shell_call")]
    [InlineData("function_call")]
    [InlineData("tool_search_call")]
    [InlineData("function_call_output")]
    [InlineData("tool_search_output")]
    [InlineData("custom_tool_call")]
    [InlineData("custom_tool_call_output")]
    [InlineData("web_search_call")]
    [InlineData("image_generation_call")]
    [InlineData("compaction")]
    [InlineData("compaction_summary")]
    [InlineData("context_compaction")]
    public async Task PersistedNonAccountingResponseItemTypesAreSafe(string payloadType)
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Line("response_item", new
        {
            type = payloadType,
            padding = new string('x', 2 * 1024),
        })));

        var result = await ParseWithRecordLimitAsync(input, 1024);

        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped,
            Assert.Single(result.Diagnostics.OversizedRecords).Disposition);
    }

    [Fact]
    public async Task OversizedImageGenerationResultYieldsAndRemainsSafe()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Line("response_item", new
        {
            type = "image_generation_call",
            status = "completed",
            result = new string('x', 2 * 1024 * 1024),
        })));
        var yields = 0;

        var result = await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
            64 * 1024, 20, TimeSpan.FromMilliseconds(8), 1024, _ =>
            {
                yields++;
                return ValueTask.CompletedTask;
            }));

        Assert.True(yields > 2);
        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped,
            Assert.Single(result.Diagnostics.OversizedRecords).Disposition);
    }

    [Fact]
    public async Task OversizedClassificationCanCancelWhileExpandingALargeValue()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Line("response_item", new
        {
            type = "image_generation_call",
            result = new string('x', 2 * 1024 * 1024),
        })));
        using var cancellation = new CancellationTokenSource();
        var yields = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
                64 * 1024, 20, TimeSpan.FromMilliseconds(8), 1024, _ =>
                {
                    if (++yields == 2) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }), cancellationToken: cancellation.Token));

        Assert.Equal(2, yields);
    }

    [Fact]
    public async Task OversizedClassificationCanCancelInsideASixteenMegabyteString()
    {
        var input = Encoding.UTF8.GetBytes(Jsonl(Line("response_item", new
        {
            type = "image_generation_call",
            result = new string('x', 16 * 1024 * 1024),
        })));
        using var cancellation = new CancellationTokenSource();
        var yields = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
                64 * 1024, 20, TimeSpan.FromMilliseconds(8), 1024, _ =>
                {
                    if (++yields == 4) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }), cancellationToken: cancellation.Token));

        Assert.Equal(4, yields);
    }

    [Fact]
    public async Task OversizedMalformedEscapeAndUtf8AreRejectedAfterCooperativeValidation()
    {
        var badEscape = Encoding.UTF8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\""
            + new string('x', 2 * 1024) + "\\q\"}}\n");
        var prefix = Encoding.UTF8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}}\n");
        var badUtf8 = prefix.Concat(Enumerable.Repeat((byte)'x', 2 * 1024))
            .Concat([(byte)0xC3, (byte)0x28]).Concat(suffix).ToArray();

        var escapeResult = await ParseWithRecordLimitAsync(badEscape, 1024);
        var utf8Result = await ParseWithRecordLimitAsync(badUtf8, 1024);

        Assert.Equal(OversizedRecordDisposition.Malformed,
            Assert.Single(escapeResult.Diagnostics.OversizedRecords).Disposition);
        Assert.Equal(OversizedRecordDisposition.Malformed,
            Assert.Single(utf8Result.Diagnostics.OversizedRecords).Disposition);
    }

    [Theory]
    [InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"v\":[true,false,null,-12.5e+2],\"s\":\"\\uD800\\n\"}}")]
    [InlineData("{\"payload\":{\"type\":\"message\"},\"type\":\"response_item\"}")]
    [InlineData("[1,{\"a\":2},3]")]
    [InlineData("{\"a\":01}")]
    [InlineData("{\"a\":1.}")]
    [InlineData("{\"a\":1e}")]
    [InlineData("{\"a\":tru}")]
    [InlineData("{\"a\":true,}")]
    [InlineData("{\"a\" 1}")]
    [InlineData("{\"a\":\"\\q\"}")]
    [InlineData("{\"a\":[1 2]}")]
    public async Task OversizedLexicalAndStructuralAcceptanceMatchesSystemTextJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        var expectedValid = IsValidSystemTextJson(Encoding.UTF8.GetBytes(json));

        var result = await ParseWithRecordLimitAsync(bytes, 1);
        var actualValid = Assert.Single(result.Diagnostics.OversizedRecords).Disposition
            != OversizedRecordDisposition.Malformed;

        Assert.Equal(expectedValid, actualValid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task OversizedSanitizedCarryPreservesTokensAcrossTinyFlushBoundaries(int sliceBytes)
    {
        var input = Encoding.UTF8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\","
            + "\"number\":-12.5e+2,\"literal\":true,\"string\":\"value\",\"array\":[null,false],"
            + $"\"content\":\"{new string('x', 2 * 1024)}\"}}}}\n");

        var result = await ParseWithSliceAsync(input, sliceBytes, 1);

        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped,
            Assert.Single(result.Diagnostics.OversizedRecords).Disposition);
    }

    [Fact]
    public async Task OversizedSanitizedCarryPreservesPropertyBeforeColonAtProductionSliceBoundary()
    {
        const int sliceBytes = 256 * 1024;
        const int zeroCount = 131_067;
        var json = new StringBuilder(sliceBytes + 32);
        json.Append("{\"p\":[");
        for (var index = 0; index < zeroCount; index++)
        {
            if (index > 0) json.Append(',');
            json.Append('0');
        }
        json.Append("],\"a\":1}\n");
        var input = Encoding.UTF8.GetBytes(json.ToString());

        var result = await ParseWithSliceAsync(input, sliceBytes, 1);

        Assert.Equal(OversizedRecordDisposition.UnsafeUnclassified,
            Assert.Single(result.Diagnostics.OversizedRecords).Disposition);
    }

    [Fact]
    public async Task OversizedPayloadTypeMustBeADirectChildOfTheUniquePayloadObject()
    {
        var padding = new string('x', 2 * 1024);
        var siblingSpoof = Encoding.UTF8.GetBytes(
            $"{{\"type\":\"response_item\",\"payload\":{{\"content\":\"{padding}\"}},\"sibling\":{{\"type\":\"message\"}}}}\n");
        var nestedThenDirect = Encoding.UTF8.GetBytes(
            $"{{\"payload\":{{\"nested\":{{\"type\":\"future\"}},\"type\":\"message\",\"content\":\"{padding}\"}},\"type\":\"response_item\"}}\n");

        var spoofResult = await ParseWithRecordLimitAsync(siblingSpoof, 1024);
        var orderedResult = await ParseWithRecordLimitAsync(nestedThenDirect, 1024);

        Assert.Equal(OversizedRecordDisposition.UnsafeUnclassified,
            Assert.Single(spoofResult.Diagnostics.OversizedRecords).Disposition);
        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped,
            Assert.Single(orderedResult.Diagnostics.OversizedRecords).Disposition);
    }

    [Theory]
    [InlineData("{\"type\":\"response_item\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\"__PADDING__\"}}")]
    [InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"type\":\"message\",\"content\":\"__PADDING__\"}}")]
    [InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\"__PADDING__\"},\"payload\":{\"type\":\"message\"}}")]
    public async Task OversizedDuplicateDiscriminatorsAreUnsafe(string template)
    {
        var input = Encoding.UTF8.GetBytes(
            template.Replace("__PADDING__", new string('x', 2 * 1024), StringComparison.Ordinal) + "\n");

        var result = await ParseWithRecordLimitAsync(input, 1024);

        Assert.Equal(OversizedRecordDisposition.UnsafeUnclassified,
            Assert.Single(result.Diagnostics.OversizedRecords).Disposition);
    }

    [Fact]
    public async Task OversizedEscapedDiscriminatorNamesAndValuesAreRecognized()
    {
        var input = Encoding.UTF8.GetBytes(
            $"{{\"t\\u0079pe\":\"response_\\u0069tem\",\"payl\\u006fad\":{{\"t\\u0079pe\":\"mess\\u0061ge\",\"content\":\"{new string('x', 2 * 1024)}\"}}}}\n");

        var result = await ParseWithRecordLimitAsync(input, 1024);

        Assert.Equal(OversizedRecordDisposition.SafeOpaqueSkipped,
            Assert.Single(result.Diagnostics.OversizedRecords).Disposition);
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

    private static ValueTask<RolloutChunkParseResult> ParseWithRecordLimitAsync(byte[] input, int maximumRecordBytes) =>
        RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
            512, 20, TimeSpan.FromMilliseconds(8), maximumRecordBytes, _ => ValueTask.CompletedTask));

    private static ValueTask<RolloutChunkParseResult> ParseWithSliceAsync(
        byte[] input,
        int sliceBytes,
        int maximumRecordBytes) =>
        RolloutParser.ParseChunkCooperativelyAsync(input, "fallback", new(
            sliceBytes, 20, TimeSpan.FromMilliseconds(8), maximumRecordBytes, _ => ValueTask.CompletedTask));

    private static bool IsValidSystemTextJson(byte[] input)
    {
        try
        {
            using var _ = JsonDocument.Parse(input);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
