// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class WelcomeFocusResolverTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Older = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);

    private IAgentConditionScopeResolver _scopeResolver = null!;
    private IScheduleActivityProbe _setupProbe = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentTriggerPreferenceService _preferenceService = null!;
    private ILogger<WelcomeFocusResolver> _logger = null!;
    private WelcomeFocusResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _scopeResolver = Substitute.For<IAgentConditionScopeResolver>();
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());

        _setupProbe = Substitute.For<IScheduleActivityProbe>();
        _setupProbe.GetSetupStateAsync(Arg.Any<CancellationToken>()).Returns(FullySetUp());

        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _conditionRepository
            .GetOpenForScopeAsync(Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _dispatchRepository
            .GetAcknowledgedConditionIdsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());

        _preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        _logger = Substitute.For<ILogger<WelcomeFocusResolver>>();

        _resolver = new WelcomeFocusResolver(
            _scopeResolver,
            _setupProbe,
            _conditionRepository,
            _dispatchRepository,
            _preferenceService,
            _logger);
    }

    [Test]
    public async Task ResolveAsync_UserIsNotAPlanner_ReturnsNull()
    {
        _scopeResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldBeNull();
        await _conditionRepository.DidNotReceive().GetOpenForScopeAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_EmptyInstallationWithEmptyJournal_ReturnsNoOrdersFocus()
    {
        _setupProbe.GetSetupStateAsync(Arg.Any<CancellationToken>()).Returns(NothingYet());

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.NoScheduleYet);
        result.PromptKey.ShouldBe(WelcomeFocusI18nKeys.NoOrdersPrompt);
        result.ActionKind.ShouldBe(WelcomeFocusActionKinds.Consultation);
        result.ActionLabelKey.ShouldBe(WelcomeFocusI18nKeys.SetupConsultationAction);
        result.ActionRoute.ShouldBeNull();
        result.ConditionId.ShouldBeNull();
        result.PromptParams.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveAsync_OrdersButNoShifts_ReturnsNoShiftsPromptAsConsultation()
    {
        _setupProbe.GetSetupStateAsync(Arg.Any<CancellationToken>()).Returns(OrdersButNoShifts());

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.NoShiftsPrompt);
        result.ActionKind.ShouldBe(WelcomeFocusActionKinds.Consultation);
        result.ActionRoute.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_ShiftsButNoWork_ReturnsNoWorkPromptAsConsultation()
    {
        _setupProbe.GetSetupStateAsync(Arg.Any<CancellationToken>()).Returns(ShiftsButNoWork());

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.NoWorkPrompt);
        result.ActionKind.ShouldBe(WelcomeFocusActionKinds.Consultation);
        result.ActionRoute.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_FreshSetupCandidateReplacesTheJournalRow()
    {
        _setupProbe.GetSetupStateAsync(Arg.Any<CancellationToken>()).Returns(ShiftsButNoWork());
        GivenJournal(Condition(AgentTriggerKinds.NoScheduleYet, AgentTriggerSeverity.Medium, Older));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.NoWorkPrompt);
        result.ConditionId.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_JournalNoScheduleYetRow_IsDiscardedWhenWorkExists()
    {
        var overdue = Condition(
            AgentTriggerKinds.PeriodOverdue,
            AgentTriggerSeverity.Medium,
            Newer,
            PeriodOverduePayload("Nord", "2026-08-31", 7));
        GivenJournal(Condition(AgentTriggerKinds.NoScheduleYet, AgentTriggerSeverity.Medium, Older), overdue);

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.PeriodOverdue);
        result.ConditionId.ShouldBe(overdue.Id);
    }

    [Test]
    public async Task ResolveAsync_PeriodOverdueOutranksPeriodCloseDueAtEqualSeverity()
    {
        GivenJournal(
            Condition(AgentTriggerKinds.PeriodCloseDue, AgentTriggerSeverity.Medium, Older, PeriodCloseDuePayload("Sued", "2026-09-30", 3)),
            Condition(AgentTriggerKinds.PeriodOverdue, AgentTriggerSeverity.Medium, Newer, PeriodOverduePayload("Nord", "2026-08-31", 7)));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.PeriodOverdue);
    }

    [Test]
    public async Task ResolveAsync_SameRank_HighSeverityBeatsMedium()
    {
        GivenJournal(
            Condition(AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium, Older),
            Condition(AgentTriggerKinds.OpenOrder, AgentTriggerSeverity.High, Newer));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.OpenOrder);
    }

    [Test]
    public async Task ResolveAsync_SnoozedKindIsDropped_NextCandidateWins()
    {
        GivenJournal(
            Condition(AgentTriggerKinds.PeriodOverdue, AgentTriggerSeverity.Medium, Older, PeriodOverduePayload("Nord", "2026-08-31", 7)),
            Condition(AgentTriggerKinds.PeriodCloseDue, AgentTriggerSeverity.Medium, Newer, PeriodCloseDuePayload("Sued", "2026-09-30", 3)));
        _preferenceService.IsAllowedAsync(UserId, AgentTriggerKinds.PeriodOverdue, Arg.Any<string>()).Returns(false);

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.PeriodCloseDue);
    }

    [Test]
    public async Task ResolveAsync_AcknowledgedConditionIsDropped_NextCandidateWins()
    {
        var overdue = Condition(AgentTriggerKinds.PeriodOverdue, AgentTriggerSeverity.Medium, Older, PeriodOverduePayload("Nord", "2026-08-31", 7));
        var closeDue = Condition(AgentTriggerKinds.PeriodCloseDue, AgentTriggerSeverity.Medium, Newer, PeriodCloseDuePayload("Sued", "2026-09-30", 3));
        GivenJournal(overdue, closeDue);
        _dispatchRepository
            .GetAcknowledgedConditionIdsAsync(UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid> { overdue.Id });

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.PeriodCloseDue);
        result.ConditionId.ShouldBe(closeDue.Id);
    }

    [Test]
    public async Task ResolveAsync_UnrankedKinds_ProduceGenericFocusWithSurvivingCount()
    {
        GivenJournal(
            Condition(AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High, Older),
            Condition(AgentTriggerKinds.OpenOrder, AgentTriggerSeverity.Medium, Newer),
            Condition(AgentTriggerKinds.ContractExpiringSoon, AgentTriggerSeverity.Low, Newer));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.GenericPrompt);
        result.ActionLabelKey.ShouldBe(WelcomeFocusI18nKeys.GenericAction);
        result.ActionKind.ShouldBe(WelcomeFocusActionKinds.Navigate);
        result.ActionRoute.ShouldBe(ProactiveActionRoutes.Schedule);
        result.PromptParams[WelcomeFocusParamKeys.Count].ShouldBe("3");
    }

    [Test]
    public async Task ResolveAsync_UnrankedKindWithoutOwnRoute_FallsBackToTheScheduleRoute()
    {
        GivenJournal(Condition(AgentTriggerKinds.CuriosityQuestion, AgentTriggerSeverity.Medium, Older));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.ActionRoute.ShouldBe(ProactiveActionRoutes.Schedule);
    }

    [Test]
    public async Task ResolveAsync_PeriodOverdue_FillsPromptParamsFromPayload()
    {
        GivenJournal(Condition(AgentTriggerKinds.PeriodOverdue, AgentTriggerSeverity.Medium, Older, PeriodOverduePayload("Nord", "2026-08-31", 7)));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.PeriodOverduePrompt);
        result.ActionLabelKey.ShouldBe(WelcomeFocusI18nKeys.PeriodOverdueAction);
        result.ActionRoute.ShouldBe(ProactiveActionRoutes.PeriodClosing);
        result.PromptParams[WelcomeFocusParamKeys.Group].ShouldBe("Nord");
        result.PromptParams[WelcomeFocusParamKeys.PeriodEnd].ShouldBe("2026-08-31");
        result.PromptParams[WelcomeFocusParamKeys.Days].ShouldBe("7");
    }

    [Test]
    public async Task ResolveAsync_PeriodCloseDue_FillsPromptParamsFromPayload()
    {
        GivenJournal(Condition(AgentTriggerKinds.PeriodCloseDue, AgentTriggerSeverity.Medium, Older, PeriodCloseDuePayload("Sued", "2026-09-30", 3)));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.PeriodCloseDuePrompt);
        result.ActionLabelKey.ShouldBe(WelcomeFocusI18nKeys.PeriodCloseDueAction);
        result.ActionRoute.ShouldBe(ProactiveActionRoutes.PeriodClosing);
        result.PromptParams[WelcomeFocusParamKeys.Group].ShouldBe("Sued");
        result.PromptParams[WelcomeFocusParamKeys.PeriodEnd].ShouldBe("2026-09-30");
        result.PromptParams[WelcomeFocusParamKeys.Days].ShouldBe("3");
    }

    [Test]
    public async Task ResolveAsync_NextPeriodSchedulingDue_FillsPromptParamsFromPayload()
    {
        GivenJournal(Condition(AgentTriggerKinds.NextPeriodSchedulingDue, AgentTriggerSeverity.Medium, Older, NextPeriodPayload("Ost", "2026-10-01", 5)));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PromptKey.ShouldBe(WelcomeFocusI18nKeys.NextPeriodPrompt);
        result.ActionLabelKey.ShouldBe(WelcomeFocusI18nKeys.NextPeriodAction);
        result.ActionRoute.ShouldBe(ProactiveActionRoutes.Schedule);
        result.PromptParams[WelcomeFocusParamKeys.Group].ShouldBe("Ost");
        result.PromptParams[WelcomeFocusParamKeys.PeriodStart].ShouldBe("2026-10-01");
        result.PromptParams[WelcomeFocusParamKeys.Days].ShouldBe("5");
    }

    [Test]
    public async Task ResolveAsync_PeriodCandidateWithBrokenPayload_IsSkippedAndNextCandidateWins()
    {
        GivenJournal(
            Condition(AgentTriggerKinds.PeriodOverdue, AgentTriggerSeverity.Medium, Older, "not json at all"),
            Condition(AgentTriggerKinds.PeriodCloseDue, AgentTriggerSeverity.Medium, Newer, PeriodCloseDuePayload("Sued", "2026-09-30", 3)));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(AgentTriggerKinds.PeriodCloseDue);
    }

    [Test]
    public async Task ResolveAsync_PeriodCandidateMissingAPayloadField_IsSkipped()
    {
        GivenJournal(Condition(AgentTriggerKinds.PeriodOverdue, AgentTriggerSeverity.Medium, Older, "{\"groupName\":\"Nord\"}"));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_NothingSurvivesTheFilters_ReturnsNull()
    {
        GivenJournal(Condition(AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium, Older));
        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_RepositoryThrows_ReturnsNull()
    {
        _conditionRepository
            .GetOpenForScopeAsync(Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<List<AgentCondition>>(_ => throw new InvalidOperationException("Ledger read failed."));

        var result = await _resolver.ResolveAsync(UserId, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_PreferenceIsAskedOncePerKindAndSeverity()
    {
        GivenJournal(
            Condition(AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium, Older),
            Condition(AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium, Newer));

        await _resolver.ResolveAsync(UserId, CancellationToken.None);

        await _preferenceService.Received(1)
            .IsAllowedAsync(UserId, AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium);
    }

    private void GivenJournal(params AgentCondition[] conditions)
    {
        _conditionRepository
            .GetOpenForScopeAsync(Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(conditions.ToList());
    }

    private static AgentCondition Condition(string kind, string severity, DateTime detectedAtUtc, string payloadJson = "{}") => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = kind,
        Severity = severity,
        DetectedAtUtc = detectedAtUtc,
        LastSeenAtUtc = detectedAtUtc,
        PayloadJson = payloadJson
    };

    private static string PeriodOverduePayload(string groupName, string periodEndDate, int daysOverdue)
        => "{\"groupId\":\"00000000-0000-0000-0000-000000000000\",\"groupName\":\"" + groupName
           + "\",\"periodEndDate\":\"" + periodEndDate + "\",\"daysOverdue\":" + daysOverdue + "}";

    private static string PeriodCloseDuePayload(string groupName, string periodEndDate, int daysUntilDue)
        => "{\"groupId\":\"00000000-0000-0000-0000-000000000000\",\"groupName\":\"" + groupName
           + "\",\"periodEndDate\":\"" + periodEndDate + "\",\"daysUntilDue\":" + daysUntilDue + "}";

    private static string NextPeriodPayload(string groupName, string periodStartDate, int daysUntilStart)
        => "{\"groupId\":\"00000000-0000-0000-0000-000000000000\",\"groupName\":\"" + groupName
           + "\",\"periodStartDate\":\"" + periodStartDate + "\",\"periodEndDate\":\"2026-10-31\",\"daysUntilStart\":"
           + daysUntilStart + "}";

    private static ScheduleSetupState FullySetUp() => new(true, true, true, true, true);

    private static ScheduleSetupState NothingYet() => new(false, false, false, false, false);

    private static ScheduleSetupState OrdersButNoShifts() => new(true, false, false, true, true);

    private static ScheduleSetupState ShiftsButNoWork() => new(true, true, false, true, true);
}
