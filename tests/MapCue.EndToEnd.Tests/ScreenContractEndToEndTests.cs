using System.Text.Json;
using MapCue.Application.Configuration;
using MapCue.Application.ScreenContracts;
using MapCue.Contracts.Schemas;
using MapCue.Contracts.ScreenContracts;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.EndToEnd.Tests;

/// <summary>
/// STEP-08: screen contracts of the demo app (both profiles, <see cref="FakeDemoProfiles"/>) from <c>AnalyzeScreen</c> (full hierarchy) and
/// <c>AuditAccessibility</c> (keyboard focus probe with Tab), versioned by content hash, with the accessibility matrix: no PASS from the tree,
/// <c>unsupported</c> where TapCue cannot do it, <c>assistiveVerified</c> only from manual records.
/// </summary>
public class ScreenContractEndToEndTests
{
    private static readonly ContractSettings _captures = new(AnalyzeScreens: true, KeyboardFocus: true, MaxTabPresses: 12);

    private static MapCueSettings Settings(ContractSettings? contracts = null, Budgets? budgets = null) =>
        ExplorationHarness.ProfilesSettings(budgets) with { Contracts = contracts ?? _captures };

    private static ExplorationHarness Create(FakeAppModel? app = null, ContractSettings? contracts = null, Budgets? budgets = null) =>
        ExplorationHarness.Create(app ?? FakeDemoProfiles.Create(), Settings(contracts, budgets));

    private static string Marker(ScreenContractDocument c) =>
        c.Elements.Select(e => e.ElementKey).FirstOrDefault(k => k.Contains(":id/screen.", StringComparison.Ordinal)) is { } key ? key[(key.LastIndexOf('/') + 1)..] : c.ScreenKey;

    private static async Task<IReadOnlyList<ScreenContractDocument>> BuildAsync(ExplorationHarness h) =>
        await ScreenContractBuilder.BuildAsync(h.Store, h.Artifacts, CancellationToken.None);

    private static AccessibilityMatrixRow Row(ScreenContractDocument c, AccessibilityTechnology technology) =>
        c.Accessibility.Matrix.Single(r => r.Technology == technology.ToString());

    private static void AssertSchemaValid(ScreenContractDocument contract)
    {
        using var document = JsonDocument.Parse(ScreenContractJson.Serialize(contract with { Version = Math.Max(1, contract.Version) }));
        Assert.Empty(ContractSchemas.ScreenContract.Validate(document.RootElement));
    }

