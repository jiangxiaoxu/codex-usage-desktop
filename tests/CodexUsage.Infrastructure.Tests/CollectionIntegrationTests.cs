using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexUsage.Infrastructure.Tests;

public sealed class CollectionIntegrationTests
{
    [Fact]
    public void SessionIndexParserUsesLatestValidUpdatedAtRecordAndRejectsInvalidRecords()
    {
        const string conversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        var result = SessionIndexParser.Parse(Encoding.UTF8.GetBytes("""
            {"id":"019fe0d7-dd64-7412-8fa0-ea96334569dd","thread_name":"Earlier title","updated_at":"2026-08-08T10:00:00Z"}
            {"id":"019fe0d7-dd64-7412-8fa0-ea96334569dd","thread_name":"Latest title","updated_at":"2026-08-08T10:01:00Z"}
            {"id":"not-a-conversation","thread_name":"Invalid ID","updated_at":"2026-08-08T10:02:00Z"}
            {"id":"019fe0d7-dd64-7412-8fa0-ea96334569dd","thread_name":"","updated_at":"2026-08-08T10:03:00Z"}
            {"id":"019fe0d7-dd64-7412-8fa0-ea96334569dd","thread_name":"Invalid timestamp","updated_at":"not-a-timestamp"}
            """));

        Assert.Equal("Latest title", Assert.Single(result.ThreadTitles).Value);
        Assert.True(result.ThreadTitles.ContainsKey(conversationId));
        Assert.Equal(3, result.InvalidRecords);
        Assert.False(result.IsAuthoritative);
    }

