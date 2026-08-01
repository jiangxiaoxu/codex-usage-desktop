using System.Collections.Immutable;
using CodexUsage.Domain;
using Xunit;

namespace CodexUsage.Domain.Tests;

public sealed class RolloutParserStateCodecTests
{
    [Fact]
    public void RoundTripsEveryAttributionAndReplayField()
    {
        var state = new RolloutParserState(
            true,
            new RolloutMetadata("conversation", "rollout", "parent", ThreadType.Subagent,
                "worker", "/root/worker", "worker-a", false),
            ImmutableDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>("turn-b", "gpt-5.6-terra"),
                new KeyValuePair<string, string>("turn-a", "gpt-5.6-sol"),
            ]),
            "turn-b",
            true,
            "gpt-5.6-terra",
            new RolloutForkReplayState(ForkReplayStatus.AwaitingRecipient, 123, "turn-b", "gpt-5.6-terra"),
            "[10,2,3,1,13]",
            42,
            ImmutableSortedSet.Create(StringComparer.Ordinal, "turn-c"),
            ImmutableSortedSet.Create(StringComparer.Ordinal, "turn-d"));

        var json = RolloutParserStateCodec.Serialize(state);
        var succeeded = RolloutParserStateCodec.TryDeserialize(json, out var restored, out var error);

        Assert.True(succeeded, error);
        Assert.Equal(state.HasMetadata, restored!.HasMetadata);
        Assert.Equal(state.Metadata, restored.Metadata);
        Assert.Equal(state.TurnModels.OrderBy(value => value.Key), restored.TurnModels.OrderBy(value => value.Key));
        Assert.Equal(state.CurrentTurnId, restored.CurrentTurnId);
        Assert.Equal(state.CurrentTurnModelOverridden, restored.CurrentTurnModelOverridden);
        Assert.Equal(state.CurrentModel, restored.CurrentModel);
        Assert.Equal(state.ForkReplay, restored.ForkReplay);
        Assert.Equal(state.PreviousSnapshot, restored.PreviousSnapshot);
        Assert.Equal(state.NextTokenEventOrdinal, restored.NextTokenEventOrdinal);
        Assert.Equal(state.UnresolvedTurnIds, restored.UnresolvedTurnIds);
        Assert.Equal(state.ProvisionalTurnIds, restored.ProvisionalTurnIds);
        Assert.Equal(json, RolloutParserStateCodec.Serialize(restored!));
    }

    [Fact]
    public void RejectsUnknownFormatRevisionAndDuplicateTurnModels()
    {
        const string metadata = "\"metadata\":{\"conversationId\":\"c\",\"rolloutId\":\"r\",\"parentThreadId\":\"\",\"threadType\":0,\"agentRole\":\"main\",\"agentPath\":\"/root\",\"agentNickname\":\"\",\"isRealtimeVoice\":false}";
        var duplicate = $"{{\"formatRevision\":1,\"hasMetadata\":true,{metadata},\"turnModels\":[{{\"turnId\":\"t\",\"model\":\"m\"}},{{\"turnId\":\"t\",\"model\":\"m\"}}],\"currentTurnId\":\"\",\"currentTurnModelOverridden\":false,\"currentModel\":\"unknown\",\"forkReplay\":{{\"status\":0}},\"previousSnapshot\":null,\"nextTokenEventOrdinal\":0,\"unresolvedTurnIds\":[],\"provisionalTurnIds\":[]}}";
        var unknownRevision = duplicate.Replace("\"formatRevision\":1", "\"formatRevision\":99", StringComparison.Ordinal);

        Assert.False(RolloutParserStateCodec.TryDeserialize(duplicate, out _, out _));
        Assert.False(RolloutParserStateCodec.TryDeserialize(unknownRevision, out _, out _));
    }
}
