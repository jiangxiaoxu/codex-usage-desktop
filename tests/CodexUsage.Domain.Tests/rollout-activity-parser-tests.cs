using System.Text;
using System.Text.Json;
using CodexUsage.Domain;
using Xunit;

namespace CodexUsage.Domain.Tests;

public sealed class RolloutActivityParserTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JoinsReasoningInEitherOrderAcrossPersistedChunks(bool payloadFirst)
    {
        var timing = Completed("r", "Reasoning");
        var volume = Reasoning("r", new string('a', 1200));
        var first = RolloutParser.ParseChunk(Setup() + (payloadFirst ? volume : timing), "thread");
        Assert.Empty(first.Activities);
        Assert.True(RolloutParserStateCodec.TryDeserialize(RolloutParserStateCodec.Serialize(first.State), out var restored, out var error), error);
        var second = RolloutParser.ParseChunk(payloadFirst ? timing : volume, "thread", restored);
        var sample = Assert.Single(second.Activities);
        Assert.Equal(ModelActivityKind.Reasoning, sample.Kind);
        Assert.Equal(1200, sample.RawPayloadBytes);
        Assert.Equal(250, sample.EstimatedContentBytes);
        Assert.Equal(ReasoningEffort.XHigh, sample.Effort);
        Assert.Equal(ServiceTier.Standard, sample.ServiceTier);
        Assert.Equal("gpt-6.1-sol", sample.Model);
        Assert.Equal(0, sample.ActivityOrdinal);
        Assert.Empty(second.State.ActivitiesState.PendingReasoning);
        Assert.Empty(RolloutParser.ParseChunk(timing + volume, "thread", second.State).Activities);
    }

    [Fact]
    public void CountsTextUtf8BytesAndPreservesIndividualEffortSnapshots()
    {
        var result = RolloutParser.ParseChunk(Setup() + Completed("a", "AgentMessage", "中文🙂")
            + Line("event_msg", new { type = "thread_settings_applied", thread_id = "thread", thread_settings = new { service_tier = "default", reasoning_effort = "high" } })
            + Completed("b", "AgentMessage", "ok")
            + Configuration("high", false) + Completed("c", "AgentMessage", "ok")
            + Configuration("high", true), "thread");
        Assert.True(RolloutParserStateCodec.TryDeserialize(RolloutParserStateCodec.Serialize(result.State), out var restored, out var error), error);
        var next = RolloutParser.ParseChunk(Completed("d", "AgentMessage", "ok"), "thread", restored);
        Assert.Equal(Encoding.UTF8.GetByteCount("中文🙂"), result.Activities[0].RawPayloadBytes);
        Assert.Equal(new[] { ReasoningEffort.XHigh, ReasoningEffort.XHigh, ReasoningEffort.XHigh, ReasoningEffort.High },
            result.Activities.Concat(next.Activities).Select(value => value.Effort));
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(1000, 1000)]
    [InlineData(2000, 1000)]
    [InlineData(1000, 253402300800000)]
    public void InvalidTimingDoesNotProduceZeroDurationSamples(long start, long end)
    {
        Assert.Empty(RolloutParser.Parse(Setup() + Completed("a", "AgentMessage", "body", start: start, end: end)
            + Completed("r", "Reasoning", start: start, end: end) + Reasoning("r", new string('a', 1200)), "thread").Activities);
    }

    [Fact]
    public void MissingAndZeroReasoningVolumeRemainUnavailable()
    {
        var result = RolloutParser.ParseChunk(Setup() + Completed("missing", "Reasoning")
            + Completed("zero", "Reasoning") + Reasoning("zero", "short")
            + Completed("text", "AgentMessage", ""), "thread");
        Assert.Empty(result.Activities);
        Assert.Equal("missing", Assert.Single(result.State.ActivitiesState.PendingReasoning).ItemId);
    }

    [Fact]
    public void OwnThreadIdIsIndependentOfRootConversationAndForkReplayIsExcluded()
    {
        var metadata = Line("session_meta", new { id = "child", session_id = "parent", source = new { subagent = new { thread_spawn = new { parent_thread_id = "parent", agent_path = "/root/worker" } } } });
        var result = RolloutParser.Parse(metadata + Context() + Completed("parent-item", "AgentMessage", "body", "parent")
            + Completed("child-item", "AgentMessage", "body", "child"), "child");
        var sample = Assert.Single(result.Activities);
        Assert.Equal("child", sample.ThreadId);
        Assert.Equal("parent", sample.ConversationId);
        Assert.Empty(RolloutParser.Parse(Line("session_meta", new { id = "thread", forked_from_id = "parent", thread_source = "remote" })
            + Context() + Completed("r", "Reasoning") + Reasoning("r", new string('a', 1200)), "thread").Activities);
        Assert.Empty(RolloutParser.Parse(Line("session_meta", new { id = "thread", thread_source = "realtime_voice" })
            + Context() + Completed("v", "AgentMessage", "body"), "thread").Activities);
    }

    [Fact]
    public void MidTurnTierChangeMakesWholeActivityTurnUnknownAndRequestsStoredRepair()
    {
        var first = RolloutParser.ParseChunk(Setup() + Completed("a", "AgentMessage", "body"), "thread");
        var second = RolloutParser.ParseChunk(Line("event_msg", new { type = "thread_settings_applied", thread_id = "thread", thread_settings = new { service_tier = "priority" } })
            + Completed("b", "AgentMessage", "body"), "thread", first.State);
        Assert.True(second.State.ActivitiesState.RequiresReparse);
        Assert.Equal(ServiceTier.Unknown, Assert.Single(second.Activities).ServiceTier);
        var full = RolloutParser.Parse(Setup() + Completed("a", "AgentMessage", "body")
            + Line("event_msg", new { type = "thread_settings_applied", thread_id = "thread", thread_settings = new { service_tier = "priority" } })
            + Completed("b", "AgentMessage", "body"), "thread");
        Assert.All(full.Activities, value => Assert.Equal(ServiceTier.Unknown, value.ServiceTier));
    }

    [Fact]
    public void LateContextRepairsActivitiesWithoutApplyingLaterConfigurationRetroactively()
    {
        var first = RolloutParser.ParseChunk(Line("session_meta", new { id = "thread", thread_source = "user" })
            + Line("event_msg", new { type = "task_started", turn_id = "turn" })
            + Completed("a", "AgentMessage", "body"), "thread");
        Assert.Equal("unknown", Assert.Single(first.Activities).Model);
        var next = RolloutParser.ParseChunk(Context(), "thread", first.State);
        Assert.True(next.State.ActivitiesState.RequiresReparse);
        var full = RolloutParser.Parse(Line("session_meta", new { id = "thread", thread_source = "user" })
            + Line("event_msg", new { type = "task_started", turn_id = "turn" })
            + Completed("a", "AgentMessage", "body") + Context() + Configuration("high", true)
            + Completed("b", "AgentMessage", "body"), "thread");
        Assert.Equal("gpt-6.1-sol", full.Activities[0].Model);
        Assert.Equal(ReasoningEffort.XHigh, full.Activities[0].Effort);
        Assert.Equal(ReasoningEffort.High, full.Activities[1].Effort);
    }

    [Fact]
    public void ExplicitUnknownEffortOverrideIsNotReplacedByRepeatedTurnContext()
    {
        var result = RolloutParser.Parse(Setup() + Configuration("unsupported", true)
            + Completed("a", "AgentMessage", "body") + Context()
            + Completed("b", "AgentMessage", "body"), "thread");
        Assert.All(result.Activities, sample => Assert.Equal(ReasoningEffort.Unknown, sample.Effort));
    }

    [Fact]
    public void MidTurnModelDefaultChangeMakesActivityModelUnknownAndRepairsStoredSamples()
    {
        var initial = Setup() + Line("event_msg", new
        {
            type = "thread_settings_applied",
            thread_id = "foreign",
            thread_settings = new { service_tier = "default", model = "gpt-6.1-luna" }
        })
            + Completed("a", "AgentMessage", "body");
        var first = RolloutParser.ParseChunk(initial, "thread");
        Assert.Equal("gpt-6.1-sol", Assert.Single(first.Activities).Model);
        var changed = Line("event_msg", new
        {
            type = "thread_settings_applied",
            thread_id = "thread",
            thread_settings = new { service_tier = "default", model = "gpt-6.1-luna" }
        });
        var next = RolloutParser.ParseChunk(changed + Completed("b", "AgentMessage", "body"), "thread", first.State);
        Assert.True(next.State.ActivitiesState.RequiresReparse);
        Assert.Equal("unknown", Assert.Single(next.Activities).Model);
        var full = RolloutParser.Parse(initial + changed + Completed("b", "AgentMessage", "body"), "thread");
        Assert.All(full.Activities, sample =>
        {
            Assert.Equal("unknown", sample.Model);
            Assert.Equal(ServiceTier.Standard, sample.ServiceTier);
        });
    }

    private static string Setup() => Line("session_meta", new { id = "thread", thread_source = "user" })
        + Line("event_msg", new { type = "thread_settings_applied", thread_id = "thread", thread_settings = new { service_tier = "default" } }) + Context();
    private static string Context() => Line("turn_context", new { turn_id = "turn", model = "gpt-6.1-sol", effort = "xhigh" });
    private static string Completed(string id, string kind, string text = "", string owner = "thread", long start = 1000, long end = 2000) =>
        Line("event_msg", new
        {
            type = "item_completed",
            thread_id = owner,
            turn_id = "turn",
            started_at_ms = start,
            completed_at_ms = end,
            item = new { type = kind, id, content = new[] { new { type = "Text", text } } }
        });
    private static string Reasoning(string id, string encrypted) => Line("response_item", new
    {
        type = "reasoning",
        id,
        encrypted_content = encrypted,
        internal_chat_message_metadata_passthrough = new { turn_id = "turn" }
    });
    private static string Configuration(string effort, bool trusted) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-10-03T12:00:00Z",
        type = "response_item",
        payload = new { type = "configuration_update", reasoning = new { effort } },
        metadata = new { harness_authored_configuration = trusted }
    }) + "\n";
    private static string Line(string type, object payload, string timestamp = "2026-10-03T12:00:00Z") => JsonSerializer.Serialize(new { timestamp, type, payload }) + "\n";
}
