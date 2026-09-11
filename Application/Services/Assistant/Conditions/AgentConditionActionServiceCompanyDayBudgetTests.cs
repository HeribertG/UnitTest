// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins that the daily action budget resets at the COMPANY's own midnight, not the UTC calendar day's -
/// the bug found in the 2026-09-11 date/time-zone review: ActionBudget used to bucket a claim by
/// <c>nowUtc.Date</c> (a UTC-calendar-day boundary stamped with Kind=Utc), while
/// IAgentConditionRepository.CountActionClaimsAsync compares that boundary against the audit events' real
/// UTC instants. For a positive-offset zone like Pacific/Auckland (UTC+12/+13) the UTC day starts many
/// hours before the company's day does, so a claim made late in the company's PREVIOUS day still fell
/// after the UTC-day boundary and was wrongly counted as spent "today" - exhausting a budget that should
/// still have been full.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceCompanyDayBudgetTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string SkillName = "test_remediation_skill";
    private const string RequiredArgument = "containerId";

    // 2026-01-14T11:30:00Z is 2026-01-15T00:30 in Pacific/Auckland (NZDT, UTC+13) - thirty minutes into
    // the company's new day, but still firmly inside 2026-01-14 by the UTC calendar.
    private static readonly DateTime NowUtc = new(2026, 1, 14, 11, 30, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private FakeAgentConditionRepository _repository = null!;
    private IProactiveGovernanceResolver _governance = null!;
    private IQuietWindowService _quietWindow = null!;
    private IProactiveActionIdentityProvider _identityProvider = null!;
    private ISkillExecutor _skillExecutor = null!;
    private IProactiveActionReporter _reporter = null!;
    private SingleKindRemediationRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeAgentConditionRepository();

        _governance = Substitute.For<IProactiveGovernanceResolver>();
        _governance
            .ResolveAsync(Kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: Kind,
                GroupId: null,
                EffectiveMaxAction: ProactiveMaxAction.Execute,
                ConfiguredMaxAction: ProactiveMaxAction.Execute,
                Enabled: true,
                KillSwitchActive: false,
                ResponsibleOwnerUserId: OwnerUserId,
                DailyActionBudget: 1,
                WindowActionLimit: 50,
                WindowMinutes: 60,
                IsStored: true));

        _quietWindow = Substitute.For<IQuietWindowService>();
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
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

        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Template created."));

        _reporter = Substitute.For<IProactiveActionReporter>();
        _reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _registry = new SingleKindRemediationRegistry();
    }

    [Test]
    public async Task ClaimFromLateOnTheCompanysPreviousDay_DoesNotCountAgainstTodaysBudget()
    {
        var condition = _repository.Seed(Kind, Guid.NewGuid().ToString(), AgentConditionStatus.Reported, NowUtc.AddHours(-6));
        condition.EntityId = Guid.NewGuid();
        condition.PayloadJson = "{}";

        // 2026-01-14T10:00Z is 2026-01-14T23:00 in Auckland - the company's PREVIOUS day, even though it
        // shares the UTC calendar day with NowUtc.
        await _repository.InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = AgentConditionStatus.Prepared.ToString(),
            AtUtc = new DateTime(2026, 1, 14, 10, 0, 0, DateTimeKind.Utc),
            Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
        });

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Executed,
                Is.EqualTo(1),
                "The spent claim belongs to the company's previous day and must not count against " +
                "today's budget of one.");
            Assert.That(result.LeftForBudget, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
        });
    }

    /// <summary>
    /// Mirror of the Auckland (positive-offset) test above, for a negative-offset zone: proves the OTHER
    /// direction of the same bug. With the old <c>nowUtc.Date</c> logic, a negative offset means the
    /// company's local midnight falls LATER in the UTC clock than the UTC calendar day's own midnight, so
    /// a claim made early in that UTC day - before the company's midnight has actually occurred - would be
    /// wrongly counted as spent "today", even though the company's day has not started yet.
    /// </summary>
    [Test]
    public async Task ClaimOnCompanysPreviousDay_SameUtcCalendarDayAsNow_DoesNotCountAgainstTodaysBudget()
    {
        // 2026-06-15T10:00Z is 2026-06-15T06:00 in America/New_York (EDT, UTC-4) - the company's day has
        // already rolled over to 06-15.
        var nowUtc = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAgentConditionRepository();
        var condition = repository.Seed(Kind, Guid.NewGuid().ToString(), AgentConditionStatus.Reported, nowUtc.AddHours(-6));
        condition.EntityId = Guid.NewGuid();
        condition.PayloadJson = "{}";

        // 2026-06-15T02:00Z is 2026-06-14T22:00 in America/New_York - the company's PREVIOUS day, even
        // though it shares the UTC calendar day (06-15) with nowUtc.
        await repository.InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = AgentConditionStatus.Prepared.ToString(),
            AtUtc = new DateTime(2026, 6, 15, 2, 0, 0, DateTimeKind.Utc),
            Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
        });

        var result = await RunAsync(nowUtc, NewYork, repository);

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Executed,
                Is.EqualTo(1),
                "The spent claim belongs to the company's previous day and must not count against " +
                "today's budget of one.");
            Assert.That(result.LeftForBudget, Is.Zero);
            Assert.That(repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
        });
    }

    [Test]
    public async Task ClaimOnCompanysToday_PriorUtcCalendarDay_CountsAgainstTodaysBudget()
    {
        // 2026-06-15T02:00Z is 2026-06-14T22:00 in America/New_York (EDT, UTC-4) - the company's day is
        // still 06-14, even though nowUtc's own UTC calendar day is already 06-15.
        var nowUtc = new DateTime(2026, 6, 15, 2, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAgentConditionRepository();
        var condition = repository.Seed(Kind, Guid.NewGuid().ToString(), AgentConditionStatus.Reported, nowUtc.AddHours(-6));
        condition.EntityId = Guid.NewGuid();
        condition.PayloadJson = "{}";

        // 2026-06-14T20:00Z is 2026-06-14T16:00 in America/New_York - still the company's CURRENT day,
        // even though it is the UTC calendar day before nowUtc's own (06-15).
        await repository.InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = AgentConditionStatus.Prepared.ToString(),
            AtUtc = new DateTime(2026, 6, 14, 20, 0, 0, DateTimeKind.Utc),
            Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
        });

        var result = await RunAsync(nowUtc, NewYork, repository);

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Executed,
                Is.Zero,
                "The claim belongs to the company's current day and must count against today's budget of one.");
            Assert.That(result.LeftForBudget, Is.EqualTo(1));
            Assert.That(repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    private Task<AgentConditionActionTickResult> RunAsync() => RunAsync(NowUtc, Auckland, _repository);

    private Task<AgentConditionActionTickResult> RunAsync(
        DateTime nowUtc, TimeZoneInfo zone, FakeAgentConditionRepository repository)
    {
        var timeProvider = new SettableTimeProvider(nowUtc);
        var companyClock = new FixedCompanyClock(nowUtc, zone);
        var ledger = new AgentConditionLedgerService(repository, timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

        return new AgentConditionActionService(
            repository,
            ledger,
            _governance,
            _registry,
            _quietWindow,
            _identityProvider,
            _skillExecutor,
            _reporter,
            timeProvider,
            companyClock,
            NullLogger<AgentConditionActionService>.Instance)
            .RunAsync(CancellationToken.None);
    }

    private sealed class SingleKindRemediationRegistry : IConditionRemediationRegistry
    {
        public IReadOnlyCollection<string> RegisteredKinds => [Kind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == Kind
                ? new ConditionRemediationEntry(SkillName, new FixedArgumentBinder(), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == Kind ? configuredMaxAction : ProactiveMaxAction.Hint;

        private sealed class FixedArgumentBinder : IConditionRemediationParameterBinder
        {
            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [RequiredArgument] = Guid.NewGuid().ToString()
                };
        }
    }
}
