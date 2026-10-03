using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;
using Xunit;

namespace CodexUsage.Infrastructure.Tests;

public sealed partial class CollectionIntegrationTests
{
    [Fact]
    public async Task ModelActivityOnlyAppendsRefreshUsageAndSurviveRestartAndBackfill()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string thread = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        var source = Path.Combine(codexHome, "sessions", "rollout-model-activity.jsonl");
        WriteRollout(source, ActivityHeader(thread));
        await using (var collector = CreateCollector(codexHome, temporary.Path))
        {
            await StartAndWaitForInventoryAsync(collector);
            File.AppendAllText(source, ActivityText(thread, "item-a", "abc"));
            var appended = await collector.RefreshAsync();
            Assert.True(appended.UsageChanged);
            Assert.Equal(3, Assert.Single(await collector.QueryModelActivitiesAsync(AllTimeQuery())).EstimatedContentBytes);
            Assert.Empty(await collector.QueryEventsAsync(AllTimeQuery()));
        }
        await using (var restarted = CreateCollector(codexHome, temporary.Path))
        {
            await StartAndWaitForInventoryAsync(restarted);
            Assert.Single(await restarted.QueryModelActivitiesAsync(AllTimeQuery()));
        }
        using (var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite")))
            store.SetCollectorState("rollout_parser_revision", "21", 1);
        await using var upgraded = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(upgraded);
        Assert.Single(await upgraded.QueryModelActivitiesAsync(AllTimeQuery()));
    }

    [Fact]
    public async Task SameTokensWithDifferentModelActivityConflictsAndMetricExtensionPromotes()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string thread = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        var original = Path.Combine(codexHome, "sessions", "rollout-model-original.jsonl");
        var candidate = Path.Combine(codexHome, "archived_sessions", "rollout-model-candidate.jsonl");
        var baseline = ActivityHeader(thread) + ActivityText(thread, "item-a", "abc");
        WriteRollout(original, baseline);
        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(candidate, ActivityHeader(thread) + ActivityText(thread, "item-a", "different volume"));
        var conflict = await collector.RefreshAsync();
        Assert.False(conflict.UsageChanged);
        Assert.Equal(1, conflict.Status.Conflicts);
        Assert.Equal(3, Assert.Single(await collector.QueryModelActivitiesAsync(AllTimeQuery())).EstimatedContentBytes);
        WriteRollout(candidate, baseline + ActivityText(thread, "item-b", "abcdef"));
        var extended = await collector.RefreshAsync();
        Assert.True(extended.UsageChanged);
        Assert.Equal(0, extended.Status.Conflicts);
        Assert.Equal(2, (await collector.QueryModelActivitiesAsync(AllTimeQuery())).Count);
        using var store = new UsageStore(Path.Combine(temporary.Path, "usage.sqlite"));
        Assert.Equal(candidate, store.GetCanonicalSourcePath(thread));
    }

    [Fact]
    public async Task CanonicalRewriteReplacesModelSamplesWithoutDuplicates()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        const string thread = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
        var source = Path.Combine(codexHome, "sessions", "rollout-model-rewrite.jsonl");
        WriteRollout(source, ActivityHeader(thread) + ActivityText(thread, "item-a", "abc"));
        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(source, ActivityHeader(thread) + ActivityText(thread, "replacement", "abcde"));
        Assert.True((await collector.RefreshAsync()).UsageChanged);
        var sample = Assert.Single(await collector.QueryModelActivitiesAsync(AllTimeQuery()));
        Assert.Equal("replacement", sample.ItemId);
        Assert.Equal(5, sample.EstimatedContentBytes);
    }

    private static string ActivityHeader(string thread) => string.Join('\n', new[]
    {
        Line("session_meta", new { session_id = thread, id = thread, thread_source = "user" }),
        Line("event_msg", new { type = "task_started", turn_id = "turn-a" }, "2026-07-15T01:02:00Z"),
        Line("turn_context", new { turn_id = "turn-a", model = "gpt-6.1-sol", effort = "high", service_tier = "fast" }),
    }) + "\n";

    private static string ActivityText(string thread, string item, string text) => Line("event_msg", new
    {
        type = "item_completed",
        thread_id = thread,
        turn_id = "turn-a",
        started_at_ms = 1_784_077_321_000L,
        completed_at_ms = 1_784_077_322_000L,
        item = new { type = "AgentMessage", id = item, content = new[] { new { type = "Text", text } } },
    }) + "\n";
}
