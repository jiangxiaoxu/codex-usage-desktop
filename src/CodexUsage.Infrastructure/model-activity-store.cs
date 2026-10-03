using System.Text.Json;
using CodexUsage.Domain;
using Microsoft.Data.Sqlite;

namespace CodexUsage.Infrastructure;

public sealed partial class UsageStore
{
    private void CreateModelActivityTables(SqliteTransaction transaction) => ExecuteNonQuery(transaction, """
        CREATE TABLE IF NOT EXISTS model_activity_samples (
            rollout_id TEXT NOT NULL REFERENCES rollouts(rollout_id) ON DELETE CASCADE,
            activity_ordinal INTEGER NOT NULL CHECK (activity_ordinal >= 0),
            timestamp_epoch_ms INTEGER NOT NULL CHECK (timestamp_epoch_ms >= 0),
            thread_id TEXT NOT NULL, turn_id TEXT NOT NULL, item_id TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK (kind IN (0, 1)), model TEXT NOT NULL,
            effort INTEGER NOT NULL CHECK (effort BETWEEN 0 AND 7),
            service_tier TEXT NOT NULL CHECK (service_tier IN ('unknown', 'standard', 'fast')),
            started_at_epoch_ms INTEGER NOT NULL CHECK (started_at_epoch_ms >= 0),
            completed_at_epoch_ms INTEGER NOT NULL CHECK (completed_at_epoch_ms > started_at_epoch_ms),
            raw_payload_bytes INTEGER NOT NULL CHECK (raw_payload_bytes > 0),
            estimated_content_bytes INTEGER NOT NULL CHECK (estimated_content_bytes > 0),
            estimator_revision INTEGER NOT NULL CHECK (estimator_revision > 0),
            deterministic_signature TEXT NOT NULL,
            PRIMARY KEY (rollout_id, activity_ordinal),
            UNIQUE (rollout_id, thread_id, turn_id, kind, item_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS model_activity_samples_time_idx ON model_activity_samples(timestamp_epoch_ms);
        """);

