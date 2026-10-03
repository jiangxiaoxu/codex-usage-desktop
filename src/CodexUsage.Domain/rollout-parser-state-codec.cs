using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsage.Domain;

public static class RolloutParserStateCodec
{
    public const int FormatRevision = 4;

    public static string Serialize(RolloutParserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        var document = new RolloutParserStateDocument(
            FormatRevision,
            state.HasMetadata,
            state.Metadata,
            state.TurnModels.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new TurnModelDocument(value.Key, value.Value)).ToArray(),
            state.CurrentTurnId,
            state.CurrentTurnModelOverridden,
            state.CurrentModel,
            state.ForkReplay,
            state.PreviousSnapshot,
            state.NextTokenEventOrdinal,
            state.UnresolvedTurnIds.ToArray(),
            state.ProvisionalTurnIds.ToArray(),
            state.CurrentServiceTier,
            state.TurnServiceTiers.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new TurnServiceTierDocument(value.Key, value.Value)).ToArray(),
            state.ActivitiesState);
        return JsonSerializer.Serialize(document, RolloutParserStateJsonContext.Default.RolloutParserStateDocument);
    }

    public static bool TryDeserialize(string json, out RolloutParserState? state, out string? error)
    {
        state = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Parser state JSON is empty.";
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize(json, RolloutParserStateJsonContext.Default.RolloutParserStateDocument);
            if (document is null || document.FormatRevision != FormatRevision)
            {
                error = "Parser state format revision is unsupported.";
                return false;
            }
            if (document.TurnModels is null || document.UnresolvedTurnIds is null || document.ProvisionalTurnIds is null || document.TurnServiceTiers is null
                || document.CurrentServiceTier is null || document.ActivitiesState is null)
            {
                error = "Parser state collections are missing.";
                return false;
            }

            var turnModels = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var item in document.TurnModels)
            {
                if (item is null || string.IsNullOrEmpty(item.TurnId) || string.IsNullOrEmpty(item.Model)
                    || !turnModels.TryAdd(item.TurnId, item.Model))
                {
                    error = "Parser state contains an invalid or duplicate turn model.";
                    return false;
                }
            }

            var turnServiceTiers = ImmutableDictionary.CreateBuilder<string, ServiceTier>(StringComparer.Ordinal);
            foreach (var item in document.TurnServiceTiers)
            {
                if (item is null || string.IsNullOrEmpty(item.TurnId) || item.ServiceTier is null || !Enum.IsDefined(item.ServiceTier.Value)
                    || !turnServiceTiers.TryAdd(item.TurnId, item.ServiceTier.Value))
                {
                    error = "Parser state contains an invalid or duplicate turn service tier.";
                    return false;
                }
            }

            if (!TryCreateSet(document.UnresolvedTurnIds, out var unresolved)
                || !TryCreateSet(document.ProvisionalTurnIds, out var provisional))
            {
                error = "Parser state contains an invalid turn-id set.";
                return false;
            }

            var candidate = new RolloutParserState(
                document.HasMetadata,
                document.Metadata,
                turnModels.ToImmutable(),
                document.CurrentTurnId,
                document.CurrentTurnModelOverridden,
                document.CurrentModel,
                document.ForkReplay,
                document.PreviousSnapshot,
                document.NextTokenEventOrdinal,
                unresolved,
                provisional,
                document.CurrentServiceTier.Value,
                turnServiceTiers.ToImmutable(),
                document.ActivitiesState);
            ValidateState(candidate);
            state = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryCreateSet(string[] values, out ImmutableSortedSet<string> result)
    {
        var builder = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value) || !builder.Add(value))
            {
                result = ImmutableSortedSet<string>.Empty;
                return false;
            }
        }
        result = builder.ToImmutable();
        return true;
    }

    private static void ValidateState(RolloutParserState state)
    {
        ArgumentNullException.ThrowIfNull(state.Metadata);
        ValidateActivityState(state.ActivitiesState);
        ArgumentNullException.ThrowIfNull(state.TurnModels);
        ArgumentNullException.ThrowIfNull(state.TurnServiceTiers);
        ArgumentNullException.ThrowIfNull(state.ForkReplay);
        ArgumentNullException.ThrowIfNull(state.UnresolvedTurnIds);
        ArgumentNullException.ThrowIfNull(state.ProvisionalTurnIds);
        if (!Enum.IsDefined(state.Metadata.ThreadType) || !Enum.IsDefined(state.ForkReplay.Status)
            || !Enum.IsDefined(state.CurrentServiceTier)
            || (state.ForkReplay.ServiceTier is { } replayTier && !Enum.IsDefined(replayTier)))
            throw new ArgumentException("Parser state contains an unknown enum value.", nameof(state));
        RequireText(state.Metadata.ConversationId, nameof(state.Metadata.ConversationId));
        RequireText(state.Metadata.RolloutId, nameof(state.Metadata.RolloutId));
        RequireOptionalText(state.Metadata.ThreadId, nameof(state.Metadata.ThreadId));
        RequireNotNull(state.Metadata.ParentThreadId, nameof(state.Metadata.ParentThreadId));
        RequireText(state.Metadata.AgentRole, nameof(state.Metadata.AgentRole));
        RequireText(state.Metadata.AgentPath, nameof(state.Metadata.AgentPath));
        RequireNotNull(state.Metadata.AgentNickname, nameof(state.Metadata.AgentNickname));
        RequireText(state.Metadata.ProjectName, nameof(state.Metadata.ProjectName));
        RequireNotNull(state.Metadata.ThreadTitle, nameof(state.Metadata.ThreadTitle));
        if (state.Metadata.LastActivityEpochMs < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Last activity cannot be negative.");
        RequireNotNull(state.CurrentTurnId, nameof(state.CurrentTurnId));
        RequireText(state.CurrentModel, nameof(state.CurrentModel));
        if (state.NextTokenEventOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Next token event ordinal cannot be negative.");
        if (state.ForkReplay.ForkBoundaryEpochMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Fork boundary cannot be negative.");
        RequireOptionalText(state.ForkReplay.TurnId, nameof(state.ForkReplay.TurnId));
        RequireOptionalText(state.ForkReplay.Model, nameof(state.ForkReplay.Model));
        RequireOptionalText(state.PreviousSnapshot, nameof(state.PreviousSnapshot));
        foreach (var pair in state.TurnModels)
        {
            RequireText(pair.Key, "turn model key");
            RequireText(pair.Value, "turn model value");
        }
        foreach (var pair in state.TurnServiceTiers)
        {
            RequireText(pair.Key, "turn service tier key");
            if (!Enum.IsDefined(pair.Value)) throw new ArgumentException("Parser state contains an unknown service tier.", nameof(state));
        }
        foreach (var turnId in state.UnresolvedTurnIds) RequireText(turnId, "unresolved turn id");
        foreach (var turnId in state.ProvisionalTurnIds) RequireText(turnId, "provisional turn id");
    }

    private static void ValidateActivityState(RolloutActivityParserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.SeenActivityIds);
        ArgumentNullException.ThrowIfNull(state.TurnEfforts);
        ArgumentNullException.ThrowIfNull(state.ConfiguredEffortTurnIds);
        ArgumentNullException.ThrowIfNull(state.UnresolvedTurnIds);
        ArgumentNullException.ThrowIfNull(state.AmbiguousTurnIds);
        ArgumentNullException.ThrowIfNull(state.AmbiguousModelTurnIds);
        if (state.NextActivityOrdinal < 0
            || state.PendingReasoning.IsDefault)
            throw new ArgumentException("Parser activity state is invalid.", nameof(state));
        var identities = new HashSet<(string, string, string)>();
        foreach (var pending in state.PendingReasoning)
        {
            ArgumentNullException.ThrowIfNull(pending);
            RequireText(pending.ThreadId, "activity thread id");
            RequireText(pending.TurnId, "activity turn id");
            RequireText(pending.ItemId, "activity item id");
            RequireText(pending.Model, "activity model");
            if (!Enum.IsDefined(pending.Effort) || !Enum.IsDefined(pending.ServiceTier)
                || !identities.Add((pending.ThreadId, pending.TurnId, pending.ItemId))
                || (pending.RawPayloadBytes is { } bytes && bytes <= 0)
                || (pending.StartedAtEpochMs is { } start && (start <= 0 || start > 253_402_300_799_999))
                || (pending.CompletedAtEpochMs is { } end && (end <= 0 || end > 253_402_300_799_999))
                || pending.StartedAtEpochMs.HasValue != pending.CompletedAtEpochMs.HasValue
                || (pending.StartedAtEpochMs.HasValue && pending.CompletedAtEpochMs <= pending.StartedAtEpochMs)
                || (!pending.StartedAtEpochMs.HasValue && !pending.RawPayloadBytes.HasValue))
                throw new ArgumentException("Parser pending activity is invalid.", nameof(state));
        }
        foreach (var turnId in state.ConfiguredEffortTurnIds) RequireText(turnId, "configured activity effort turn id");
        foreach (var identity in state.SeenActivityIds) RequireText(identity, "activity identity");
        foreach (var turnId in state.UnresolvedTurnIds) RequireText(turnId, "activity unresolved turn id");
        foreach (var turnId in state.AmbiguousTurnIds) RequireText(turnId, "activity ambiguous turn id");
        foreach (var turnId in state.AmbiguousModelTurnIds) RequireText(turnId, "activity ambiguous model turn id");
        foreach (var pair in state.TurnEfforts)
        {
            RequireText(pair.Key, "activity effort turn id");
            if (!Enum.IsDefined(pair.Value)) throw new ArgumentException("Parser activity effort is invalid.", nameof(state));
        }
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException($"{name} cannot be empty.", name);
    }

    private static void RequireNotNull(string? value, string name)
    {
        if (value is null) throw new ArgumentNullException(name);
    }

    private static void RequireOptionalText(string? value, string name)
    {
        if (value is not null && value.Length == 0) throw new ArgumentException($"{name} cannot be empty.", name);
    }
}

internal sealed record TurnServiceTierDocument(string TurnId, ServiceTier? ServiceTier);

internal sealed record TurnModelDocument(string TurnId, string Model);

internal sealed record RolloutParserStateDocument(
    int FormatRevision,
    bool HasMetadata,
    RolloutMetadata Metadata,
    TurnModelDocument[] TurnModels,
    string CurrentTurnId,
    bool CurrentTurnModelOverridden,
    string CurrentModel,
    RolloutForkReplayState ForkReplay,
    string? PreviousSnapshot,
    long NextTokenEventOrdinal,
    string[] UnresolvedTurnIds,
    string[] ProvisionalTurnIds,
    ServiceTier? CurrentServiceTier,
    TurnServiceTierDocument[] TurnServiceTiers,
    RolloutActivityParserState ActivitiesState);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(RolloutParserStateDocument))]
internal sealed partial class RolloutParserStateJsonContext : JsonSerializerContext;
