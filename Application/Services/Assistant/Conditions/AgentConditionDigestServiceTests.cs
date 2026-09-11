// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionDigestService: due-time gating via the persisted watermark (restart-safe -
/// re-checked against a stored value, never in-memory state), the fresh-installation seed path, lost-CAS
/// handling, no-message-when-nothing-open, and that two planners with different GroupVisibility scopes
/// each get their own scope-correct severity/new-count summary built from
/// AgentConditionRepository.GetOpenForScopeAsync per planner.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionDigestServiceTests
{
    private const string WatermarkKey = Klacks.Api.Application.Constants.Settings.AGENT_CONDITION_DIGEST_LAST_RUN_DATE;

    private static readonly DateTime PastTargetUtc = new(2026, 8, 24, 6, 31, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeTargetUtc = new(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc);

    private IAgentConditionRepository _conditionRepository = null!;
    private IAgentConditionScopeResolver _scopeResolver = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private IAgentTriggerService _triggerService = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private FixedCompanyClock _companyClock = null!;
    private AgentConditionDigestService _service = null!;
    private List<IAgentTriggerEvent> _dispatched = null!;

    [SetUp]
    public void SetUp()
    {
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _scopeResolver = Substitute.For<IAgentConditionScopeResolver>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        // No installation timezone/country configured -> company zone resolves to UTC, so "local" time
        // equals UTC here.
        _companyClock = new FixedCompanyClock(PastTargetUtc, TimeZoneInfo.Utc);
        _dispatched = new List<IAgentTriggerEvent>();

        _triggerService
            .When(x => x.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => _dispatched.Add(callInfo.Arg<IAgentTriggerEvent>()));

        _service = new AgentConditionDigestService(
            _conditionRepository,
            _scopeResolver,
            _planningAudienceResolver,
            _triggerService,
            _settingsRepository,
            _unitOfWork,
            Options.Create(new BackgroundServiceOptions()),
            _companyClock,
            Substitute.For<ILogger<AgentConditionDigestService>>());
    }

    private void GivenNoMarkerRowYet()
    {
        _settingsRepository.GetSettingNoTracking(WatermarkKey).Returns((SettingsEntity?)null);
    }

    private void GivenMarkerRow(string value)
    {
        _settingsRepository.GetSettingNoTracking(WatermarkKey)
            .Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = WatermarkKey, Value = value });
    }

    private static AgentCondition Seed(string triggerKind, string severity, DateTime detectedAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = triggerKind,
        Fingerprint = triggerKind + ":" + Guid.NewGuid(),
        Severity = severity,
        Status = AgentConditionStatus.Detected,
        DetectedAtUtc = detectedAtUtc,
        LastSeenAtUtc = detectedAtUtc,
        PayloadJson = "{}"
    };

    [Test]
    public async Task RunIfDue_BeforeConfiguredLocalTime_ReturnsNotDueYet_AndNeverClaims()
    {
        _companyClock.Now = BeforeTargetUtc;
        GivenNoMarkerRowYet();

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.NotDueYet);
        result.RecipientsNotified.ShouldBe(0);
        await _settingsRepository.DidNotReceive().TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task RunIfDue_MarkerAlreadyMatchesToday_ReturnsAlreadyRanToday_AndNeverTouchesPlanners()
    {
        GivenMarkerRow("2026-08-24");

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.AlreadyRanToday);
        await _planningAudienceResolver.DidNotReceive().GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIfDue_ThisIsTheSecondServiceInstanceAfterARestart_StillOnlyRunsOncePerDay()
    {
        // Simulates: instance A already ran today and persisted the watermark; this is a freshly
        // constructed service instance (as a restart would produce) reading that same persisted value -
        // proving the gate is restart-safe rather than relying on in-memory state.
        GivenMarkerRow("2026-08-24");

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.AlreadyRanToday);
        _dispatched.ShouldBeEmpty();
    }

    [Test]
    public async Task RunIfDue_FreshInstallationWithNoMarkerRow_SeedsItThenClaimsTodayAndRuns()
    {
        GivenNoMarkerRowYet();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "", "2026-08-24").Returns(true);

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.Ran);
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsEntity>(s => s.Type == WatermarkKey && s.Value == string.Empty));
        await _unitOfWork.Received(1).CompleteAsync();
        await _settingsRepository.Received(1).TryAdvanceSettingAsync(WatermarkKey, "", "2026-08-24");
    }

    [Test]
    public async Task RunIfDue_LostTheCasClaim_ReturnsLostRace_AndNeverDispatches()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(false);

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.LostRace);
        await _planningAudienceResolver.DidNotReceive().GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
        _dispatched.ShouldBeEmpty();
    }

    [Test]
    public async Task RunIfDue_NoPlannerHasAnyOpenCondition_ClaimsTheDayButSendsNoMessage()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(true);

        var plannerId = Guid.NewGuid();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { plannerId.ToString() });
        _scopeResolver.ResolveAsync(plannerId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.Ran);
        result.RecipientsNotified.ShouldBe(0);
        _dispatched.ShouldBeEmpty();
    }

    [Test]
    public async Task RunIfDue_TwoPlannersWithDifferentGroupScopes_EachReceivesTheirOwnScopeCorrectDigest()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(true);

        var admin = Guid.NewGuid();
        var restrictedPlanner = Guid.NewGuid();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { admin.ToString(), restrictedPlanner.ToString() });

        _scopeResolver.ResolveAsync(admin.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        var visibleRoot = Guid.NewGuid();
        _scopeResolver.ResolveAsync(restrictedPlanner.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Restricted(new HashSet<Guid> { visibleRoot }));

        // newCutoff = PastTargetUtc - 24h = 2026-08-23 06:31 UTC.
        var adminConditions = new List<AgentCondition>
        {
            Seed(AgentTriggerKinds.OpenOrder, AgentTriggerSeverity.High, PastTargetUtc.AddHours(-1)), // new
            Seed(AgentTriggerKinds.EmptyContainer, AgentTriggerSeverity.Medium, PastTargetUtc.AddHours(-30)) // not new
        };
        var restrictedConditions = new List<AgentCondition>
        {
            Seed(AgentTriggerKinds.UncutFulldayShift, AgentTriggerSeverity.Low, PastTargetUtc.AddHours(-2)) // new
        };

        _conditionRepository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(adminConditions);
        _conditionRepository.GetOpenForScopeAsync(false, Arg.Is<IReadOnlySet<Guid>>(s => s.Contains(visibleRoot)), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(restrictedConditions);

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.Ran);
        result.RecipientsNotified.ShouldBe(2);
        _dispatched.Count.ShouldBe(2);

        var digests = _dispatched.OfType<AgentConditionDigestTriggerEvent>().ToList();
        var adminDigest = digests.Single(e => e.PlannerUserId == admin);
        adminDigest.TotalCount.ShouldBe(2);
        adminDigest.HighCount.ShouldBe(1);
        adminDigest.MediumCount.ShouldBe(1);
        adminDigest.LowCount.ShouldBe(0);
        adminDigest.NewCount.ShouldBe(1);
        adminDigest.Severity.ShouldBe(AgentTriggerSeverity.High);

        var restrictedDigest = digests.Single(e => e.PlannerUserId == restrictedPlanner);
        restrictedDigest.TotalCount.ShouldBe(1);
        restrictedDigest.HighCount.ShouldBe(0);
        restrictedDigest.MediumCount.ShouldBe(0);
        restrictedDigest.LowCount.ShouldBe(1);
        restrictedDigest.NewCount.ShouldBe(1);
        restrictedDigest.Severity.ShouldBe(AgentTriggerSeverity.Low);

        await _conditionRepository.DidNotReceive().CountOpenForScopeAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Live-verified 2026-08-24 against the dev database: an unrestricted scope with ~2900 open
    /// High-severity rows hit AgentConditionDigestDefaults.ScopeQueryCap and the digest reported
    /// "200 open, 0 medium, 0 low" even though real Medium/Low rows existed beyond the cap. This pins
    /// the fix: whenever GetOpenForScopeAsync's result size reaches the cap, TotalCount is re-read from
    /// the uncapped CountOpenForScopeAsync instead of the capped bucket sum.
    /// </summary>
    [Test]
    public async Task RunIfDue_ScopeHitsTheQueryCap_TotalCountFallsBackToTheUncappedCount()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(true);

        var plannerId = Guid.NewGuid();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { plannerId.ToString() });
        _scopeResolver.ResolveAsync(plannerId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());

        var cappedRows = Enumerable.Range(0, AgentConditionDigestDefaults.ScopeQueryCap)
            .Select(_ => Seed(AgentTriggerKinds.OpenOrder, AgentTriggerSeverity.High, PastTargetUtc.AddHours(-1)))
            .ToList();
        _conditionRepository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(cappedRows);
        _conditionRepository.CountOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2913);

        await _service.RunIfDueAsync();

        var digest = _dispatched.OfType<AgentConditionDigestTriggerEvent>().Single();
        digest.TotalCount.ShouldBe(2913);
        digest.HighCount.ShouldBe(AgentConditionDigestDefaults.ScopeQueryCap);
    }

    [Test]
    public async Task RunIfDue_TimeOfDayConfiguredWithoutAColon_DoesNotParseAsDaysAndFallsBackToDefault()
    {
        // "6" parses successfully as TimeSpan.TryParse("6") = 6 days, not 6 o'clock - without a range
        // check that would make nowLocalTimeOfDay (always < 24h) permanently "before" the target, silently
        // disabling the digest forever with no warning. The fallback path must still fire it.
        var service = new AgentConditionDigestService(
            _conditionRepository,
            _scopeResolver,
            _planningAudienceResolver,
            _triggerService,
            _settingsRepository,
            _unitOfWork,
            Options.Create(new BackgroundServiceOptions { AgentConditionDigestTimeOfDayLocal = "6" }),
            _companyClock,
            Substitute.For<ILogger<AgentConditionDigestService>>());

        GivenNoMarkerRowYet();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "", "2026-08-24").Returns(true);

        var result = await service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.Ran);
    }

    [Test]
    public async Task RunIfDue_PlannerNotAPlannerAnymore_IsSkippedWithoutQueryingConditions()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(true);

        var formerPlanner = Guid.NewGuid();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { formerPlanner.ToString() });
        _scopeResolver.ResolveAsync(formerPlanner.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _service.RunIfDueAsync();

        result.RecipientsNotified.ShouldBe(0);
        await _conditionRepository.DidNotReceive().GetOpenForScopeAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIfDue_OnePlannerScopeThrows_OtherPlannersStillGetTheirDigest()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(true);

        var brokenPlanner = Guid.NewGuid();
        var healthyPlanner = Guid.NewGuid();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { brokenPlanner.ToString(), healthyPlanner.ToString() });

        _scopeResolver.ResolveAsync(brokenPlanner.ToString(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated transient DB error"));
        _scopeResolver.ResolveAsync(healthyPlanner.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { Seed(AgentTriggerKinds.OpenOrder, AgentTriggerSeverity.High, PastTargetUtc) });

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.Ran);
        result.RecipientsNotified.ShouldBe(1);
        var digest = _dispatched.OfType<AgentConditionDigestTriggerEvent>().Single();
        digest.PlannerUserId.ShouldBe(healthyPlanner);
    }

    [Test]
    public async Task RunIfDue_DedupKeyCarriesTheLocalCalendarDay_SoTomorrowsDigestIsNotSuppressedByTodays()
    {
        GivenMarkerRow("2026-08-23");
        _settingsRepository.TryAdvanceSettingAsync(WatermarkKey, "2026-08-23", "2026-08-24").Returns(true);

        var plannerId = Guid.NewGuid();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { plannerId.ToString() });
        _scopeResolver.ResolveAsync(plannerId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository.GetOpenForScopeAsync(true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition> { Seed(AgentTriggerKinds.OpenOrder, AgentTriggerSeverity.High, PastTargetUtc) });

        await _service.RunIfDueAsync();

        _dispatched.Single().DedupKey.ShouldBe("2026-08-24");
        _dispatched.Single().Kind.ShouldBe(AgentTriggerKinds.DailyDigest);
        _dispatched.Single().PlannersOnly.ShouldBeTrue();
    }
}