    [Fact]
    public async Task Both_profiles_give_one_contract_per_screen_with_variants_per_context()
    {
        await using var h = Create();
        Assert.Equal(RunFinishReason.Exhausted, (await h.RunProfileAsync("basic")).Reason);
        Assert.Equal(RunFinishReason.Exhausted, (await h.RunProfileAsync("investor")).Reason);

        var contracts = await BuildAsync(h);
        string[] screens = ["screen.accounts", "screen.details", "screen.home", "screen.investments", "screen.profile", "screen.settings", "screen.verify", "screen.whatsnew"];
        Assert.Equal(screens, contracts.Select(Marker).Order(StringComparer.Ordinal));
        var contexts = await h.ReadAsync(uow => uow.Graph.ListContextsAsync(CancellationToken.None));
        var basic = contexts.Single(c => c.ProfileId == "basic").ContextId;
        var investor = contexts.Single(c => c.ProfileId == "investor").ContextId;

        foreach (var contract in contracts)
        {
            AssertSchemaValid(contract);
            Assert.Equal("Android", contract.Group.Platform);
            Assert.Equal("demo-1", contract.Group.AppBuildId);
            Assert.All(contract.Variants, v => Assert.Equal("full", v.Source));
            Assert.All(contract.Keyboard.Predicted, p => Assert.Equal(("predicted", "unverified"), (p.Level, p.Status)));
            Assert.Equal(contract.ContentHash, ScreenContractJson.ContentHashOf(contract));
        }

        // Shared screens have a variant per context (both profiles); Investments only in investor.
        var home = contracts.Single(c => Marker(c) == "screen.home");
        Assert.Equal(new[] { basic, investor }.Order(StringComparer.Ordinal), home.Variants.Select(v => v.ContextId).Order(StringComparer.Ordinal));
        Assert.Equal([investor], contracts.Single(c => Marker(c) == "screen.investments").Variants.Select(v => v.ContextId));

        // The Investments tab: present only in investor, its visibility an observed guard on the investments capability.
        var tab = home.Elements.Single(e => e.ElementKey.EndsWith("/tab.investments", StringComparison.Ordinal));
        Assert.Equal([investor], tab.Presence.Select(p => p.ContextId).Distinct(StringComparer.Ordinal));
        Assert.NotNull(tab.VisibleWhen);
        Assert.Equal(("investments", "observed"), (tab.VisibleWhen!.Capability, tab.VisibleWhen.Level));
        Assert.Equal([basic], tab.VisibleWhen.AbsentIn);
        var shared = home.Elements.Single(e => e.ElementKey.EndsWith("/home.accounts", StringComparison.Ordinal));
        Assert.Null(shared.VisibleWhen);
        Assert.Equal(new[] { basic, investor }.Order(StringComparer.Ordinal), shared.Presence.Select(p => p.ContextId).Order(StringComparer.Ordinal));

        // Full captures are snapshots of their state and context; the explored graph is unchanged by them (no capture edge).
        var (captures, snapshots, edges) = await h.ReadAsync(async uow => (
            await uow.Contracts.ListCapturesAsync(null, CancellationToken.None),
            await uow.Graph.ListSnapshotsAsync(CancellationToken.None),
            await uow.Graph.ListEdgesAsync(CancellationToken.None)));
        Assert.Equal(15, captures.Count(c => c.Kind == ScreenCaptureKind.FullHierarchy && c.Status == ScreenCaptureStatus.Captured));
        Assert.Equal(15, captures.Count(c => c.Kind == ScreenCaptureKind.KeyboardFocus && c.Status == ScreenCaptureStatus.Captured));
        Assert.All(captures.Where(c => c.Kind == ScreenCaptureKind.FullHierarchy), c =>
        {
            var snapshot = snapshots.Single(s => s.SnapshotId == c.SnapshotId);
            Assert.Equal((SnapshotFidelity.Full, c.StateId, c.ContextId), (snapshot.Fidelity, snapshot.StateId, snapshot.ContextId));
        });
        Assert.Equal(15, h.Device.FullObservations);
        Assert.DoesNotContain(edges, e => captures.Any(c => c.SnapshotId is not null && snapshots.Single(s => s.SnapshotId == c.SnapshotId).AttemptId is not null));
    }

