using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CodexUsage.Domain;

public static partial class RolloutParser
{
    private sealed partial class ParserAccumulator
    {
        private readonly Dictionary<string, PendingReasoningActivity> _pendingReasoning = new(StringComparer.Ordinal);
        private readonly List<ActivityCandidate> _activityCandidates = [];
        private ImmutableSortedSet<string>.Builder _seenActivities = null!;
        private ImmutableDictionary<string, ReasoningEffort>.Builder _turnEfforts = null!;
        private ImmutableSortedSet<string>.Builder _ambiguousActivityTurns = null!;
        private ImmutableSortedSet<string>.Builder _ambiguousActivityModelTurns = null!;
        private ImmutableSortedSet<string>.Builder _configuredEffortTurns = null!;
        private bool _activitiesRequireReparse;

        private void InitializeActivities(RolloutActivityParserState? state)
        {
            state ??= RolloutActivityParserState.Empty;
            _seenActivities = state.SeenActivityIds.ToBuilder();
            _turnEfforts = state.TurnEfforts.ToBuilder();
            _configuredEffortTurns = state.ConfiguredEffortTurnIds.ToBuilder();
            _ambiguousActivityTurns = state.AmbiguousTurnIds.ToBuilder();
            _ambiguousActivityModelTurns = state.AmbiguousModelTurnIds.ToBuilder();
            foreach (var pending in state.PendingReasoning)
                _pendingReasoning.Add(ActivityIdentity(pending.ThreadId, pending.TurnId, ModelActivityKind.Reasoning, pending.ItemId), pending);
        }

        private static string ActivityIdentity(string threadId, string turnId, ModelActivityKind kind, string itemId) =>
            JsonSerializer.Serialize(new object[] { threadId, turnId, kind, itemId });

        private static ReasoningEffort EffortFrom(string? value) => value switch
        {
            "none" => ReasoningEffort.None,
            "minimal" => ReasoningEffort.Minimal,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" => ReasoningEffort.XHigh,
            "max" => ReasoningEffort.Max,
            _ => ReasoningEffort.Unknown,
        };

        private void ProcessActivityConfiguration(JsonElement root, JsonElement payload)
        {
            if (_metadata.IsRealtimeVoice || _currentTurnId.Length == 0
                || !TryGetObject(root, "metadata", out var metadata)
                || GetBoolean(metadata, "harness_authored_configuration") != true
                || !TryGetObject(payload, "reasoning", out var reasoning)) return;
            if (reasoning.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.String)
            {
                _turnEfforts[_currentTurnId] = EffortFrom(effort.GetString());
                _configuredEffortTurns.Add(_currentTurnId);
            }
        }

        private void ResolveLateActivityEffort(string turnId, ReasoningEffort effort)
        {
            if (effort == ReasoningEffort.Unknown) return;
            for (var index = 0; index < _activityCandidates.Count; index++)
                if (_activityCandidates[index].TurnId == turnId && _activityCandidates[index].Effort == ReasoningEffort.Unknown && _activityCandidates[index].EffortMayResolve)
                    _activityCandidates[index] = _activityCandidates[index] with { Effort = effort };
            foreach (var (key, pending) in _pendingReasoning.ToArray())
                if (pending.TurnId == turnId && pending.Effort == ReasoningEffort.Unknown && pending.EffortMayResolve && pending.StartedAtEpochMs.HasValue)
                    _pendingReasoning[key] = pending with { Effort = effort };
        }

        private string ActivityModel(string turnId) =>
            _turnModels.TryGetValue(turnId, out var model) ? model : "unknown";

        private ReasoningEffort ActivityEffort(string turnId) =>
            _turnEfforts.TryGetValue(turnId, out var effort) ? effort : ReasoningEffort.Unknown;

        private ServiceTier ActivityTier(string turnId) =>
            _turnServiceTiers.TryGetValue(turnId, out var tier) ? tier : ServiceTier.Unknown;

