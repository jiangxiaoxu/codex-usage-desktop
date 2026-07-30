using CodexUsage.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexUsage.Infrastructure.Tests;

public sealed class UsageStoreTests
{
    private static readonly RolloutMetadata Metadata = new(
        "conversation-1", "rollout-1", string.Empty, ThreadType.Main,
        "main", "/root", string.Empty);

    [Fact]
    public void EmptyDatabaseMigratesToExactSchemaV1AndRequiredPragmas()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        using (var store = new UsageStore(databasePath))
        {
            Assert.Equal(1, store.CurrentSchemaVersion);
        }

        using var connection = Open(databasePath);
        Assert.Equal(1L, ScalarLong(connection, "PRAGMA user_version"));
        Assert.Equal(1L, ScalarLong(connection, "PRAGMA foreign_keys"));
        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode"));
        Assert.Equal(
            [
                "collector_diagnostics",
                "collector_runs",
                "collector_state",
                "rollouts",
                "source_files",
                "usage_events",
            ],
            ReadStrings(connection, """
                SELECT name FROM sqlite_schema
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name
                """));
        Assert.Equal(
            [
                "collector_diagnostics_run_idx",
                "source_files_rollout_idx",
                "usage_events_model_timestamp_idx",
                "usage_events_timestamp_idx",
            ],
            ReadStrings(connection, """
                SELECT name FROM sqlite_schema
                WHERE type = 'index' AND name NOT LIKE 'sqlite_%'
                ORDER BY name
                """));
    }

    [Fact]
    public void DatabaseCannotBeCreatedInsideProtectedObservationDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = Path.Combine(temporary.Path, ".codex");
        var sessions = Path.Combine(codexHome, "sessions");
        Directory.CreateDirectory(sessions);
        var policy = ProtectedPathPolicy.ForCodexHome(codexHome);
        var databasePath = Path.Combine(sessions, "usage.sqlite");

        Assert.Throws<InvalidOperationException>(() =>
            new UsageStore(databasePath, protectedPathPolicy: policy));
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public void AppendIsIdempotentAndOrdinalConflictRollsBackAtomically()
    {
        using var temporary = new TemporaryDirectory();
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"));
        var events = new[] { Event(1, 2_000), Event(0, 1_000) };

        Assert.Equal(new AppendEventsResult(2, 0), store.AppendEvents(Metadata, events, 3_000));
        Assert.Equal(new AppendEventsResult(0, 2), store.AppendEvents(Metadata, events, 4_000));
        Assert.Throws<InvalidOperationException>(() => store.AppendEvents(
            Metadata,
            [Event(2, 3_000), Event(0, 1_000, "different")],
            5_000));

        Assert.Equal(["signature-0", "signature-1"], store.GetRolloutEventSignatures(Metadata.RolloutId));
        Assert.Equal(["[1000,100,20,30,10]", "[2000,101,20,30,10]"],
            store.GetRolloutEventIdentities(Metadata.RolloutId));
        Assert.Equal([0L, 1L], store.QueryEvents(new UsageEventQuery(0, 10_000))
            .Select(item => item.TokenEventOrdinal));
    }

    [Fact]
    public void AppendRolloutSourceRollsBackEventsMetadataAndSourceTogether()
    {
        using var temporary = new TemporaryDirectory();
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"));
        var sourcePath = Path.Combine(temporary.Path, "rollout.jsonl");
        var initial = new AppendRolloutSourceInput(
            Metadata, [Event(0, 1_000, "original")], Source(sourcePath), 3_000);
        Assert.Equal(new AppendEventsResult(1, 0), store.AppendRolloutSource(initial));

        Assert.Throws<InvalidOperationException>(() => store.AppendRolloutSource(new AppendRolloutSourceInput(
            Metadata with { AgentRole = "must-roll-back" },
            [Event(1, 2_000), Event(0, 1_000, "conflict")],
            Source(sourcePath) with { ByteOffset = 900, LastScannedAtEpochMs = 5_000 },
            5_000)));

        Assert.Equal(["original"], store.GetRolloutEventSignatures(Metadata.RolloutId));
        Assert.Equal(3_000, store.ListSourceFiles().Single().LastScannedAtEpochMs);
        Assert.Equal("main", store.GetRolloutMetadata(Metadata.RolloutId)!.AgentRole);
    }

    [Fact]
    public void CanonicalReplacementRollsBackEveryChangeWhenPromotionFails()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var oldCanonicalPath = Path.Combine(temporary.Path, "old-canonical.jsonl");
        var replacementPath = Path.Combine(temporary.Path, "replacement.jsonl");
        using var store = new UsageStore(databasePath);
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            Metadata,
            [Event(0, 1_000, "old")],
            CanonicalSource(oldCanonicalPath),
            2_000));
        var oldSource = store.ListSourceFiles().Single();

        using (var connection = Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_canonical_promotion
                BEFORE UPDATE OF canonical_source_path ON rollouts
                BEGIN
                    SELECT RAISE(ABORT, 'promotion blocked');
                END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => store.ReplaceCanonicalRollout(
            new ReplaceCanonicalRolloutInput(
                Metadata with { AgentRole = "must-roll-back" },
                [Event(0, 2_000, "replacement")],
                CanonicalSource(replacementPath) with
                {
                    SizeBytes = 2_000,
                    ByteOffset = 2_000,
                    LastScannedAtEpochMs = 5_000,
                },
                5_000)));

        Assert.Equal(oldCanonicalPath, store.GetCanonicalSourcePath(Metadata.RolloutId));
        Assert.Equal(["old"], store.GetRolloutEventSignatures(Metadata.RolloutId));
        Assert.Equal("main", store.GetRolloutMetadata(Metadata.RolloutId)!.AgentRole);
        Assert.Equal([oldSource], store.ListSourceFiles());
    }

    [Fact]
    public void CanonicalReplacementCommitsEventsSourceAndPromotionTogether()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var oldCanonicalPath = Path.Combine(temporary.Path, "old-canonical.jsonl");
        var replacementPath = Path.Combine(temporary.Path, "replacement.jsonl");
        using var store = new UsageStore(databasePath);
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            Metadata,
            [Event(0, 1_000, "old")],
            CanonicalSource(oldCanonicalPath),
            2_000));

        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            Metadata with { AgentRole = "replacement" },
            [Event(0, 2_000, "replacement")],
            CanonicalSource(replacementPath) with
            {
                SizeBytes = 2_000,
                ByteOffset = 2_000,
                LastScannedAtEpochMs = 5_000,
            },
            5_000));

        Assert.Equal(replacementPath, store.GetCanonicalSourcePath(Metadata.RolloutId));
        Assert.Equal(["replacement"], store.GetRolloutEventSignatures(Metadata.RolloutId));
        Assert.Equal("replacement", store.GetRolloutMetadata(Metadata.RolloutId)!.AgentRole);
        var sources = store.ListSourceFiles();
        Assert.Equal(CanonicalStatus.Candidate,
            sources.Single(item => item.FilePath == oldCanonicalPath).CanonicalStatus);
        Assert.Equal(CanonicalStatus.Canonical,
            sources.Single(item => item.FilePath == replacementPath).CanonicalStatus);
    }

    [Fact]
    public void RecoverDivergedCanonicalReplacesOneRolloutAtomicallyAndPreservesDiagnosticsAndSiblings()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(temporary.Path, "canonical.jsonl");
        var siblingPath = Path.Combine(temporary.Path, "archived.jsonl");
        long diagnosticId;
        using (var store = new UsageStore(databasePath))
        {
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                Metadata, [Event(0, 1_000, "old")],
                CanonicalSource(canonicalPath) with
                {
                    PrefixStatus = PrefixStatus.Diverged,
                    LastError = "diverged",
                },
                2_000));
            store.UpsertSourceFile(ToSourceFile(
                Source(siblingPath) with
                {
                    PrefixStatus = PrefixStatus.Diverged,
                    CanonicalStatus = CanonicalStatus.Conflict,
                    LastError = "sibling conflict",
                }, Metadata.RolloutId));
            var siblingBefore = store.ListSourceFiles().Single(item => item.FilePath == siblingPath);
            diagnosticId = store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "source-diverged", "Canonical source diverged", "{}", 3_000));

            store.RecoverDivergedCanonicalSource(new RecoverDivergedCanonicalSourceInput(
                Metadata with { AgentRole = "recovered" },
                [Event(0, 1_500, "new-0"), Event(1, 2_500, "new-1")],
                RecoverableSource(canonicalPath),
                5_000));

            Assert.Equal(canonicalPath, store.GetCanonicalSourcePath(Metadata.RolloutId));
            Assert.Equal(["new-0", "new-1"], store.GetRolloutEventSignatures(Metadata.RolloutId));
            Assert.Equal("recovered", store.GetRolloutMetadata(Metadata.RolloutId)!.AgentRole);
            Assert.Equal(siblingBefore, store.ListSourceFiles().Single(item => item.FilePath == siblingPath));
            Assert.Equal(1, store.CountSourceConflicts());
        }

        Assert.True(diagnosticId > 0);
        using var connection = Open(databasePath);
        Assert.Equal(1, ScalarLong(connection, "SELECT count(*) FROM collector_diagnostics"));
    }

    [Fact]
    public void FailedCanonicalReplacementRestoresMetadataEventsAndSource()
    {
        using var temporary = new TemporaryDirectory();
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"));
        var canonicalPath = Path.Combine(temporary.Path, "canonical.jsonl");
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            Metadata, [Event(0, 1_000, "old")], CanonicalSource(canonicalPath), 2_000));
        var before = store.ListSourceFiles().Single();

        Assert.Throws<SqliteException>(() => store.RecoverDivergedCanonicalSource(
            new RecoverDivergedCanonicalSourceInput(
                Metadata with { AgentRole = "must-roll-back" },
                [Event(0, 2_000, "duplicate"), Event(1, 3_000, "duplicate")],
                RecoverableSource(canonicalPath),
                5_000)));

        Assert.Equal("main", store.GetRolloutMetadata(Metadata.RolloutId)!.AgentRole);
        Assert.Equal(["old"], store.GetRolloutEventSignatures(Metadata.RolloutId));
        Assert.Equal(before, store.ListSourceFiles().Single());
    }

    [Fact]
    public void MissingCanonicalSourceCanReappearWithoutDeletingPermanentUsage()
    {
        using var temporary = new TemporaryDirectory();
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"));
        var canonicalPath = Path.Combine(temporary.Path, "canonical.jsonl");
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            Metadata, [Event(0, 1_000, "old")], CanonicalSource(canonicalPath), 2_000));

        Assert.True(store.MarkSourceMissing(canonicalPath, 3_000));
        Assert.Equal(0, store.CountPresentSources());
        Assert.Single(store.QueryEvents(new UsageEventQuery(0, 10_000)));

        store.RecoverDivergedCanonicalSource(new RecoverDivergedCanonicalSourceInput(
            Metadata, [Event(0, 2_000, "recovered")], RecoverableSource(canonicalPath), 5_000));
        Assert.Equal(["recovered"], store.GetRolloutEventSignatures(Metadata.RolloutId));
        Assert.True(store.ListSourceFiles().Single().IsPresent);
    }

    [Fact]
    public void CollectorRunDiagnosticsAndStatePersistAcrossRestart()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        using (var store = new UsageStore(databasePath))
        {
            store.BeginCollectorRun(new CollectorRunStartInput("run-1", "startup", 2_000));
            store.HeartbeatCollector(new CollectorRunHeartbeatInput(
                "run-1", 3_000, new Dictionary<string, string> { ["phase"] = "reconcile" }));
            store.AddDiagnostic(new CollectorDiagnosticInput(
                "run-1", null, DiagnosticSeverity.Warning, "sample", "sample warning", null, 3_500));
            store.FinishCollectorRun(new CollectorRunFinishInput(
                "run-1", CollectorRunStatus.Succeeded, 4_000, 1, 2, 1, null));
            store.BeginCollectorRun(new CollectorRunStartInput("run-2", "watcher", 5_000));
        }

        using var reopened = new UsageStore(databasePath);
        Assert.Equal("reconcile", reopened.GetCollectorState("phase"));
        Assert.Equal(new CollectorRunRecord(
            "run-1", "startup", CollectorRunStatus.Succeeded, 2_000, 4_000,
            4_000, 1, 2, 1, null), reopened.GetCollectorRun("run-1"));
        Assert.Equal("run-2", reopened.GetLatestCollectorRun()!.RunId);
        Assert.Equal(CollectorRunStatus.Running, reopened.GetLatestCollectorRun()!.Status);
    }

    [Fact]
    public void QueryUsesHalfOpenEpochIntervalAndAllFilters()
    {
        using var temporary = new TemporaryDirectory();
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"));
        store.AppendEvents(Metadata, [Event(0, 1_000), Event(1, 2_000), Event(2, 3_000)], 3_000);

        var events = store.QueryEvents(new UsageEventQuery(
            1_000, 3_000, ["gpt-5.6-sol"], ["main"], [ThreadType.Main], "rollout-1"));

        Assert.Equal([1_000L, 2_000L], events.Select(item => item.TimestampEpochMs));
        Assert.Equal(DateTimeOffset.Parse("1970-01-01T00:00:01Z"), events[0].TimestampUtc);
    }

    private static UsageEventInput Event(long ordinal, long timestamp, string? signature = null) => new(
        ordinal, timestamp, "gpt-5.6-sol", 100 + ordinal, 20, 30, 10,
        signature ?? $"signature-{ordinal}");

    private static CandidateSourceInput Source(string path) => new(
        path, 1_000, 2_000, 1_000, "prefix", PrefixStatus.Matches,
        CanonicalStatus.Candidate, true, 3_000, null);

    private static CanonicalSourceInput CanonicalSource(string path) => new(
        path, 1_000, 2_000, 1_000, "prefix", PrefixStatus.Matches, 3_000, null);

    private static RecoverableCanonicalSourceInput RecoverableSource(string path) => new(
        path, 1_200, 4_000, 1_200, "recovered-prefix", 5_000);

    private static SourceFileInput ToSourceFile(CandidateSourceInput source, string rolloutId) => new(
        source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
        source.ByteOffset, source.PrefixHash, source.PrefixStatus,
        source.CanonicalStatus, source.IsPresent, source.LastScannedAtEpochMs,
        source.LastError);

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static IReadOnlyList<string> ReadStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