    private static void ValidateModelInputs(
        IReadOnlyList<ModelActivityInput> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        foreach (var item in activities)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireNonNegative(item.ActivityOrdinal, nameof(item.ActivityOrdinal));
            ValidateModelIdentity(item.TimestampEpochMs, item.ThreadId, item.TurnId,
                item.StartedAtEpochMs, item.CompletedAtEpochMs, item.DeterministicSignature);
            RequireText(item.ItemId, nameof(item.ItemId));
            RequireText(item.Model, nameof(item.Model));
            if (!Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Effort) || !Enum.IsDefined(item.ServiceTier))
                throw new ArgumentException("Invalid model activity attribution.");
            if (item.RawPayloadBytes <= 0 || item.EstimatedContentBytes <= 0 || item.EstimatorRevision <= 0)
                throw new ArgumentException("Model activity volumes and estimator revision must be positive.");
        }
    }

    private static void ValidateModelIdentity(long timestamp, string threadId, string turnId,
        long started, long completed, string signature)
    {
        RequireNonNegative(timestamp, nameof(timestamp));
        RequireNonNegative(started, nameof(started));
        if (completed <= started) throw new ArgumentOutOfRangeException(nameof(completed));
        RequireText(threadId, nameof(threadId));
        RequireText(turnId, nameof(turnId));
        RequireText(signature, nameof(signature));
    }

    private void ReplaceModelInputs(SqliteTransaction transaction, string rolloutId,
        IReadOnlyList<ModelActivityInput> activities)
    {
        ExecuteNonQuery(transaction, "DELETE FROM model_activity_samples WHERE rollout_id = $rollout", ("$rollout", rolloutId));
        AppendModelInputs(transaction, rolloutId, activities, allowDuplicates: false);
    }

    private void AppendModelInputs(SqliteTransaction transaction, string rolloutId,
        IReadOnlyList<ModelActivityInput> activities, bool allowDuplicates = true)
    {
        foreach (var item in activities)
        {
            var changes = ExecuteNonQuery(transaction, $"""
                INSERT INTO model_activity_samples VALUES (
                    $rollout, $ordinal, $timestamp, $thread, $turn, $item, $kind, $model, $effort,
                    $tier, $started, $completed, $raw, $estimated, $revision, $signature)
                {(allowDuplicates ? "ON CONFLICT(rollout_id, activity_ordinal) DO NOTHING" : "")}
                """, ("$rollout", rolloutId), ("$ordinal", item.ActivityOrdinal), ("$timestamp", item.TimestampEpochMs),
                ("$thread", item.ThreadId), ("$turn", item.TurnId), ("$item", item.ItemId), ("$kind", (int)item.Kind),
                ("$model", item.Model), ("$effort", (int)item.Effort), ("$tier", ServiceTierToDb(item.ServiceTier)),
                ("$started", item.StartedAtEpochMs), ("$completed", item.CompletedAtEpochMs),
                ("$raw", item.RawPayloadBytes), ("$estimated", item.EstimatedContentBytes),
                ("$revision", item.EstimatorRevision), ("$signature", item.DeterministicSignature));
            if (changes == 0 && !string.Equals(item.DeterministicSignature, ExecuteNullableScalarString(transaction,
                    "SELECT deterministic_signature FROM model_activity_samples WHERE rollout_id = $rollout AND activity_ordinal = $ordinal",
                    ("$rollout", rolloutId), ("$ordinal", item.ActivityOrdinal)), StringComparison.Ordinal))
                throw new InvalidOperationException($"Conflicting model activity at {rolloutId}:{item.ActivityOrdinal}");
        }
    }

    public RolloutModelActivityCursor GetRolloutModelActivityCursor(string rolloutId)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT (SELECT count(*) FROM model_activity_samples WHERE rollout_id = $rollout),
                   (SELECT COALESCE(max(activity_ordinal) + 1, 0) FROM model_activity_samples WHERE rollout_id = $rollout)
            """, ("$rollout", rolloutId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Model activity cursor query returned no row.");
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    public IReadOnlyList<string> GetRolloutActivitySignatures(string rolloutId)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        const string sql = "SELECT thread_id, turn_id, item_id, kind, model, effort, service_tier, started_at_epoch_ms, completed_at_epoch_ms, raw_payload_bytes, estimated_content_bytes, estimator_revision FROM model_activity_samples WHERE rollout_id = $rollout ORDER BY activity_ordinal";
        using var command = CreateCommand(null, sql, ("$rollout", rolloutId));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            result.Add(JsonSerializer.Serialize(values));
        }
        return result;
    }

    public IReadOnlyList<StoredModelActivity> QueryModelActivities(UsageEventQuery query)
    {
        var (conditions, parameters) = ModelQueryConditions(query);
        using var command = CreateCommand(null, $"""
            SELECT r.conversation_id, r.rollout_id, r.parent_thread_id, r.thread_type,
                   r.agent_role, r.agent_path, r.agent_nickname, e.timestamp_epoch_ms, e.activity_ordinal,
                   e.thread_id, e.turn_id, e.item_id, e.kind, e.model, e.effort, e.service_tier,
                   e.started_at_epoch_ms, e.completed_at_epoch_ms, e.raw_payload_bytes,
                   e.estimated_content_bytes, e.estimator_revision, e.deterministic_signature
            FROM model_activity_samples AS e JOIN rollouts AS r ON r.rollout_id = e.rollout_id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY e.timestamp_epoch_ms, r.rollout_id, e.activity_ordinal
            """, parameters.ToArray());
        using var reader = command.ExecuteReader();
        var result = new List<StoredModelActivity>();
        while (reader.Read())
            result.Add(new StoredModelActivity(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                ParseThreadType(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                (ModelActivityKind)reader.GetInt32(12), reader.GetString(13), (ReasoningEffort)reader.GetInt32(14),
                ParseServiceTier(reader.GetString(15)), reader.GetInt64(16), reader.GetInt64(17),
                reader.GetInt64(18), reader.GetInt64(19), reader.GetInt32(20), reader.GetString(21)));
        return result;
    }

    private (List<string> Conditions, List<(string Name, object? Value)> Parameters) ModelQueryConditions(
        UsageEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireNonNegative(query.StartEpochMs, nameof(query.StartEpochMs));
        if (query.EndEpochMs < query.StartEpochMs) throw new ArgumentOutOfRangeException(nameof(query.EndEpochMs));
        if (query.MainThreadConversationId is { } id && !ConversationId.IsUuidV7(id))
            throw new ArgumentException("Main thread conversation ID must be UUIDv7.", nameof(query));
        AssertOpen();
        var conditions = new List<string> { "e.timestamp_epoch_ms >= $start", "e.timestamp_epoch_ms < $end" };
        var parameters = new List<(string Name, object? Value)> { ("$start", query.StartEpochMs), ("$end", query.EndEpochMs) };
        AddListFilter(conditions, parameters, "e.model", query.Models, "model");
        AddListFilter(conditions, parameters, "r.agent_role", query.AgentRoles, "role");
        if (query.ThreadTypes is { Count: > 0 })
            AddListFilter(conditions, parameters, "r.thread_type", query.ThreadTypes.Select(ThreadTypeToDb).ToArray(), "thread");
        if (query.MainThreadConversationId is { } main)
        {
            conditions.Add("""
                r.rollout_id IN (
                    WITH RECURSIVE rollout_parent_keys(rollout_id, parent_key) AS (
                        SELECT rollout_id, rollout_id FROM rollouts
                        UNION SELECT rollout_id, conversation_id FROM rollouts WHERE thread_type = 'main'
                    ), thread_rollouts(rollout_id) AS (
                        SELECT rollout_id FROM rollouts WHERE conversation_id = $main AND thread_type = 'main'
                        UNION SELECT child.rollout_id FROM thread_rollouts AS parent
                        JOIN rollout_parent_keys AS keys ON keys.rollout_id = parent.rollout_id
                        JOIN rollouts AS child ON child.parent_thread_id = keys.parent_key
                    ) SELECT rollout_id FROM thread_rollouts
                )
                """);
            parameters.Add(("$main", main));
        }
        return (conditions, parameters);
    }
}