    [Fact]
    public async Task Captures_never_change_the_explored_graph()
    {
        await using var with = Create();
        await using var without = Create(contracts: ContractSettings.Default);
        foreach (var h in new[] { with, without })
        {
            await h.RunProfileAsync("basic");
            await h.RunProfileAsync("investor");
        }

        var a = await with.ReadAsync(uow => uow.Graph.ListEdgesAsync(CancellationToken.None));
        var b = await without.ReadAsync(uow => uow.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.Equal(b.Select(e => (e.EdgeId, e.ToStateId, e.Status)), a.Select(e => (e.EdgeId, e.ToStateId, e.Status)));
        Assert.Empty(await without.ReadAsync(uow => uow.Contracts.ListCapturesAsync(null, CancellationToken.None)));
        Assert.Equal(0, without.Device.KeyPresses);
    }

    [Fact]
    public async Task The_matrix_never_passes_from_the_tree_and_says_unsupported_where_TapCue_cannot_check()
    {
        await using var h = Create();
        await h.RunProfileAsync("basic");
        await h.RunProfileAsync("investor");

        var contracts = await BuildAsync(h);
        foreach (var contract in contracts)
        {
            Assert.Equal(["TalkBack", "VoiceOver", "Keyboard"], contract.Accessibility.Matrix.Select(r => r.Technology));
            Assert.All(contract.Accessibility.Matrix, r => Assert.Contains(r.Inspected.Status, A11yStatus.InspectedAllowed));
            Assert.All(contract.Accessibility.Matrix, r => Assert.NotEqual(A11yStatus.Pass, r.Inspected.Status));
            Assert.Equal(A11yStatus.Unsupported, Row(contract, AccessibilityTechnology.TalkBack).Automated.Status);
            Assert.Equal(A11yStatus.NotTested, Row(contract, AccessibilityTechnology.TalkBack).AssistiveVerified.Status);
            var voiceOver = Row(contract, AccessibilityTechnology.VoiceOver);
            Assert.Equal([A11yStatus.Unsupported, A11yStatus.Unsupported, A11yStatus.Unsupported], new[] { voiceOver.Inspected.Status, voiceOver.Automated.Status, voiceOver.AssistiveVerified.Status });
            Assert.Equal(A11yStatus.NotTested, Row(contract, AccessibilityTechnology.Keyboard).AssistiveVerified.Status);
        }

        // The unlabeled switch of Settings is a finding to check (inspected), never a verdict.
        var settings = contracts.Single(c => Marker(c) == "screen.settings");
        Assert.Equal(A11yStatus.Issues, Row(settings, AccessibilityTechnology.TalkBack).Inspected.Status);
        var finding = Assert.Single(settings.Accessibility.Findings, f => f.RuleId == "label.missing" && f.ElementKey!.EndsWith("/settings.notifications", StringComparison.Ordinal) && f.ContextId == settings.Contexts[0].ContextId);
        Assert.Equal("inspected", finding.Level);
        Assert.NotEmpty(finding.EvidenceIds);
        Assert.Equal(A11yStatus.Present, Row(contracts.Single(c => Marker(c) == "screen.whatsnew"), AccessibilityTechnology.TalkBack).Inspected.Status);

        // Keyboard: every target of every state reached and the focus cycle closed (FakeDevice moves focus over the clickable elements).
        Assert.All(contracts, c => Assert.Equal(A11yStatus.Pass, Row(c, AccessibilityTechnology.Keyboard).Automated.Status));
        var home = contracts.Single(c => Marker(c) == "screen.home");
        Assert.All(home.Keyboard.Observed, g => Assert.True(g.CycleClosed && g.Unreached.Count == 0 && g.Status == A11yStatus.Pass));
        Assert.All(home.Keyboard.Observed, g => Assert.Equal(true, g.MatchesPredicted));
        Assert.Contains(home.Keyboard.Observed, g => g.Order.Any(k => k.EndsWith("/tab.investments", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_element_the_keyboard_never_reaches_fails_the_automated_keyboard_cell()
    {
        var app = FakeDemoProfiles.Create();
        app = app with
        {
            Screens = [.. app.Screens.Select(s => s with { Elements = [.. s.Elements.Select(e => e.ResourceId.EndsWith("/home.whatsnew", StringComparison.Ordinal) ? e with { KeyboardFocusable = false } : e)] })],
        };
        await using var h = Create(app);
        await h.RunProfileAsync("basic");

        var contracts = await BuildAsync(h);
        var home = contracts.Single(c => Marker(c) == "screen.home");
        Assert.Equal(A11yStatus.Fail, Row(home, AccessibilityTechnology.Keyboard).Automated.Status);
        var graph = Assert.Single(home.Keyboard.Observed);
        Assert.Equal(A11yStatus.Fail, graph.Status);
        Assert.True(graph.CycleClosed);
        Assert.Equal([FakeDemoApp.Id("home.whatsnew")], graph.Unreached.Select(k => k["id:".Length..]));
        Assert.Contains(home.Accessibility.Findings, f => f.RuleId == "keyboard.unreachable" && f.Level == "automated");

        // The only target of What's new is still reachable: other screens keep their verdict.
        Assert.Equal(A11yStatus.Pass, Row(contracts.Single(c => Marker(c) == "screen.whatsnew"), AccessibilityTechnology.Keyboard).Automated.Status);
    }

    [Fact]
    public async Task Focus_that_stops_moving_is_unverified_never_a_closed_cycle()
    {
        // Emulator, STEP-08: Tab focused one element and the next press left it there. Two presses on the same element are not a cycle,
        // so the verdict is unverified (with the element the focus stayed on), never pass and never fail.
        var app = FakeDemoProfiles.Create();
        app = app with
        {
            Screens = [.. app.Screens.Select(s => s with { Elements = [.. s.Elements.Select(e => e.ResourceId.EndsWith("/settings.notifications", StringComparison.Ordinal) ? e with { TrapsKeyboardFocus = true } : e)] })],
        };
        await using var h = Create(app);
        await h.RunProfileAsync("basic");

        var contracts = await BuildAsync(h);
        var settings = contracts.Single(c => Marker(c) == "screen.settings");
        var graph = Assert.Single(settings.Keyboard.Observed);
        Assert.Equal(A11yStatus.Unverified, graph.Status);
        Assert.False(graph.CycleClosed);
        Assert.Contains("focusDidNotMove", graph.Detail!, StringComparison.Ordinal);
        Assert.Contains("settings.notifications", graph.Detail!, StringComparison.Ordinal);
        Assert.Equal(A11yStatus.Unverified, Row(settings, AccessibilityTechnology.Keyboard).Automated.Status);
        Assert.DoesNotContain(settings.Accessibility.Findings, f => f.RuleId == "keyboard.unreachable");

        // A screen with one keyboard target that keeps the focus on it is fully covered (a cycle of one).
        var whatsNew = contracts.Single(c => Marker(c) == "screen.whatsnew");
        Assert.True(Assert.Single(whatsNew.Keyboard.Observed).CycleClosed);
        Assert.Equal(A11yStatus.Pass, Row(whatsNew, AccessibilityTechnology.Keyboard).Automated.Status);
    }

    [Fact]
    public async Task Without_an_observable_focus_the_keyboard_cell_is_unverified_not_pass()
    {
        await using var h = Create(FakeDemoProfiles.Create() with { ReportsKeyboardFocus = false });
        Assert.Equal(RunFinishReason.Exhausted, (await h.RunProfileAsync("basic")).Reason);

        var contracts = await BuildAsync(h);
        Assert.All(contracts, c => Assert.Equal(A11yStatus.Unverified, Row(c, AccessibilityTechnology.Keyboard).Automated.Status));
        var captures = await h.ReadAsync(uow => uow.Contracts.ListCapturesAsync(null, CancellationToken.None));
        Assert.All(captures.Where(c => c.Kind == ScreenCaptureKind.KeyboardFocus), c => Assert.Equal(ScreenCaptureStatus.NoFocusObserved, c.Status));

        // Three presses per state, then the probe gives up (focus is not observable this way).
        Assert.Equal(3 * captures.Count(c => c.Kind == ScreenCaptureKind.KeyboardFocus), h.Device.KeyPresses);
    }

    [Fact]
    public async Task Contract_versions_change_only_with_the_content_and_a_manual_record_is_the_only_way_to_assistive_verified()
    {
        await using var h = Create();
        await h.RunProfileAsync("basic");

        var first = await ScreenContractPublisher.PublishAsync(h.Store, h.Artifacts, h.Clock, await BuildAsync(h), h.Root, CancellationToken.None);
        Assert.All(first, p => Assert.True(p.NewVersion && p.Contract.Version == 1));
        Assert.All(first, p => Assert.True(File.Exists(Path.Combine(h.Root, p.RelativePath))));
        Assert.All(first, p => Assert.Matches(@"^screens/screen-[A-Za-z0-9-]+/grp_[0-9a-f]{12}/screen-contract\.v1\.json$", p.RelativePath));

        // The same store gives the same contracts: no new version.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await ScreenContractPublisher.PublishAsync(h.Store, h.Artifacts, h.Clock, await BuildAsync(h), h.Root, CancellationToken.None);
        Assert.All(again, p => Assert.False(p.NewVersion));
        Assert.Equal(first.Select(p => p.Contract.ContentHash), again.Select(p => p.Contract.ContentHash));

        // A manual TalkBack check of the Home screen: version 2 of Home only, with assistiveVerified pass and the record as evidence.
        var home = first.Single(p => Marker(p.Contract) == "screen.home").Contract;
        var record = await AccessibilityRecorder.RecordAsync(
            h.Store,
            h.Artifacts,
            h.Redactor,
            h.Clock,
            new AccessibilityRecordRequest(home.ScreenKey, "basic", null, null, AccessibilityTechnology.TalkBack, null, A11yProperties.Screen, AccessibilityRecordResult.Pass, "operator-1", "Pixel 8, Android 15, TalkBack 15.0", "1. Swipe right through Home: every element announced with its name and role.\n2. Double tap View accounts: opens Accounts.\n", null),
            CancellationToken.None);
        var third = await ScreenContractPublisher.PublishAsync(h.Store, h.Artifacts, h.Clock, await BuildAsync(h), h.Root, CancellationToken.None);
        var homeV2 = third.Single(p => p.Contract.ScreenId == home.ScreenId);
        Assert.True(homeV2.NewVersion);
        Assert.Equal(2, homeV2.Contract.Version);
        Assert.Single(third, p => p.NewVersion);
        var talkBack = Row(homeV2.Contract, AccessibilityTechnology.TalkBack);
        Assert.Equal(A11yStatus.Pass, talkBack.AssistiveVerified.Status);
        Assert.Equal([record.RecordId], talkBack.AssistiveVerified.RecordIds);
        Assert.Contains(record.EvidenceId, homeV2.Contract.EvidenceIds);
        Assert.NotEqual(A11yStatus.Pass, talkBack.Inspected.Status);
        Assert.Equal(2, (await h.ReadAsync(uow => uow.Contracts.ListContractsAsync(CancellationToken.None))).Count(v => v.ScreenId == home.ScreenId));

        // A failing element record wins over the screen pass.
        await AccessibilityRecorder.RecordAsync(
            h.Store,
            h.Artifacts,
            h.Redactor,
            h.Clock,
            new AccessibilityRecordRequest(home.ScreenKey, "basic", null, null, AccessibilityTechnology.TalkBack, home.Elements.First(e => e.KeyboardTarget).ElementKey, "label", AccessibilityRecordResult.Fail, "operator-1", "Pixel 8", "announced as 'button' only", null),
            CancellationToken.None);
        var homeV3 = (await BuildAsync(h)).Single(c => c.ScreenId == home.ScreenId);
        Assert.Equal(A11yStatus.Fail, Row(homeV3, AccessibilityTechnology.TalkBack).AssistiveVerified.Status);
    }

    [Fact]
    public async Task Manual_records_that_cannot_be_true_are_refused()
    {
        await using var h = Create();
        await h.RunProfileAsync("basic");
        await h.RunProfileAsync("investor");
        var home = (await BuildAsync(h)).Single(c => Marker(c) == "screen.home");

        Task Record(AccessibilityRecordRequest request) => AccessibilityRecorder.RecordAsync(h.Store, h.Artifacts, h.Redactor, h.Clock, request, CancellationToken.None);
        var valid = new AccessibilityRecordRequest(home.ScreenKey, "basic", null, null, AccessibilityTechnology.TalkBack, null, A11yProperties.Screen, AccessibilityRecordResult.Pass, "op", "Pixel 8", "transcript", null);

        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { Technology = AccessibilityTechnology.VoiceOver }));
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { EvidenceText = " " }));
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { Screen = "screen-unknown" }));
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { Profile = null }));
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { Property = "overall" }));
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { ElementKey = "id:x" }));
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => Record(valid with { Operator = "a\u0000b" }));
        Assert.Empty(await h.ReadAsync(uow => uow.Contracts.ListRecordsAsync(CancellationToken.None)));

        // A secret in the evidence never reaches the store.
        h.Redactor.RegisterSensitiveValue("hunter2-secret");
        var record = await AccessibilityRecorder.RecordAsync(h.Store, h.Artifacts, h.Redactor, h.Clock, valid with { EvidenceText = "logged in with hunter2-secret" }, CancellationToken.None);
        var evidence = (await h.ReadAsync(uow => uow.Graph.ListEvidenceAsync(record.ContextId, null, CancellationToken.None))).Single(e => e.EvidenceId == record.EvidenceId);
        Assert.DoesNotContain("hunter2-secret", await h.Artifacts.ReadTextAsync(evidence.ArtifactSha256, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_iOS_the_keyboard_and_screen_reader_automation_cells_are_unsupported()
    {
        var ios = ExplorationHarness.Profile with { Platform = Platform.Ios };
        var settings = ExplorationHarness.DemoSettings() with { Profiles = [ios], Contracts = _captures };
        await using var h = ExplorationHarness.Create(FakeDemoApp.Basic(), settings);
        var device = new FakeDevice(FakeDemoApp.Basic(), h.Clock, platform: Platform.Ios);
        var result = await h.Engine(device: device).RunAsync(h.Options(), CancellationToken.None);
        Assert.Equal(RunFinishReason.Exhausted, result.Reason);

        // No keyboard probe on iOS (G-04): no AuditAccessibility task, no key press.
        Assert.Equal(0, device.KeyPresses);
        var tasks = await h.ReadAsync(uow => uow.Frontier.ListTasksAsync(null, CancellationToken.None));
        Assert.DoesNotContain(tasks, t => t.Type == TaskType.AuditAccessibility);
        Assert.Contains(tasks, t => t.Type == TaskType.AnalyzeScreen && t.Status == FrontierTaskStatus.Completed);

        var contracts = await BuildAsync(h);
        Assert.NotEmpty(contracts);
        foreach (var contract in contracts)
        {
            AssertSchemaValid(contract);
            Assert.Equal("Ios", contract.Group.Platform);
            var talkBack = Row(contract, AccessibilityTechnology.TalkBack);
            Assert.All(new[] { talkBack.Inspected.Status, talkBack.Automated.Status, talkBack.AssistiveVerified.Status }, s => Assert.Equal(A11yStatus.Unsupported, s));
            var voiceOver = Row(contract, AccessibilityTechnology.VoiceOver);
            Assert.NotEqual(A11yStatus.Pass, voiceOver.Inspected.Status);
            Assert.Equal(A11yStatus.Unsupported, voiceOver.Automated.Status);
            Assert.Equal(A11yStatus.NotTested, voiceOver.AssistiveVerified.Status);
            var keyboard = Row(contract, AccessibilityTechnology.Keyboard);
            Assert.All(new[] { keyboard.Inspected.Status, keyboard.Automated.Status, keyboard.AssistiveVerified.Status }, s => Assert.Equal(A11yStatus.Unsupported, s));
            Assert.Empty(contract.Keyboard.Observed);
            Assert.All(contract.Elements, e => Assert.Equal("inferred", e.RoleLevel));
        }

        // A keyboard record on iOS is refused (G-04).
        var any = contracts[0];
        await Assert.ThrowsAsync<AccessibilityRecordException>(() => AccessibilityRecorder.RecordAsync(
            h.Store,
            h.Artifacts,
            h.Redactor,
            h.Clock,
            new AccessibilityRecordRequest(any.ScreenKey, null, null, null, AccessibilityTechnology.Keyboard, null, A11yProperties.Screen, AccessibilityRecordResult.Pass, "op", "iPhone", "transcript", null),
            CancellationToken.None));
    }

    [Fact]
    public async Task Key_presses_are_charged_to_the_action_budget()
    {
        var budgets = Budgets.Default with { MaxActions = 60 };
        await using var h = Create(budgets: budgets);
        var result = await h.RunProfileAsync("basic");

        Assert.Equal(RunFinishReason.BudgetExhausted, result.Reason);
        Assert.True(result.Counters.ChargedActions <= 60);
        Assert.True(result.Counters.KeyPresses > 0);
        Assert.Equal(h.Device.KeyPresses, result.Counters.KeyPresses);

        // The next run with a larger budget finishes the captures that are left (pending tasks), none twice.
        await using var next = ExplorationHarness.Open(h.Root, FakeDemoProfiles.Create(), Settings());
        Assert.Equal(RunFinishReason.Exhausted, (await next.RunProfileAsync("basic", "seed-basic-2")).Reason);
        var captures = await next.ReadAsync(uow => uow.Contracts.ListCapturesAsync(null, CancellationToken.None));
        Assert.Equal(14, captures.Count);
        Assert.All(captures, c => Assert.Equal(ScreenCaptureStatus.Captured, c.Status));
    }
}