    [Fact]
    public async Task MainSessionMetadataCwdProjectIgnoresStateDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string conversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        using (var connection = OpenDatabase(Path.Combine(codexHome, "state_5.sqlite")))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE threads (id TEXT PRIMARY KEY, cwd TEXT);
                INSERT INTO threads (id, cwd) VALUES
                    ('019fe0d7-dd64-7412-8fa0-ea96334569dd', 'C:/Projects/incorrect-state-project');
                """;
            command.ExecuteNonQuery();
        }
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-project.jsonl"), string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = conversationId,
                id = conversationId,
                thread_source = "user",
                cwd = "C:/Projects/fallback-project",
            }),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
        ]) + "\n");
        await using var collector = CreateCollector(codexHome, temporary.Path);

        await StartAndWaitForInventoryAsync(collector);

        Assert.Equal("fallback-project", Assert.Single(
            await collector.QueryRecentMainThreadsAsync(10)).ProjectName);
    }

    [Fact]
    public async Task SessionIndexRefreshOverridesExistingMainThreadTitleAndWatcherRefreshesIt()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string conversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-session-index.jsonl");
        WriteRollout(rolloutPath, Rollout(conversationId, Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteSessionIndex(codexHome, conversationId, "First official title", "2026-08-08T10:00:00Z");
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        await StartAndWaitForInventoryAsync(collector);

        var initial = Assert.Single(await collector.QueryRecentMainThreadsAsync(10));
        Assert.Equal("First official title", initial.Title);

        WriteSessionIndex(codexHome, conversationId, "Updated official title", "2026-08-08T10:01:00Z");
        collector.EnqueueSessionIndexObservationForTest();
        var updated = await WaitForSessionIndexTitleAsync(collector, "Updated official title");

        Assert.Equal(conversationId, updated.ConversationId);
    }

    [Fact]
    public async Task InitialInventoryParsesAndQueriesUsage()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl"),
            Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(CollectorPhase.Watching, status.Phase);
        var usage = Assert.Single(events);
        Assert.Equal("rollout-one", usage.RolloutId);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal("gpt-5.6-sol", usage.Model);
    }

    [Fact]
    public async Task SafeOversizedOpaqueRecordStillImportsMetadataAndTokenUsageAsPartial()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-oversized-safe.jsonl");
        WriteRollout(rolloutPath, string.Join('\n',
        [
            Line("session_meta", new { session_id = "oversized-safe", id = "oversized-safe", thread_source = "user" }),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
            Line("response_item", new
            {
                type = "custom_tool_call_output",
                output = new string('x', RolloutParser.CooperativeHardMaximumRecordBytes + 1024),
            }),
            Line("event_msg", new
            {
                type = "mcp_tool_call_end",
                result = new string('x', RolloutParser.CooperativeHardMaximumRecordBytes + 1024),
            }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
        ]) + "\n");

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal(10, usage.InputTokens);
        Assert.Equal("gpt-5.6-sol", usage.Model);
        Assert.Equal(CollectorPhase.Partial, status.Phase);
        Assert.Null(status.LastSuccessfulInventoryUtc);
        Assert.Equal(1, status.Diagnostics.PartialSources);
        Assert.Equal(2, status.Diagnostics.SafeOpaqueOversizedRecordsSkipped);
        using var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.StartsWith("partial-opaque-oversized:",
            Assert.Single(store.ListSourceFiles()).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeOversizedCriticalUnknownAndMalformedSourcesDoNotBlockOtherInventorySources()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var unsafePath = Path.Combine(codexHome, "sessions", "rollout-a-unsafe.jsonl");
        var unknownPath = Path.Combine(codexHome, "sessions", "rollout-b-unknown.jsonl");
        var malformedPath = Path.Combine(codexHome, "sessions", "rollout-c-malformed.jsonl");
        var validPath = Path.Combine(codexHome, "sessions", "rollout-z-valid.jsonl");
        WriteRollout(unsafePath, string.Join('\n',
        [
            Line("session_meta", new { session_id = "unsafe", id = "unsafe", thread_source = "user" }),
            Line("event_msg", new
            {
                type = "token_count",
                padding = new string('x', RolloutParser.CooperativeHardMaximumRecordBytes + 1024),
            }),
        ]) + "\n");
        WriteRollout(unknownPath, Line("future_record", new
        {
            type = "future_payload",
            padding = new string('x', RolloutParser.CooperativeHardMaximumRecordBytes + 1024),
        }) + "\n");
        WriteRollout(malformedPath,
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\""
            + new string('x', RolloutParser.CooperativeHardMaximumRecordBytes + 1024) + "\"}\n");
        WriteRollout(validPath, Rollout(
            "valid", Token([7, 1, 3, 1, 10], [7, 1, 3, 1, 10])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal("valid", usage.RolloutId);
        Assert.Equal(7, usage.InputTokens);
        Assert.Equal(CollectorPhase.Degraded, status.Phase);
        Assert.Null(status.LastSuccessfulInventoryUtc);
    }

    [Fact]
    public async Task UsageRevisionOnlyAdvancesWhenLedgerVisibleUsageChanges()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-revision.jsonl");
        WriteRollout(rolloutPath,
            Rollout("rollout-revision", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var initial = await StartAndWaitForInventoryAsync(collector);

        var unchanged = await collector.RefreshAsync();

        Assert.False(unchanged.UsageChanged);
        Assert.Equal(initial.UsageRevision, unchanged.Status.UsageRevision);

        WriteRollout(rolloutPath,
            Rollout("rollout-revision", Token([20, 3, 6, 2, 26], [20, 3, 6, 2, 26])));
        var changed = await collector.RefreshAsync();

        Assert.True(changed.UsageChanged);
        Assert.Equal(initial.UsageRevision + 1, changed.Status.UsageRevision);
    }

    [Fact]
    public async Task StableCanonicalSelfRewriteReplacesOnlyThatRollout()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var firstPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        var siblingPath = Path.Combine(codexHome, "sessions", "rollout-test-two.jsonl");
        WriteRollout(firstPath, Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(siblingPath, Rollout("rollout-two", Token([7, 1, 3, 1, 10], [7, 1, 3, 1, 10])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(firstPath, Rollout("rollout-one", Token([20, 3, 6, 2, 26], [20, 3, 6, 2, 26])));

        var sync = await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.True(sync.UsageChanged);
        Assert.Equal(2, events.Count);
        Assert.Equal(20, events.Single(value => value.RolloutId == "rollout-one").InputTokens);
        Assert.Equal(7, events.Single(value => value.RolloutId == "rollout-two").InputTokens);
        Assert.Equal(0, sync.Status.Conflicts);
    }

    [Fact]
    public async Task LongerArchiveCandidatePromotesWithoutDoubleCounting()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-live-one.jsonl"),
            Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(Path.Combine(codexHome, "archived_sessions", "rollout-archive-one.jsonl"),
            Rollout("rollout-one",
                Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
                Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z")));

        await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(2, events.Count);
        Assert.Equal([10L, 5L], events.Select(value => value.InputTokens));
    }

    [Fact]
    public async Task ArchivedAndDeletedMainSourceRetainsExactHistoricalThreadFilterAndRecentOption()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string mainConversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        const string childConversationId = "019fe0d7-dd65-7412-8fa0-ea96334569dd";
        var activePath = Path.Combine(codexHome, "sessions", "rollout-main.jsonl");
        var archivePath = Path.Combine(codexHome, "archived_sessions", "rollout-main.jsonl");
        var childPath = Path.Combine(codexHome, "sessions", "rollout-child.jsonl");
        WriteRollout(activePath, string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = mainConversationId,
                id = "main-rollout",
                thread_source = "user",
                cwd = "C:/Projects/thread-history",
            }),
            Line("turn_context", new { turn_id = "main-turn", model = "gpt-5.6-sol" }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
        ]) + "\n");
        WriteRollout(childPath, string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = childConversationId,
                id = "child-rollout",
                thread_source = "subagent",
                source = new
                {
                    subagent = new
                    {
                        thread_spawn = new
                        {
                            parent_thread_id = mainConversationId,
                            agent_role = "worker",
                            agent_path = "/root/worker",
                            agent_nickname = "worker-a",
                        },
                    },
                },
            }),
            Line("turn_context", new { turn_id = "child-turn", model = "gpt-5.6-sol" }),
            Token([5, 1, 2, 1, 7], [5, 1, 2, 1, 7], "2026-07-15T01:03:03.004Z"),
        ]) + "\n");
        var threadQuery = new UsageEventQuery(0, 4_102_444_800_000, MainThreadConversationId: mainConversationId);

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        Assert.Collection(
            await collector.QueryEventsAsync(threadQuery),
            value =>
            {
                Assert.Equal("main-rollout", value.RolloutId);
                Assert.Equal(ThreadType.Main, value.ThreadType);
            },
            value =>
            {
                Assert.Equal("child-rollout", value.RolloutId);
                Assert.Equal(ThreadType.Subagent, value.ThreadType);
            });

        File.Move(activePath, archivePath);
        var archived = await collector.RefreshAsync();
        var archivedEvents = await collector.QueryEventsAsync(threadQuery);

        Assert.False(archived.UsageChanged);
        Assert.Equal(["main-rollout", "child-rollout"], archivedEvents.Select(value => value.RolloutId));
        Assert.Equal(15, archivedEvents.Sum(value => value.InputTokens));
        var archivedRecent = Assert.Single(await collector.QueryRecentMainThreadsAsync(20));
        Assert.Equal(mainConversationId, archivedRecent.ConversationId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-15T01:03:03.004Z"), archivedRecent.LastActivityUtc);

        File.Delete(archivePath);
        var deleted = await collector.RefreshAsync();
        var deletedEvents = await collector.QueryEventsAsync(threadQuery);

        Assert.False(deleted.UsageChanged);
        Assert.Equal(["main-rollout", "child-rollout"], deletedEvents.Select(value => value.RolloutId));
        Assert.Equal(15, deletedEvents.Sum(value => value.InputTokens));
        var deletedRecent = Assert.Single(await collector.QueryRecentMainThreadsAsync(20));
        Assert.Equal(mainConversationId, deletedRecent.ConversationId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-15T01:03:03.004Z"), deletedRecent.LastActivityUtc);
    }

    [Fact]
    public async Task RealtimeVoiceSessionsAreDistinctUnbilledInventoryWithoutUsageEvents()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var activePath = Path.Combine(codexHome, "sessions", "rollout-voice-active.jsonl");
        var archivePath = Path.Combine(codexHome, "archived_sessions", "rollout-voice-archive.jsonl");
        var renamedArchivePath = Path.Combine(codexHome, "archived_sessions", "rollout-voice-renamed.jsonl");
        WriteRollout(activePath, VoiceRollout(
            "voice-session",
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(archivePath, VoiceRollout("voice-session"));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var initial = await StartAndWaitForInventoryAsync(collector);

        Assert.Equal(1, initial.RealtimeVoiceSessions);
        Assert.Equal(0, initial.UsageRevision);
        Assert.Empty(await collector.QueryEventsAsync(AllTimeQuery()));

        WriteRollout(activePath, VoiceRollout("voice-session"));
        var rewritten = await collector.RefreshAsync();
        Assert.False(rewritten.UsageChanged);
        Assert.Equal(initial.UsageRevision, rewritten.Status.UsageRevision);
        Assert.Equal(1, rewritten.Status.RealtimeVoiceSessions);

        File.Delete(activePath);
        var archiveOnly = await collector.RefreshAsync();
        Assert.False(archiveOnly.UsageChanged);
        Assert.Equal(initial.UsageRevision, archiveOnly.Status.UsageRevision);
        Assert.Equal(1, archiveOnly.Status.RealtimeVoiceSessions);

        File.Move(archivePath, renamedArchivePath);
        var renamed = await collector.RefreshAsync();
        Assert.False(renamed.UsageChanged);
        Assert.Equal(initial.UsageRevision, renamed.Status.UsageRevision);
        Assert.Equal(1, renamed.Status.RealtimeVoiceSessions);

        WriteRollout(activePath, VoiceRollout("voice-session"));
        var reappeared = await collector.RefreshAsync();
        Assert.False(reappeared.UsageChanged);
        Assert.Equal(initial.UsageRevision, reappeared.Status.UsageRevision);
        Assert.Equal(1, reappeared.Status.RealtimeVoiceSessions);

        File.Delete(renamedArchivePath);
        var activeOnly = await collector.RefreshAsync();
        Assert.False(activeOnly.UsageChanged);
        Assert.Equal(initial.UsageRevision, activeOnly.Status.UsageRevision);
        Assert.Equal(1, activeOnly.Status.RealtimeVoiceSessions);

        File.Delete(activePath);
        var missing = await collector.RefreshAsync();
        Assert.False(missing.UsageChanged);
        Assert.Equal(initial.UsageRevision, missing.Status.UsageRevision);
        Assert.Equal(0, missing.Status.RealtimeVoiceSessions);
    }

    [Fact]
    public async Task RealtimeVoiceParserRevisionDoesNotAdvanceUsageRevisionWhenLedgerUsageIsUnchanged()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        WriteRollout(
            Path.Combine(codexHome, "sessions", "rollout-voice.jsonl"),
            VoiceRollout("voice-session"));

        await using (var initialCollector = CreateCollector(codexHome, temporary.Path))
        {
            var initial = await StartAndWaitForInventoryAsync(initialCollector);
            Assert.Equal(1, initial.RealtimeVoiceSessions);
            Assert.Equal(0, initial.UsageRevision);
        }
        using (var store = new UsageStore(
                   databasePath,
                   protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            store.SetCollectorState("rollout_parser_revision", "6", 1);
        }

        await using var reparsingCollector = CreateCollector(codexHome, temporary.Path);
        var reparsed = await StartAndWaitForInventoryAsync(reparsingCollector);

        Assert.Equal(1, reparsed.RealtimeVoiceSessions);
        Assert.Equal(0, reparsed.UsageRevision);
        Assert.Empty(await reparsingCollector.QueryEventsAsync(AllTimeQuery()));
    }

    [Fact]
    public async Task StableMalformedCanonicalPreservesLedgerAndReportsConflict()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        WriteRollout(rolloutPath, Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(rolloutPath, "{not-json}\n");

        var sync = await collector.RefreshAsync();
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal(CollectorPhase.Degraded, sync.Status.Phase);
        Assert.Equal(1, sync.Status.Conflicts);
        Assert.Equal(10, usage.InputTokens);
    }

    [Fact]
    public async Task UnchangedConflictIsRetriedWithoutChangingPreservedLedger()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-conflict.jsonl");
        WriteRollout(rolloutPath, Rollout("conflict", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using (var first = CreateCollector(codexHome, temporary.Path))
        {
            await StartAndWaitForInventoryAsync(first);
            WriteRollout(rolloutPath, "{not-json}\n");
            var conflict = await first.RefreshAsync();
            Assert.Equal(1, conflict.Status.Conflicts);
        }

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.Conflicts);
        Assert.Equal(1, status.ChangedFilesLastSync);
        Assert.Equal(1, status.Diagnostics.FilesScanned);
        Assert.Equal(10, Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task RestartRehydratesCheckpointAndSubsequentAppendReadsOnlyBoundaryAndTail()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-checkpoint.jsonl");
        WriteRollout(rolloutPath, Rollout(
            "checkpoint",
            Line("response_item", new { type = "message", content = new string('x', 200_000) }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);

        long bytesRead = 0;
        var hooks = new CollectorTestHooks(SourceBytesRead: value => bytesRead += value);
        await using var restarted = CreateCollector(codexHome, temporary.Path, hooks);
        var restartedStatus = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(0, restartedStatus.ChangedFilesLastSync);
        Assert.InRange(bytesRead, 1, 3 * 64 * 1024);
        var bytesAfterRestart = bytesRead;
        File.AppendAllText(rolloutPath,
            Token([20, 4, 8, 2, 28], [30, 6, 12, 3, 42], "2026-07-15T02:02:03.004Z") + "\n");

        await restarted.RefreshAsync();

        Assert.True(bytesRead - bytesAfterRestart < new FileInfo(rolloutPath).Length / 2);
        var events = await restarted.QueryEventsAsync(AllTimeQuery());
        Assert.Equal(2, events.Count);
        Assert.Equal(30, events.Sum(value => value.InputTokens));
    }

    [Fact]
    public async Task RestartInvalidatesChangedFileIdentityAndFailsClosedToFullReconciliation()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-identity.jsonl");
        WriteRollout(rolloutPath, Rollout(
            "identity",
            Line("response_item", new { type = "message", content = new string('x', 100_000) }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using (var initial = CreateCollector(codexHome, temporary.Path,
                         new CollectorTestHooks(SourceIdentityReader: new FixedSourceIdentityReader("identity-a"))))
            await StartAndWaitForInventoryAsync(initial);

        long bytesRead = 0;
        var restartHooks = new CollectorTestHooks(
            SourceIdentityReader: new FixedSourceIdentityReader("identity-b"),
            SourceBytesRead: value => bytesRead += value);
        await using var restarted = CreateCollector(codexHome, temporary.Path, restartHooks);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.ChangedFilesLastSync);
        Assert.True(bytesRead >= new FileInfo(rolloutPath).Length);
        Assert.Equal(10, Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task RestartPreservesPartialTailUntilTheRecordGetsItsNewline()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-partial-tail.jsonl");
        var nextToken = Token([20, 4, 8, 2, 28], [30, 6, 12, 3, 42], "2026-07-15T02:02:03.004Z");
        var split = nextToken.Length / 2;
        WriteRollout(rolloutPath,
            Rollout("partial-tail", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]))
            + nextToken[..split]);

        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var restartedStatus = await StartAndWaitForInventoryAsync(restarted);
        Assert.Equal(0, restartedStatus.ChangedFilesLastSync);
        Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery()));

        File.AppendAllText(rolloutPath, nextToken[split..] + "\n");
        await restarted.RefreshAsync();

        var events = await restarted.QueryEventsAsync(AllTimeQuery());
        Assert.Equal(2, events.Count);
        Assert.Equal(30, events.Sum(value => value.InputTokens));
    }

    [Fact]
    public async Task RestartRejectsLedgerTailTokenTamperingEvenWhenOrdinalCountStillMatches()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-ledger-tamper.jsonl");
        WriteRollout(rolloutPath, Rollout("ledger-tamper", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        ExecuteDatabaseMutation(databasePath, "UPDATE usage_events SET input_tokens = 99 WHERE rollout_id = 'ledger-tamper'");

        long bytesRead = 0;
        await using var restarted = CreateCollector(codexHome, temporary.Path,
            new CollectorTestHooks(SourceBytesRead: value => bytesRead += value));
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.ChangedFilesLastSync);
        Assert.True(bytesRead >= new FileInfo(rolloutPath).Length);
        Assert.Equal(10, Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task RestartRejectsSelfHashedCheckpointSnapshotTampering()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-state-tamper.jsonl");
        WriteRollout(rolloutPath, Rollout("state-tamper", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        TamperCheckpointSnapshot(databasePath);

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.ChangedFilesLastSync);
        Assert.Equal(10, Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task OfflineAppendDoesNotBypassLedgerTailTamperReconciliation()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-offline-ledger-tamper.jsonl");
        WriteRollout(rolloutPath, Rollout(
            "offline-ledger-tamper",
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        ExecuteDatabaseMutation(databasePath,
            "UPDATE usage_events SET input_tokens = 99 WHERE rollout_id = 'offline-ledger-tamper'");
        File.AppendAllText(rolloutPath,
            Token([20, 4, 8, 2, 28], [30, 6, 12, 3, 42], "2026-07-15T02:02:03.004Z") + "\n");

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.ChangedFilesLastSync);
        var events = await restarted.QueryEventsAsync(AllTimeQuery());
        Assert.Equal(2, events.Count);
        Assert.Equal(30, events.Sum(value => value.InputTokens));
    }

    [Fact]
    public async Task OfflineAppendDoesNotBypassSelfHashedCheckpointSnapshotTampering()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-offline-state-tamper.jsonl");
        WriteRollout(rolloutPath, Rollout(
            "offline-state-tamper",
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        TamperCheckpointSnapshot(databasePath);
        File.AppendAllText(rolloutPath,
            Token([20, 4, 8, 2, 28], [30, 6, 12, 3, 42], "2026-07-15T02:02:03.004Z") + "\n");

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.ChangedFilesLastSync);
        var events = await restarted.QueryEventsAsync(AllTimeQuery());
        Assert.Equal(2, events.Count);
        Assert.Equal(30, events.Sum(value => value.InputTokens));
    }

    [Fact]
    public async Task RestartReverseScanSkipsHugeOpaqueTailWithoutAccumulatingTheRecord()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-huge-tail.jsonl");
        WriteRollout(rolloutPath, Rollout(
            "huge-tail",
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
            Line("response_item", new { type = "message", content = new string('x', 2 * 1024 * 1024) })));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);

        long bytesRead = 0;
        await using var restarted = CreateCollector(codexHome, temporary.Path,
            new CollectorTestHooks(SourceBytesRead: value => bytesRead += value));
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(0, status.ChangedFilesLastSync);
        Assert.InRange(bytesRead, 2 * 1024 * 1024, 4 * 1024 * 1024);
        Assert.Equal(10, Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task MissingCheckpointSourceDoesNotPreventOtherCheckpointFromRehydrating()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var missingPath = Path.Combine(codexHome, "sessions", "rollout-missing.jsonl");
        var survivingPath = Path.Combine(codexHome, "sessions", "rollout-surviving.jsonl");
        WriteRollout(missingPath, Rollout("missing", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(survivingPath, Rollout("surviving", Token([20, 4, 8, 2, 28], [20, 4, 8, 2, 28])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        File.Delete(missingPath);

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.ChangedFilesLastSync);
        Assert.Equal(2, (await restarted.QueryEventsAsync(AllTimeQuery())).Count);
        using var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(survivingPath, Assert.Single(store.ListRolloutCheckpoints()).FilePath);
    }

    [Fact]
    public async Task OfflineActiveToArchiveMoveBuildsCheckpointOnlyForSelectedCanonicalPath()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var activePath = Path.Combine(codexHome, "sessions", "rollout-moving.jsonl");
        var archivePath = Path.Combine(codexHome, "archived_sessions", "rollout-moving.jsonl");
        WriteRollout(activePath, Rollout("moving", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        File.Move(activePath, archivePath);

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.True(status.ChangedFilesLastSync >= 1);
        Assert.Equal(10, Assert.Single(await restarted.QueryEventsAsync(AllTimeQuery())).InputTokens);
        using var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(archivePath, store.GetCanonicalSourcePath("moving"));
        Assert.Equal(archivePath, Assert.Single(store.ListRolloutCheckpoints()).FilePath);
    }

    [Fact]
    public async Task RestartRehydratesUnbilledVoiceSourceWithRecordedTokenLines()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var voicePath = Path.Combine(codexHome, "sessions", "rollout-voice-empty.jsonl");
        WriteRollout(voicePath, VoiceRollout(
            "voice-empty",
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(0, status.ChangedFilesLastSync);
        Assert.Equal(1, status.RealtimeVoiceSessions);
        Assert.Empty(await restarted.QueryEventsAsync(AllTimeQuery()));
    }

    [Fact]
    public async Task UnavailableIdentityForOneCheckpointDoesNotBlockOtherCheckpointHitOrStartup()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var blockedPath = Path.Combine(codexHome, "sessions", "rollout-blocked.jsonl");
        var availablePath = Path.Combine(codexHome, "sessions", "rollout-available.jsonl");
        WriteRollout(blockedPath, Rollout("blocked", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(availablePath, Rollout("available", Token([20, 4, 8, 2, 28], [20, 4, 8, 2, 28])));
        var stableIdentity = new FixedSourceIdentityReader("stable-test-identity");
        await using (var initial = CreateCollector(codexHome, temporary.Path,
                         new CollectorTestHooks(SourceIdentityReader: stableIdentity)))
            await StartAndWaitForInventoryAsync(initial);

        var hooks = new CollectorTestHooks(
            SourceIdentityReader: new SelectivelyUnavailableSourceIdentityReader(blockedPath, "stable-test-identity"));
        await using var restarted = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(CollectorPhase.Degraded, status.Phase);
        Assert.Equal(2, (await restarted.QueryEventsAsync(AllTimeQuery())).Count);
        using var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(availablePath, Assert.Single(store.ListRolloutCheckpoints()).FilePath);
    }

    [Fact]
    public async Task MultipleConflictsReuseOneConfirmedCorpusScanPerInventory()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var firstConflict = Path.Combine(codexHome, "sessions", "rollout-conflict-one.jsonl");
        var secondConflict = Path.Combine(codexHome, "sessions", "rollout-conflict-two.jsonl");
        WriteRollout(firstConflict,
            Rollout("conflict-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(secondConflict,
            Rollout("conflict-two", Token([11, 2, 4, 1, 15], [11, 2, 4, 1, 15])));
        for (var index = 0; index < 20; index++)
        {
            WriteRollout(
                Path.Combine(codexHome, "sessions", $"rollout-unrelated-{index}.jsonl"),
                Rollout($"unrelated-{index}", Token([5, 1, 2, 1, 7], [5, 1, 2, 1, 7])));
        }
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        WriteRollout(firstConflict, "{not-json}\n");
        WriteRollout(secondConflict, "{also-not-json}\n");
        for (var index = 0; index < 10; index++)
        {
            WriteRollout(
                Path.Combine(codexHome, "sessions", $"rollout-z-new-{index}.jsonl"),
                Rollout($"new-{index}", Token([6, 1, 2, 1, 8], [6, 1, 2, 1, 8])));
        }
        var confirmedByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hooks = new CollectorTestHooks(AfterConfirmedRecoverySnapshot: path =>
            confirmedByPath[path] = confirmedByPath.GetValueOrDefault(path) + 1);

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);

        Assert.Equal(2, status.Conflicts);
        Assert.All(confirmedByPath.Values, count => Assert.Equal(1, count));
        Assert.Equal(10, confirmedByPath.Count);

        await collector.RefreshAsync();

        Assert.All(confirmedByPath.Values, count => Assert.Equal(1, count));
        Assert.Equal(10, confirmedByPath.Count);
    }

    [Fact]
    public async Task EqualFallbackAutomaticallyRecoversMalformedCanonicalWithoutWritingSources()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var fallbackPath = Path.Combine(codexHome, "archived_sessions", "rollout-fallback.jsonl");
        var content = Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(canonicalPath, content);
        WriteRollout(fallbackPath, content);

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        string actualCanonical;
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            actualCanonical = store.GetCanonicalSourcePath("rollout-one")!;
        var actualFallback = string.Equals(actualCanonical, canonicalPath, StringComparison.OrdinalIgnoreCase)
            ? fallbackPath : canonicalPath;
        var fallbackBytes = File.ReadAllBytes(actualFallback);
        WriteRollout(actualCanonical, "{not-json}\n");
        var damagedBytes = File.ReadAllBytes(actualCanonical);

        var sync = await collector.RefreshAsync();

        Assert.Equal(0, sync.Status.Conflicts);
        Assert.False(sync.UsageChanged);
        Assert.Equal(10, Assert.Single(await collector.QueryEventsAsync(AllTimeQuery())).InputTokens);
        Assert.Equal(fallbackBytes, File.ReadAllBytes(actualFallback));
        Assert.Equal(damagedBytes, File.ReadAllBytes(actualCanonical));
    }

    [Fact]
    public async Task ExtensionFallbackWinsConflictRecoveryAndReplacesLedger()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var fallbackPath = Path.Combine(codexHome, "archived_sessions", "rollout-extension.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        WriteRollout(canonicalPath, Rollout("rollout-one", first));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(fallbackPath, Rollout("rollout-one", first,
            Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z")));
        var canonicalBytes = File.ReadAllBytes(canonicalPath);
        var fallbackBytes = File.ReadAllBytes(fallbackPath);

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(0, status.Conflicts);
        Assert.Equal([10L, 5L], events.Select(value => value.InputTokens));
        Assert.Equal(canonicalBytes, File.ReadAllBytes(canonicalPath));
        Assert.Equal(fallbackBytes, File.ReadAllBytes(fallbackPath));
    }

    [Fact]
    public async Task EqualFallbackSelectionPrefersLargestStableSnapshotBeforePathOrder()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var shortPath = Path.Combine(codexHome, "archived_sessions", "rollout-a-short.jsonl");
        var longPath = Path.Combine(codexHome, "archived_sessions", "rollout-z-long.jsonl");
        var content = Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(canonicalPath, content);
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(shortPath, content);
        WriteRollout(longPath, content + Line("ignored_record", new { value = "padding" }) + "\n");

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);

        Assert.Equal(0, status.Conflicts);
        using var verified = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(longPath, verified.GetCanonicalSourcePath("rollout-one"));
    }

    [Fact]
    public async Task SelectedRecoveryCandidateIsReconfirmedAndUsesAppendedExtension()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var fallbackPath = Path.Combine(codexHome, "archived_sessions", "rollout-fallback.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        WriteRollout(canonicalPath, Rollout("rollout-one", first));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(fallbackPath, Rollout("rollout-one", first));
        var extension = Rollout("rollout-one", first,
            Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z"));
        var hooks = new CollectorTestHooks(AfterFullRecoveryIndexBuiltAsync: _ =>
        {
            WriteRollout(fallbackPath, extension);
            return ValueTask.CompletedTask;
        });

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(0, status.Conflicts);
        Assert.Equal([10L, 5L], events.Select(value => value.InputTokens));
        Assert.Equal(Encoding.UTF8.GetBytes(extension), File.ReadAllBytes(fallbackPath));
    }

    [Fact]
    public async Task DivergedSelectedCandidateIsRejectedAndNextCandidateIsReconfirmed()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var preferredPath = Path.Combine(codexHome, "archived_sessions", "rollout-a-preferred.jsonl");
        var fallbackPath = Path.Combine(codexHome, "archived_sessions", "rollout-z-fallback.jsonl");
        var content = Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(canonicalPath, content);
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(preferredPath, content + Line("ignored_record", new { value = "padding" }) + "\n");
        WriteRollout(fallbackPath, content);
        var hooks = new CollectorTestHooks(AfterFullRecoveryIndexBuiltAsync: _ =>
        {
            WriteRollout(preferredPath,
                Rollout("rollout-one", Token([99, 2, 4, 1, 103], [99, 2, 4, 1, 103])));
            return ValueTask.CompletedTask;
        });

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal(0, status.Conflicts);
        Assert.Equal(10, usage.InputTokens);
        using var verified = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(fallbackPath, verified.GetCanonicalSourcePath("rollout-one"));
    }

    [Fact]
    public async Task FreshExtensionOutranksDiscoveryWinnerRewrittenToEqual()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var staleWinnerPath = Path.Combine(codexHome, "archived_sessions", "rollout-a-stale-winner.jsonl");
        var freshWinnerPath = Path.Combine(codexHome, "archived_sessions", "rollout-z-fresh-winner.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        var second = Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z");
        var third = Token([3, 1, 1, 0, 4], [18, 4, 7, 2, 25], "2026-07-15T01:04:03.004Z");
        var canonicalContent = Rollout("rollout-one", first);
        WriteRollout(canonicalPath, canonicalContent);
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(staleWinnerPath, Rollout("rollout-one", first, second, third));
        WriteRollout(freshWinnerPath, Rollout("rollout-one", first, second));
        var hooks = new CollectorTestHooks(AfterFullRecoveryIndexBuiltAsync: _ =>
        {
            WriteRollout(staleWinnerPath, canonicalContent);
            return ValueTask.CompletedTask;
        });

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(0, status.Conflicts);
        Assert.Equal([10L, 5L], events.Select(value => value.InputTokens));
        using var verified = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(freshWinnerPath, verified.GetCanonicalSourcePath("rollout-one"));
    }

    [Fact]
    public async Task FreshStableLengthThenMtimeDetermineEqualCandidateWinner()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var staleWinnerPath = Path.Combine(codexHome, "archived_sessions", "rollout-a-stale-winner.jsonl");
        var olderLongPath = Path.Combine(codexHome, "archived_sessions", "rollout-b-older-long.jsonl");
        var newerLongPath = Path.Combine(codexHome, "archived_sessions", "rollout-c-newer-long.jsonl");
        var content = Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        var longContent = content + Line("ignored_record", new { value = "same-padding" }) + "\n";
        WriteRollout(canonicalPath, content);
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(staleWinnerPath, longContent + Line("ignored_record", new { value = "stale-extra" }) + "\n");
        WriteRollout(olderLongPath, content);
        WriteRollout(newerLongPath, content);
        var hooks = new CollectorTestHooks(AfterFullRecoveryIndexBuiltAsync: _ =>
        {
            WriteRollout(staleWinnerPath, content);
            WriteRollout(olderLongPath, longContent);
            WriteRollout(newerLongPath, longContent);
            var baseline = DateTime.UtcNow.AddMinutes(-2);
            File.SetLastWriteTimeUtc(olderLongPath, baseline);
            File.SetLastWriteTimeUtc(newerLongPath, baseline.AddMinutes(1));
            File.SetLastWriteTimeUtc(staleWinnerPath, baseline.AddMinutes(2));
            return ValueTask.CompletedTask;
        });

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);

        Assert.Equal(0, status.Conflicts);
        using var verified = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(newerLongPath, verified.GetCanonicalSourcePath("rollout-one"));
        Assert.Equal(10, Assert.Single(await collector.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task ChangedCanonicalIdentityRecoversOldRolloutThenIngestsNewRolloutIndependently()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var firstPath = Path.Combine(codexHome, "sessions", "rollout-live.jsonl");
        var secondPath = Path.Combine(codexHome, "archived_sessions", "rollout-copy.jsonl");
        var oldContent = Rollout("rollout-old", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(firstPath, oldContent);
        WriteRollout(secondPath, oldContent);
        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        string canonicalPath;
        using (var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            canonicalPath = store.GetCanonicalSourcePath("rollout-old")!;
        var fallbackPath = string.Equals(canonicalPath, firstPath, StringComparison.OrdinalIgnoreCase)
            ? secondPath : firstPath;
        var newContent = Rollout("rollout-new", Token([7, 1, 3, 1, 10], [7, 1, 3, 1, 10]));
        WriteRollout(canonicalPath, newContent);
        var fallbackBytes = File.ReadAllBytes(fallbackPath);
        var changedBytes = File.ReadAllBytes(canonicalPath);

        var sync = await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(0, sync.Status.Conflicts);
        Assert.Equal(["rollout-new", "rollout-old"], events.Select(value => value.RolloutId).Order());
        Assert.Equal(10, events.Single(value => value.RolloutId == "rollout-old").InputTokens);
        Assert.Equal(7, events.Single(value => value.RolloutId == "rollout-new").InputTokens);
        Assert.Equal(fallbackBytes, File.ReadAllBytes(fallbackPath));
        Assert.Equal(changedBytes, File.ReadAllBytes(canonicalPath));
    }

    [Fact]
    public async Task DivergedFallbackDoesNotResolveConflictOrReplaceLedger()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var canonicalPath = Path.Combine(codexHome, "sessions", "rollout-canonical.jsonl");
        var fallbackPath = Path.Combine(codexHome, "archived_sessions", "rollout-diverged.jsonl");
        WriteRollout(canonicalPath, Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        await using (var initial = CreateCollector(codexHome, temporary.Path))
            await StartAndWaitForInventoryAsync(initial);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
            store.RecordSourceConflict(new SourceConflictInput(
                null, canonicalPath, "canonical-source-malformed", "malformed", null, 2));
        WriteRollout(canonicalPath, "{not-json}\n");
        WriteRollout(fallbackPath,
            Rollout("rollout-one", Token([99, 2, 4, 1, 103], [99, 2, 4, 1, 103])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);

        Assert.True(status.Conflicts > 0);
        Assert.Equal(10, Assert.Single(await collector.QueryEventsAsync(AllTimeQuery())).InputTokens);
    }

    [Fact]
    public async Task ManualRequestQueuedDuringInventoryRunsFreshTrailingInventory()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        for (var index = 0; index < 40; index++)
        {
            WriteRollout(Path.Combine(codexHome, "sessions", $"rollout-test-{index}.jsonl"),
                Rollout($"rollout-{index}", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        }

        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        await using (var collector = CreateCollector(codexHome, temporary.Path))
        {
            await StartAndWaitForInventoryAsync(collector);
            var first = collector.RefreshAsync().AsTask();
            var trailing = collector.RefreshAsync().AsTask();
            await Task.WhenAll(first, trailing);
        }

        using var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal("3", store.GetCollectorState("full_inventory_run_count"));
    }

    [Fact]
    public async Task WatcherDebouncesRepeatedNotificationsAndProcessesAppendedUsage()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        WriteRollout(rolloutPath, Rollout("rollout-one", first));
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            WatcherDebounce = TimeSpan.FromMilliseconds(25),
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        await StartAndWaitForInventoryAsync(collector);

        WriteRollout(rolloutPath, Rollout("rollout-one", first,
            Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z")));
        File.SetLastWriteTimeUtc(rolloutPath, DateTime.UtcNow.AddMilliseconds(10));

        IReadOnlyList<StoredUsageEvent> events = [];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            events = await collector.QueryEventsAsync(AllTimeQuery());
            if (events.Count == 2) break;
            await Task.Delay(25);
        }

        Assert.Equal(2, events.Count);
        var status = await collector.GetStatusAsync();
        Assert.InRange(status.ChangedFilesLastSync, 1, 1);
    }

    [Fact]
    public async Task WatcherChangesDoNotResetFailureBackoffAndRepeatedDiagnosticsAreThrottled()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-retry.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        var validPrefix = Rollout("retry", first);
        WriteRollout(rolloutPath, validPrefix);
        await using (var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = databasePath,
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            RetryAttempts = 1,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        }))
        {
            await StartAndWaitForInventoryAsync(collector);
            var unsafeAppend = Line("event_msg", new
            {
                type = "token_count",
                padding = new string('x', RolloutParser.CooperativeHardMaximumRecordBytes + 1024),
            }) + "\n";
            WriteRollout(rolloutPath, validPrefix + unsafeAppend);
            collector.EnqueueWatcherObservationForTest(rolloutPath);
            for (var index = 0; index < 50; index++)
            {
                collector.EnqueueWatcherObservationForTest(rolloutPath);
                await Task.Delay(4);
            }

            var second = Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z");
            WriteRollout(rolloutPath, Rollout("retry", first, second));
            collector.EnqueueWatcherObservationForTest(rolloutPath);
            IReadOnlyList<StoredUsageEvent> events = [];
            for (var attempt = 0; attempt < 100; attempt++)
            {
                events = await collector.QueryEventsAsync(AllTimeQuery());
                if (events.Count == 2) break;
                await Task.Delay(20);
            }
            Assert.Equal(2, events.Count);
            CollectorStatus? recovered = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                recovered = await collector.GetStatusAsync();
                if (recovered.PendingFiles == 0) break;
                await Task.Delay(20);
            }
            Assert.Equal(0, recovered?.PendingFiles);
        }

        using var store = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.InRange(store.CountDiagnosticsForTest("source-read-retry"), 1, 3);
        Assert.Equal(1, store.MaximumRepeatedDiagnosticForTest("source-read-retry"));
    }

    [Fact]
    public async Task ScheduledWatcherRetryUsesRetryingPhase()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-retrying.jsonl");
        var valid = Rollout("retrying", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(rolloutPath, valid);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RetryBaseDelay = TimeSpan.FromSeconds(2),
            RetryAttempts = 1,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        await StartAndWaitForInventoryAsync(collector);

        WriteRollout(rolloutPath, valid + "{not-json}\n");
        collector.EnqueueWatcherObservationForTest(rolloutPath);

        var status = await WaitForPhaseAsync(collector, CollectorPhase.Retrying);

        Assert.Equal(0, status.Conflicts);
        Assert.Equal(1, status.PendingFiles);
    }

    [Fact]
    public async Task InFlightWatcherRetryKeepsStatusAndQueriesResponsive()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-retrying-in-flight.jsonl");
        var valid = Rollout("retrying-in-flight", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        var corrected = valid + string.Join('\n', Enumerable.Range(0, 64)
            .Select(index => Line("ignored_record", new { index }))) + "\n";
        var retryAttemptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetryAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureRetryAttempt = 0;
        var hooks = new CollectorTestHooks(AfterStableAppendSnapshotCapturedAsync: async (_, token) =>
        {
            if (Volatile.Read(ref captureRetryAttempt) == 0) return;
            retryAttemptEntered.TrySetResult();
            await releaseRetryAttempt.Task.WaitAsync(token);
        });
        WriteRollout(rolloutPath, valid);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RetryBaseDelay = TimeSpan.FromMilliseconds(500),
            RetryAttempts = 1,
            CooperativeItemLimit = 1,
            CooperativeTimeBudget = TimeSpan.FromMilliseconds(1),
            ParserSliceBytes = 128,
            ParserSliceRecords = 1,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, hooks);
        await StartAndWaitForInventoryAsync(collector);

        WriteRollout(rolloutPath, valid + "{not-json}\n");
        collector.EnqueueWatcherObservationForTest(rolloutPath);
        _ = await WaitForPhaseAsync(collector, CollectorPhase.Retrying);
        WriteRollout(rolloutPath, corrected);
        Interlocked.Exchange(ref captureRetryAttempt, 1);

        await retryAttemptEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var status = collector.GetStatusAsync().AsTask();
        var query = collector.QueryEventsAsync(AllTimeQuery()).AsTask();
        releaseRetryAttempt.TrySetResult();

        Assert.Equal(CollectorPhase.Retrying, (await status.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.Single(await query.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task MultipleScheduledWatcherRetriesUseRetryingPhase()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var firstPath = Path.Combine(codexHome, "sessions", "rollout-retrying-first.jsonl");
        var secondPath = Path.Combine(codexHome, "sessions", "rollout-retrying-second.jsonl");
        var first = Rollout("retrying-first", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        var second = Rollout("retrying-second", Token([20, 3, 6, 2, 26], [20, 3, 6, 2, 26]));
        WriteRollout(firstPath, first);
        WriteRollout(secondPath, second);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RetryBaseDelay = TimeSpan.FromSeconds(2),
            RetryAttempts = 1,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        await StartAndWaitForInventoryAsync(collector);

        WriteRollout(firstPath, first + "{not-json}\n");
        WriteRollout(secondPath, second + "{not-json}\n");
        collector.EnqueueWatcherObservationForTest(firstPath);
        collector.EnqueueWatcherObservationForTest(secondPath);

        var status = await WaitForPhaseAsync(collector, CollectorPhase.Retrying);

        Assert.Equal(0, status.Conflicts);
        Assert.Equal(2, status.PendingFiles);
    }

    [Fact]
    public async Task ExhaustedWatcherRetryUsesDegradedPhase()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-retry-exhausted.jsonl");
        var valid = Rollout("retry-exhausted", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(rolloutPath, valid);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RetryBaseDelay = TimeSpan.FromMilliseconds(100),
            RetryAttempts = 1,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        await StartAndWaitForInventoryAsync(collector);

        WriteRollout(rolloutPath, valid + "{not-json}\n");
        collector.EnqueueWatcherObservationForTest(rolloutPath);

        _ = await WaitForPhaseAsync(collector, CollectorPhase.Retrying);
        var status = await WaitForPhaseAsync(collector, CollectorPhase.Degraded);

        Assert.Equal(0, status.Conflicts);
        Assert.True(status.PendingFiles > 0);
    }

    [Fact]
    public async Task RetryDeadlineUsesInjectedMonotonicTimestamp()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-monotonic-retry.jsonl");
        var valid = Rollout("monotonic", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(rolloutPath, valid);
        long timestamp = 0;
        var hooks = new CollectorTestHooks(
            GetMonotonicTimestamp: () => Interlocked.Read(ref timestamp),
            MonotonicTimestampFrequency: 1000);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            WatcherDebounce = TimeSpan.FromMilliseconds(5),
            RetryBaseDelay = TimeSpan.FromSeconds(10),
            RetryAttempts = 1,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, hooks);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(rolloutPath, valid + "{not-json}\n");
        collector.EnqueueWatcherObservationForTest(rolloutPath);

        TimeSpan? initialRemaining = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            _ = await collector.GetStatusAsync();
            initialRemaining = collector.GetRetryRemainingDelayForTest(rolloutPath);
            if (initialRemaining is not null) break;
            await Task.Delay(10);
        }
        Assert.Equal(TimeSpan.FromSeconds(4), initialRemaining);

        Interlocked.Exchange(ref timestamp, 2500);

        Assert.Equal(TimeSpan.FromSeconds(1.5), collector.GetRetryRemainingDelayForTest(rolloutPath));
    }

    [Fact]
    public async Task ParserRevisionRebuildsExistingCanonicalAndOverwritesProjectFromMainSessionMetadataCwd()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        const string conversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        WriteRollout(rolloutPath, string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = conversationId,
                id = "rollout-one",
                thread_source = "user",
                cwd = "C:/Projects/codex-usage-desktop",
            }),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
        ]) + "\n");
        var file = new FileInfo(rolloutPath);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            var metadata = new RolloutMetadata(
                conversationId, "rollout-one", "", ThreadType.Main, "main", "/root", "", false, "Codex", "", 0);
            var source = new CandidateSourceInput(
                rolloutPath, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                file.Length, "old-prefix", PrefixStatus.Matches, CanonicalStatus.Canonical, true, 1, null);
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(metadata,
                [new UsageEventInput(0, DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                    "unknown", 1, 0, 1, 0, "old-signature")],
                new CanonicalSourceInput(
                    source.FilePath, source.SizeBytes, source.ModifiedAtEpochMs, source.ByteOffset,
                    source.PrefixHash, source.PrefixStatus, source.LastScannedAtEpochMs, source.LastError),
                1,
                null));
            store.SetCollectorState("rollout_parser_revision", "5", 1);
        }

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal("gpt-5.6-sol", usage.Model);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal("codex-usage-desktop", Assert.Single(
            await collector.QueryRecentMainThreadsAsync(20)).ProjectName);
    }

    [Fact]
    public async Task ParserRevisionReclassifiesAgentCreatedThreadAsMainAndMakesItQueryable()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        const string conversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        const string rolloutId = "rollout-agent-created";
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-agent-created.jsonl");
        var content = string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = conversationId,
                id = rolloutId,
                thread_source = "agent_created_thread",
                cwd = "C:/Projects/codex-usage-desktop",
            }),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
        ]) + "\n";
        WriteRollout(rolloutPath, content);
        var file = new FileInfo(rolloutPath);
        var parsed = await RolloutParser.ParseChunkCooperativelyAsync(
            Encoding.UTF8.GetBytes(content),
            rolloutId,
            new(64 * 1024, 32, TimeSpan.FromMilliseconds(8), 1024 * 1024, _ => ValueTask.CompletedTask));
        var parserStateJson = RolloutParserStateCodec.Serialize(parsed.State);
        var parserStateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parserStateJson)))
            .ToLowerInvariant();

        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                new RolloutMetadata(conversationId, rolloutId, "", ThreadType.Unknown,
                    "unknown", "/root", "", false, "stale-project", "", 0),
                [new UsageEventInput(
                    0,
                    DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                    "gpt-5.6-sol", 10, 2, 4, 1, "stale-agent-created")],
                new CanonicalSourceInput(
                    rolloutPath,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    file.Length,
                    HashBoundary(content),
                    PrefixStatus.Matches,
                    1,
                    null),
                1,
                null,
                new RolloutCheckpointInput(
                    rolloutPath,
                    rolloutId,
                    RolloutParserStateCodec.FormatRevision,
                    15,
                    new SourceIdentity(SourceIdentityKind.ConservativeStat, "revision-15-seed"),
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    file.Length,
                    HashBoundary(content),
                    parserStateJson,
                    parserStateHash,
                    0,
                    0,
                    0,
                    1)));
            store.SetCollectorState("rollout_parser_revision", "15", 1);
        }

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);

        var option = Assert.Single(await collector.QueryRecentMainThreadsAsync(20));
        Assert.Equal(conversationId, option.ConversationId);
        Assert.Equal("codex-usage-desktop", option.ProjectName);

        using var verified = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(ThreadType.Main, verified.GetRolloutMetadata(rolloutId)!.ThreadType);
        Assert.Equal("17", verified.GetCollectorState("rollout_parser_revision"));
        Assert.Equal(17, Assert.Single(verified.ListRolloutCheckpoints()).ParserRevision);
    }

    [Fact]
    public async Task ParserRevisionReclassifiesExistingGuardianReviewWithoutSourceChanges()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        const string conversationId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        const string rolloutId = "guardian-rollout";
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-guardian.jsonl");
        var content = string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = conversationId,
                id = rolloutId,
                parent_thread_id = "parent-rollout",
                thread_source = "guardian_review",
                source = new { subagent = new { other = "guardian" } },
            }),
            Line("turn_context", new { turn_id = "turn-a", model = "codex-auto-review" }),
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
        ]) + "\n";
        WriteRollout(rolloutPath, content);
        var file = new FileInfo(rolloutPath);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                new RolloutMetadata(conversationId, rolloutId, "parent-rollout", ThreadType.Unknown,
                    "unknown", "/root", "", false, "Codex", "", 0),
                [new UsageEventInput(
                    0,
                    DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                    "codex-auto-review", 10, 2, 4, 1, "stale-guardian")],
                new CanonicalSourceInput(
                    rolloutPath,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    file.Length,
                    HashBoundary(content),
                    PrefixStatus.Matches,
                    1,
                    null),
                1,
                null));
            store.SetCollectorState("rollout_parser_revision", "16", 1);
        }

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal(1, status.UsageRevision);
        Assert.Equal(ThreadType.GuardianReview, usage.ThreadType);
        Assert.Equal("guardian", usage.AgentRole);
        using var verified = new UsageStore(
            databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal(ThreadType.GuardianReview, verified.GetRolloutMetadata(rolloutId)!.ThreadType);
        Assert.Equal("17", verified.GetCollectorState("rollout_parser_revision"));
    }

    [Fact]
    public void TimestampedRolloutFallbackUsesExactTrailingUuidV7()
    {
        const string id = "019fb70e-1234-7abc-8def-0123456789ab";
        var path = Path.Combine("sessions", $"rollout-2026-07-31T15-23-09-{id}.jsonl");

        Assert.Equal(id, RolloutFileIdentity.FallbackRolloutId(path));
        Assert.Equal($"07-31T15-23-09-{id}", RolloutFileIdentity.LegacyFallbackRolloutId(path));
        Assert.Equal("one", RolloutFileIdentity.FallbackRolloutId("rollout-test-one.jsonl"));
    }

    [Fact]
    public async Task ParserRevisionAtomicallyRekeysStrictLegacyFilenameFallback()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        const string actualId = "019fb70e-1234-7abc-8def-0123456789ab";
        var rolloutPath = Path.Combine(
            codexHome,
            "sessions",
            $"rollout-2026-07-31T15-23-09-{actualId}.jsonl");
        var content = new string('\0', 3411) + "\n"
            + Rollout(actualId, Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]));
        WriteRollout(rolloutPath, content);
        var legacyId = RolloutFileIdentity.LegacyFallbackRolloutId(rolloutPath);
        var file = new FileInfo(rolloutPath);
        var parsed = await RolloutParser.ParseChunkCooperativelyAsync(
            Encoding.UTF8.GetBytes(content),
            legacyId,
            new(64 * 1024, 32, TimeSpan.FromMilliseconds(8), 1024 * 1024, _ => ValueTask.CompletedTask));
        var parserStateJson = RolloutParserStateCodec.Serialize(parsed.State);
        var parserStateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parserStateJson))).ToLowerInvariant();
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            var source = new CanonicalSourceInput(
                rolloutPath,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                file.Length,
                HashBoundary(content),
                PrefixStatus.Matches,
                1,
                null);
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                new RolloutMetadata(legacyId, legacyId, "", ThreadType.Main, "main", "/root", "", false, "Codex", "", 0),
                [new UsageEventInput(0, DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                    "gpt-5.6-sol", 1, 0, 1, 0, "legacy")],
                source,
                1,
                null,
                new RolloutCheckpointInput(
                    rolloutPath,
                    legacyId,
                    RolloutParserStateCodec.FormatRevision,
                    5,
                    new SourceIdentity(SourceIdentityKind.ConservativeStat, "legacy-seed"),
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    file.Length,
                    HashBoundary(content),
                    parserStateJson,
                    parserStateHash,
                    0,
                    0,
                    0,
                    1)));
            store.SetCollectorState("rollout_parser_revision", "5", 1);
        }

        await using (var collector = CreateCollector(codexHome, temporary.Path))
        {
            var status = await StartAndWaitForInventoryAsync(collector);
            var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));
            Assert.Equal(0, status.Conflicts);
            Assert.Equal(actualId, usage.RolloutId);
            Assert.Equal(10, usage.InputTokens);
        }

        using (var verified = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            Assert.Null(verified.GetRolloutMetadata(legacyId));
            Assert.NotNull(verified.GetRolloutMetadata(actualId));
            var source = Assert.Single(verified.ListSourceFiles());
            Assert.Equal(actualId, source.RolloutId);
            Assert.Equal(CanonicalStatus.Canonical, source.CanonicalStatus);
            var checkpoint = Assert.Single(verified.ListRolloutCheckpoints());
            Assert.Equal(actualId, checkpoint.RolloutId);
            Assert.Equal(17, checkpoint.ParserRevision);
            Assert.Equal(1, checkpoint.SafeNullPaddingRecords);
            Assert.Equal("17", verified.GetCollectorState("rollout_parser_revision"));
        }

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var restartedStatus = await StartAndWaitForInventoryAsync(restarted);
        Assert.Equal(0, restartedStatus.Conflicts);
        Assert.Equal(0, restartedStatus.ChangedFilesLastSync);
        Assert.Null(restarted.GetRetryRemainingDelayForTest(rolloutPath));
    }

    [Fact]
    public async Task ExactLegacyIdentityConflictRequiresTwoStableFullReadsBeforeRekey()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string actualId = "019fb70e-1234-7abc-8def-0123456789ab";
        var rolloutPath = Path.Combine(codexHome, "sessions",
            $"rollout-2026-07-31T15-23-09-{actualId}.jsonl");
        WriteRollout(rolloutPath, Rollout(actualId, Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var legacyId = SeedLegacyIdentityConflict(
            Path.Combine(temporary.Path, "usage.sqlite"), codexHome, rolloutPath, actualId);
        var fullReads = 0;
        var hooks = new CollectorTestHooks(FullSnapshotRead: path =>
        {
            if (string.Equals(path, rolloutPath, StringComparison.OrdinalIgnoreCase)) fullReads++;
        });

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);

        Assert.Equal(0, status.Conflicts);
        Assert.True(fullReads >= 2);
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Null(store.GetRolloutMetadata(legacyId));
        Assert.NotNull(store.GetRolloutMetadata(actualId));
        Assert.Equal("17", store.GetCollectorState("rollout_parser_revision"));
    }

    [Fact]
    public async Task LegacyIdentityConflictDoesNotRekeyWhenConfirmationSnapshotChanges()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string actualId = "019fb70e-1234-7abc-8def-0123456789ab";
        var rolloutPath = Path.Combine(codexHome, "sessions",
            $"rollout-2026-07-31T15-23-09-{actualId}.jsonl");
        WriteRollout(rolloutPath, Rollout(actualId, Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var legacyId = SeedLegacyIdentityConflict(databasePath, codexHome, rolloutPath, actualId);
        var changed = 0;
        var hooks = new CollectorTestHooks(BeforeConfirmationSnapshotAsync: (path, _) =>
        {
            if (string.Equals(path, rolloutPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref changed, 1) == 0)
                File.AppendAllText(path, Line("ignored_record", new { value = "changed" }) + "\n");
            return ValueTask.CompletedTask;
        });

        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        var status = await StartAndWaitForInventoryAsync(collector);

        Assert.True(status.Conflicts > 0);
        using var store = new UsageStore(databasePath,
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.NotNull(store.GetRolloutMetadata(legacyId));
        Assert.Null(store.GetRolloutMetadata(actualId));
        Assert.Equal(["seed-legacy-conflict"], store.GetRolloutEventSignatures(legacyId));
        Assert.Equal("5", store.GetCollectorState("rollout_parser_revision"));
    }

    [Fact]
    public async Task ParserRevisionAdvancesUsageRevisionForAttributionOnlyChange()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-attribution.jsonl");
        WriteRollout(rolloutPath, Rollout(
            "rollout-attribution",
            Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var file = new FileInfo(rolloutPath);
        using (var store = new UsageStore(
                   databasePath,
                   protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            var staleMetadata = new RolloutMetadata(
                "legacy-conversation", "rollout-attribution", "", ThreadType.Main,
                "legacy-main", "/legacy", "legacy", false, "Codex", "", 0);
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                staleMetadata,
                [new UsageEventInput(
                    0,
                    DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                    "gpt-5.6-sol",
                    10,
                    2,
                    4,
                    1,
                    "same-visible-usage")],
                new CanonicalSourceInput(
                    rolloutPath,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    file.Length,
                    "old-prefix",
                    PrefixStatus.Matches,
                    1,
                    null),
                1,
                null));
            store.SetCollectorState("rollout_parser_revision", "6", 1);
        }

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal(1, status.UsageRevision);
        Assert.Equal("rollout-attribution", usage.ConversationId);
        Assert.Equal("main", usage.AgentRole);
        Assert.Equal("/root", usage.AgentPath);
        Assert.Equal(10, usage.InputTokens);
    }

    [Fact]
    public async Task CanonicalRewriteAdvancesUsageRevisionForConversationAttributionOnlyChange()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-attribution.jsonl");
        var token = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        WriteRollout(rolloutPath, Rollout("rollout-attribution", token));
        await using var collector = CreateCollector(codexHome, temporary.Path);
        var initial = await StartAndWaitForInventoryAsync(collector);
        var rewritten = string.Join('\n',
        [
            Line("session_meta", new
            {
                session_id = "updated-conversation",
                id = "rollout-attribution",
                thread_source = "user",
            }),
            Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
            token,
        ]) + "\n";

        WriteRollout(rolloutPath, rewritten);
        var sync = await collector.RefreshAsync();
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.True(sync.UsageChanged);
        Assert.Equal(initial.UsageRevision + 1, sync.Status.UsageRevision);
        Assert.Equal("updated-conversation", usage.ConversationId);
        Assert.Equal(10, usage.InputTokens);
    }

    [Fact]
    public async Task IncrementalCommitUsesEventsAndBoundaryFromOneStableSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        var versionA = Rollout("rollout-one", first,
            Token([15, 1, 2, 1, 17], [25, 3, 6, 2, 31], "2026-07-15T01:03:03.004Z"));
        var versionB = Rollout("rollout-one", first,
            Token([19, 1, 2, 1, 21], [29, 3, 6, 2, 35], "2026-07-15T01:03:03.004Z"));
        Assert.Equal(Encoding.UTF8.GetByteCount(versionA), Encoding.UTF8.GetByteCount(versionB));
        WriteRollout(rolloutPath, Rollout("rollout-one", first));
        var replacementPath = Path.Combine(temporary.Path, "replacement.jsonl");
        var replaced = 0;
        var hooks = new CollectorTestHooks(AfterStableAppendSnapshotCapturedAsync: (path, _) =>
        {
            if (Interlocked.Exchange(ref replaced, 1) != 0) return ValueTask.CompletedTask;
            var timestamp = File.GetLastWriteTimeUtc(path);
            WriteRollout(replacementPath, versionB);
            File.SetLastWriteTimeUtc(replacementPath, timestamp);
            File.Move(replacementPath, path, overwrite: true);
            return ValueTask.CompletedTask;
        });
        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(rolloutPath, versionA);

        await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal([10L, 15L], events.Select(value => value.InputTokens));
        await collector.DisposeAsync();
        using var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        var source = Assert.Single(store.ListSourceFiles());
        Assert.Equal(HashBoundary(versionA), source.PrefixHash);
    }

    [Fact]
    public async Task CanceledInFlightManualRunSkipsCanceledBacklog()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl"),
            Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var inventoryCalls = 0;
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queriesExecuted = 0;
        using var dispatchEntered = new ManualResetEventSlim();
        using var releaseDispatch = new ManualResetEventSlim();
        var hooks = new CollectorTestHooks(
            BeforeInventoryEnumerationAsync: async token =>
            {
                if (Interlocked.Increment(ref inventoryCalls) == 1) return;
                manualEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            BeforeQuery: () => Interlocked.Increment(ref queriesExecuted),
            BeforeInteractiveDispatch: () =>
            {
                if (!manualEntered.Task.IsCompleted) return;
                dispatchEntered.Set();
                if (!releaseDispatch.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("Test did not release interactive dispatch.");
            });
        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        await StartAndWaitForInventoryAsync(collector);
        using var manualCancellation = new CancellationTokenSource();
        var manual = collector.RefreshAsync(manualCancellation.Token).AsTask();
        await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var backlogCancellations = Enumerable.Range(0, 64).Select(_ => new CancellationTokenSource()).ToArray();
        var backlog = backlogCancellations
            .Select(cancellation => collector.QueryEventsAsync(AllTimeQuery(), cancellation.Token).AsTask())
            .ToArray();
        Assert.True(dispatchEntered.Wait(TimeSpan.FromSeconds(2)));
        foreach (var cancellation in backlogCancellations) cancellation.Cancel();
        releaseDispatch.Set();
        manualCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
        foreach (var request in backlog)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, Volatile.Read(ref queriesExecuted));
        foreach (var cancellation in backlogCancellations) cancellation.Dispose();
    }

    [Fact]
    public async Task DisposeCancelsInFlightInventoryAndReturnsPromptly()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var inventoryCalls = 0;
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new CollectorTestHooks(BeforeInventoryEnumerationAsync: async token =>
        {
            if (Interlocked.Increment(ref inventoryCalls) == 1) return;
            manualEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var collector = CreateCollector(codexHome, temporary.Path, hooks);
        await StartAndWaitForInventoryAsync(collector);
        var manual = collector.RefreshAsync().AsTask();
        await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();

        await collector.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
    }

    [Fact]
    public async Task InventoryAndWatcherRejectJunctionsOutsideScopeAndCycles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var sessions = Path.Combine(codexHome, "sessions");
        var outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(outside);
        WriteRollout(Path.Combine(sessions, "rollout-inside.jsonl"),
            Rollout("inside", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(Path.Combine(outside, "rollout-outside.jsonl"),
            Rollout("outside", Token([20, 2, 4, 1, 24], [20, 2, 4, 1, 24])));
        var outsideLink = Path.Combine(sessions, "outside-link");
        var cycleLink = Path.Combine(sessions, "cycle-link");
        var outsideLinkCreated = false;
        var cycleLinkCreated = false;
        try
        {
            CreateJunction(outsideLink, outside);
            outsideLinkCreated = true;
            CreateJunction(cycleLink, sessions);
            cycleLinkCreated = true;
            await using var collector = new UsageCollector(new CollectorOptions
            {
                CodexHome = codexHome,
                DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
                WatcherDebounce = TimeSpan.FromMilliseconds(10),
                FullInventoryInterval = TimeSpan.FromHours(1),
                EnableWatchers = false,
            });
            await StartAndWaitForInventoryAsync(collector);
            collector.EnqueueWatcherObservationForTest(Path.Combine(outsideLink, "rollout-outside.jsonl"));
            await Task.Delay(50);
            var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));
            Assert.Equal("inside", usage.RolloutId);
            Assert.Equal(1, (await collector.GetStatusAsync()).FilesKnown);
        }
        finally
        {
            if (cycleLinkCreated) Directory.Delete(cycleLink);
            if (outsideLinkCreated) Directory.Delete(outsideLink);
        }
    }

    [Fact]
    public async Task StartReturnsAndQueriesPreexistingLedgerWhileInitialInventoryIsBlocked()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-seeded.jsonl");
        WriteRollout(rolloutPath, Rollout("seeded", Token([21, 2, 3, 1, 24], [21, 2, 3, 1, 24])));
        SeedLedger(databasePath, codexHome, rolloutPath, "seeded", 21);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = databasePath,
            EnableWatchers = false,
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, new CollectorTestHooks(BeforeInventoryEnumerationAsync: async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }));

        var startStatus = await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CollectorPhase.Syncing, startStatus.Phase);
        Assert.False(release.Task.IsCompleted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var query = collector.QueryEventsAsync(AllTimeQuery()).AsTask();
        var usage = Assert.Single(await query.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(21, usage.InputTokens);
        Assert.False(release.Task.IsCompleted);
        await collector.GetStatusAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        release.TrySetResult();
        await WaitForInventoryAsync(collector);
    }

    [Fact]
    public async Task TimerTicksDuringInventoryAreDroppedAndNextRunWaitsAFullInterval()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var interval = TimeSpan.FromMilliseconds(200);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventoryCalls = 0;
        var completions = 0;
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            FullInventoryInterval = interval,
        }, new CollectorTestHooks(
            BeforeInventoryEnumerationAsync: async token =>
            {
                var call = Interlocked.Increment(ref inventoryCalls);
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }
                else if (call == 2)
                {
                    secondStarted.TrySetResult(Stopwatch.GetTimestamp());
                }
            },
            AfterInventoryCompleted: () =>
            {
                if (Interlocked.Increment(ref completions) == 1)
                    firstCompleted.TrySetResult(Stopwatch.GetTimestamp());
            }));

        await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        Assert.Equal(1, Volatile.Read(ref inventoryCalls));

        releaseFirst.TrySetResult();
        var completedAt = await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var startedAt = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Stopwatch.GetElapsedTime(completedAt, startedAt) >= interval);
        await WaitForInventoryAsync(collector);
    }

    [Fact]
    public async Task LargeInitialInventoryPublishesProgressAndKeepsQueriesResponsive()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        for (var index = 0; index < 96; index++)
            WriteRollout(Path.Combine(codexHome, "sessions", $"rollout-progress-{index}.jsonl"),
                Rollout($"progress-{index}", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var progress = new List<string>();
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            CooperativeItemLimit = 1,
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        collector.StatusChanged += (_, status) =>
        {
            lock (progress) progress.Add(status.Message);
        };

        await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await collector.QueryEventsAsync(AllTimeQuery()).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(40);
        }
        var status = await WaitForInventoryAsync(collector, TimeSpan.FromSeconds(10));

        Assert.Equal(96, status.FilesKnown);
        lock (progress) Assert.Contains(progress, message => message.Contains("sources", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WatcherStormStaysBoundedWhileInitialInventoryIsBlocked()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, new CollectorTestHooks(BeforeInventoryEnumerationAsync: async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }));
        await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var path = Path.Combine(codexHome, "sessions", "rollout-storm.jsonl");

        for (var index = 0; index < 100_000; index++) collector.EnqueueWatcherObservationForTest(path);

        var metrics = collector.GetWatcherBufferMetricsForTest();
        Assert.InRange(metrics.UniquePaths, 0, 1);
        Assert.InRange(metrics.WakeSignals, 0, 1);
        release.TrySetResult();
        await WaitForInventoryAsync(collector);
        await collector.DisposeAsync();
    }

    private static UsageCollector CreateCollector(string codexHome, string temporaryRoot) => new(new CollectorOptions
    {
        CodexHome = codexHome,
        DatabasePath = Path.Combine(temporaryRoot, "usage.sqlite"),
        EnableWatchers = false,
        RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
        FullInventoryInterval = TimeSpan.FromHours(1),
    });

    private static UsageCollector CreateCollector(
        string codexHome,
        string temporaryRoot,
        CollectorTestHooks hooks) => new(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporaryRoot, "usage.sqlite"),
            EnableWatchers = false,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, hooks);

    private static UsageEventQuery AllTimeQuery() => new(0, 4_102_444_800_000);

    private static async Task<CollectorStatus> StartAndWaitForInventoryAsync(UsageCollector collector)
    {
        await collector.StartAsync();
        return await WaitForInventoryAsync(collector);
    }

    private static async Task<CollectorStatus> WaitForInventoryAsync(
        UsageCollector collector,
        TimeSpan? timeout = null)
    {
        var expires = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        while (expires.Elapsed < limit)
        {
            var status = await collector.GetStatusAsync();
            if (status.Phase is CollectorPhase.Watching or CollectorPhase.Partial or CollectorPhase.Degraded) return status;
            await Task.Delay(10);
        }
        throw new TimeoutException("Collector inventory did not complete within the test deadline.");
    }

    private static async Task<CollectorStatus> WaitForPhaseAsync(
        UsageCollector collector,
        CollectorPhase expected,
        TimeSpan? timeout = null)
    {
        var expires = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        while (expires.Elapsed < limit)
        {
            var status = await collector.GetStatusAsync();
            if (status.Phase == expected) return status;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Collector did not enter {expected} within the test deadline.");
    }

    private static async Task<MainThreadOption> WaitForSessionIndexTitleAsync(
        UsageCollector collector,
        string expectedTitle)
    {
        var expires = Stopwatch.StartNew();
        while (expires.Elapsed < TimeSpan.FromSeconds(2))
        {
            var options = await collector.QueryRecentMainThreadsAsync(10);
            if (options.FirstOrDefault() is { } option && option.Title == expectedTitle) return option;
            await Task.Delay(10);
        }
        throw new TimeoutException("Session index title was not refreshed within the test deadline.");
    }

    private static void SeedLedger(
        string databasePath,
        string codexHome,
        string rolloutPath,
        string rolloutId,
        long inputTokens)
    {
        var file = new FileInfo(rolloutPath);
        using var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        var metadata = new RolloutMetadata(rolloutId, rolloutId, "", ThreadType.Main, "main", "/root", "", false, "Codex", "", 0);
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            metadata,
            [new UsageEventInput(0, DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                "gpt-5.6-sol", inputTokens, 2, 3, 1, $"seed-{rolloutId}")],
            new CanonicalSourceInput(
                rolloutPath,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                file.Length,
                HashBoundary(File.ReadAllText(rolloutPath)),
                PrefixStatus.Matches,
                1,
                null),
                1,
                null));
        store.SetCollectorState("rollout_parser_revision", "6", 1);
    }

    private static string SeedLegacyIdentityConflict(
        string databasePath,
        string codexHome,
        string rolloutPath,
        string actualId)
    {
        var legacyId = RolloutFileIdentity.LegacyFallbackRolloutId(rolloutPath);
        var file = new FileInfo(rolloutPath);
        using var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            new RolloutMetadata(legacyId, legacyId, "", ThreadType.Main, "main", "/root", "", false, "Codex", "", 0),
            [new UsageEventInput(0, 1, "gpt-5.6-sol", 1, 0, 1, 0, "seed-legacy-conflict")],
            new CanonicalSourceInput(
                rolloutPath, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                file.Length, HashBoundary(File.ReadAllText(rolloutPath)), PrefixStatus.Matches, 1, null),
            1,
            null));
        store.RecordSourceConflict(new SourceConflictInput(
            null, rolloutPath, "canonical-source-rollout-changed",
            $"Canonical source rollout changed from {legacyId} to {actualId}.", null, 2));
        store.SetCollectorState("rollout_parser_revision", "5", 2);
        return legacyId;
    }

    private static string CreateCodexHome(string temporaryRoot)
    {
        var codexHome = Path.Combine(temporaryRoot, ".codex");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "archived_sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "agents"));
        return codexHome;
    }

    private static void WriteRollout(string filePath, string content) => File.WriteAllText(filePath, content);

    private static void WriteSessionIndex(
        string codexHome,
        string conversationId,
        string title,
        string updatedAt) =>
        File.WriteAllText(
            Path.Combine(codexHome, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = conversationId, thread_name = title, updated_at = updatedAt }) + "\n");

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteDatabaseMutation(string databasePath, string sql)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TamperCheckpointSnapshot(string databasePath)
    {
        using var connection = OpenDatabase(databasePath);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT parser_state_json FROM rollout_checkpoints";
        var original = Assert.IsType<string>(read.ExecuteScalar());
        Assert.True(RolloutParserStateCodec.TryDeserialize(original, out var originalState, out var stateError), stateError);
        var tampered = RolloutParserStateCodec.Serialize(originalState! with
        {
            PreviousSnapshot = "tampered-cumulative-snapshot",
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tampered))).ToLowerInvariant();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE rollout_checkpoints SET parser_state_json = $json, parser_state_hash = $hash";
        update.Parameters.AddWithValue("$json", tampered);
        update.Parameters.AddWithValue("$hash", hash);
        update.ExecuteNonQuery();
    }

    private static string HashBoundary(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var start = Math.Max(0, bytes.Length - 64 * 1024);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(start))).ToLowerInvariant();
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start junction creation process.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static string Rollout(string rolloutId, params string[] tokens) => string.Join('\n', new[]
    {
        Line("session_meta", new { session_id = rolloutId, id = rolloutId, thread_source = "user" }),
        Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
    }.Concat(tokens)) + "\n";

    private static string VoiceRollout(string rolloutId, params string[] records) => string.Join('\n', new[]
    {
        Line("session_meta", new { session_id = rolloutId, id = rolloutId, thread_source = "realtime_voice" }),
    }.Concat(records)) + "\n";

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

    private static string Line(string type, object payload, string timestamp = "2026-07-15T01:02:03.004Z") =>
        JsonSerializer.Serialize(new { timestamp, type, payload });

    private static object Tuple(long[] values) => new
    {
        input_tokens = values[0],
        cached_input_tokens = values[1],
        output_tokens = values[2],
        reasoning_output_tokens = values[3],
        total_tokens = values[4],
    };

    private sealed class FixedSourceIdentityReader(string value) : ISourceIdentityReader
    {
        public SourceIdentity Read(FileStream stream, string filePath, long sizeBytes, long modifiedAtEpochMs) =>
            new(SourceIdentityKind.WindowsFileId, value);
    }

    private sealed class SelectivelyUnavailableSourceIdentityReader(string blockedPath, string value) : ISourceIdentityReader
    {
        public SourceIdentity Read(FileStream stream, string filePath, long sizeBytes, long modifiedAtEpochMs)
        {
            if (string.Equals(filePath, blockedPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Synthetic checkpoint identity access denial.");
            return new SourceIdentity(SourceIdentityKind.WindowsFileId, value);
        }
    }
}