        private void ProcessCompletedActivity(JsonElement payload)
        {
            if (_metadata.IsRealtimeVoice || !_hasMetadata || string.IsNullOrEmpty(_metadata.ThreadId)
                || GetNonEmptyString(payload, "thread_id") != _metadata.ThreadId
                || GetNonEmptyString(payload, "turn_id") is not { } turnId
                || !TryGetObject(payload, "item", out var item)) return;
            var kind = GetNonEmptyString(item, "type") switch
            {
                "Reasoning" => ModelActivityKind.Reasoning,
                "AgentMessage" => ModelActivityKind.TextOutput,
                _ => (ModelActivityKind?)null,
            };
            if (kind is null) return;
            if (GetNonEmptyString(item, "id") is not { } itemId)
            {
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            var identity = ActivityIdentity(_metadata.ThreadId, turnId, kind.Value, itemId);
            if (_seenActivities.Contains(identity)) return;
            if (!TryGetEpochMs(payload, "started_at_ms", out var started)
                || !TryGetEpochMs(payload, "completed_at_ms", out var completed) || completed <= started)
            {
                _pendingReasoning.Remove(identity);
                _seenActivities.Add(identity);
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            if (kind == ModelActivityKind.Reasoning)
            {
                _pendingReasoning.TryGetValue(identity, out var prior);
                var pending = new PendingReasoningActivity(_metadata.ThreadId, turnId, itemId, started, completed,
                    prior?.RawPayloadBytes, ActivityModel(turnId), ActivityEffort(turnId), ActivityTier(turnId), !_configuredEffortTurns.Contains(turnId));
                _pendingReasoning[identity] = pending;
                TryCompleteReasoning(identity, pending);
                return;
            }
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            long bytes = 0;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || GetNonEmptyString(part, "type") != "Text"
                    || !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    _diagnostics.UnavailableModelActivityRecords++;
                    return;
                }
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(text.GetString()!));
            }
            if (bytes <= 0)
            {
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            AddActivity(identity, new(_metadata.ThreadId, turnId, itemId, ModelActivityKind.TextOutput,
                started, completed, bytes, bytes, ActivityModel(turnId), ActivityEffort(turnId), ActivityTier(turnId), !_configuredEffortTurns.Contains(turnId)));
        }

