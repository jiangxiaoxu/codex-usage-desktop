using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Immutable;
using CodexUsage.Application;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;
using Xunit;

namespace CodexUsage.Application.Tests;

public sealed class DashboardPresentationTests
{
    [Fact]
    public void ViewportRestoreKeepsCapturedOffsetForTheLatestDataRefresh()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var ticket = coordinator.PrepareForDataRefresh(true, () => 384.5)!.Value;

        Assert.True(coordinator.TryConsumeLatest(ticket, out var offset));
        Assert.Equal(384.5, offset);
    }

    [Fact]
    public void ViewportRestoreRejectsAnEarlierRefreshAfterALaterRefreshCaptures()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var earlier = coordinator.PrepareForDataRefresh(true, () => 240)!.Value;
        var later = coordinator.PrepareForDataRefresh(true, () => 480)!.Value;

        Assert.False(coordinator.TryConsumeLatest(earlier, out _));
        Assert.True(coordinator.TryConsumeLatest(later, out var offset));
        Assert.Equal(240, offset);
    }

    [Fact]
    public void ViewportRestoreCarriesForwardTheLogicalOffsetAcrossConsecutiveRefreshes()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var first = coordinator.PrepareForDataRefresh(true, () => 500)!.Value;

        var second = coordinator.PrepareForDataRefresh(true, () => 0)!.Value;

        Assert.False(coordinator.TryConsumeLatest(first, out _));
        Assert.True(coordinator.TryConsumeLatest(second, out var offset));
        Assert.Equal(500, offset);
    }

    [Fact]
    public void ViewportRestoreNeverOverridesScrollInputAfterCapture()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var ticket = coordinator.PrepareForDataRefresh(true, () => 240)!.Value;
        coordinator.RecordUserInteraction();

        Assert.False(coordinator.TryConsumeLatest(ticket, out _));
    }

    [Fact]
    public void ViewportRestoreAllowsTheNextRefreshAfterUserInputCancelledThePriorOne()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var cancelled = coordinator.PrepareForDataRefresh(true, () => 240)!.Value;
        coordinator.RecordUserInteraction();
        var replacement = coordinator.PrepareForDataRefresh(true, () => 480)!.Value;

        Assert.False(coordinator.TryConsumeLatest(cancelled, out _));
        Assert.True(coordinator.TryConsumeLatest(replacement, out var offset));
        Assert.Equal(480, offset);
    }

    [Fact]
    public void ViewportRestoreUsesTheActualOffsetAfterUserInputInterruptedThePriorRefresh()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        _ = coordinator.PrepareForDataRefresh(true, () => 500);
        coordinator.RecordUserInteraction();

        var replacement = coordinator.PrepareForDataRefresh(true, () => 180)!.Value;

        Assert.True(coordinator.TryConsumeLatest(replacement, out var offset));
        Assert.Equal(180, offset);
    }

    [Fact]
    public void ViewportRestoreDoesNotReadViewportAndPreservesPendingTicketForSameStructureRefresh()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var structuralTicket = coordinator.PrepareForDataRefresh(true, () => 240)!.Value;
        var viewportReadCount = 0;

        var sameStructureTicket = coordinator.PrepareForDataRefresh(false, () =>
        {
            viewportReadCount++;
            return 480;
        });

        Assert.Null(sameStructureTicket);
        Assert.Equal(0, viewportReadCount);
        Assert.True(coordinator.TryConsumeLatest(structuralTicket, out var offset));
        Assert.Equal(240, offset);
    }

    [Fact]
    public void ViewportRefreshLifecycleKeepsPendingStructuralRestoreAcrossSameStructureRefresh()
    {
        var lifecycle = new DashboardViewportRefreshLifecycle();
        var structural = new DashboardSnapshotApplicationEventArgs(
            1,
            DashboardSnapshotApplyPurpose.DataRefresh,
            hasStructuralChanges: true);
        var sameStructure = new DashboardSnapshotApplicationEventArgs(
            2,
            DashboardSnapshotApplyPurpose.DataRefresh,
            hasStructuralChanges: false);
        var viewportReadCount = 0;

        var beginStructural = lifecycle.BeginSnapshotApplication(structural, () =>
        {
            viewportReadCount++;
            return 240;
        });
        var completeStructural = lifecycle.CompleteSnapshotApplication(structural);
        var beginSameStructure = lifecycle.BeginSnapshotApplication(sameStructure, () =>
        {
            viewportReadCount++;
            return 480;
        });
        var completeSameStructure = lifecycle.CompleteSnapshotApplication(sameStructure);
        var completedLayout = lifecycle.CompleteLayout(1_000);

        Assert.Equal(1, viewportReadCount);
        Assert.Equal(default(DashboardViewportRefreshTransition), beginStructural);
        Assert.True(completeStructural.SubscribeLayoutUpdated);
        Assert.Equal(default(DashboardViewportRefreshTransition), beginSameStructure);
        Assert.Equal(default(DashboardViewportRefreshTransition), completeSameStructure);
        Assert.True(completedLayout.UnsubscribeLayoutUpdated);
        Assert.Equal(240, completedLayout.VerticalOffsetToRestore);
    }

    [Fact]
    public void ViewportRefreshLifecycleDoesNothingForConsecutiveSameStructureRefreshes()
    {
        var lifecycle = new DashboardViewportRefreshLifecycle();
        var viewportReadCount = 0;

        foreach (var generation in new long[] { 1, 2 })
        {
            var application = new DashboardSnapshotApplicationEventArgs(
                generation,
                DashboardSnapshotApplyPurpose.DataRefresh,
                hasStructuralChanges: false);
            Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.BeginSnapshotApplication(application, () =>
            {
                viewportReadCount++;
                return 240;
            }));
            Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.CompleteSnapshotApplication(application));
        }

        Assert.Equal(0, viewportReadCount);
        Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.CompleteLayout(1_000));
    }

    [Fact]
    public void ViewportRefreshLifecycleCancelsPendingRestoreForUserInputAndDispose()
    {
        var lifecycle = new DashboardViewportRefreshLifecycle();
        var structural = new DashboardSnapshotApplicationEventArgs(
            1,
            DashboardSnapshotApplyPurpose.DataRefresh,
            hasStructuralChanges: true);

        _ = lifecycle.BeginSnapshotApplication(structural, () => 240);
        Assert.True(lifecycle.CompleteSnapshotApplication(structural).SubscribeLayoutUpdated);

        var userInput = lifecycle.RecordUserInteraction();
        var disposed = lifecycle.Cancel();

        Assert.True(userInput.UnsubscribeLayoutUpdated);
        Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.CompleteLayout(1_000));
        Assert.Equal(default(DashboardViewportRefreshTransition), disposed);
        Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.Cancel());
    }

    [Fact]
    public void ViewportRefreshLifecycleCancelsForUserFilterAndDoesNotLeakLayoutSubscription()
    {
        var lifecycle = new DashboardViewportRefreshLifecycle();
        var structural = new DashboardSnapshotApplicationEventArgs(
            1,
            DashboardSnapshotApplyPurpose.DataRefresh,
            hasStructuralChanges: true);
        var userFilter = new DashboardSnapshotApplicationEventArgs(
            2,
            DashboardSnapshotApplyPurpose.UserFilter,
            hasStructuralChanges: false);

        _ = lifecycle.BeginSnapshotApplication(structural, () => 240);
        Assert.True(lifecycle.CompleteSnapshotApplication(structural).SubscribeLayoutUpdated);

        var cancel = lifecycle.BeginSnapshotApplication(userFilter, () => throw new InvalidOperationException());

        Assert.True(cancel.UnsubscribeLayoutUpdated);
        Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.CompleteSnapshotApplication(userFilter));
        Assert.Equal(default(DashboardViewportRefreshTransition), lifecycle.CompleteLayout(1_000));
    }

    [Fact]
    public void SnapshotApplicationLifecycleClassifiesTheProductionApplyEvents()
    {
        var collections = new DashboardPresentationCollections();
        collections.Apply(CreatePresentationInput("first"));
        var input = CreatePresentationInput("second");
        var lifecycle = new DashboardSnapshotApplicationLifecycle();

        var applying = lifecycle.Begin(
            DashboardSnapshotApplyPurpose.DataRefresh,
            collections.WouldApplyHaveStructuralChanges(input));
        var applied = DashboardSnapshotApplicationLifecycle.Complete(
            applying,
            collections.Apply(input));

        Assert.Equal(applying.ApplicationGeneration, applied.ApplicationGeneration);
        Assert.Equal(DashboardSnapshotApplyPurpose.DataRefresh, applying.Purpose);
        Assert.False(applying.HasStructuralChanges);
        Assert.False(applied.HasStructuralChanges);
        Assert.False(applying.RequiresVerticalViewportRestore);
        Assert.False(applied.RequiresVerticalViewportRestore);
    }

    [Fact]
    public void CollectionReconcilerUpdatesMatchedRowsInPlaceWithoutCollectionChange()
    {
        var existing = new TestDashboardRow("gpt-5.6-sol", "before");
        var rows = new ObservableCollection<TestDashboardRow> { existing };
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        var result = DashboardCollectionReconciler.Synchronize(
            rows,
            [new TestDashboardRow("gpt-5.6-sol", "after")],
            static row => row.Id,
            static (current, incoming) => current.UpdateFrom(incoming));

        Assert.False(result.HasStructuralChanges);
        Assert.Same(existing, rows[0]);
        Assert.Equal("after", existing.Value);
        Assert.Empty(collectionChanges);
    }

    [Fact]
    public void CollectionReconcilerReportsStructuralChangeForAReorderedSort()
    {
        var first = new TestDashboardRow("gpt-5.6-sol", "first");
        var second = new TestDashboardRow("gpt-5.6-terra", "second");
        var rows = new ObservableCollection<TestDashboardRow> { first, second };
        var reordered = new[]
        {
            new TestDashboardRow("gpt-5.6-terra", "after-terra"),
            new TestDashboardRow("gpt-5.6-sol", "after-sol"),
        };

        Assert.True(DashboardCollectionReconciler.WouldRequireStructuralChanges(
            rows,
            reordered,
            static row => row.Id));

        var result = DashboardCollectionReconciler.Synchronize(
            rows,
            reordered,
            static row => row.Id,
            static (current, incoming) => current.UpdateFrom(incoming));

        Assert.True(result.HasStructuralChanges);
        Assert.Same(second, rows[0]);
        Assert.Same(first, rows[1]);
    }

    [Fact]
    public void CostCompositionUsesTheFourPricedCategoriesWithStructuredDetails()
    {
        var summary = new UsageSummary(
            Calls: 1,
            InputTokens: 200,
            CachedInputTokens: 60,
            UncachedInputTokens: 140,
            OutputTokens: 40,
            ReasoningOutputTokens: 15,
            OtherOutputTokens: 25,
            CanonicalTotalTokens: 240,
            UnpricedTokens: 0,
            Cost: new CostBreakdown(20, 60, 15, 5, 100, Priced: true));

        var slices = DashboardCostComposition.From(summary, 200, "gpt-5.6-sol");

        Assert.Collection(
            slices,
            slice => AssertSlice(slice, DashboardCostCategory.UncachedInput, "gpt-5.6-sol", 20, 10, 140, "PrimaryBrush"),
            slice => AssertSlice(slice, DashboardCostCategory.CachedInput, "gpt-5.6-sol", 60, 30, 60, "SuccessBrush"),
            slice => AssertSlice(slice, DashboardCostCategory.ReasoningOutput, "gpt-5.6-sol", 15, 7.5, 15, "WarningBrush"),
            slice => AssertSlice(slice, DashboardCostCategory.OtherOutput, "gpt-5.6-sol", 5, 2.5, 25, "PurpleBrush"));
        Assert.Equal("$60.0", slices[1].Cost);
        Assert.Equal("60.0%", slices[1].EntityShare);
        Assert.Equal("30.0%", slices[1].OverallShare);
        Assert.Equal("60", slices[1].Tokens);
        Assert.Contains("gpt-5.6-sol · 缓存输入", slices[1].ToolTipText, StringComparison.Ordinal);
    }

    [Fact]
    public void CostCompositionRendersPricedZeroWithoutInvalidPercentages()
    {
        var summary = UsageSummaryFor(CostBreakdown.PricedZero);
        var slices = DashboardCostComposition.From(summary, 0, "Others");

        Assert.Equal(4, slices.Count);
        Assert.All(slices, slice =>
        {
            Assert.Equal(0, slice.Percentage);
            Assert.Equal("$0.0", slice.Cost);
            Assert.Equal("0.0%", slice.EntityShare);
            Assert.Equal("0.0%", slice.OverallShare);
            Assert.Equal(CostPricingStatus.Priced, slice.PricingStatus);
        });
    }

    [Fact]
    public void CostCompositionDoesNotInventSharesForUnpricedUsage()
    {
        var summary = UsageSummaryFor(
            CostBreakdown.UnpricedZero,
            inputTokens: 21_000,
            cachedInputTokens: 4_000,
            outputTokens: 300,
            reasoningOutputTokens: 200,
            unpricedTokens: 21_300);

        var slices = DashboardCostComposition.From(summary, 706.3m, "codex-auto-review");
        var presentation = DashboardCostPresentation.From(summary, 706.3m);

        Assert.Equal("未定价", presentation.Cost);
        Assert.Equal("—", presentation.Share);
        Assert.Equal(CostPricingStatus.Unpriced, presentation.PricingStatus);
        Assert.All(slices, slice =>
        {
            Assert.Equal(CostPricingStatus.Unpriced, slice.PricingStatus);
            Assert.False(slice.IsPriced);
            Assert.Equal(0, slice.Percentage);
            Assert.Equal("未定价", slice.Cost);
            Assert.Equal("—", slice.EntityShare);
            Assert.Equal("—", slice.OverallShare);
        });
        Assert.Equal(17_000, slices[0].TokenCount);
        Assert.Equal(4_000, slices[1].TokenCount);
        Assert.Equal(200, slices[2].TokenCount);
        Assert.Equal(100, slices[3].TokenCount);
    }

    [Fact]
    public void SubagentAggregateSumsOnlyTheAlreadyAggregatedSubagentRows()
    {
        var aggregate = DashboardSubagentAggregation.From(
        [
            new RoleUsageRow(
                ThreadType.Main,
                "root",
                ThreadCount: 2,
                UsageSummaryFor(new CostBreakdown(10, 20, 3, 7, 40, Priced: true), 100, 20, 10, 3)),
            new RoleUsageRow(
                ThreadType.Subagent,
                "worker",
                ThreadCount: 3,
                UsageSummaryFor(new CostBreakdown(11, 21, 4, 8, 44, Priced: true), 110, 21, 12, 4)),
            new RoleUsageRow(
                ThreadType.Subagent,
                "reviewer",
                ThreadCount: 4,
                UsageSummaryFor(new CostBreakdown(12, 22, 5, 9, 48, Priced: true), 120, 22, 13, 5)),
        ]);

        Assert.NotNull(aggregate);
        Assert.Equal(7, aggregate.ThreadCount);
        Assert.Equal(230, aggregate.Summary.InputTokens);
        Assert.Equal(43, aggregate.Summary.CachedInputTokens);
        Assert.Equal(187, aggregate.Summary.UncachedInputTokens);
        Assert.Equal(25, aggregate.Summary.OutputTokens);
        Assert.Equal(9, aggregate.Summary.ReasoningOutputTokens);
        Assert.Equal(16, aggregate.Summary.OtherOutputTokens);
        Assert.Equal(23, aggregate.Summary.Cost.UncachedInput);
        Assert.Equal(43, aggregate.Summary.Cost.CachedInput);
        Assert.Equal(9, aggregate.Summary.Cost.ReasoningOutput);
        Assert.Equal(17, aggregate.Summary.Cost.OtherOutput);
        Assert.Equal(92, aggregate.Summary.Cost.Total);
    }

    [Fact]
    public void SubagentAggregateIsAbsentWhenNoSubagentRowsAreFiltered()
    {
        var aggregate = DashboardSubagentAggregation.From(
        [
            SubjectRow(40, ThreadType.Main, "root"),
            SubjectRow(10, ThreadType.Unknown, "unknown"),
        ]);

        Assert.Null(aggregate);
    }

    [Fact]
    public void SubjectRowsDisambiguateUnknownAndSubagentWorkers()
    {
        var rows = new[]
        {
            new SubjectUsageRow(
                SubjectUsageRowKind.Role,
                "role:Subagent:worker",
                "子代理",
                "worker",
                "$10.0",
                "10.0%",
                CreateTestSlices("子代理 · worker", "subagent")),
            new SubjectUsageRow(
                SubjectUsageRowKind.Role,
                "role:Unknown:worker",
                "unknown",
                "worker",
                "$5.0",
                "5.0%",
                CreateTestSlices("unknown · worker", "unknown")),
        };

        Assert.Equal(["worker", "unknown · worker"], rows.Select(row => row.DisplayName));
    }

    [Theory]
    [InlineData(CollectorPhase.Watching, "正在监测", "\uE73E", DashboardHeaderStatusTone.Success)]
    [InlineData(CollectorPhase.Partial, "正在监测 · 部分数据可能不完整", "\uE7BA", DashboardHeaderStatusTone.Warning)]
    [InlineData(CollectorPhase.Syncing, "正在同步", "\uE895", DashboardHeaderStatusTone.Accent)]
    [InlineData(CollectorPhase.Retrying, "正在更新数据", "\uE72C", DashboardHeaderStatusTone.Accent)]
    [InlineData(CollectorPhase.Degraded, "需要关注", "\uE7BA", DashboardHeaderStatusTone.Danger)]
    [InlineData(CollectorPhase.Stopped, "已暂停", "\uE711", DashboardHeaderStatusTone.Muted)]
    public void HeaderStatusPresentationUsesExpectedToneForEachPhase(
        CollectorPhase phase,
        string text,
        string glyph,
        DashboardHeaderStatusTone tone)
    {
        var presentation = DashboardHeaderStatusPresentation.From(phase);

        Assert.Equal(text, presentation.Text);
        Assert.Equal(glyph, presentation.Glyph);
        Assert.Equal(tone, presentation.Tone);
    }

    [Fact]
    public void HeaderStatusPresentationUsesStartingTextForAnUnknownPhase()
    {
        var presentation = DashboardHeaderStatusPresentation.From((CollectorPhase)int.MaxValue);

        Assert.Equal("正在启动", presentation.Text);
        Assert.Equal("\uE895", presentation.Glyph);
        Assert.Equal(DashboardHeaderStatusTone.Muted, presentation.Tone);
    }

    [Fact]
    public void PresentationCollectionsRefreshExistingDashboardRowsInPlace()
    {
        var collections = new DashboardPresentationCollections();
        collections.Apply(CreatePresentationInput("first"));
        collections.ModelOptions[0].IsSelected = false;

        var metric = collections.Metrics[0];
        var cost = collections.CostSlices[0];
        var model = collections.Models[0];
        var subject = collections.Subjects[0];
        var diagnostic = collections.Diagnostics[0];
        var modelOption = collections.ModelOptions[0];
        var agentOption = collections.AgentOptions[0];
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        SubscribeToAllCollectionChanges(collections, collectionChanges);

        Assert.False(collections.WouldApplyHaveStructuralChanges(CreatePresentationInput("second")));
        var result = collections.Apply(CreatePresentationInput("second"));

        Assert.Empty(collectionChanges);
        Assert.False(result.HasStructuralChanges);
        Assert.Empty(collectionChanges);
        Assert.Same(metric, collections.Metrics[0]);
        Assert.Same(cost, collections.CostSlices[0]);
        Assert.Same(model, collections.Models[0]);
        Assert.Same(subject, collections.Subjects[0]);
        Assert.Same(diagnostic, collections.Diagnostics[0]);
        Assert.Same(modelOption, collections.ModelOptions[0]);
        Assert.Same(agentOption, collections.AgentOptions[0]);
        Assert.Equal("metric-second", metric.Value);
        Assert.Equal(42, cost.Percentage);
        Assert.Equal("cost-second", cost.EntityLabel);
        Assert.Equal("model-cost-second", model.Cost);
        Assert.Equal("model-share-second", model.Share);
        Assert.Equal(2, model.CostSlices[0].CostAmount);
        Assert.Equal("subject-cost-second", subject.Cost);
        Assert.Equal("subject-share-second", subject.Share);
        Assert.Equal("主线程 · root", subject.DisplayName);
        Assert.Equal(2, subject.CostSlices[0].CostAmount);
        Assert.Equal("diagnostic-value-second", diagnostic.Value);
        Assert.Equal("diagnostic-detail-second", diagnostic.Detail);
        Assert.False(modelOption.IsSelected);
        Assert.Equal(string.Empty, modelOption.SelectionGlyph);
        Assert.True(agentOption.IsSelected);
        Assert.Equal("root", agentOption.Label);
    }

    [Fact]
    public void PresentationCollectionsChangeOnlyModelAndSubjectStructuresWhenFacetsChange()
    {
        var collections = new DashboardPresentationCollections();
        collections.Apply(CreatePresentationInput("first"));
        var changes = new List<string>();
        SubscribeToCollectionChanges(collections.Metrics, "Metrics", changes);
        SubscribeToCollectionChanges(collections.CostSlices, "CostSlices", changes);
        SubscribeToCollectionChanges(collections.Models, "Models", changes);
        SubscribeToCollectionChanges(collections.Subjects, "Subjects", changes);
        SubscribeToCollectionChanges(collections.Diagnostics, "Diagnostics", changes);
        SubscribeToCollectionChanges(collections.ModelOptions, "ModelOptions", changes);
        SubscribeToCollectionChanges(collections.AgentOptions, "AgentOptions", changes);

        var second = CreatePresentationInput("second", includeAdditionalFacet: true);
        Assert.True(collections.WouldApplyHaveStructuralChanges(second));
        var result = collections.Apply(second);

        AssertFacetStructureChanges(changes);
        Assert.True(result.HasStructuralChanges);
        changes.Clear();

        var third = CreatePresentationInput("third");
        Assert.True(collections.WouldApplyHaveStructuralChanges(third));
        result = collections.Apply(third);

        AssertFacetStructureChanges(changes);
        Assert.True(result.HasStructuralChanges);
    }

    [Fact]
    public void PresentationCollectionsKeepAggregateAndRealRoleRowsDistinctAndStable()
    {
        var collections = new DashboardPresentationCollections();
        var first = CreatePresentationInput("first") with
        {
            Subjects =
            [
                new(SubjectUsageRowKind.SubagentAggregate, "subagent-aggregate", "子代理", "合计", "$10.0", "10.0%", CreateTestSlices("子代理合计", "first")),
                new(SubjectUsageRowKind.Role, "role:Subagent:aggregate", "子代理", "aggregate", "$5.0", "5.0%", CreateTestSlices("子代理 · aggregate", "first")),
            ],
        };
        collections.Apply(first);
        var aggregate = collections.Subjects[0];
        var role = collections.Subjects[1];

        var second = CreatePresentationInput("second") with
        {
            Subjects =
            [
                new(SubjectUsageRowKind.SubagentAggregate, "subagent-aggregate", "子代理", "合计", "$12.0", "12.0%", CreateTestSlices("子代理合计", "second")),
                new(SubjectUsageRowKind.Role, "role:Subagent:aggregate", "子代理", "aggregate", "$6.0", "6.0%", CreateTestSlices("子代理 · aggregate", "second")),
            ],
        };

        Assert.False(collections.WouldApplyHaveStructuralChanges(second));
        var result = collections.Apply(second);

        Assert.False(result.HasStructuralChanges);
        Assert.Same(aggregate, collections.Subjects[0]);
        Assert.Same(role, collections.Subjects[1]);
        Assert.Equal(SubjectUsageRowKind.SubagentAggregate, aggregate.Kind);
        Assert.Equal("子代理合计", aggregate.DisplayName);
        Assert.Equal(SubjectUsageRowKind.Role, role.Kind);
        Assert.Equal("aggregate", role.DisplayName);
        Assert.Equal("$12.0", aggregate.Cost);
        Assert.Equal("$6.0", role.Cost);
    }

    [Fact]
    public void StatusDiagnosticSubsetUpdatePreservesFullSnapshotDiagnostics()
    {
        var collections = new DashboardPresentationCollections();
        collections.Apply(CreateFullDiagnosticPresentationInput("first"));
        var originalRows = collections.Diagnostics.ToArray();
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        collections.Diagnostics.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        collections.UpdateDiagnosticsSubset(
        [
            .. CreateStatusDiagnostics("status"),
            new("操作状态", "operation-updated", "operation-updated-detail"),
        ]);

        Assert.Empty(collectionChanges);
        Assert.Equal(16, collections.Diagnostics.Count);
        Assert.Equal(originalRows, collections.Diagnostics);
        Assert.Equal("health-status", collections.Diagnostics[0].Value);
        Assert.Equal("watcher-status", collections.Diagnostics[1].Value);
        Assert.Equal("operation-updated", collections.Diagnostics[6].Value);
        Assert.Equal("collector-first", collections.Diagnostics[7].Value);
        Assert.Equal("malformed-first", collections.Diagnostics[10].Value);
        Assert.Contains(collections.Diagnostics, row => row.Label == "扫描文件");
        Assert.Contains(collections.Diagnostics, row => row.Label == "重复累计快照");
        Assert.Contains(collections.Diagnostics, row => row.Label == "无拆分快照");
        Assert.Contains(collections.Diagnostics, row => row.Label == "关系无效");
        Assert.Contains(collections.Diagnostics, row => row.Label == "部分解析源 / 安全跳过");

        collections.Apply(CreateFullDiagnosticPresentationInput("second"));

        Assert.Empty(collectionChanges);
        Assert.Equal(16, collections.Diagnostics.Count);
        Assert.Equal(originalRows, collections.Diagnostics);
        Assert.Equal("operation-status", collections.Diagnostics[6].Value);
        Assert.Equal("collector-second", collections.Diagnostics[7].Value);
        Assert.Equal("malformed-second", collections.Diagnostics[10].Value);
    }

    [Fact]
    public void ViewportRestoreIsInvalidatedByUserFilterNavigation()
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var ticket = coordinator.PrepareForDataRefresh(true, () => 240)!.Value;
        coordinator.InvalidatePendingRestoration();

        Assert.False(coordinator.TryConsumeLatest(ticket, out _));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void ViewportRestoreNormalizesInvalidCapturedOffsets(double capturedOffset, double expectedOffset)
    {
        var coordinator = new DashboardViewportRestoreCoordinator();
        var ticket = coordinator.PrepareForDataRefresh(true, () => capturedOffset)!.Value;

        Assert.True(coordinator.TryConsumeLatest(ticket, out var offset));
        Assert.Equal(expectedOffset, offset);
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1, 12)]
    [InlineData(2, 24)]
    [InlineData(3, 168)]
    [InlineData(4, 336)]
    public void TimeRangeScaleMapsEqualVisualAnchorsToPiecewiseHours(double position, double hours)
    {
        Assert.Equal(hours, DashboardTimeRangeScale.PositionToHours(position));
        Assert.Equal(position, DashboardTimeRangeScale.HoursToPosition(hours), precision: 10);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(18.5)]
    [InlineData(24)]
    [InlineData(80.5)]
    [InlineData(168)]
    [InlineData(252.5)]
    [InlineData(336)]
    public void TimeRangeScaleRoundTripsHalfHourValues(double hours)
    {
        var position = DashboardTimeRangeScale.HoursToPosition(hours);

        Assert.Equal(hours, DashboardTimeRangeScale.PositionToHours(position));
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(0.74, 0.5)]
    [InlineData(0.75, 1)]
    [InlineData(336.4, 336)]
    [InlineData(400, 336)]
    public void TimeRangeScaleClampsAndQuantizesToHalfHours(double hours, double expected)
    {
        Assert.Equal(expected, DashboardTimeRangeScale.NormalizeHours(hours));
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(5, 336)]
    public void TimeRangeScaleClampsPositionsOutsideTheTrack(double position, double expectedHours)
    {
        Assert.Equal(expectedHours, DashboardTimeRangeScale.PositionToHours(position));
    }

    [Fact]
    public void TimeRangeScalePlacesEightyPointFiveHoursBetweenOneAndSevenDayAnchors()
    {
        var position = DashboardTimeRangeScale.HoursToPosition(80.5);

        Assert.InRange(position, 2, 3);
        Assert.True(position > 2.3);
    }

    [Theory]
    [InlineData(0.5, "0.5小时")]
    [InlineData(12, "12小时")]
    [InlineData(24, "1天")]
    [InlineData(80.5, "3.4天")]
    [InlineData(168, "7天")]
    [InlineData(336, "14天")]
    public void TimeRangeScaleFormatsHoursAndDays(double hours, string expected)
    {
        Assert.Equal(expected, DashboardTimeRangeScale.FormatHours(hours));
    }

    [Fact]
    public void PointerInputReturnsCanonicalHalfHourPositionWithoutASecondLogicalChange()
    {
        var transition = DashboardTimeRangeTransition.FromUserPosition(
            currentHours: 12,
            inputPosition: 2.3924,
            hasCustomRange: false);
        var feedback = DashboardTimeRangeTransition.FromUserPosition(
            transition.Selection.Hours,
            transition.Selection.Position,
            hasCustomRange: false);

        Assert.Equal(80.5, transition.Selection.Hours);
        Assert.Equal(
            DashboardTimeRangeScale.HoursToPosition(80.5),
            transition.Selection.Position,
            precision: 10);
        Assert.True(transition.HoursChanged);
        Assert.True(transition.QueryRequired);
        Assert.False(feedback.HoursChanged);
        Assert.False(feedback.QueryRequired);
    }

    [Fact]
    public void UserSliderInputClearsCustomRangeEvenWhenHoursStayTheSame()
    {
        var transition = DashboardTimeRangeTransition.FromUserPosition(
            currentHours: 12,
            inputPosition: DashboardTimeRangeScale.HoursToPosition(12),
            hasCustomRange: true);

        Assert.False(transition.HoursChanged);
        Assert.True(transition.ClearCustomRange);
        Assert.True(transition.QueryRequired);
    }

    [Fact]
    public void ProgrammaticRangeSynchronizationHasNoUserSideEffects()
    {
        var initialization = DashboardTimeRangeTransition.FromProgrammaticHours(12, 12);
        var synchronization = DashboardTimeRangeTransition.FromProgrammaticHours(12, 80.5);

        Assert.False(initialization.HoursChanged);
        Assert.False(initialization.ClearCustomRange);
        Assert.False(initialization.QueryRequired);
        Assert.True(synchronization.HoursChanged);
        Assert.Equal(80.5, synchronization.Selection.Hours);
        Assert.False(synchronization.ClearCustomRange);
        Assert.False(synchronization.QueryRequired);
    }

    [Theory]
    [InlineData(12, DashboardTimeRangeAdjustment.Decrease, 11.5)]
    [InlineData(12, DashboardTimeRangeAdjustment.Increase, 12.5)]
    [InlineData(80.5, DashboardTimeRangeAdjustment.PreviousAnchor, 24)]
    [InlineData(80.5, DashboardTimeRangeAdjustment.NextAnchor, 168)]
    [InlineData(12, DashboardTimeRangeAdjustment.Minimum, 0.5)]
    [InlineData(12, DashboardTimeRangeAdjustment.Maximum, 336)]
    [InlineData(0.5, DashboardTimeRangeAdjustment.Decrease, 0.5)]
    [InlineData(336, DashboardTimeRangeAdjustment.Increase, 336)]
    public void KeyboardAdjustmentsOperateOnHoursAndAnchors(
        double hours,
        DashboardTimeRangeAdjustment adjustment,
        double expected)
    {
        Assert.Equal(expected, DashboardTimeRangeScale.AdjustHours(hours, adjustment));
    }

    [Theory]
    [InlineData(DashboardDirectionalKey.Left, false, DashboardTimeRangeAdjustment.Decrease)]
    [InlineData(DashboardDirectionalKey.Right, false, DashboardTimeRangeAdjustment.Increase)]
    [InlineData(DashboardDirectionalKey.Left, true, DashboardTimeRangeAdjustment.Increase)]
    [InlineData(DashboardDirectionalKey.Right, true, DashboardTimeRangeAdjustment.Decrease)]
    [InlineData(DashboardDirectionalKey.Up, true, DashboardTimeRangeAdjustment.Increase)]
    [InlineData(DashboardDirectionalKey.Down, true, DashboardTimeRangeAdjustment.Decrease)]
    public void DirectionalKeyboardSemanticsRespectHorizontalFlowOnly(
        DashboardDirectionalKey key,
        bool rightToLeft,
        DashboardTimeRangeAdjustment expected)
    {
        Assert.Equal(expected, DashboardTimeRangeInput.DirectionalAdjustment(key, rightToLeft));
    }

    [Theory]
    [InlineData(1, 100, false, 25)]
    [InlineData(1, 100, true, 75)]
    [InlineData(3, 100, false, 75)]
    [InlineData(3, 100, true, 25)]
    public void TimeRangeGeometryMapsLogicalPositionToPhysicalTrack(
        double position,
        double trackWidth,
        bool rightToLeft,
        double expectedPhysicalX)
    {
        var physicalX = DashboardTimeRangeGeometry.PositionToPhysicalX(position, trackWidth, rightToLeft);

        Assert.Equal(expectedPhysicalX, physicalX);
        Assert.Equal(
            position,
            DashboardTimeRangeGeometry.PhysicalXToPosition(physicalX, trackWidth, rightToLeft),
            precision: 10);
    }

    [Fact]
    public void RtlDragUsesRawPhysicalDeltaAndMirrorsExactlyOnce()
    {
        var startPhysicalX = DashboardTimeRangeGeometry.PositionToPhysicalX(1, 100, rightToLeft: true);
        var positionAfterDraggingRight = DashboardTimeRangeGeometry.PhysicalXToPosition(
            startPhysicalX + 10,
            100,
            rightToLeft: true);

        Assert.Equal(0.6, positionAfterDraggingRight, precision: 10);
        Assert.Equal(7.5, DashboardTimeRangeScale.PositionToHours(positionAfterDraggingRight));
    }

    [Theory]
    [InlineData(double.NaN, 2, 2, true)]
    [InlineData(double.PositiveInfinity, double.NaN, 1, true)]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity, 1, true)]
    [InlineData(-1, 2, 0, true)]
    [InlineData(5, 2, 4, true)]
    [InlineData(2.5, double.NaN, 2.5, false)]
    public void TimeRangePositionCoercionNeverPropagatesNonFiniteValues(
        double requested,
        double previous,
        double expectedPosition,
        bool expectedCorrection)
    {
        var coercion = DashboardTimeRangeScale.CoercePositionInput(requested, previous);

        Assert.Equal(expectedPosition, coercion.Position);
        Assert.Equal(expectedCorrection, coercion.RequiresCorrection);
    }


    [Theory]
    [InlineData(null, "2026-07-02T00:00:00+08:00", "请选择开始和结束日期.")]
    [InlineData("2026-07-02T00:00:00+08:00", null, "请选择开始和结束日期.")]
    [InlineData("2026-07-02T00:00:00+08:00", "2026-07-02T18:00:00-04:00", "结束日期必须晚于开始日期.")]
    [InlineData("2026-07-03T00:00:00+08:00", "2026-07-02T00:00:00+08:00", "结束日期必须晚于开始日期.")]
    public void CustomRangeRejectsMissingAndNonIncreasingDates(
        string? startText,
        string? endText,
        string expectedMessage)
    {
        var start = startText is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(startText);
        var end = endText is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(endText);

        var valid = DashboardCustomRange.TryCreateFromSgtDates(
            start,
            end,
            out var range,
            out var message);

        Assert.False(valid);
        Assert.Null(range);
        Assert.Equal(expectedMessage, message);
    }

    [Fact]
    public void CustomRangeUsesCalendarDatesAtSgtMidnightAndUtcHalfOpenBounds()
    {
        var valid = DashboardCustomRange.TryCreateFromSgtDates(
            DateTimeOffset.Parse("2026-07-01T18:45:00-04:00"),
            DateTimeOffset.Parse("2026-07-03T05:15:00+02:00"),
            out var range,
            out var message);

        Assert.True(valid);
        Assert.Equal(string.Empty, message);
        Assert.NotNull(range);
        Assert.Equal(DateTimeOffset.Parse("2026-06-30T16:00:00Z"), range.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T16:00:00Z"), range.EndUtc);
        Assert.Equal("07/01 - 07/03", range.Label);
    }

    [Fact]
    public void CoveragePresentationUsesNonQuantitativeStateSemantics()
    {
        var baseline = CoveragePresentation.From(ObservationCoverage.Baseline, null);
        var continuous = CoveragePresentation.From(ObservationCoverage.Continuous, null);
        var gap = CoveragePresentation.From(
            ObservationCoverage.Gap,
            new ObservationGap(
                DateTimeOffset.Parse("2026-07-30T02:12:22Z"),
                DateTimeOffset.Parse("2026-07-30T02:18:07Z")));

        Assert.Equal((0, 0), (baseline.ContinuousOpacity, baseline.GapOpacity));
        Assert.Equal("已建立本次运行基线", baseline.Text);
        Assert.Equal((1, 0), (continuous.ContinuousOpacity, continuous.GapOpacity));
        Assert.Equal("持续观测中", continuous.Text);
        Assert.Equal((0, 1), (gap.ContinuousOpacity, gap.GapOpacity));
        Assert.Equal("10:12:22 - 10:18:07 未观测", gap.Text);
    }

    [Fact]
    public void PartialPricingRetainsKnownCostAndSignalsTheStatus()
    {
        var presentation = DashboardCostPresentation.From(
            UsageSummaryFor(
                new CostBreakdown(1, 2, 3, 4, 10, Priced: true),
                unpricedTokens: 20),
            20);

        Assert.Equal("$10.0", presentation.Cost);
        Assert.Equal("50.0%", presentation.Share);
        Assert.Equal(CostPricingStatus.PartiallyPriced, presentation.PricingStatus);
    }

    [Fact]
    public void PricedAndZeroCostPresentationRetainNumericValues()
    {
        var priced = DashboardCostPresentation.From(
            UsageSummaryFor(new CostBreakdown(40, 80, 40, 34.4m, 194.4m, Priced: true)),
            205.6m);
        var others = DashboardCostPresentation.From(UsageSummaryFor(CostBreakdown.PricedZero), 205.6m);

        Assert.Equal("$194.4", priced.Cost);
        Assert.Equal("94.6%", priced.Share);
        Assert.Equal(CostPricingStatus.Priced, priced.PricingStatus);
        Assert.Equal("$0.0", others.Cost);
        Assert.Equal("0.0%", others.Share);
        Assert.Equal(CostPricingStatus.Priced, others.PricingStatus);
    }

    [Fact]
    public void MainThreadFilterOptionUsesTwelveCharacterIdPrefixAndTitle()
    {
        var option = new MainThreadFilterOption(new MainThreadOption(
            "019fe0d7-dd64-7412-8fa0-ea96334569dd",
            "codex-usage-desktop",
            "实现主线程筛选",
            DateTimeOffset.Parse("2026-08-08T11:00:00Z")));

        Assert.Equal("codex-usage-desktop - 019fe0d7-dd6 - 实现主线程筛选", option.DisplayLabel);
    }

    [Fact]
    public void SubjectRowsSortByRawCostBeforePresentationOrder()
    {
        var sorted = DashboardSubjectOrdering.SortByDescendingCost(
        [
            SubjectRow(17.6m, ThreadType.Subagent, "scoped_worker"),
            SubjectRow(61.2m, ThreadType.Main, "root"),
            SubjectRow(17.7m, ThreadType.Subagent, "worker"),
        ]);

        Assert.Equal(
            ["root", "worker", "scoped_worker"],
            sorted.Select(row => row.AgentRole).ToArray());
    }

    [Fact]
    public void SubjectRowsUseSemanticOrderThenOrdinalFallbackForEqualCosts()
    {
        var sorted = DashboardSubjectOrdering.SortByDescendingCost(
        [
            SubjectRow(5m, ThreadType.Subagent, "simple_worker"),
            SubjectRow(5m, ThreadType.Subagent, "zeta"),
            SubjectRow(5m, ThreadType.Subagent, "reviewer"),
            SubjectRow(5m, ThreadType.Subagent, "unknown"),
            SubjectRow(5m, ThreadType.Subagent, "scoped_worker"),
            SubjectRow(5m, ThreadType.Main, "root"),
            SubjectRow(5m, ThreadType.Subagent, "worker"),
            SubjectRow(5m, ThreadType.Subagent, "alpha"),
            SubjectRow(5m, ThreadType.Unknown, "omega"),
        ]);

        Assert.Equal(
            ["root", "worker", "reviewer", "scoped_worker", "simple_worker", "unknown", "alpha", "zeta", "omega"],
            sorted.Select(row => row.AgentRole).ToArray());
    }

    [Theory]
    [InlineData(1, 56)]
    [InlineData(1.5, 68)]
    [InlineData(2, 80)]
    [InlineData(2.25, 86)]
    public void TableRowsUseOneSharedHeightAtSupportedTextScales(double scale, double expectedHeight)
    {
        Assert.Equal(expectedHeight, DashboardAccessibilityLayout.TableRowHeight(scale));
    }

    [Theory]
    [InlineData(0, 56)]
    [InlineData(4, 86)]
    [InlineData(double.NaN, 56)]
    public void TableRowHeightClampsInvalidOrUnsupportedTextScales(double scale, double expectedHeight)
    {
        Assert.Equal(expectedHeight, DashboardAccessibilityLayout.TableRowHeight(scale));
    }

    [Theory]
    [InlineData(0, 900, 720)]
    [InlineData(96, 900, 720)]
    [InlineData(120, 1125, 900)]
    [InlineData(144, 1350, 1080)]
    [InlineData(192, 1800, 1440)]
    public void MinimumTrackClientPixelsScaleEffectiveSizeForWindowDpi(
        uint dpi,
        int expectedWidth,
        int expectedHeight)
    {
        var size = DashboardWindowSizing.MinimumTrackClientPixels(900, 720, dpi);

        Assert.Equal((expectedWidth, expectedHeight), (size.Width, size.Height));
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(900, 0)]
    [InlineData(-1, 720)]
    public void MinimumTrackClientPixelsRejectNonPositiveEffectiveSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DashboardWindowSizing.MinimumTrackClientPixels(width, height, 96));
    }

    [Fact]
    public void ShellResourceCleanupRemovesTrayBeforeRestoringProcedureAndDestroyingIcon()
    {
        var steps = ShellResourceCleanupPlan.OrderedSteps(
            trayIconAdded: true,
            windowProcedureInstalled: true,
            iconHandleOwned: true);

        Assert.Equal<ShellResourceCleanupStep>(
            [
                ShellResourceCleanupStep.RemoveTrayIcon,
                ShellResourceCleanupStep.RestoreWindowProcedure,
                ShellResourceCleanupStep.DestroyOwnedIcon,
            ],
            steps.AsEnumerable());
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, true, true, 2)]
    [InlineData(true, false, true, 2)]
    public void ShellResourceCleanupIncludesOnlyOwnedResources(
        bool trayIconAdded,
        bool windowProcedureInstalled,
        bool iconHandleOwned,
        int expectedSteps)
    {
        var steps = ShellResourceCleanupPlan.OrderedSteps(
            trayIconAdded,
            windowProcedureInstalled,
            iconHandleOwned);

        Assert.Equal(expectedSteps, steps.Length);
    }

    private static DashboardPresentationInput CreateFullDiagnosticPresentationInput(string marker) =>
        CreatePresentationInput(marker) with
        {
            Diagnostics =
            [
                .. CreateStatusDiagnostics(marker),
                new("操作状态", "operation-status", "operation-detail"),
                new("Collector phase", $"collector-{marker}", $"collector-detail-{marker}"),
                new("Pending files", $"pending-{marker}", $"pending-detail-{marker}"),
                new("Cooperative yields", $"yields-{marker}", $"yields-detail-{marker}"),
                new("Malformed lines", $"malformed-{marker}", $"malformed-detail-{marker}"),
                new("扫描文件", $"scanned-{marker}", $"scanned-detail-{marker}"),
                new("重复累计快照", $"duplicate-{marker}", $"duplicate-detail-{marker}"),
                new("无拆分快照", $"zero-breakdown-{marker}", $"zero-breakdown-detail-{marker}"),
                new("关系无效", $"relationships-{marker}", $"relationships-detail-{marker}"),
                new("部分解析源 / 安全跳过", $"partial-{marker}", $"partial-detail-{marker}"),
            ],
        };

    private static DiagnosticRow[] CreateStatusDiagnostics(string marker) =>
    [
        new("健康状态", $"health-{marker}", $"health-detail-{marker}"),
        new("Watcher", $"watcher-{marker}", $"watcher-detail-{marker}"),
        new("上次对账", $"reconciliation-{marker}", $"reconciliation-detail-{marker}"),
        new("源文件", $"sources-{marker}", $"sources-detail-{marker}"),
        new("待处理文件", $"retry-{marker}", $"retry-detail-{marker}"),
        new("观察覆盖", $"coverage-{marker}", $"coverage-detail-{marker}"),
    ];

    private static DashboardPresentationInput CreatePresentationInput(
        string marker,
        bool includeAdditionalFacet = false)
    {
        var root = new SubjectFilter(ThreadType.Main, "root");
        var worker = new SubjectFilter(ThreadType.Subagent, "worker");
        var models = new List<ModelUsageRow>
        {
            new("gpt-5.6-sol", $"model-cost-{marker}", $"model-share-{marker}", CreateTestSlices("gpt-5.6-sol", marker)),
        };
        var subjects = new List<SubjectUsageRow>
        {
            new(SubjectUsageRowKind.Role, "role:Main:root", "主线程", "root", $"subject-cost-{marker}", $"subject-share-{marker}", CreateTestSlices("主线程 · root", marker)),
        };
        var modelOptions = new List<ModelFilterOption> { new("gpt-5.6-sol") };
        var agentOptions = new List<SubjectFilterOption> { new(root) };
        if (includeAdditionalFacet)
        {
            models.Add(new("gpt-5.6-terra", "model-cost-terra", "model-share-terra", CreateTestSlices("gpt-5.6-terra", "terra")));
            subjects.Add(new(SubjectUsageRowKind.Role, "role:Subagent:worker", "子代理", "worker", "subject-cost-worker", "subject-share-worker", CreateTestSlices("子代理 · worker", "worker")));
            modelOptions.Add(new("gpt-5.6-terra"));
            agentOptions.Add(new(worker));
        }

        return new DashboardPresentationInput(
            [new("总 tokens", $"metric-{marker}")],
            [new(DashboardCostCategory.UncachedInput, $"cost-{marker}", 1, 42, 12, 10, CostPricingStatus.Priced, "PrimaryBrush")],
            models,
            subjects,
            [new("Collector phase", $"diagnostic-value-{marker}", $"diagnostic-detail-{marker}")],
            modelOptions,
            agentOptions);
    }

    private static void SubscribeToAllCollectionChanges(
        DashboardPresentationCollections collections,
        List<NotifyCollectionChangedAction> changes)
    {
        collections.Metrics.CollectionChanged += (_, args) => changes.Add(args.Action);
        collections.CostSlices.CollectionChanged += (_, args) => changes.Add(args.Action);
        collections.Models.CollectionChanged += (_, args) => changes.Add(args.Action);
        collections.Subjects.CollectionChanged += (_, args) => changes.Add(args.Action);
        collections.Diagnostics.CollectionChanged += (_, args) => changes.Add(args.Action);
        collections.ModelOptions.CollectionChanged += (_, args) => changes.Add(args.Action);
        collections.AgentOptions.CollectionChanged += (_, args) => changes.Add(args.Action);
    }

    private static void AssertSlice(
        CostSlice slice,
        DashboardCostCategory category,
        string entityLabel,
        double entityShare,
        double overallShare,
        long tokenCount,
        string brushKey)
    {
        Assert.Equal(category, slice.Category);
        Assert.Equal(entityLabel, slice.EntityLabel);
        Assert.Equal(entityShare, slice.EntitySharePercentage);
        Assert.Equal(overallShare, slice.OverallSharePercentage);
        Assert.Equal(tokenCount, slice.TokenCount);
        Assert.Equal(CostPricingStatus.Priced, slice.PricingStatus);
        Assert.Equal(brushKey, slice.BrushKey);
    }

    private static IReadOnlyList<CostSlice> CreateTestSlices(string entityLabel, string marker) =>
    [
        new(
            DashboardCostCategory.UncachedInput,
            entityLabel,
            marker switch
            {
                "first" => 1,
                "second" => 2,
                _ => 3,
            },
            100,
            100,
            1,
            CostPricingStatus.Priced,
            "PrimaryBrush"),
    ];

    private static void SubscribeToCollectionChanges<TItem>(
        ObservableCollection<TItem> collection,
        string name,
        List<string> changes) => collection.CollectionChanged += (_, _) => changes.Add(name);

    private static void AssertFacetStructureChanges(IReadOnlyList<string> changes)
    {
        Assert.Contains("Models", changes);
        Assert.Contains("Subjects", changes);
        Assert.Contains("ModelOptions", changes);
        Assert.Contains("AgentOptions", changes);
        Assert.All(changes, change => Assert.Contains(
            change,
            new[] { "Models", "Subjects", "ModelOptions", "AgentOptions" }));
    }

    private static RoleUsageRow SubjectRow(decimal totalCost, ThreadType threadType, string role) => new(
        threadType,
        role,
        ThreadCount: 0,
        new UsageSummary(
            Calls: 0,
            InputTokens: 0,
            CachedInputTokens: 0,
            UncachedInputTokens: 0,
            OutputTokens: 0,
            ReasoningOutputTokens: 0,
            OtherOutputTokens: 0,
            CanonicalTotalTokens: 0,
            UnpricedTokens: 0,
            Cost: new CostBreakdown(0, 0, 0, 0, totalCost, Priced: true)));

    private static UsageSummary UsageSummaryFor(
        CostBreakdown cost,
        long inputTokens = 0,
        long cachedInputTokens = 0,
        long outputTokens = 0,
        long reasoningOutputTokens = 0,
        long unpricedTokens = 0) => new(
            Calls: 1,
            InputTokens: inputTokens,
            CachedInputTokens: cachedInputTokens,
            UncachedInputTokens: inputTokens - cachedInputTokens,
            OutputTokens: outputTokens,
            ReasoningOutputTokens: reasoningOutputTokens,
            OtherOutputTokens: outputTokens - reasoningOutputTokens,
            CanonicalTotalTokens: inputTokens + outputTokens,
            UnpricedTokens: unpricedTokens,
            Cost: cost);

    private sealed class TestDashboardRow(string id, string value)
    {
        public string Id { get; } = id;
        public string Value { get; private set; } = value;

        public void UpdateFrom(TestDashboardRow source) => Value = source.Value;
    }
}
