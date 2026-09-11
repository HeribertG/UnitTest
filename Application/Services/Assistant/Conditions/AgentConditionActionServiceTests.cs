// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the Etappe 5b action dispatcher - the first place where Klacksy changes something without
/// being asked, so every one of these tests pins a SAFETY property rather than a convenience.
///
/// The ledger below the service under test is the real AgentConditionLedgerService over
/// FakeAgentConditionRepository, not a substitute, because the claim's semantics ARE the safety: the
/// state machine guard, the compare-and-swap, and the AttemptCount raised inside the claim rather than
/// after the outcome. Everything outside the ledger - governance, quiet window, identity, skill
/// execution, reporting - is substituted, since what matters about those here is only what the
/// dispatcher does with their answers.
///
/// What these cannot show, and what the integration suite is for: that two dispatchers racing the same
/// row produce exactly one execution, and that the budget join translates to SQL Npgsql accepts. The
/// fake has no transactions and the EF InMemory provider accepts LINQ real Postgres rejects.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string SkillName = "test_remediation_skill";

    private static readonly DateTime NowUtc = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private FakeAgentConditionRepository _repository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private FixedCompanyClock _companyClock = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private IProactiveGovernanceResolver _governance = null!;
    private IQuietWindowService _quietWindow = null!;
    private IProactiveActionIdentityProvider _identityProvider = null!;
    private ISkillExecutor _skillExecutor = null!;
    private IProactiveActionReporter _reporter = null!;
    private TestRemediationRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeAgentConditionRepository();
        _timeProvider = new SettableTimeProvider(NowUtc);
        _companyClock = new FixedCompanyClock(NowUtc);
        _ledger = new AgentConditionLedgerService(
            _repository, _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

        _governance = Substitute.For<IProactiveGovernanceResolver>();
        GivenGovernance(ProactiveMaxAction.Execute);

        _quietWindow = Substitute.For<IQuietWindowService>();
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        GivenIdentityResolves();

        _skillExecutor = Substitute.For<ISkillExecutor>();
        GivenSkillSucceeds();

        _reporter = Substitute.For<IProactiveActionReporter>();
        _reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _registry = new TestRemediationRegistry();
    }

    [Test]
    public async Task AReportedCondition_IsClaimedExecutedAndReported()
    {
        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(stored.HandlingKind, Is.EqualTo(AgentConditionHandlingKind.Executed));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(stored.LastAttemptAtUtc, Is.EqualTo(NowUtc));
        });

        await _reporter.Received(1).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AConditionAlreadyMarkedAsCausedByKlacksy_IsNeverAutoHandled()
    {
        var condition = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(condition.Id).CausedByConditionId = Guid.NewGuid();

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCascade, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.Stored(condition.Id).AttemptCount, Is.Zero);
        });

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AConditionAppearingAfterAKlacksyExecutionOnTheSameEntity_IsMarkedAndOnlyHinted()
    {
        var targetEntityId = Guid.NewGuid();
        var cause = GivenCondition(AgentConditionStatus.Executed, entityId: targetEntityId);
        _repository.Stored(cause.Id).HandledAtUtc = NowUtc.AddMinutes(-5);

        var follower = GivenCondition(
            AgentConditionStatus.Reported, entityId: targetEntityId, detectedAtUtc: NowUtc.AddMinutes(-2));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCascade, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(
                _repository.Stored(follower.Id).CausedByConditionId,
                Is.EqualTo(cause.Id),
                "The provenance has to be written, not only acted on, or the next tick re-decides it "
                + "from scratch once the cascade window has passed.");
        });
    }

    [Test]
    public async Task AConditionDetectedBeforeTheExecution_IsNotTreatedAsItsConsequence()
    {
        var targetEntityId = Guid.NewGuid();
        var earlier = GivenCondition(AgentConditionStatus.Executed, entityId: targetEntityId);
        _repository.Stored(earlier.Id).HandledAtUtc = NowUtc.AddMinutes(-5);

        GivenCondition(
            AgentConditionStatus.Reported, entityId: targetEntityId, detectedAtUtc: NowUtc.AddMinutes(-30));

        var result = await RunAsync();

        Assert.That(
            result.Executed,
            Is.EqualTo(1),
            "A finding that already existed before the execution cannot have been caused by it.");
    }

    [Test]
    public async Task AConditionAttemptedThreeTimes_IsEscalatedInsteadOfRetried()
    {
        var condition = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(condition.Id).AttemptCount = AgentConditionActionDefaults.MaxAttemptsBeforeEscalation;

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Escalated, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Escalated));
            Assert.That(_repository.Stored(condition.Id).EscalatedAtUtc, Is.EqualTo(NowUtc));
        });

        await _reporter.Received(1).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEscalationAnotherInstanceWon_IsNotCountedTwice()
    {
        var condition = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(condition.Id).AttemptCount = AgentConditionActionDefaults.MaxAttemptsBeforeEscalation;
        _repository.LoseNextTransitionFor(condition.Id);

        var result = await RunAsync();

        Assert.That(
            result.Escalated,
            Is.Zero,
            "Escalated is the number a planner reads to spot a stuck kind. Counting a lost "
            + "compare-and-swap would report an escalation this instance never made.");

        await _reporter.DidNotReceive().ReportAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ABudgetAlreadySpentOnAnEarlierTick_IsLoggedButNotReportedAgain()
    {
        GivenGovernance(ProactiveMaxAction.Execute, dailyActionBudget: 1);
        var condition = GivenCondition(AgentConditionStatus.Reported);
        GivenClaimEventToday(condition.Id);

        var result = await RunAsync();

        Assert.That(result.LeftForBudget, Is.EqualTo(1));

        await _reporter.DidNotReceive().ReportAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AQuietWindow_SkipsWithoutCountingAnAttempt()
    {
        var condition = GivenCondition(AgentConditionStatus.Reported);
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedQuiet, Is.EqualTo(1));
            Assert.That(
                _repository.Stored(condition.Id).AttemptCount,
                Is.Zero,
                "Counting a quiet skip as an attempt would escalate a condition after three quiet ticks "
                + "without a single remediation ever having been tried.");
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    [Test]
    public async Task AnExhaustedDailyBudget_LeavesTheRestOpenAndSaysSo()
    {
        GivenGovernance(ProactiveMaxAction.Execute, dailyActionBudget: 1);
        GivenCondition(AgentConditionStatus.Reported, severity: AgentTriggerSeverity.High);
        var second = GivenCondition(AgentConditionStatus.Reported, severity: AgentTriggerSeverity.Medium);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(result.LeftForBudget, Is.EqualTo(1));
            Assert.That(_repository.Stored(second.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });

        await _reporter.Received().ReportAsync(
            OwnerUserId,
            Arg.Is<string>(message => message.Contains("stay open")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheCircuitBreaker_StopsTheKindWithinItsWindow()
    {
        GivenGovernance(ProactiveMaxAction.Execute, windowActionLimit: 1);
        GivenCondition(AgentConditionStatus.Reported, severity: AgentTriggerSeverity.High);
        GivenCondition(AgentConditionStatus.Reported, severity: AgentTriggerSeverity.Medium);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(result.LeftForBudget, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TheAbsoluteTickCap_HoldsEvenWhenGovernanceAllowsMore()
    {
        var overCap = AgentConditionActionDefaults.MaxExecutionsPerKindPerTick + 2;
        GivenGovernance(ProactiveMaxAction.Execute, dailyActionBudget: 1000, windowActionLimit: 1000);
        for (var index = 0; index < overCap; index++)
        {
            GivenCondition(AgentConditionStatus.Reported);
        }

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(AgentConditionActionDefaults.MaxExecutionsPerKindPerTick));
            Assert.That(result.LeftForBudget, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task UnderScarcity_TheOldestOfTheMostSevereFindingsIsServedFirst()
    {
        GivenGovernance(ProactiveMaxAction.Execute, dailyActionBudget: 1);
        GivenCondition(
            AgentConditionStatus.Reported, severity: AgentTriggerSeverity.Medium, detectedAtUtc: NowUtc.AddDays(-9));
        var oldHigh = GivenCondition(
            AgentConditionStatus.Reported, severity: AgentTriggerSeverity.High, detectedAtUtc: NowUtc.AddDays(-3));
        GivenCondition(
            AgentConditionStatus.Reported, severity: AgentTriggerSeverity.High, detectedAtUtc: NowUtc.AddDays(-1));

        await RunAsync();

        Assert.That(
            _repository.Stored(oldHigh.Id).Status,
            Is.EqualTo(AgentConditionStatus.Executed),
            "Severity descending first, then age ascending - a very old Low must not outrank a High.");
    }

    [Test]
    public async Task ADelegation_RaisesASingleConditionAboveTheKindsGovernance()
    {
        GivenGovernance(ProactiveMaxAction.Hint);
        var plain = GivenCondition(AgentConditionStatus.Reported);
        var delegated = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(delegated.Id).DelegatedMaxAction = ProactiveMaxAction.Execute;

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(delegated.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(_repository.Stored(plain.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    [Test]
    public async Task ADelegation_NeverSurvivesTheKillSwitch()
    {
        GivenGovernance(ProactiveMaxAction.Execute, killSwitchActive: true);
        var delegated = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(delegated.Id).DelegatedMaxAction = ProactiveMaxAction.Execute;

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(
                _repository.Stored(delegated.Id).Status,
                Is.EqualTo(AgentConditionStatus.Reported),
                "The kill switch is the emergency stop. A grant a human gave before it was pulled must "
                + "not act afterwards.");
        });
    }

    [Test]
    public async Task ADelegation_NeverSurvivesADisabledKind()
    {
        GivenGovernance(ProactiveMaxAction.Execute, enabled: false);
        var delegated = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(delegated.Id).DelegatedMaxAction = ProactiveMaxAction.Execute;

        var result = await RunAsync();

        Assert.That(result.Executed, Is.Zero);
    }

    [Test]
    public async Task ADelegation_IsCappedByTheGlobalAutonomyLevel()
    {
        // The global level (Owner decision 2026-08-28) caps a delegation exactly like the kill switch
        // and a disabled kind do: a human's earlier "you handle this one" grant does not survive an
        // admin holding the installation at Prepare.
        GivenGovernance(ProactiveMaxAction.Hint, globalAutonomyCap: ProactiveMaxAction.Prepare);
        var delegated = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(delegated.Id).DelegatedMaxAction = ProactiveMaxAction.Execute;

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero, "Execute is above the level cap; the delegation may raise only up to Prepare.");
            Assert.That(
                _repository.Stored(delegated.Id).Status,
                Is.Not.EqualTo(AgentConditionStatus.Executed));
        });
    }

    [Test]
    public async Task ADelegation_UpToTheGlobalAutonomyLevelStillExecutes()
    {
        GivenGovernance(ProactiveMaxAction.Hint, globalAutonomyCap: ProactiveMaxAction.Execute);
        var delegated = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(delegated.Id).DelegatedMaxAction = ProactiveMaxAction.Execute;

        var result = await RunAsync();

        Assert.That(result.Executed, Is.EqualTo(1));
    }

    [Test]
    public async Task ANonRegisteredKind_IsNeverEvenQueried()
    {
        // §3.3 "Nur registrierte Kinds" - RunAsync loops _registry.RegisteredKinds only, so a condition
        // of a kind absent from the registry is never fetched from the repository at all, regardless of
        // governance or status. TestRemediationRegistry.RegisteredKinds only ever names Kind (see the
        // class below), matching production's single-source-of-truth (ConditionRemediationRegistry).
        const string unregisteredKind = "some_kind_with_no_remediation";
        var stray = _repository.Seed(
            unregisteredKind, Guid.NewGuid().ToString(), AgentConditionStatus.Reported, NowUtc.AddHours(-1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Considered, Is.Zero, "An unregistered kind's rows must never reach the tally.");
            Assert.That(_repository.Stored(stray.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    [Test]
    public async Task PrepareOnAnExecuteOnlyRemediation_SkipsRatherThanStagingAScenario()
    {
        // §3.3 "Effektiv < Execute" combined with the spec's own most-likely-to-be-wrong cell (§9):
        // TestRemediationRegistry's entry is IsScenarioCapable=false by construction (the record's
        // default, see ConditionRemediationEntry), exactly like the real empty_container entry. Prepare
        // must therefore behave like Hint here - report and wait - not stage a scenario nobody can ever
        // accept back out.
        GivenGovernance(ProactiveMaxAction.Prepare);
        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnIdentityRefusal_NeverExecutesAndCountsAsAFailedAttempt()
    {
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Refused(
                ProactiveActionIdentityRefusal.PolicyRefused,
                "Skill is classified as irreversible and never runs unattended on the proactive path."));

        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Prepared));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
        });

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A false from the claim is "somebody else has this row", never "nothing happened": the retrying
    /// execution strategy can report false after a transaction that committed. The row must therefore be
    /// skipped and NEVER executed - executing on an unclaimed row is exactly the double execution the
    /// compare-and-swap exists to prevent.
    /// </summary>
    [Test]
    public async Task ALostClaim_SkipsTheRowWithoutEverRunningTheRemediation()
    {
        GivenGovernance(ProactiveMaxAction.Execute, dailyActionBudget: 2);
        var contested = GivenCondition(AgentConditionStatus.Reported, severity: AgentTriggerSeverity.High);
        var second = GivenCondition(AgentConditionStatus.Reported, severity: AgentTriggerSeverity.Medium);
        _repository.LoseNextTransitionFor(contested.Id);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedClaimLost, Is.EqualTo(1));
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(contested.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.Stored(second.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
        });

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAbandonedClaim_IsResumedOnceItHasGoneStale()
    {
        var condition = GivenCondition(AgentConditionStatus.Prepared);
        var stored = _repository.Stored(condition.Id);
        stored.AttemptCount = 1;
        stored.LastAttemptAtUtc = NowUtc.AddMinutes(-AgentConditionActionDefaults.StaleClaimMinutes - 1);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(
                _repository.Stored(condition.Id).AttemptCount,
                Is.EqualTo(2),
                "Resuming is an attempt too - a crash loop that only counted outcomes would never escalate.");
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
        });
    }

    [Test]
    public async Task AClaimThatIsStillFresh_IsLeftToWhoeverHoldsIt()
    {
        var condition = GivenCondition(AgentConditionStatus.Prepared);
        var stored = _repository.Stored(condition.Id);
        stored.AttemptCount = 1;
        stored.LastAttemptAtUtc = NowUtc.AddMinutes(-1);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.SkippedClaimLost, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).AttemptCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ARemediationThatThrows_StillLeavesTheAttemptCounted()
    {
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the API was unreachable"));

        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(
                stored.AttemptCount,
                Is.EqualTo(1),
                "AttemptCount is raised by the CLAIM. Raising it after the outcome would let a run that "
                + "dies mid-remediation retry forever.");
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Prepared));
        });

        Assert.That(
            _repository.EventsFor(condition.Id).Any(e => e.EventType == AgentConditionEventTypes.AttemptFailed),
            Is.True);
    }

    [Test]
    public async Task AnUnbindablePayload_CostsNeitherAnAttemptNorBudget()
    {
        _registry.BindsNothing = true;
        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedUnbindable, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).AttemptCount, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.EventsFor(condition.Id), Is.Empty);
        });
    }

    [Test]
    public async Task AKindWithoutAResponsibleOwner_NeverActs()
    {
        GivenGovernance(ProactiveMaxAction.Execute, withResponsibleOwner: false);
        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedNoOwner, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).AttemptCount, Is.Zero);
        });
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

    private void GivenGovernance(
        ProactiveMaxAction maxAction,
        bool enabled = true,
        bool killSwitchActive = false,
        bool withResponsibleOwner = true,
        int dailyActionBudget = 50,
        int windowActionLimit = 50,
        int windowMinutes = 60,
        ProactiveMaxAction globalAutonomyCap = ProactiveMaxAction.Execute)
    {
        var cappedMaxAction = maxAction < globalAutonomyCap ? maxAction : globalAutonomyCap;
        var decision = new ProactiveGovernanceDecision(
            TriggerKind: Kind,
            GroupId: null,
            EffectiveMaxAction: killSwitchActive || !enabled ? ProactiveMaxAction.Hint : cappedMaxAction,
            ConfiguredMaxAction: maxAction,
            Enabled: enabled,
            KillSwitchActive: killSwitchActive,
            ResponsibleOwnerUserId: withResponsibleOwner ? OwnerUserId : null,
            DailyActionBudget: dailyActionBudget,
            WindowActionLimit: windowActionLimit,
            WindowMinutes: windowMinutes,
            IsStored: true,
            GlobalAutonomyCap: globalAutonomyCap);

        _governance
            .ResolveAsync(Kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(decision);
    }

    private void GivenIdentityResolves()
    {
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = OwnerUserId,
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = ["some.permission"],
                    BypassAutonomyGate = true
                },
                ["some.permission"]));
    }

    private void GivenSkillSucceeds()
    {
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Template created."));
    }

    /// <summary>
    /// A budget-consuming claim event from an earlier tick of the same day, so the next run starts with
    /// the daily budget already spent.
    /// </summary>
    private void GivenClaimEventToday(Guid conditionId) =>
        _repository.InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = conditionId,
            EventType = AgentConditionStatus.Prepared.ToString(),
            AtUtc = NowUtc.AddHours(-2),
            Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
        }).GetAwaiter().GetResult();

    private AgentCondition GivenCondition(
        AgentConditionStatus status,
        string severity = AgentTriggerSeverity.Medium,
        Guid? entityId = null,
        DateTime? detectedAtUtc = null)
    {
        var condition = _repository.Seed(
            Kind, Guid.NewGuid().ToString(), status, detectedAtUtc ?? NowUtc.AddHours(-1));
        condition.Severity = severity;
        condition.EntityId = entityId ?? Guid.NewGuid();
        condition.PayloadJson = "{}";

        return condition;
    }

    /// <summary>
    /// A registry with one entry under this fixture's control, so the tests never depend on which real
    /// kind happens to have a remediation today. BindsNothing reproduces the payload the binder cannot
    /// turn into arguments.
    /// </summary>
    private sealed class TestRemediationRegistry : IConditionRemediationRegistry
    {
        private const string RequiredArgument = "containerId";

        public bool BindsNothing { get; set; }

        public IReadOnlyCollection<string> RegisteredKinds => [Kind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == Kind
                ? new ConditionRemediationEntry(
                    SkillName, new TestBinder(BindsNothing), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == Kind ? configuredMaxAction : ProactiveMaxAction.Hint;

        private sealed class TestBinder : IConditionRemediationParameterBinder
        {
            private readonly bool _bindsNothing;

            public TestBinder(bool bindsNothing)
            {
                _bindsNothing = bindsNothing;
            }

            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                _bindsNothing
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [RequiredArgument] = Guid.NewGuid().ToString()
                    };
        }
    }
}