        private void ProcessReasoningPayload(JsonElement payload)
        {
            if (_metadata.IsRealtimeVoice || !_hasMetadata || string.IsNullOrEmpty(_metadata.ThreadId)
                || GetNonEmptyString(payload, "type") != "reasoning") return;
            if (payload.TryGetProperty("thread_id", out var owner) && owner.ValueKind != JsonValueKind.Null
                && (owner.ValueKind != JsonValueKind.String || owner.GetString() != _metadata.ThreadId)) return;
            var turnId = TryGetObject(payload, "internal_chat_message_metadata_passthrough", out var metadata)
                ? GetNonEmptyString(metadata, "turn_id") : null;
            turnId ??= GetNonEmptyString(payload, "turn_id");
            if (GetNonEmptyString(payload, "id") is not { } itemId
                || GetNonEmptyString(payload, "encrypted_content") is not { } encrypted)
            {
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            if (turnId is null)
            {
                var matching = _pendingReasoning.Values.Where(value => value.ThreadId == _metadata.ThreadId && value.ItemId == itemId).Take(2).ToArray();
                turnId = matching.Length == 1 ? matching[0].TurnId : string.Empty;
            }
            if (turnId.Length == 0)
            {
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            var identity = ActivityIdentity(_metadata.ThreadId, turnId, ModelActivityKind.Reasoning, itemId);
            if (_seenActivities.Contains(identity)) return;
            var bytes = (long)Encoding.UTF8.GetByteCount(encrypted);
            _pendingReasoning.TryGetValue(identity, out var prior);
            var pending = prior is null
                ? new PendingReasoningActivity(_metadata.ThreadId, turnId, itemId, null, null, bytes,
                    "unknown", ReasoningEffort.Unknown, ServiceTier.Unknown, false)
                : prior with { RawPayloadBytes = bytes };
            _pendingReasoning[identity] = pending;
            TryCompleteReasoning(identity, pending);
        }

        private void TryCompleteReasoning(string identity, PendingReasoningActivity pending)
        {
            if (pending.StartedAtEpochMs is not { } start || pending.CompletedAtEpochMs is not { } end
                || pending.RawPayloadBytes is not { } bytes) return;
            _pendingReasoning.Remove(identity);
            var estimatedBytes = ModelActivityVolumeEstimator.EstimateReasoningContentBytes(bytes);
            if (estimatedBytes <= 0)
            {
                _seenActivities.Add(identity);
                _diagnostics.UnavailableModelActivityRecords++;
                return;
            }
            AddActivity(identity, new(pending.ThreadId, pending.TurnId, pending.ItemId, ModelActivityKind.Reasoning,
                start, end, bytes, estimatedBytes, pending.Model, pending.Effort, pending.ServiceTier, pending.EffortMayResolve));
        }

        private void AddActivity(string identity, ActivityCandidate candidate)
        {
            if (!_seenActivities.Add(identity)) return;
            _activityCandidates.Add(candidate);
        }

        private static bool TryGetEpochMs(JsonElement payload, string name, out long value)
        {
            value = 0;
            return payload.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out value) && value > 0 && value <= 253_402_300_799_999;
        }

        private ImmutableArray<ParsedModelActivitySample> MaterializeActivities()
        {
            var result = ImmutableArray.CreateBuilder<ParsedModelActivitySample>(_activityCandidates.Count);
            for (var index = 0; index < _activityCandidates.Count; index++) result.Add(MaterializeActivity(_activityCandidates[index], index));
            return result.ToImmutable();
        }

        private ParsedModelActivitySample MaterializeActivity(ActivityCandidate candidate, int candidateIndex)
        {
            var model = _ambiguousActivityModelTurns.Contains(candidate.TurnId) ? "unknown"
                : candidate.Model == "unknown" && _turnModels.TryGetValue(candidate.TurnId, out var resolved)
                ? resolved : candidate.Model;
            var tier = _ambiguousActivityTurns.Contains(candidate.TurnId) ? ServiceTier.Unknown : candidate.ServiceTier;
            var signature = JsonSerializer.Serialize(new object[] {
                candidate.ThreadId, candidate.TurnId, candidate.Kind, candidate.ItemId,
                candidate.StartedAtEpochMs, candidate.CompletedAtEpochMs, candidate.RawPayloadBytes,
                candidate.EstimatedContentBytes, model, candidate.Effort, tier, ModelActivityVolumeEstimator.CurrentRevision });
            return new(_metadata.ConversationId, _metadata.RolloutId, _metadata.ParentThreadId,
                _metadata.ThreadType, _metadata.AgentRole, _metadata.AgentPath, _metadata.AgentNickname,
                DateTimeOffset.FromUnixTimeMilliseconds(candidate.CompletedAtEpochMs).ToString("O"),
                checked((_priorState?.ActivitiesState.NextActivityOrdinal ?? 0) + candidateIndex),
                candidate.ThreadId, candidate.TurnId, candidate.ItemId, candidate.Kind, model, candidate.Effort, tier,
                candidate.StartedAtEpochMs, candidate.CompletedAtEpochMs, candidate.RawPayloadBytes,
                candidate.EstimatedContentBytes, ModelActivityVolumeEstimator.CurrentRevision, signature);
        }

        private RolloutActivityParserState BuildActivityState()
        {
            var unresolved = (_priorState?.ActivitiesState.UnresolvedTurnIds ?? ImmutableSortedSet<string>.Empty).ToBuilder();
            foreach (var turnId in unresolved.ToArray())
                if (_turnModels.ContainsKey(turnId) && ActivityEffort(turnId) != ReasoningEffort.Unknown) unresolved.Remove(turnId);
            foreach (var candidate in _activityCandidates)
                if ((candidate.Model == "unknown" && !_turnModels.ContainsKey(candidate.TurnId))
                    || (candidate.Effort == ReasoningEffort.Unknown && candidate.EffortMayResolve)) unresolved.Add(candidate.TurnId);
            return new(checked((_priorState?.ActivitiesState.NextActivityOrdinal ?? 0) + _activityCandidates.Count),
                _pendingReasoning.Values.OrderBy(value => value.TurnId, StringComparer.Ordinal)
                    .ThenBy(value => value.ItemId, StringComparer.Ordinal).ToImmutableArray(),
                _seenActivities.ToImmutable(), _turnEfforts.ToImmutable(), _configuredEffortTurns.ToImmutable(), unresolved.ToImmutable(),
                _ambiguousActivityTurns.ToImmutable(), _ambiguousActivityModelTurns.ToImmutable(), _activitiesRequireReparse);
        }
    }

    private sealed record ActivityCandidate(
        string ThreadId, string TurnId, string ItemId, ModelActivityKind Kind,
        long StartedAtEpochMs, long CompletedAtEpochMs, long RawPayloadBytes, long EstimatedContentBytes,
        string Model, ReasoningEffort Effort, ServiceTier ServiceTier, bool EffortMayResolve);
}
