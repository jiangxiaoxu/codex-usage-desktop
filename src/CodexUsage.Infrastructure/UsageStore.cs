using System.Text.Json;
using CodexUsage.Domain;
using Microsoft.Data.Sqlite;

namespace CodexUsage.Infrastructure;

public sealed class UsageStore : IDisposable
{
    private const int SchemaVersion = 8;
    private const int DefaultBusyTimeoutMs = 5_000;

    private readonly SqliteConnection _connection;
    private bool _closed;

    public UsageStore(
        string databasePath,
        int busyTimeoutMs = DefaultBusyTimeoutMs,
        ProtectedPathPolicy? protectedPathPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegative(busyTimeoutMs);
        DatabasePath = databasePath;
        if (databasePath != ":memory:")
        {
            protectedPathPolicy ??= ProtectedPathPolicy.ForCodexHome(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex"));
            protectedPathPolicy.AssertWritablePath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        try
        {
            _connection.Open();
            ExecuteNonQuery(null, "PRAGMA foreign_keys = ON");
            ExecuteNonQuery(null, $"PRAGMA busy_timeout = {busyTimeoutMs}");
            ExecuteScalarString(null, "PRAGMA journal_mode = WAL");
            Migrate();
        }
        catch
        {
            _connection.Dispose();
            _closed = true;
            throw;
        }
    }

    public string DatabasePath { get; }

    public int CurrentSchemaVersion
    {
        get
        {
            AssertOpen();
            return checked((int)ExecuteScalarLong(null, "PRAGMA user_version"));
        }
    }

    public AppendEventsResult AppendEvents(
        RolloutMetadata metadata,
        IReadOnlyList<UsageEventInput> events,
        long observedAtEpochMs)
    {
        ValidateMetadata(metadata);
        ValidateEvents(events);
        RequireNonNegative(observedAtEpochMs, nameof(observedAtEpochMs));
        return WriteTransaction(transaction => AppendWithinTransaction(transaction, metadata, events, observedAtEpochMs));
    }

    public AppendEventsResult AppendRolloutSource(AppendRolloutSourceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateMetadata(input.Metadata);
        ValidateEvents(input.Events);
        RequireNonNegative(input.ObservedAtEpochMs, nameof(input.ObservedAtEpochMs));
        var source = ToSource(input.Source, input.Metadata.RolloutId);
        ValidateSource(source);
        if (input.Checkpoint is not null) ValidateCheckpoint(input.Checkpoint, source);
        return WriteTransaction(transaction =>
        {
            var result = AppendWithinTransaction(transaction, input.Metadata, input.Events, input.ObservedAtEpochMs);
            UpsertSourceWithinTransaction(transaction, source);
            if (input.Checkpoint is not null) UpsertCheckpointWithinTransaction(transaction, input.Checkpoint);
            return result;
        });
    }

    public void ReplaceCanonicalRollout(ReplaceCanonicalRolloutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateMetadata(input.Metadata);
        ValidateEvents(input.Events);
        RequireNonNegative(input.ObservedAtEpochMs, nameof(input.ObservedAtEpochMs));
        RequireOptionalText(input.ResolvedConflictSourcePath, nameof(input.ResolvedConflictSourcePath));
        var source = ToCanonicalSource(input.Source, input.Metadata.RolloutId);
        ValidateSource(source);
        if (input.Checkpoint is not null) ValidateCheckpoint(input.Checkpoint, source);
        WriteTransaction(transaction =>
        {
            UpsertRollout(transaction, input.Metadata, input.ObservedAtEpochMs);
            ReplaceEventsWithinTransaction(transaction, input.Metadata.RolloutId, input.Events);
            UpsertSourceWithinTransaction(transaction, source);
            PromoteRolloutWithinTransaction(
                transaction,
                input.Metadata.RolloutId,
                input.Source.FilePath,
                input.ObservedAtEpochMs,
                input.ResolvedConflictSourcePath);
            if (input.Checkpoint is not null) UpsertCheckpointWithinTransaction(transaction, input.Checkpoint);
            return 0;
        });
    }

    public void RekeyLegacyCanonicalRollout(RekeyLegacyCanonicalRolloutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireText(input.LegacyRolloutId, nameof(input.LegacyRolloutId));
        ValidateMetadata(input.Metadata);
        ValidateEvents(input.Events);
        RequireNonNegative(input.ObservedAtEpochMs, nameof(input.ObservedAtEpochMs));
        if (!string.Equals(
                input.LegacyRolloutId,
                RolloutFileIdentity.LegacyFallbackRolloutId(input.Source.FilePath),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Legacy rollout id is not the exact historical filename fallback");
        if (!RolloutFileIdentity.TryGetTrailingUuidV7(input.Source.FilePath, out var filenameRolloutId)
            || !string.Equals(filenameRolloutId, input.Metadata.RolloutId, StringComparison.Ordinal)
            || !RolloutFileIdentity.IsUuidV7(input.Metadata.RolloutId)
            || string.Equals(input.LegacyRolloutId, input.Metadata.RolloutId, StringComparison.Ordinal))
            throw new InvalidOperationException("Replacement rollout id must equal the filename's strict trailing UUIDv7");

        var source = ToCanonicalSource(input.Source, input.Metadata.RolloutId);
        ValidateSource(source);
        ValidateCheckpoint(input.Checkpoint, source);
        WriteTransaction(transaction =>
        {
            var canonicalPath = ExecuteNullableScalarString(
                transaction,
                "SELECT canonical_source_path FROM rollouts WHERE rollout_id = $rolloutId",
                ("$rolloutId", input.LegacyRolloutId));
            var legacySourceCount = ExecuteScalarLong(
                transaction,
                "SELECT COUNT(*) FROM source_files WHERE rollout_id = $rolloutId",
                ("$rolloutId", input.LegacyRolloutId));
            var exactCanonicalCount = ExecuteScalarLong(
                transaction,
                """
                SELECT COUNT(*) FROM source_files
                WHERE rollout_id = $rolloutId AND file_path = $filePath
                  AND is_present = 1
                  AND (
                      canonical_status = 'canonical'
                      OR (canonical_status = 'conflict' AND last_error = $identityConflict)
                  )
                """,
                ("$rolloutId", input.LegacyRolloutId),
                ("$filePath", input.Source.FilePath),
                ("$identityConflict",
                    $"Canonical source rollout changed from {input.LegacyRolloutId} to {input.Metadata.RolloutId}."));
            if (!string.Equals(canonicalPath, input.Source.FilePath, StringComparison.Ordinal)
                || legacySourceCount != 1
                || exactCanonicalCount != 1)
                throw new InvalidOperationException("Legacy rollout is not backed by one unique present designated canonical source");

            var targetReferences = ExecuteScalarLong(
                transaction,
                """
                SELECT
                    (SELECT COUNT(*) FROM rollouts WHERE rollout_id = $rolloutId)
                  + (SELECT COUNT(*) FROM source_files WHERE rollout_id = $rolloutId)
                  + (SELECT COUNT(*) FROM usage_events WHERE rollout_id = $rolloutId)
                  + (SELECT COUNT(*) FROM rollout_checkpoints WHERE rollout_id = $rolloutId)
                """,
                ("$rolloutId", input.Metadata.RolloutId));
            if (targetReferences != 0)
                throw new InvalidOperationException("Replacement rollout id already has ledger state");

            ExecuteNonQuery(transaction,
                "DELETE FROM rollout_checkpoints WHERE file_path = $filePath",
                ("$filePath", input.Source.FilePath));
            ExecuteNonQuery(transaction,
                "DELETE FROM source_files WHERE file_path = $filePath",
                ("$filePath", input.Source.FilePath));
            var deleted = ExecuteNonQuery(transaction,
                "DELETE FROM rollouts WHERE rollout_id = $rolloutId",
                ("$rolloutId", input.LegacyRolloutId));
            if (deleted != 1) throw new InvalidOperationException("Legacy rollout disappeared during re-key");

            UpsertRollout(transaction, input.Metadata, input.ObservedAtEpochMs);
            ReplaceEventsWithinTransaction(transaction, input.Metadata.RolloutId, input.Events);
            UpsertSourceWithinTransaction(transaction, source);
            PromoteRolloutWithinTransaction(
                transaction,
                input.Metadata.RolloutId,
                input.Source.FilePath,
                input.ObservedAtEpochMs,
                null);
            UpsertCheckpointWithinTransaction(transaction, input.Checkpoint);
            return 0;
        });
    }

    public void RecoverDivergedCanonicalSource(RecoverDivergedCanonicalSourceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateMetadata(input.Metadata);
        ValidateEvents(input.Events);
        RequireNonNegative(input.ObservedAtEpochMs, nameof(input.ObservedAtEpochMs));
        var source = new SourceFileInput(
            input.Source.FilePath,
            input.Metadata.RolloutId,
            input.Source.SizeBytes,
            input.Source.ModifiedAtEpochMs,
            input.Source.ByteOffset,
            input.Source.PrefixHash,
            PrefixStatus.Matches,
            CanonicalStatus.Canonical,
            true,
            input.Source.LastScannedAtEpochMs,
            null);
        ValidateSource(source);
        if (input.Checkpoint is not null) ValidateCheckpoint(input.Checkpoint, source);

        WriteTransaction(transaction =>
        {
            var canonicalPath = ExecuteNullableScalarString(
                transaction,
                "SELECT canonical_source_path FROM rollouts WHERE rollout_id = $rolloutId",
                ("$rolloutId", input.Metadata.RolloutId));
            if (!RolloutExists(transaction, input.Metadata.RolloutId))
            {
                throw new InvalidOperationException($"Unknown rollout: {input.Metadata.RolloutId}");
            }

            if (!string.Equals(canonicalPath, source.FilePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Recovery source is not the rollout's canonical source");
            }

            var existingSourceRollout = ExecuteNullableScalarString(
                transaction,
                "SELECT rollout_id FROM source_files WHERE file_path = $filePath",
                ("$filePath", source.FilePath));
            if (!SourceExists(transaction, source.FilePath))
            {
                throw new InvalidOperationException("Recovery source does not exist in the ledger");
            }

            if (!string.Equals(existingSourceRollout, input.Metadata.RolloutId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Recovery source belongs to a different rollout");
            }

            UpsertRollout(transaction, input.Metadata, input.ObservedAtEpochMs);
            ReplaceEventsWithinTransaction(transaction, input.Metadata.RolloutId, input.Events);
            UpsertSourceWithinTransaction(transaction, source);
            if (input.Checkpoint is not null) UpsertCheckpointWithinTransaction(transaction, input.Checkpoint);
            return 0;
        });
    }

    public void UpsertSourceFile(SourceFileInput source)
    {
        ValidateSource(source);
        WriteTransaction(transaction =>
        {
            UpsertSourceWithinTransaction(transaction, source);
            return 0;
        });
    }

    public bool MarkSourceMissing(string filePath, long lastScannedAtEpochMs)
    {
        RequireText(filePath, nameof(filePath));
        RequireNonNegative(lastScannedAtEpochMs, nameof(lastScannedAtEpochMs));
        return WriteTransaction(transaction =>
        {
            var changed = ExecuteNonQuery(
                transaction,
                "UPDATE source_files SET is_present = 0, last_scanned_at_epoch_ms = $at WHERE file_path = $path",
                ("$at", lastScannedAtEpochMs),
                ("$path", filePath));
            ExecuteNonQuery(transaction, "DELETE FROM rollout_checkpoints WHERE file_path = $path", ("$path", filePath));
            return changed == 1;
        });
    }

    public IReadOnlyList<SourceFileRecord> ListSourceFiles()
    {
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT file_path, rollout_id, size_bytes, modified_at_epoch_ms,
                   byte_offset, prefix_hash, prefix_status, canonical_status,
                   is_present, last_scanned_at_epoch_ms, last_error
            FROM source_files
            ORDER BY file_path
            """);
        using var reader = command.ExecuteReader();
        var result = new List<SourceFileRecord>();
        while (reader.Read())
        {
            result.Add(new SourceFileRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5),
                ParsePrefixStatus(reader.GetString(6)),
                ParseCanonicalStatus(reader.GetString(7)),
                ReadBoolean(reader, 8),
                reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return result;
    }

    public IReadOnlyList<RolloutCheckpointRecord> ListRolloutCheckpoints()
    {
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT file_path, rollout_id, checkpoint_format_revision, parser_revision,
                   source_identity_kind, source_identity, observed_size_bytes,
                   observed_modified_at_epoch_ms, stable_complete_offset, boundary_hash,
                   parser_state_json, parser_state_hash, trailing_partial_bytes,
                   safe_opaque_oversized_records, safe_null_padding_records, last_verified_at_epoch_ms
            FROM rollout_checkpoints
            ORDER BY file_path
            """);
        using var reader = command.ExecuteReader();
        var result = new List<RolloutCheckpointRecord>();
        while (reader.Read())
        {
            result.Add(new RolloutCheckpointRecord(
                reader.GetString(0),
                reader.GetString(1),
                checked((int)reader.GetInt64(2)),
                checked((int)reader.GetInt64(3)),
                new SourceIdentity(ParseSourceIdentityKind(reader.GetString(4)), reader.GetString(5)),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt64(12),
                checked((int)reader.GetInt64(13)),
                checked((int)reader.GetInt64(14)),
                reader.GetInt64(15)));
        }
        return result;
    }

    public bool DeleteRolloutCheckpoint(string filePath)
    {
        RequireText(filePath, nameof(filePath));
        return WriteTransaction(transaction => ExecuteNonQuery(
            transaction,
            "DELETE FROM rollout_checkpoints WHERE file_path = $filePath",
            ("$filePath", filePath)) == 1);
    }

    public RolloutEventCursor GetRolloutEventCursor(string rolloutId)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT count(*), COALESCE(max(token_event_ordinal) + 1, 0)
            FROM usage_events
            WHERE rollout_id = $rolloutId
            """, ("$rolloutId", rolloutId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Rollout event cursor query returned no row.");
        return new RolloutEventCursor(reader.GetInt64(0), reader.GetInt64(1));
    }

    public RolloutLedgerTail? GetRolloutLedgerTail(string rolloutId)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT token_event_ordinal, timestamp_epoch_ms, input_tokens, cached_input_tokens,
                   output_tokens, reasoning_output_tokens
            FROM usage_events
            WHERE rollout_id = $rolloutId
            ORDER BY token_event_ordinal DESC
            LIMIT 1
            """, ("$rolloutId", rolloutId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new RolloutLedgerTail(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5))
            : null;
    }

    public IReadOnlyList<string> GetRolloutEventSignatures(string rolloutId) =>
        ReadStringList(
            rolloutId,
            "SELECT event_signature FROM usage_events WHERE rollout_id = $rolloutId ORDER BY token_event_ordinal");

    public IReadOnlyList<string> GetRolloutEventIdentities(string rolloutId) =>
        ReadEventSignatureTuples(rolloutId, includeModel: false);

    public IReadOnlyList<string> GetRolloutSemanticSignatures(string rolloutId) =>
        ReadEventSignatureTuples(rolloutId, includeModel: true);

    public RolloutMetadata? GetRolloutMetadata(string rolloutId)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT rollout_id, conversation_id, parent_thread_id, thread_type,
                   agent_role, agent_path, agent_nickname, is_realtime_voice,
                   project_name, thread_title, last_activity_epoch_ms
            FROM rollouts WHERE rollout_id = $rolloutId
            """, ("$rolloutId", rolloutId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new RolloutMetadata(
                reader.GetString(1), reader.GetString(0), reader.GetString(2),
                ParseThreadType(reader.GetString(3)), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), ReadBoolean(reader, 7),
                reader.GetString(8), reader.GetString(9), reader.GetInt64(10))
            : null;
    }

    public string? GetCanonicalSourcePath(string rolloutId)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        return ExecuteNullableScalarString(
            null,
            "SELECT canonical_source_path FROM rollouts WHERE rollout_id = $rolloutId",
            ("$rolloutId", rolloutId));
    }

    public IReadOnlyList<string> ListCanonicalSourcesWithUnknownModels()
    {
        AssertOpen();
        using var command = CreateCommand(null, """
            SELECT DISTINCT r.canonical_source_path
            FROM rollouts AS r
            JOIN usage_events AS e ON e.rollout_id = r.rollout_id
            WHERE e.model = 'unknown' AND r.canonical_source_path IS NOT NULL
            ORDER BY r.canonical_source_path
            """);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public long CountSourceConflicts()
    {
        AssertOpen();
        return ExecuteScalarLong(
            null,
            "SELECT count(*) FROM source_files WHERE canonical_status = 'conflict' AND is_present = 1");
    }

    public long CountPresentSources()
    {
        AssertOpen();
        return ExecuteScalarLong(null, "SELECT count(*) FROM source_files WHERE is_present = 1");
    }

    public long CountPresentRealtimeVoiceSessions()
    {
        AssertOpen();
        return ExecuteScalarLong(null, """
            SELECT count(*)
            FROM rollouts AS r
            WHERE r.is_realtime_voice = 1
              AND EXISTS (
                  SELECT 1
                  FROM source_files AS s
                  WHERE s.rollout_id = r.rollout_id AND s.is_present = 1
              )
            """);
    }

    public long RecordSourceConflict(SourceConflictInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireText(input.SourceFilePath, nameof(input.SourceFilePath));
        RequireOptionalText(input.RunId, nameof(input.RunId));
        RequireText(input.Code, nameof(input.Code));
        RequireText(input.Message, nameof(input.Message));
        RequireNonNegative(input.ObservedAtEpochMs, nameof(input.ObservedAtEpochMs));
        return WriteTransaction(transaction =>
        {
            ExecuteNonQuery(
                transaction,
                """
                UPDATE source_files
                SET canonical_status = 'conflict', last_error = $message, last_scanned_at_epoch_ms = $at
                WHERE file_path = $path
                """,
                ("$message", input.Message), ("$at", input.ObservedAtEpochMs), ("$path", input.SourceFilePath));
            ExecuteNonQuery(transaction,
                "DELETE FROM rollout_checkpoints WHERE file_path = $path",
                ("$path", input.SourceFilePath));
            return InsertDiagnostic(transaction, new CollectorDiagnosticInput(
                input.RunId,
                input.SourceFilePath,
                DiagnosticSeverity.Error,
                input.Code,
                input.Message,
                input.DetailsJson,
                input.ObservedAtEpochMs));
        });
    }

    public long AddDiagnostic(CollectorDiagnosticInput input)
    {
        ValidateDiagnostic(input);
        return WriteTransaction(transaction => InsertDiagnostic(transaction, input));
    }

    internal long CountDiagnosticsForTest(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        AssertOpen();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM collector_diagnostics WHERE code = $code";
        command.Parameters.AddWithValue("$code", code);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal long MaximumRepeatedDiagnosticForTest(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        AssertOpen();
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(repeated), 0) FROM (SELECT count(*) repeated FROM collector_diagnostics WHERE code = $code GROUP BY source_file_path, message)";
        command.Parameters.AddWithValue("$code", code);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public IReadOnlyList<StoredUsageEvent> QueryEvents(UsageEventQuery filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        RequireNonNegative(filter.StartEpochMs, nameof(filter.StartEpochMs));
        RequireNonNegative(filter.EndEpochMs, nameof(filter.EndEpochMs));
        if (filter.EndEpochMs < filter.StartEpochMs)
        {
            throw new ArgumentOutOfRangeException(nameof(filter.EndEpochMs), "EndEpochMs cannot precede StartEpochMs.");
        }
        if (filter.MainThreadConversationId is { } requestedMainThreadConversationId
            && !ConversationId.IsUuidV7(requestedMainThreadConversationId))
        {
            throw new ArgumentException("Main thread conversation ID must be UUIDv7.", nameof(filter));
        }

        AssertOpen();
        var conditions = new List<string> { "e.timestamp_epoch_ms >= $start", "e.timestamp_epoch_ms < $end" };
        var parameters = new List<(string Name, object? Value)>
        {
            ("$start", filter.StartEpochMs),
            ("$end", filter.EndEpochMs),
        };
        AddListFilter(conditions, parameters, "e.model", filter.Models, "model");
        AddListFilter(conditions, parameters, "r.agent_role", filter.AgentRoles, "role");
        if (filter.ThreadTypes is { Count: > 0 })
        {
            AddListFilter(
                conditions,
                parameters,
                "r.thread_type",
                filter.ThreadTypes.Select(ThreadTypeToDb).ToArray(),
                "thread");
        }

        if (filter.MainThreadConversationId is { Length: > 0 } mainThreadConversationId)
        {
            conditions.Add("""
                r.rollout_id IN (
                    WITH RECURSIVE
                    rollout_parent_keys(rollout_id, parent_key) AS (
                        SELECT rollout_id, rollout_id
                        FROM rollouts
                        UNION
                        SELECT rollout_id, conversation_id
                        FROM rollouts
                        WHERE thread_type = 'main'
                    ),
                    thread_rollouts(rollout_id) AS (
                        SELECT rollout_id
                        FROM rollouts
                        WHERE conversation_id = $mainThreadConversationId
                          AND thread_type = 'main'
                        UNION
                        SELECT child.rollout_id
                        FROM thread_rollouts AS parent
                        JOIN rollout_parent_keys AS parent_key
                          ON parent_key.rollout_id = parent.rollout_id
                        JOIN rollouts AS child
                          ON child.parent_thread_id = parent_key.parent_key
                    )
                    SELECT rollout_id FROM thread_rollouts
                )
                """);
            parameters.Add(("$mainThreadConversationId", mainThreadConversationId));
        }

        using var command = CreateCommand(null, $"""
            SELECT e.timestamp_epoch_ms, e.token_event_ordinal, e.event_signature,
                   e.model, e.input_tokens, e.cached_input_tokens, e.output_tokens,
                   e.reasoning_output_tokens, r.conversation_id, r.rollout_id,
                   r.parent_thread_id, r.thread_type, r.agent_role, r.agent_path,
                   r.agent_nickname
            FROM usage_events AS e
            JOIN rollouts AS r ON r.rollout_id = e.rollout_id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY e.timestamp_epoch_ms, r.rollout_id, e.token_event_ordinal
            """, parameters.ToArray());
        using var reader = command.ExecuteReader();
        var result = new List<StoredUsageEvent>();
        while (reader.Read())
        {
            var epoch = reader.GetInt64(0);
            result.Add(new StoredUsageEvent(
                DateTimeOffset.FromUnixTimeMilliseconds(epoch),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                ParseThreadType(reader.GetString(11)), reader.GetString(12),
                reader.GetString(13), reader.GetString(14), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                reader.GetInt64(7), reader.GetInt64(1), epoch, reader.GetString(2)));
        }

        return result;
    }

    public IReadOnlyList<MainThreadOption> QueryRecentMainThreads(int maximumCount)
    {
        if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));

        AssertOpen();
        using var command = CreateCommand(null, """
            WITH RECURSIVE ranked_main_rollouts AS (
                SELECT conversation_id, project_name, thread_title, last_activity_epoch_ms,
                       ROW_NUMBER() OVER (
                           PARTITION BY conversation_id
                           ORDER BY last_activity_epoch_ms DESC, rollout_id DESC) AS rank
                FROM rollouts
                WHERE thread_type = 'main'
                  AND is_realtime_voice = 0
                  AND (parent_thread_id = '' OR (rollout_id = conversation_id AND parent_thread_id <> conversation_id))
            ),
            main_threads AS (
                SELECT conversation_id, project_name, thread_title
                FROM ranked_main_rollouts
                WHERE rank = 1
            ),
            rollout_parent_keys(rollout_id, parent_key) AS (
                SELECT rollout_id, rollout_id
                FROM rollouts
                UNION
                SELECT rollout_id, conversation_id
                FROM rollouts
                WHERE thread_type = 'main'
            ),
            thread_rollouts(root_conversation_id, rollout_id) AS (
                SELECT main.conversation_id, root.rollout_id
                FROM main_threads AS main
                JOIN rollouts AS root
                  ON root.conversation_id = main.conversation_id
                 AND root.thread_type = 'main'
                UNION
                SELECT parent.root_conversation_id, child.rollout_id
                FROM thread_rollouts AS parent
                JOIN rollout_parent_keys AS parent_key
                  ON parent_key.rollout_id = parent.rollout_id
                JOIN rollouts AS child
                  ON child.parent_thread_id = parent_key.parent_key
            )
            SELECT main.conversation_id,
                   main.project_name,
                   main.thread_title,
                   MAX(related.last_activity_epoch_ms) AS last_activity_epoch_ms
            FROM main_threads AS main
            JOIN thread_rollouts AS thread
              ON thread.root_conversation_id = main.conversation_id
            JOIN rollouts AS related
              ON related.rollout_id = thread.rollout_id
            GROUP BY main.conversation_id, main.project_name, main.thread_title
            ORDER BY last_activity_epoch_ms DESC, main.conversation_id DESC
            """);
        using var reader = command.ExecuteReader();
        var result = new List<MainThreadOption>();
        while (reader.Read() && result.Count < maximumCount)
        {
            var conversationId = reader.GetString(0);
            if (ConversationId.IsUuidV7(conversationId))
            {
                result.Add(new MainThreadOption(
                    conversationId,
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
            }
        }

        return result;
    }

    public bool SynchronizeMainThreadTitles(
        IReadOnlyDictionary<string, string> titles,
        bool clearMissingTitles,
        long observedAtEpochMs)
    {
        ArgumentNullException.ThrowIfNull(titles);
        RequireNonNegative(observedAtEpochMs, nameof(observedAtEpochMs));
        foreach (var (conversationId, title) in titles)
        {
            if (!ConversationId.IsUuidV7(conversationId))
                throw new ArgumentException("Main thread conversation ID must be UUIDv7.", nameof(titles));
            if (string.IsNullOrWhiteSpace(title))
                throw new ArgumentException("Main thread title cannot be blank.", nameof(titles));
        }

        AssertOpen();
        return WriteTransaction(transaction =>
        {
            var changed = false;
            if (clearMissingTitles)
            {
                changed |= ExecuteNonQuery(transaction, """
                    UPDATE rollouts
                    SET thread_title = '', updated_at_epoch_ms = $observedAt
                    WHERE thread_type = 'main'
                      AND is_realtime_voice = 0
                      AND thread_title <> ''
                    """, ("$observedAt", observedAtEpochMs)) > 0;
            }

            foreach (var (conversationId, title) in titles)
            {
                changed |= ExecuteNonQuery(transaction, """
                    UPDATE rollouts
                    SET thread_title = $threadTitle, updated_at_epoch_ms = $observedAt
                    WHERE conversation_id = $conversationId
                      AND thread_type = 'main'
                      AND is_realtime_voice = 0
                      AND thread_title <> $threadTitle
                    """,
                    ("$conversationId", conversationId),
                    ("$threadTitle", title),
                    ("$observedAt", observedAtEpochMs)) > 0;
            }

            return changed;
        });
    }

    public void BeginCollectorRun(CollectorRunStartInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireText(input.RunId, nameof(input.RunId));
        RequireText(input.Trigger, nameof(input.Trigger));
        RequireNonNegative(input.StartedAtEpochMs, nameof(input.StartedAtEpochMs));
        WriteTransaction(transaction =>
        {
            ExecuteNonQuery(transaction, """
                INSERT INTO collector_runs (
                    run_id, trigger, status, started_at_epoch_ms, heartbeat_at_epoch_ms,
                    completed_at_epoch_ms, files_scanned, events_added,
                    diagnostics_count, error_message
                ) VALUES ($runId, $trigger, 'running', $at, $at, NULL, 0, 0, 0, NULL)
                """, ("$runId", input.RunId), ("$trigger", input.Trigger), ("$at", input.StartedAtEpochMs));
            return 0;
        });
    }

    public void HeartbeatCollector(CollectorRunHeartbeatInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireText(input.RunId, nameof(input.RunId));
        RequireNonNegative(input.HeartbeatAtEpochMs, nameof(input.HeartbeatAtEpochMs));
        if (input.State is not null)
        {
            foreach (var item in input.State)
            {
                RequireText(item.Key, "state key");
                ArgumentNullException.ThrowIfNull(item.Value);
            }
        }

        WriteTransaction(transaction =>
        {
            var changes = ExecuteNonQuery(
                transaction,
                "UPDATE collector_runs SET heartbeat_at_epoch_ms = $at WHERE run_id = $runId AND status = 'running'",
                ("$at", input.HeartbeatAtEpochMs), ("$runId", input.RunId));
            if (changes != 1)
            {
                throw new InvalidOperationException($"Unknown or completed collector run: {input.RunId}");
            }

            if (input.State is not null)
            {
                foreach (var item in input.State)
                {
                    SetCollectorStateWithinTransaction(transaction, item.Key, item.Value, input.HeartbeatAtEpochMs);
                }
            }

            return 0;
        });
    }

    public void FinishCollectorRun(CollectorRunFinishInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireText(input.RunId, nameof(input.RunId));
        if (input.Status is not (CollectorRunStatus.Succeeded or CollectorRunStatus.Failed))
        {
            throw new ArgumentException("A finished collector run must be succeeded or failed.", nameof(input));
        }

        RequireNonNegative(input.CompletedAtEpochMs, nameof(input.CompletedAtEpochMs));
        RequireNonNegative(input.FilesScanned, nameof(input.FilesScanned));
        RequireNonNegative(input.EventsAdded, nameof(input.EventsAdded));
        RequireNonNegative(input.DiagnosticsCount, nameof(input.DiagnosticsCount));
        WriteTransaction(transaction =>
        {
            var changes = ExecuteNonQuery(transaction, """
                UPDATE collector_runs
                SET status = $status, heartbeat_at_epoch_ms = $at, completed_at_epoch_ms = $at,
                    files_scanned = $files, events_added = $events,
                    diagnostics_count = $diagnostics, error_message = $error
                WHERE run_id = $runId AND status = 'running'
                """,
                ("$status", CollectorRunStatusToDb(input.Status)), ("$at", input.CompletedAtEpochMs),
                ("$files", input.FilesScanned), ("$events", input.EventsAdded),
                ("$diagnostics", input.DiagnosticsCount), ("$error", input.ErrorMessage),
                ("$runId", input.RunId));
            if (changes != 1)
            {
                throw new InvalidOperationException($"Unknown or completed collector run: {input.RunId}");
            }

            return 0;
        });
    }

    public CollectorRunRecord? GetCollectorRun(string runId)
    {
        RequireText(runId, nameof(runId));
        return ReadCollectorRun(
            """
            SELECT run_id, trigger, status, started_at_epoch_ms, heartbeat_at_epoch_ms,
                   completed_at_epoch_ms, files_scanned, events_added,
                   diagnostics_count, error_message
            FROM collector_runs WHERE run_id = $runId
            """,
            ("$runId", runId));
    }

    public CollectorRunRecord? GetLatestCollectorRun() => ReadCollectorRun("""
        SELECT run_id, trigger, status, started_at_epoch_ms, heartbeat_at_epoch_ms,
               completed_at_epoch_ms, files_scanned, events_added,
               diagnostics_count, error_message
        FROM collector_runs
        ORDER BY started_at_epoch_ms DESC, run_id DESC
        LIMIT 1
        """);

    public void SetCollectorState(string key, string value, long updatedAtEpochMs)
    {
        RequireText(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        RequireNonNegative(updatedAtEpochMs, nameof(updatedAtEpochMs));
        WriteTransaction(transaction =>
        {
            SetCollectorStateWithinTransaction(transaction, key, value, updatedAtEpochMs);
            return 0;
        });
    }

    public string? GetCollectorState(string key)
    {
        RequireText(key, nameof(key));
        AssertOpen();
        return ExecuteNullableScalarString(
            null,
            "SELECT value FROM collector_state WHERE key = $key",
            ("$key", key));
    }

    public CheckpointResult Close()
    {
        AssertOpen();
        using var command = CreateCommand(null, "PRAGMA wal_checkpoint(TRUNCATE)");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("WAL checkpoint returned no row.");
        }

        var result = new CheckpointResult(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        _connection.Dispose();
        _closed = true;
        return result;
    }

    public void Dispose()
    {
        if (!_closed)
        {
            Close();
        }
    }

    private void Migrate()
    {
        var currentVersion = CurrentSchemaVersion;
        if (currentVersion > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {currentVersion} is newer than supported version {SchemaVersion}.");
        }

        if (currentVersion == SchemaVersion)
        {
            return;
        }

        var requiresRolloutsRebuild = currentVersion is > 0 and < SchemaVersion;
        if (requiresRolloutsRebuild) ExecuteNonQuery(null, "PRAGMA foreign_keys = OFF");
        try
        {
            WriteTransaction(transaction =>
            {
                if (currentVersion == 0)
                {
                    ExecuteNonQuery(transaction, """
                    CREATE TABLE rollouts (
                        rollout_id TEXT PRIMARY KEY,
                        conversation_id TEXT NOT NULL,
                        parent_thread_id TEXT NOT NULL,
                        thread_type TEXT NOT NULL CHECK (thread_type IN ('main', 'subagent', 'guardian_review', 'unknown')),
                        agent_role TEXT NOT NULL,
                        agent_path TEXT NOT NULL,
                        agent_nickname TEXT NOT NULL,
                        is_realtime_voice INTEGER NOT NULL CHECK (is_realtime_voice IN (0, 1)),
                        project_name TEXT NOT NULL,
                        thread_title TEXT NOT NULL,
                        last_activity_epoch_ms INTEGER NOT NULL CHECK (last_activity_epoch_ms >= 0),
                        canonical_source_path TEXT,
                        created_at_epoch_ms INTEGER NOT NULL CHECK (created_at_epoch_ms >= 0),
                        updated_at_epoch_ms INTEGER NOT NULL CHECK (updated_at_epoch_ms >= 0)
                    ) STRICT;

                    CREATE TABLE usage_events (
                        rollout_id TEXT NOT NULL REFERENCES rollouts(rollout_id) ON DELETE CASCADE,
                        token_event_ordinal INTEGER NOT NULL CHECK (token_event_ordinal >= 0),
                        timestamp_epoch_ms INTEGER NOT NULL CHECK (timestamp_epoch_ms >= 0),
                        model TEXT NOT NULL,
                        input_tokens INTEGER NOT NULL CHECK (input_tokens >= 0),
                        cached_input_tokens INTEGER NOT NULL CHECK (cached_input_tokens >= 0 AND cached_input_tokens <= input_tokens),
                        output_tokens INTEGER NOT NULL CHECK (output_tokens >= 0),
                        reasoning_output_tokens INTEGER NOT NULL CHECK (reasoning_output_tokens >= 0 AND reasoning_output_tokens <= output_tokens),
                        event_signature TEXT NOT NULL,
                        PRIMARY KEY (rollout_id, token_event_ordinal),
                        UNIQUE (rollout_id, event_signature)
                    ) WITHOUT ROWID, STRICT;

                    CREATE INDEX usage_events_timestamp_idx ON usage_events(timestamp_epoch_ms);
                    CREATE INDEX usage_events_model_timestamp_idx ON usage_events(model, timestamp_epoch_ms);

                    CREATE TABLE source_files (
                        file_path TEXT PRIMARY KEY,
                        rollout_id TEXT REFERENCES rollouts(rollout_id) ON DELETE SET NULL,
                        size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                        modified_at_epoch_ms INTEGER NOT NULL CHECK (modified_at_epoch_ms >= 0),
                        byte_offset INTEGER NOT NULL CHECK (byte_offset >= 0 AND byte_offset <= size_bytes),
                        prefix_hash TEXT NOT NULL,
                        prefix_status TEXT NOT NULL CHECK (prefix_status IN ('unknown', 'matches', 'diverged')),
                        canonical_status TEXT NOT NULL CHECK (canonical_status IN ('candidate', 'canonical', 'conflict')),
                        is_present INTEGER NOT NULL CHECK (is_present IN (0, 1)),
                        last_scanned_at_epoch_ms INTEGER NOT NULL CHECK (last_scanned_at_epoch_ms >= 0),
                        last_error TEXT
                    ) STRICT;

                    CREATE INDEX source_files_rollout_idx ON source_files(rollout_id);

                    CREATE TABLE collector_runs (
                        run_id TEXT PRIMARY KEY,
                        trigger TEXT NOT NULL,
                        status TEXT NOT NULL CHECK (status IN ('running', 'succeeded', 'failed')),
                        started_at_epoch_ms INTEGER NOT NULL CHECK (started_at_epoch_ms >= 0),
                        heartbeat_at_epoch_ms INTEGER NOT NULL CHECK (heartbeat_at_epoch_ms >= 0),
                        completed_at_epoch_ms INTEGER CHECK (completed_at_epoch_ms IS NULL OR completed_at_epoch_ms >= 0),
                        files_scanned INTEGER NOT NULL CHECK (files_scanned >= 0),
                        events_added INTEGER NOT NULL CHECK (events_added >= 0),
                        diagnostics_count INTEGER NOT NULL CHECK (diagnostics_count >= 0),
                        error_message TEXT
                    ) STRICT;

                    CREATE TABLE collector_diagnostics (
                        diagnostic_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id TEXT REFERENCES collector_runs(run_id) ON DELETE SET NULL,
                        source_file_path TEXT,
                        severity TEXT NOT NULL CHECK (severity IN ('info', 'warning', 'error')),
                        code TEXT NOT NULL,
                        message TEXT NOT NULL,
                        details_json TEXT,
                        created_at_epoch_ms INTEGER NOT NULL CHECK (created_at_epoch_ms >= 0)
                    ) STRICT;

                    CREATE INDEX collector_diagnostics_run_idx
                        ON collector_diagnostics(run_id, created_at_epoch_ms);

                    CREATE TABLE collector_state (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL,
                        updated_at_epoch_ms INTEGER NOT NULL CHECK (updated_at_epoch_ms >= 0)
                    ) STRICT;
                    """);
                }

                if (currentVersion == 1)
                {
                    ExecuteNonQuery(transaction, """
                    ALTER TABLE rollouts
                    ADD COLUMN is_realtime_voice INTEGER NOT NULL DEFAULT 0
                    CHECK (is_realtime_voice IN (0, 1));
                    """);
                }

                if (currentVersion < 3)
                {
                    ExecuteNonQuery(transaction, """
                    CREATE TABLE rollout_checkpoints (
                        file_path TEXT PRIMARY KEY REFERENCES source_files(file_path) ON DELETE CASCADE,
                        rollout_id TEXT NOT NULL REFERENCES rollouts(rollout_id) ON DELETE CASCADE,
                        checkpoint_format_revision INTEGER NOT NULL CHECK (checkpoint_format_revision > 0),
                        parser_revision INTEGER NOT NULL CHECK (parser_revision > 0),
                        source_identity_kind TEXT NOT NULL
                            CHECK (source_identity_kind IN ('windows-file-id', 'conservative-stat')),
                        source_identity TEXT NOT NULL,
                        observed_size_bytes INTEGER NOT NULL CHECK (observed_size_bytes >= 0),
                        observed_modified_at_epoch_ms INTEGER NOT NULL CHECK (observed_modified_at_epoch_ms >= 0),
                        stable_complete_offset INTEGER NOT NULL
                            CHECK (stable_complete_offset >= 0 AND stable_complete_offset <= observed_size_bytes),
                        boundary_hash TEXT NOT NULL,
                        parser_state_json TEXT NOT NULL,
                        parser_state_hash TEXT NOT NULL,
                        trailing_partial_bytes INTEGER NOT NULL CHECK (trailing_partial_bytes >= 0),
                        safe_opaque_oversized_records INTEGER NOT NULL CHECK (safe_opaque_oversized_records >= 0),
                        safe_null_padding_records INTEGER NOT NULL DEFAULT 0 CHECK (safe_null_padding_records >= 0),
                        reconciliation_status TEXT NOT NULL DEFAULT 'verified'
                            CHECK (reconciliation_status IN ('verified')),
                        invalidation_reason TEXT,
                        last_verified_at_epoch_ms INTEGER NOT NULL CHECK (last_verified_at_epoch_ms >= 0)
                    ) STRICT;

                    CREATE INDEX rollout_checkpoints_rollout_idx ON rollout_checkpoints(rollout_id);
                    """);
                }

                if (currentVersion == 3)
                {
                    ExecuteNonQuery(transaction, """
                    ALTER TABLE rollout_checkpoints
                    ADD COLUMN safe_null_padding_records INTEGER NOT NULL DEFAULT 0
                    CHECK (safe_null_padding_records >= 0);
                    """);
                }

                if (currentVersion < 5 && !HasRolloutColumn(transaction, "thread_title"))
                {
                    ExecuteNonQuery(transaction, """
                    ALTER TABLE rollouts
                    ADD COLUMN thread_title TEXT NOT NULL DEFAULT '';
                    """);
                }

                if (currentVersion < 5 && !HasRolloutColumn(transaction, "last_activity_epoch_ms"))
                {
                    ExecuteNonQuery(transaction, """
                    ALTER TABLE rollouts
                    ADD COLUMN last_activity_epoch_ms INTEGER NOT NULL DEFAULT 0
                    CHECK (last_activity_epoch_ms >= 0);
                    """);
                }

                if (currentVersion < 6 && !HasRolloutColumn(transaction, "project_name"))
                {
                    ExecuteNonQuery(transaction, """
                    ALTER TABLE rollouts
                    ADD COLUMN project_name TEXT NOT NULL DEFAULT 'Codex';
                    """);
                }

                if (currentVersion < 7)
                {
                    ExecuteNonQuery(transaction, """
                    CREATE INDEX IF NOT EXISTS rollouts_parent_thread_idx ON rollouts(parent_thread_id);
                    """);
                }

                if (requiresRolloutsRebuild)
                {
                    ExecuteNonQuery(transaction, """
                        CREATE TABLE rollouts_guardian_migration (
                            rollout_id TEXT PRIMARY KEY,
                            conversation_id TEXT NOT NULL,
                            parent_thread_id TEXT NOT NULL,
                            thread_type TEXT NOT NULL CHECK (thread_type IN ('main', 'subagent', 'guardian_review', 'unknown')),
                            agent_role TEXT NOT NULL,
                            agent_path TEXT NOT NULL,
                            agent_nickname TEXT NOT NULL,
                            is_realtime_voice INTEGER NOT NULL CHECK (is_realtime_voice IN (0, 1)),
                            project_name TEXT NOT NULL,
                            thread_title TEXT NOT NULL,
                            last_activity_epoch_ms INTEGER NOT NULL CHECK (last_activity_epoch_ms >= 0),
                            canonical_source_path TEXT,
                            created_at_epoch_ms INTEGER NOT NULL CHECK (created_at_epoch_ms >= 0),
                            updated_at_epoch_ms INTEGER NOT NULL CHECK (updated_at_epoch_ms >= 0)
                        ) STRICT;

                        INSERT INTO rollouts_guardian_migration (
                            rollout_id, conversation_id, parent_thread_id, thread_type,
                            agent_role, agent_path, agent_nickname, is_realtime_voice,
                            project_name, thread_title, last_activity_epoch_ms,
                            canonical_source_path, created_at_epoch_ms, updated_at_epoch_ms
                        )
                        SELECT rollout_id, conversation_id, parent_thread_id, thread_type,
                               agent_role, agent_path, agent_nickname, is_realtime_voice,
                               project_name, thread_title, last_activity_epoch_ms,
                               canonical_source_path, created_at_epoch_ms, updated_at_epoch_ms
                        FROM rollouts;

                        DROP TABLE rollouts;
                        ALTER TABLE rollouts_guardian_migration RENAME TO rollouts;
                        CREATE INDEX rollouts_parent_thread_idx ON rollouts(parent_thread_id);
                        """);
                }

                ExecuteNonQuery(transaction, $"PRAGMA user_version = {SchemaVersion}");
                return 0;
            });
        }
        finally
        {
            if (requiresRolloutsRebuild) ExecuteNonQuery(null, "PRAGMA foreign_keys = ON");
        }
    }

    private AppendEventsResult AppendWithinTransaction(
        SqliteTransaction transaction,
        RolloutMetadata metadata,
        IReadOnlyList<UsageEventInput> events,
        long observedAtEpochMs)
    {
        UpsertRollout(transaction, metadata, observedAtEpochMs);
        long inserted = 0;
        foreach (var item in events)
        {
            var changes = ExecuteNonQuery(transaction, """
                INSERT INTO usage_events (
                    rollout_id, token_event_ordinal, timestamp_epoch_ms, model,
                    input_tokens, cached_input_tokens, output_tokens,
                    reasoning_output_tokens, event_signature
                ) VALUES ($rolloutId, $ordinal, $timestamp, $model, $input, $cached, $output, $reasoning, $signature)
                ON CONFLICT(rollout_id, token_event_ordinal) DO NOTHING
                """,
                ("$rolloutId", metadata.RolloutId), ("$ordinal", item.TokenEventOrdinal),
                ("$timestamp", item.TimestampEpochMs), ("$model", item.Model),
                ("$input", item.InputTokens), ("$cached", item.CachedInputTokens),
                ("$output", item.OutputTokens), ("$reasoning", item.ReasoningOutputTokens),
                ("$signature", item.EventSignature));
            if (changes == 1)
            {
                inserted++;
                continue;
            }

            var existing = ExecuteNullableScalarString(
                transaction,
                "SELECT event_signature FROM usage_events WHERE rollout_id = $rolloutId AND token_event_ordinal = $ordinal",
                ("$rolloutId", metadata.RolloutId), ("$ordinal", item.TokenEventOrdinal));
            if (!string.Equals(existing, item.EventSignature, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Conflicting usage event at {metadata.RolloutId}:{item.TokenEventOrdinal}");
            }
        }

        return new AppendEventsResult(inserted, events.Count - inserted);
    }

    private void ReplaceEventsWithinTransaction(
        SqliteTransaction transaction,
        string rolloutId,
        IReadOnlyList<UsageEventInput> events)
    {
        ExecuteNonQuery(
            transaction,
            "DELETE FROM usage_events WHERE rollout_id = $rolloutId",
            ("$rolloutId", rolloutId));
        foreach (var item in events)
        {
            ExecuteNonQuery(transaction, """
                INSERT INTO usage_events (
                    rollout_id, token_event_ordinal, timestamp_epoch_ms, model,
                    input_tokens, cached_input_tokens, output_tokens,
                    reasoning_output_tokens, event_signature
                ) VALUES ($rolloutId, $ordinal, $timestamp, $model, $input, $cached, $output, $reasoning, $signature)
                """,
                ("$rolloutId", rolloutId), ("$ordinal", item.TokenEventOrdinal),
                ("$timestamp", item.TimestampEpochMs), ("$model", item.Model),
                ("$input", item.InputTokens), ("$cached", item.CachedInputTokens),
                ("$output", item.OutputTokens), ("$reasoning", item.ReasoningOutputTokens),
                ("$signature", item.EventSignature));
        }
    }

    private void UpsertRollout(SqliteTransaction transaction, RolloutMetadata metadata, long observedAtEpochMs)
    {
        ExecuteNonQuery(transaction, """
            INSERT INTO rollouts (
                rollout_id, conversation_id, parent_thread_id, thread_type,
                agent_role, agent_path, agent_nickname, is_realtime_voice, canonical_source_path,
                project_name, thread_title, last_activity_epoch_ms,
                created_at_epoch_ms, updated_at_epoch_ms
            ) VALUES ($rolloutId, $conversationId, $parentThreadId, $threadType,
                      $agentRole, $agentPath, $agentNickname, $isRealtimeVoice, NULL,
                      $projectName, $threadTitle, $lastActivityEpochMs, $observedAt, $observedAt)
            ON CONFLICT(rollout_id) DO UPDATE SET
                conversation_id = excluded.conversation_id,
                parent_thread_id = excluded.parent_thread_id,
                thread_type = excluded.thread_type,
                agent_role = excluded.agent_role,
                agent_path = excluded.agent_path,
                agent_nickname = excluded.agent_nickname,
                is_realtime_voice = excluded.is_realtime_voice,
                project_name = excluded.project_name,
                thread_title = excluded.thread_title,
                last_activity_epoch_ms = MAX(rollouts.last_activity_epoch_ms, excluded.last_activity_epoch_ms),
                updated_at_epoch_ms = excluded.updated_at_epoch_ms
            """,
            ("$rolloutId", metadata.RolloutId), ("$conversationId", metadata.ConversationId),
            ("$parentThreadId", metadata.ParentThreadId), ("$threadType", ThreadTypeToDb(metadata.ThreadType)),
            ("$agentRole", metadata.AgentRole), ("$agentPath", metadata.AgentPath),
            ("$agentNickname", metadata.AgentNickname), ("$isRealtimeVoice", metadata.IsRealtimeVoice ? 1 : 0),
            ("$projectName", metadata.ProjectName), ("$threadTitle", metadata.ThreadTitle),
            ("$lastActivityEpochMs", metadata.LastActivityEpochMs),
            ("$observedAt", observedAtEpochMs));
    }

    private void UpsertSourceWithinTransaction(SqliteTransaction transaction, SourceFileInput source)
    {
        ExecuteNonQuery(transaction, """
            INSERT INTO source_files (
                file_path, rollout_id, size_bytes, modified_at_epoch_ms, byte_offset,
                prefix_hash, prefix_status, canonical_status, is_present,
                last_scanned_at_epoch_ms, last_error
            ) VALUES ($path, $rolloutId, $size, $modified, $offset, $hash, $prefixStatus,
                      $canonicalStatus, $present, $scanned, $error)
            ON CONFLICT(file_path) DO UPDATE SET
                rollout_id = excluded.rollout_id,
                size_bytes = excluded.size_bytes,
                modified_at_epoch_ms = excluded.modified_at_epoch_ms,
                byte_offset = excluded.byte_offset,
                prefix_hash = excluded.prefix_hash,
                prefix_status = excluded.prefix_status,
                canonical_status = excluded.canonical_status,
                is_present = excluded.is_present,
                last_scanned_at_epoch_ms = excluded.last_scanned_at_epoch_ms,
                last_error = excluded.last_error
            """,
            ("$path", source.FilePath), ("$rolloutId", source.RolloutId),
            ("$size", source.SizeBytes), ("$modified", source.ModifiedAtEpochMs),
            ("$offset", source.ByteOffset), ("$hash", source.PrefixHash),
            ("$prefixStatus", PrefixStatusToDb(source.PrefixStatus)),
            ("$canonicalStatus", CanonicalStatusToDb(source.CanonicalStatus)),
            ("$present", source.IsPresent ? 1 : 0), ("$scanned", source.LastScannedAtEpochMs),
            ("$error", source.LastError));
        if (!source.IsPresent || source.CanonicalStatus != CanonicalStatus.Canonical)
        {
            ExecuteNonQuery(transaction,
                "DELETE FROM rollout_checkpoints WHERE file_path = $filePath",
                ("$filePath", source.FilePath));
        }
    }

    private void UpsertCheckpointWithinTransaction(SqliteTransaction transaction, RolloutCheckpointInput checkpoint)
    {
        ExecuteNonQuery(transaction, """
            INSERT INTO rollout_checkpoints (
                file_path, rollout_id, checkpoint_format_revision, parser_revision,
                source_identity_kind, source_identity, observed_size_bytes,
                observed_modified_at_epoch_ms, stable_complete_offset, boundary_hash,
                parser_state_json, parser_state_hash, trailing_partial_bytes,
                safe_opaque_oversized_records, safe_null_padding_records,
                reconciliation_status, invalidation_reason,
                last_verified_at_epoch_ms
            ) VALUES (
                $filePath, $rolloutId, $checkpointFormatRevision, $parserRevision,
                $sourceIdentityKind, $sourceIdentity, $observedSizeBytes,
                $observedModifiedAtEpochMs, $stableCompleteOffset, $boundaryHash,
                $parserStateJson, $parserStateHash, $trailingPartialBytes,
                $safeOpaqueOversizedRecords, $safeNullPaddingRecords,
                'verified', NULL, $lastVerifiedAtEpochMs
            )
            ON CONFLICT(file_path) DO UPDATE SET
                rollout_id = excluded.rollout_id,
                checkpoint_format_revision = excluded.checkpoint_format_revision,
                parser_revision = excluded.parser_revision,
                source_identity_kind = excluded.source_identity_kind,
                source_identity = excluded.source_identity,
                observed_size_bytes = excluded.observed_size_bytes,
                observed_modified_at_epoch_ms = excluded.observed_modified_at_epoch_ms,
                stable_complete_offset = excluded.stable_complete_offset,
                boundary_hash = excluded.boundary_hash,
                parser_state_json = excluded.parser_state_json,
                parser_state_hash = excluded.parser_state_hash,
                trailing_partial_bytes = excluded.trailing_partial_bytes,
                safe_opaque_oversized_records = excluded.safe_opaque_oversized_records,
                safe_null_padding_records = excluded.safe_null_padding_records,
                reconciliation_status = 'verified',
                invalidation_reason = NULL,
                last_verified_at_epoch_ms = excluded.last_verified_at_epoch_ms
            """,
            ("$filePath", checkpoint.FilePath),
            ("$rolloutId", checkpoint.RolloutId),
            ("$checkpointFormatRevision", checkpoint.CheckpointFormatRevision),
            ("$parserRevision", checkpoint.ParserRevision),
            ("$sourceIdentityKind", SourceIdentityKindToDb(checkpoint.SourceIdentity.Kind)),
            ("$sourceIdentity", checkpoint.SourceIdentity.Value),
            ("$observedSizeBytes", checkpoint.ObservedSizeBytes),
            ("$observedModifiedAtEpochMs", checkpoint.ObservedModifiedAtEpochMs),
            ("$stableCompleteOffset", checkpoint.StableCompleteOffset),
            ("$boundaryHash", checkpoint.BoundaryHash),
            ("$parserStateJson", checkpoint.ParserStateJson),
            ("$parserStateHash", checkpoint.ParserStateHash),
            ("$trailingPartialBytes", checkpoint.TrailingPartialBytes),
            ("$safeOpaqueOversizedRecords", checkpoint.SafeOpaqueOversizedRecords),
            ("$safeNullPaddingRecords", checkpoint.SafeNullPaddingRecords),
            ("$lastVerifiedAtEpochMs", checkpoint.LastVerifiedAtEpochMs));
    }

    private void PromoteRolloutWithinTransaction(
        SqliteTransaction transaction,
        string rolloutId,
        string canonicalFilePath,
        long promotedAtEpochMs,
        string? resolvedConflictSourcePath)
    {
        if (resolvedConflictSourcePath is not null)
        {
            if (string.Equals(canonicalFilePath, resolvedConflictSourcePath, StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery candidate cannot be the conflicted source");
            var resolvedConflictRollout = ExecuteNullableScalarString(
                transaction,
                "SELECT rollout_id FROM source_files WHERE file_path = $filePath AND canonical_status = 'conflict'",
                ("$filePath", resolvedConflictSourcePath));
            if (!string.Equals(resolvedConflictRollout, rolloutId, StringComparison.Ordinal))
                throw new InvalidOperationException("Resolved conflict source is not an exact conflict for the rollout");
        }

        var sourceRollout = ExecuteNullableScalarString(
            transaction,
            "SELECT rollout_id FROM source_files WHERE file_path = $filePath AND is_present = 1",
            ("$filePath", canonicalFilePath));
        if (!string.Equals(sourceRollout, rolloutId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Canonical source is not a present candidate for the rollout");
        }

        var changes = ExecuteNonQuery(
            transaction,
            "UPDATE rollouts SET canonical_source_path = $path, updated_at_epoch_ms = $at WHERE rollout_id = $rolloutId",
            ("$path", canonicalFilePath),
            ("$at", promotedAtEpochMs),
            ("$rolloutId", rolloutId));
        if (changes != 1)
        {
            throw new InvalidOperationException($"Unknown rollout: {rolloutId}");
        }

        ExecuteNonQuery(
            transaction,
            """
            UPDATE source_files
            SET canonical_status = CASE
                WHEN file_path = $path THEN 'canonical'
                WHEN file_path = $resolvedConflictPath AND canonical_status = 'conflict' THEN 'candidate'
                WHEN canonical_status = 'canonical' THEN 'candidate'
                ELSE canonical_status
            END,
            last_error = CASE
                WHEN file_path = $resolvedConflictPath AND canonical_status = 'conflict' THEN NULL
                ELSE last_error
            END,
            last_scanned_at_epoch_ms = CASE
                WHEN file_path = $path THEN $at
                ELSE last_scanned_at_epoch_ms
            END
            WHERE rollout_id = $rolloutId
            """,
            ("$path", canonicalFilePath),
            ("$resolvedConflictPath", resolvedConflictSourcePath),
            ("$at", promotedAtEpochMs),
            ("$rolloutId", rolloutId));
        ExecuteNonQuery(
            transaction,
            "DELETE FROM rollout_checkpoints WHERE rollout_id = $rolloutId AND file_path <> $path",
            ("$rolloutId", rolloutId),
            ("$path", canonicalFilePath));
    }

    private long InsertDiagnostic(SqliteTransaction transaction, CollectorDiagnosticInput input)
    {
        ValidateDiagnostic(input);
        ExecuteNonQuery(transaction, """
            INSERT INTO collector_diagnostics (
                run_id, source_file_path, severity, code, message, details_json, created_at_epoch_ms
            ) VALUES ($runId, $path, $severity, $code, $message, $details, $at)
            """,
            ("$runId", input.RunId), ("$path", input.SourceFilePath),
            ("$severity", DiagnosticSeverityToDb(input.Severity)), ("$code", input.Code),
            ("$message", input.Message), ("$details", input.DetailsJson), ("$at", input.CreatedAtEpochMs));
        return ExecuteScalarLong(transaction, "SELECT last_insert_rowid()");
    }

    private void SetCollectorStateWithinTransaction(
        SqliteTransaction transaction,
        string key,
        string value,
        long updatedAtEpochMs)
    {
        ExecuteNonQuery(transaction, """
            INSERT INTO collector_state (key, value, updated_at_epoch_ms)
            VALUES ($key, $value, $at)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at_epoch_ms = excluded.updated_at_epoch_ms
            """, ("$key", key), ("$value", value), ("$at", updatedAtEpochMs));
    }

    private IReadOnlyList<string> ReadStringList(string rolloutId, string sql)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        using var command = CreateCommand(null, sql, ("$rolloutId", rolloutId));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private IReadOnlyList<string> ReadEventSignatureTuples(string rolloutId, bool includeModel)
    {
        RequireText(rolloutId, nameof(rolloutId));
        AssertOpen();
        var sql = includeModel
            ? """
              SELECT timestamp_epoch_ms, model, input_tokens, cached_input_tokens,
                     output_tokens, reasoning_output_tokens
              FROM usage_events WHERE rollout_id = $rolloutId ORDER BY token_event_ordinal
              """
            : """
              SELECT timestamp_epoch_ms, input_tokens, cached_input_tokens,
                     output_tokens, reasoning_output_tokens
              FROM usage_events WHERE rollout_id = $rolloutId ORDER BY token_event_ordinal
              """;
        using var command = CreateCommand(null, sql, ("$rolloutId", rolloutId));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            object[] values = new object[reader.FieldCount];
            reader.GetValues(values);
            result.Add(JsonSerializer.Serialize(values));
        }

        return result;
    }

    private CollectorRunRecord? ReadCollectorRun(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        AssertOpen();
        using var command = CreateCommand(null, sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CollectorRunRecord(
                reader.GetString(0), reader.GetString(1), ParseCollectorRunStatus(reader.GetString(2)),
                reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9))
            : null;
    }

    private T WriteTransaction<T>(Func<SqliteTransaction, T> operation)
    {
        AssertOpen();
        using var transaction = _connection.BeginTransaction(deferred: false);
        try
        {
            var result = operation(transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (InvalidOperationException)
            {
                // Preserve the error that caused the transaction to fail.
            }

            throw;
        }
    }

    private SqliteCommand CreateCommand(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return command;
    }

    private int ExecuteNonQuery(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private long ExecuteScalarLong(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(transaction, sql, parameters);
        var value = command.ExecuteScalar()
            ?? throw new InvalidOperationException("SQLite returned no scalar value.");
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string ExecuteScalarString(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(transaction, sql, parameters);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("SQLite returned no string value.");
    }

    private string? ExecuteNullableScalarString(
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(transaction, sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    private bool RolloutExists(SqliteTransaction transaction, string rolloutId) =>
        ExecuteScalarLong(
            transaction,
            "SELECT EXISTS(SELECT 1 FROM rollouts WHERE rollout_id = $rolloutId)",
            ("$rolloutId", rolloutId)) == 1;

    private bool SourceExists(SqliteTransaction transaction, string filePath) =>
        ExecuteScalarLong(
            transaction,
            "SELECT EXISTS(SELECT 1 FROM source_files WHERE file_path = $filePath)",
            ("$filePath", filePath)) == 1;

    private bool HasRolloutColumn(SqliteTransaction transaction, string columnName) =>
        ExecuteScalarLong(
            transaction,
            "SELECT EXISTS(SELECT 1 FROM pragma_table_info('rollouts') WHERE name = $columnName)",
            ("$columnName", columnName)) == 1;

    private static void AddListFilter(
        ICollection<string> conditions,
        ICollection<(string Name, object? Value)> parameters,
        string column,
        IReadOnlyList<string>? values,
        string prefix)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        var names = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            RequireText(values[index], $"{prefix}[{index}]");
            var name = $"${prefix}{index}";
            names.Add(name);
            parameters.Add((name, values[index]));
        }

        conditions.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private static SourceFileInput ToSource(CandidateSourceInput source, string rolloutId)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus,
            source.CanonicalStatus, source.IsPresent, source.LastScannedAtEpochMs,
            source.LastError);
    }

    private static SourceFileInput ToCanonicalSource(CanonicalSourceInput source, string rolloutId)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus,
            CanonicalStatus.Canonical, true, source.LastScannedAtEpochMs,
            source.LastError);
    }

    private static void ValidateMetadata(RolloutMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        RequireText(metadata.RolloutId, nameof(metadata.RolloutId));
        RequireText(metadata.ConversationId, nameof(metadata.ConversationId));
        ArgumentNullException.ThrowIfNull(metadata.ParentThreadId);
        RequireText(metadata.AgentRole, nameof(metadata.AgentRole));
        ArgumentNullException.ThrowIfNull(metadata.AgentPath);
        ArgumentNullException.ThrowIfNull(metadata.AgentNickname);
        RequireText(metadata.ProjectName, nameof(metadata.ProjectName));
        ArgumentNullException.ThrowIfNull(metadata.ThreadTitle);
        RequireNonNegative(metadata.LastActivityEpochMs, nameof(metadata.LastActivityEpochMs));
    }

    private static void ValidateEvents(IReadOnlyList<UsageEventInput> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var item in events)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireNonNegative(item.TokenEventOrdinal, nameof(item.TokenEventOrdinal));
            RequireNonNegative(item.TimestampEpochMs, nameof(item.TimestampEpochMs));
            RequireText(item.Model, nameof(item.Model));
            RequireNonNegative(item.InputTokens, nameof(item.InputTokens));
            RequireNonNegative(item.CachedInputTokens, nameof(item.CachedInputTokens));
            RequireNonNegative(item.OutputTokens, nameof(item.OutputTokens));
            RequireNonNegative(item.ReasoningOutputTokens, nameof(item.ReasoningOutputTokens));
            RequireText(item.EventSignature, nameof(item.EventSignature));
            if (item.CachedInputTokens > item.InputTokens)
            {
                throw new ArgumentOutOfRangeException(nameof(item.CachedInputTokens));
            }

            if (item.ReasoningOutputTokens > item.OutputTokens)
            {
                throw new ArgumentOutOfRangeException(nameof(item.ReasoningOutputTokens));
            }
        }
    }

    private static void ValidateSource(SourceFileInput source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireText(source.FilePath, nameof(source.FilePath));
        RequireOptionalText(source.RolloutId, nameof(source.RolloutId));
        RequireNonNegative(source.SizeBytes, nameof(source.SizeBytes));
        RequireNonNegative(source.ModifiedAtEpochMs, nameof(source.ModifiedAtEpochMs));
        RequireNonNegative(source.ByteOffset, nameof(source.ByteOffset));
        if (source.ByteOffset > source.SizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(source.ByteOffset));
        }

        ArgumentNullException.ThrowIfNull(source.PrefixHash);
        RequireNonNegative(source.LastScannedAtEpochMs, nameof(source.LastScannedAtEpochMs));
    }

    private static void ValidateCheckpoint(RolloutCheckpointInput checkpoint, SourceFileInput source)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(checkpoint.SourceIdentity);
        RequireText(checkpoint.FilePath, nameof(checkpoint.FilePath));
        RequireText(checkpoint.RolloutId, nameof(checkpoint.RolloutId));
        RequireText(checkpoint.SourceIdentity.Value, nameof(checkpoint.SourceIdentity.Value));
        RequireText(checkpoint.BoundaryHash, nameof(checkpoint.BoundaryHash));
        RequireText(checkpoint.ParserStateJson, nameof(checkpoint.ParserStateJson));
        RequireText(checkpoint.ParserStateHash, nameof(checkpoint.ParserStateHash));
        if (checkpoint.CheckpointFormatRevision <= 0 || checkpoint.ParserRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkpoint), "Checkpoint revisions must be positive.");
        RequireNonNegative(checkpoint.ObservedSizeBytes, nameof(checkpoint.ObservedSizeBytes));
        RequireNonNegative(checkpoint.ObservedModifiedAtEpochMs, nameof(checkpoint.ObservedModifiedAtEpochMs));
        RequireNonNegative(checkpoint.StableCompleteOffset, nameof(checkpoint.StableCompleteOffset));
        RequireNonNegative(checkpoint.TrailingPartialBytes, nameof(checkpoint.TrailingPartialBytes));
        RequireNonNegative(checkpoint.SafeOpaqueOversizedRecords, nameof(checkpoint.SafeOpaqueOversizedRecords));
        RequireNonNegative(checkpoint.SafeNullPaddingRecords, nameof(checkpoint.SafeNullPaddingRecords));
        RequireNonNegative(checkpoint.LastVerifiedAtEpochMs, nameof(checkpoint.LastVerifiedAtEpochMs));
        if (checkpoint.StableCompleteOffset > checkpoint.ObservedSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(checkpoint.StableCompleteOffset));
        if (!string.Equals(checkpoint.FilePath, source.FilePath, StringComparison.Ordinal)
            || !string.Equals(checkpoint.RolloutId, source.RolloutId, StringComparison.Ordinal)
            || checkpoint.ObservedSizeBytes != source.SizeBytes
            || checkpoint.ObservedModifiedAtEpochMs != source.ModifiedAtEpochMs
            || checkpoint.StableCompleteOffset != source.ByteOffset
            || !string.Equals(checkpoint.BoundaryHash, source.PrefixHash, StringComparison.Ordinal)
            || !source.IsPresent || source.CanonicalStatus != CanonicalStatus.Canonical)
        {
            throw new ArgumentException("Checkpoint does not match its canonical source snapshot.", nameof(checkpoint));
        }
    }

    private static void ValidateDiagnostic(CollectorDiagnosticInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireOptionalText(input.RunId, nameof(input.RunId));
        RequireOptionalText(input.SourceFilePath, nameof(input.SourceFilePath));
        RequireText(input.Code, nameof(input.Code));
        RequireText(input.Message, nameof(input.Message));
        RequireNonNegative(input.CreatedAtEpochMs, nameof(input.CreatedAtEpochMs));
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }
    }

    private static void RequireOptionalText(string? value, string name)
    {
        if (value is not null)
        {
            RequireText(value, name);
        }
    }

    private static void RequireNonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal) switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("SQLite boolean value must be 0 or 1."),
    };

    private static string ThreadTypeToDb(ThreadType value) => value switch
    {
        ThreadType.Main => "main",
        ThreadType.Subagent => "subagent",
        ThreadType.GuardianReview => "guardian_review",
        ThreadType.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ThreadType ParseThreadType(string value) => value switch
    {
        "main" => ThreadType.Main,
        "subagent" => ThreadType.Subagent,
        "guardian_review" => ThreadType.GuardianReview,
        "unknown" => ThreadType.Unknown,
        _ => throw new InvalidDataException($"Unknown thread type: {value}"),
    };

    private static string PrefixStatusToDb(PrefixStatus value) => value switch
    {
        PrefixStatus.Unknown => "unknown",
        PrefixStatus.Matches => "matches",
        PrefixStatus.Diverged => "diverged",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PrefixStatus ParsePrefixStatus(string value) => value switch
    {
        "unknown" => PrefixStatus.Unknown,
        "matches" => PrefixStatus.Matches,
        "diverged" => PrefixStatus.Diverged,
        _ => throw new InvalidDataException($"Unknown prefix status: {value}"),
    };

    private static string CanonicalStatusToDb(CanonicalStatus value) => value switch
    {
        CanonicalStatus.Candidate => "candidate",
        CanonicalStatus.Canonical => "canonical",
        CanonicalStatus.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CanonicalStatus ParseCanonicalStatus(string value) => value switch
    {
        "candidate" => CanonicalStatus.Candidate,
        "canonical" => CanonicalStatus.Canonical,
        "conflict" => CanonicalStatus.Conflict,
        _ => throw new InvalidDataException($"Unknown canonical status: {value}"),
    };

    private static string SourceIdentityKindToDb(SourceIdentityKind value) => value switch
    {
        SourceIdentityKind.WindowsFileId => "windows-file-id",
        SourceIdentityKind.ConservativeStat => "conservative-stat",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SourceIdentityKind ParseSourceIdentityKind(string value) => value switch
    {
        "windows-file-id" => SourceIdentityKind.WindowsFileId,
        "conservative-stat" => SourceIdentityKind.ConservativeStat,
        _ => throw new InvalidDataException($"Unknown source identity kind: {value}"),
    };

    private static string DiagnosticSeverityToDb(DiagnosticSeverity value) => value switch
    {
        DiagnosticSeverity.Info => "info",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string CollectorRunStatusToDb(CollectorRunStatus value) => value switch
    {
        CollectorRunStatus.Running => "running",
        CollectorRunStatus.Succeeded => "succeeded",
        CollectorRunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CollectorRunStatus ParseCollectorRunStatus(string value) => value switch
    {
        "running" => CollectorRunStatus.Running,
        "succeeded" => CollectorRunStatus.Succeeded,
        "failed" => CollectorRunStatus.Failed,
        _ => throw new InvalidDataException($"Unknown collector run status: {value}"),
    };

    private void AssertOpen()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
    }
}
