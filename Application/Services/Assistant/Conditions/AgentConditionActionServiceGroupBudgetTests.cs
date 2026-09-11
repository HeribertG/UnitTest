// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the SCOPE of the action budget: the daily budget and the circuit breaker are configured per
/// group, so they have to be counted per group as well. Pooling the count across groups let a busy group
/// exhaust a quiet one, and - because a blocked candidate used to end the whole tick - silence every
/// other group for the rest of the scan.
///
/// Deliberately a sibling fixture rather than more cases in AgentConditionActionServiceTests: that
/// fixture answers EVERY scope from one catch-all stub, ResolveAsync(Kind, Arg.Any&lt;Guid?&gt;(), ...), which
/// is exactly why the defect could not surface there - every group got the same budget and the same
/// owner, so a pooled counter and a per-group counter produced identical numbers. Here every scope is
/// stubbed separately with its OWN budget and its OWN responsible owner, and a scope nobody stubbed
/// fails loudly instead of silently inheriting a default.
///
/// The reporter assertions are addressed to a specific owner id on purpose: they are the proof that the
/// per-group stubs really discriminated, not merely that some report went out.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceGroupBudgetTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string SkillName = "test_remediation_skill";
    private const string UnbindableMarker = "unbindable";
    private const string UnbindablePayloadJson = "{\"unbindable\":true}";
    private const string BindablePayloadJson = "{}";
    private const string RemainingFindingsFormat = "{0} further finding(s)";
    private const string FailedReportMarker = "did not work";
    private const int DefaultWindowMinutes = 60;
    private const int GenerousBudget = 50;

    private static readonly DateTime NowUtc = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly Guid BusyGroupId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid QuietGroupId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SecondBusyGroupId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid BusyOwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QuietOwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstallationOwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecondBusyOwnerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private FakeAgentConditionRepository _repository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private FixedCompanyClock _companyClock = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private IProactiveGovernanceResolver _governance = null!;
    private IQuietWindowService _quietWindow = null!;
    private IProactiveActionIdentityProvider _identityProvider = null!;
    private ISkillExecutor _skillExecutor = null!;
    private IProactiveActionReporter _reporter = null!;
    private PayloadAwareRemediationRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeAgentConditionRepository();
        _timeProvider = new SettableTimeProvider(NowUtc);
        _companyClock = new FixedCompanyClock(NowUtc);
        _ledger = new AgentConditionLedgerService(
            _repository, _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

        _governance = Substitute.For<IProactiveGovernanceResolver>();

        _quietWindow = Substitute.For<IQuietWindowService>();
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = InstallationOwnerId,
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = ["some.permission"],
                    BypassAutonomyGate = true
                },
                ["some.permission"]));

        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Template created."));

        _reporter = Substitute.For<IProactiveActionReporter>();
        _reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _registry = new PayloadAwareRemediationRegistry();
    }

    /// <summary>
    /// THE core property. The busy group is served first (High) and is over its own budget; the quiet
    /// group is under its own. A counter pooled across groups would hand the quiet group the busy group's
    /// two claims and block it too, and a tick that returns on the first block would never reach it at
    /// all - both of which are exactly what a group of its own is supposed to protect it from.
    /// </summary>
    [Test]
    public async Task AGroupThatSpentItsBudget_NeitherBlocksNorSilencesAGroupThatHasNot()
    {
        GivenGovernance(BusyGroupId, BusyOwnerId, dailyActionBudget: 1);
        GivenGovernance(QuietGroupId, QuietOwnerId, dailyActionBudget: 2);

        var busy = GivenCondition(BusyGroupId, AgentTriggerSeverity.High, NowUtc.AddHours(-3));
        GivenSpentClaims(busy, count: 2);
        var quiet = GivenCondition(QuietGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Executed,
                Is.EqualTo(1),
                "The quiet group has spent nothing of its own budget of two. A pooled counter would "
                + "charge it with the busy group's claims, and the old return-on-block would never even "
                + "consider it.");
            Assert.That(_repository.Stored(quiet.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(_repository.Stored(busy.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(result.LeftForBudget, Is.EqualTo(1));
        });

        await _reporter.Received(1).ReportAsync(
            QuietOwnerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The circuit breaker is the same story one gate later, and it has its own trap: the window counts
    /// are cached by window LENGTH, so two groups configured with the same window minutes share a cache
    /// slot unless the bucket is per group.
    /// </summary>
    [Test]
    public async Task ACircuitBreakerTrippedInOneGroup_LeavesTheOtherGroupActing()
    {
        GivenGovernance(BusyGroupId, BusyOwnerId, windowActionLimit: 1);
        GivenGovernance(QuietGroupId, QuietOwnerId, windowActionLimit: 1);

        var busy = GivenCondition(BusyGroupId, AgentTriggerSeverity.High, NowUtc.AddHours(-3));
        GivenSpentClaims(busy, count: 1, atUtc: NowUtc.AddMinutes(-10));
        var quiet = GivenCondition(QuietGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(quiet.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(_repository.Stored(busy.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    /// <summary>
    /// Walking on after a block means the same exhausted group is met again on every remaining candidate.
    /// The owner must hear about it once, not once per finding.
    /// </summary>
    [Test]
    public async Task AnExhaustedGroup_ReportsItsBudgetStopExactlyOnce()
    {
        GivenGovernance(QuietGroupId, QuietOwnerId);
        GivenGovernance(BusyGroupId, BusyOwnerId, dailyActionBudget: 1);

        var quiet = GivenCondition(QuietGroupId, AgentTriggerSeverity.High, NowUtc.AddHours(-5));
        var firstBusy = GivenCondition(BusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-4));
        GivenSpentClaims(firstBusy, count: 1);
        GivenCondition(BusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-3));
        GivenCondition(BusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-2));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Executed,
                Is.EqualTo(1),
                "The quiet group acts first, which is what makes the budget report reachable at all - "
                + "a tick that acted on nothing reports nothing.");
            Assert.That(_repository.Stored(quiet.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(result.LeftForBudget, Is.EqualTo(3));
        });

        await _reporter.Received(1).ReportAsync(
            BusyOwnerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// "Report once" is once per GROUP, not once per tick. Two exhausted groups usually have two
    /// different responsible owners, and a single tick-wide flag would leave the second owner never
    /// hearing that their group stopped - a silence indistinguishable from "nothing was found".
    /// </summary>
    [Test]
    public async Task TwoExhaustedGroups_EachReportToTheirOwnOwner()
    {
        GivenGovernance(QuietGroupId, QuietOwnerId);
        GivenGovernance(BusyGroupId, BusyOwnerId, dailyActionBudget: 1);
        GivenGovernance(SecondBusyGroupId, SecondBusyOwnerId, dailyActionBudget: 1);

        GivenCondition(QuietGroupId, AgentTriggerSeverity.High, NowUtc.AddHours(-5));
        var firstBusy = GivenCondition(BusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-4));
        GivenSpentClaims(firstBusy, count: 1);
        var secondBusy = GivenCondition(SecondBusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-3));
        GivenSpentClaims(secondBusy, count: 1);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(result.LeftForBudget, Is.EqualTo(2));
        });

        await _reporter.Received(1).ReportAsync(
            BusyOwnerId, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _reporter.Received(1).ReportAsync(
            SecondBusyOwnerId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The number in the report is what the owner reads as "this much of my work is waiting". Counting
    /// every remaining candidate would charge one group with another group's backlog.
    /// </summary>
    [Test]
    public async Task TheBudgetReport_CountsOnlyTheFindingsOfItsOwnGroupAsLeftOpen()
    {
        GivenGovernance(QuietGroupId, QuietOwnerId);
        GivenGovernance(BusyGroupId, BusyOwnerId, dailyActionBudget: 1);

        GivenCondition(QuietGroupId, AgentTriggerSeverity.High, NowUtc.AddHours(-5));
        var firstBusy = GivenCondition(BusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-4));
        GivenSpentClaims(firstBusy, count: 1);
        var secondQuiet = GivenCondition(QuietGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-3));
        GivenCondition(BusyGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-2));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Executed,
                Is.EqualTo(2),
                "The quiet group's second finding sits BEHIND a blocked busy one and must still be acted on.");
            Assert.That(_repository.Stored(secondQuiet.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(result.LeftForBudget, Is.EqualTo(2));
        });

        var expectedRemaining = string.Format(
            CultureInfo.InvariantCulture, RemainingFindingsFormat, 2);

        await _reporter.Received(1).ReportAsync(
            BusyOwnerId,
            Arg.Is<string>(message => message.Contains(expectedRemaining, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A condition with no group at all is not "some group": it is the installation-wide scope, matched
    /// by the installation-wide governance row. It gets its own bucket, and a group's spending never
    /// reaches it.
    /// </summary>
    [Test]
    public async Task AGroupsSpentBudget_NeverBlocksTheInstallationWideBucket()
    {
        GivenGovernance(BusyGroupId, BusyOwnerId, dailyActionBudget: 1);
        GivenGovernance(groupId: null, InstallationOwnerId, dailyActionBudget: 1);

        var busy = GivenCondition(BusyGroupId, AgentTriggerSeverity.High, NowUtc.AddHours(-3));
        GivenSpentClaims(busy, count: 2);
        var installationWide = GivenCondition(groupId: null, AgentTriggerSeverity.Medium, NowUtc.AddHours(-1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(
                _repository.Stored(installationWide.Id).Status,
                Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(_repository.Stored(busy.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    /// <summary>
    /// The mirror image, and the one a naive "just pass null" fix would still get wrong: an exhausted
    /// installation-wide bucket must not spend a group's budget either.
    /// </summary>
    [Test]
    public async Task TheInstallationWideSpentBudget_NeverBlocksAGroup()
    {
        GivenGovernance(groupId: null, InstallationOwnerId, dailyActionBudget: 1);
        GivenGovernance(QuietGroupId, QuietOwnerId, dailyActionBudget: 2);

        var installationWide = GivenCondition(groupId: null, AgentTriggerSeverity.High, NowUtc.AddHours(-3));
        GivenSpentClaims(installationWide, count: 2);
        var quiet = GivenCondition(QuietGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(quiet.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(
                _repository.Stored(installationWide.Id).Status,
                Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    /// <summary>
    /// A payload refreshed between the pre-flight binding and the claim can stop binding. The claim has
    /// already been paid for - an attempt and a slot of the budget - so this cannot be a silent log line:
    /// without a report the remediation burns all three attempts and nobody ever hears why.
    /// </summary>
    [Test]
    public async Task AConditionThatStopsBindingAfterTheClaim_ReportsTheFailedAttempt()
    {
        GivenGovernance(QuietGroupId, QuietOwnerId);
        var condition = GivenCondition(QuietGroupId, AgentTriggerSeverity.Medium, NowUtc.AddHours(-1));
        _repository.RefreshPayloadOnNextTransitionFor(condition.Id, UnbindablePayloadJson);

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Prepared));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(
                _repository.EventsFor(condition.Id).Any(e => e.EventType == AgentConditionEventTypes.AttemptFailed),
                Is.True,
                "The failed attempt has to reach the ledger, or the row's history claims the claim "
                + "simply evaporated.");
        });

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());

        await _reporter.Received(1).ReportAsync(
            QuietOwnerId,
            Arg.Is<string>(message => message.Contains(FailedReportMarker, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private Task<AgentConditionActionTickResult> RunAsync() =>
        new AgentConditionActionService(
            _repository,
            _ledger,
            _governance,
            _registry,
            _quietWindow,
            _identityProvider,
            _skillExecutor,
            _reporter,
            _timeProvider,
            _companyClock,
            NullLogger<AgentConditionActionService>.Instance)
            .RunAsync(CancellationToken.None);

    /// <summary>
    /// One governance answer for ONE scope. No catch-all: a scope this fixture forgot to stub returns no
    /// decision at all and the run fails loudly, rather than quietly borrowing another scope's budget and
    /// owner - which is the shape of stubbing that hid the pooled counter in the first place.
    /// </summary>
    private void GivenGovernance(
        Guid? groupId,
        Guid ownerUserId,
        int dailyActionBudget = GenerousBudget,
        int windowActionLimit = GenerousBudget,
        int windowMinutes = DefaultWindowMinutes)
    {
        _governance
            .ResolveAsync(Kind, groupId, Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: Kind,
                GroupId: groupId,
                EffectiveMaxAction: ProactiveMaxAction.Execute,
                ConfiguredMaxAction: ProactiveMaxAction.Execute,
                Enabled: true,
                KillSwitchActive: false,
                ResponsibleOwnerUserId: ownerUserId,
                DailyActionBudget: dailyActionBudget,
                WindowActionLimit: windowActionLimit,
                WindowMinutes: windowMinutes,
                IsStored: true));
    }

    private AgentCondition GivenCondition(Guid? groupId, string severity, DateTime detectedAtUtc)
    {
        var condition = _repository.Seed(
            Kind, Guid.NewGuid().ToString(), AgentConditionStatus.Reported, detectedAtUtc);
        condition.Severity = severity;
        condition.GroupId = groupId;
        condition.EntityId = Guid.NewGuid();
        condition.PayloadJson = BindablePayloadJson;

        return condition;
    }

    /// <summary>
    /// Budget-consuming claim events from earlier ticks, on THIS condition - which is what binds them to
    /// this condition's group. Placed two hours back by default so they land in the day but outside the
    /// sixty-minute circuit-breaker window, keeping the daily budget and the breaker separable.
    /// </summary>
    private void GivenSpentClaims(AgentCondition condition, int count, DateTime? atUtc = null)
    {
        for (var index = 0; index < count; index++)
        {
            _repository.InsertEventAsync(new AgentConditionEvent
            {
                Id = Guid.NewGuid(),
                ConditionId = condition.Id,
                EventType = AgentConditionStatus.Prepared.ToString(),
                AtUtc = atUtc ?? NowUtc.AddHours(-2),
                Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
            }).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A registry whose binder actually READS the payload, so a payload rewritten between the pre-flight
    /// binding and the claim can stop binding - the only way to reach the post-claim re-bind at all.
    /// </summary>
    private sealed class PayloadAwareRemediationRegistry : IConditionRemediationRegistry
    {
        private const string RequiredArgument = "containerId";

        public IReadOnlyCollection<string> RegisteredKinds => [Kind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == Kind
                ? new ConditionRemediationEntry(SkillName, new PayloadAwareBinder(), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(
            string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == Kind ? configuredMaxAction : ProactiveMaxAction.Hint;

        private sealed class PayloadAwareBinder : IConditionRemediationParameterBinder
        {
            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                conditionPayload.ContainsKey(UnbindableMarker)
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [RequiredArgument] = Guid.NewGuid().ToString()
                    };
        }
    }
}
