// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NextPeriodSchedulingDueDetector — covers the autonomy gating branch (hint below
/// Autonomous, automatic AutoWizard start from Autonomous upwards, MIN aggregation over admins),
/// the Individual-interval skip, the email-backlog gate and the scenario-already-exists skip.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging.Abstractions;
using AppSettings = Klacks.Api.Application.Constants.Settings;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodSchedulingDueDetectorTests
{
    private const string AdminId = "3f1c9a52-0000-0000-0000-000000000001";
    private const string SecondAdminId = "3f1c9a52-0000-0000-0000-000000000002";

    private IGroupRepository _groupRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private IAutoWizardJobRunner _autoWizardJobRunner = null!;
    private IClientRepository _clientRepository = null!;
    private IShiftScheduleRepository _shiftScheduleRepository = null!;
    private INextPeriodAutoCommitService _autoCommitService = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentAutonomyPreferenceRepository _autonomyPreferences = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private ISettingsReader _settingsReader = null!;
    private IReceivedEmailRepository _receivedEmailRepository = null!;
    private NextPeriodSchedulingDueDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _activityProbe.HasPlannableShiftsInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _autoWizardJobRunner = Substitute.For<IAutoWizardJobRunner>();
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftScheduleRepository = Substitute.For<IShiftScheduleRepository>();
        _autoCommitService = Substitute.For<INextPeriodAutoCommitService>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _autonomyPreferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _receivedEmailRepository = Substitute.For<IReceivedEmailRepository>();

        StubWeekStart(DayOfWeek.Monday);
        StubEmailAnalysis(enabled: false, backlogCount: 0);
        StubScenarios();
        StubAdmins((AdminId, AutonomyLevel.Propose));
        StubAgentsAndShifts();
        StubKillSwitch(active: false);

        _sut = CreateSut(new DateOnly(2026, 1, 28));
    }

    private NextPeriodSchedulingDueDetector CreateSut(DateOnly today)
    {
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new NextPeriodSchedulingDueDetector(
            _groupRepository,
            _weekConfiguration,
            _scenarioRepository,
            _activityProbe,
            _autoWizardJobRunner,
            _clientRepository,
            _shiftScheduleRepository,
            _autoCommitService,
            _audienceResolver,
            _autonomyPreferences,
            _governanceResolver,
            _settingsReader,
            _receivedEmailRepository,
            NullLogger<NextPeriodSchedulingDueDetector>.Instance,
            tp);
    }

    private void StubKillSwitch(bool active)
    {
        _governanceResolver.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(active);

        // Default: no global cap, so the per-admin aggregation alone decides; a test that cares
        // overrides this with a lower level.
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);
    }

    private void StubWeekStart(DayOfWeek weekStartDay)
    {
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var date = ci.Arg<DateOnly>();
                var offset = ((int)date.DayOfWeek - (int)weekStartDay + 7) % 7;
                return date.AddDays(-offset);
            });
    }

    private void StubGroups(params Group[] groups)
    {
        _groupRepository.List().Returns(groups.ToList());
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(groups.Select(group => group.Id).ToList());
    }

    private void StubEmailAnalysis(bool enabled, int backlogCount)
    {
        _settingsReader.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(Task.FromResult<SettingsRow?>(
                new SettingsRow { Type = AppSettings.EMAIL_ANALYSIS_ENABLED, Value = enabled.ToString() }));
        _receivedEmailRepository.GetUnprocessedAsync(Arg.Any<int>())
            .Returns(Enumerable.Range(0, backlogCount).Select(_ => new ReceivedEmail()).ToList());
    }

    private void StubScenarios(params AnalyseScenario[] scenarios)
    {
        _scenarioRepository.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(scenarios.ToList());
    }

    private void StubAdmins(params (string UserId, AutonomyLevel Level)[] admins)
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(admins.Select(admin => admin.UserId).ToHashSet());
        foreach (var (userId, level) in admins)
        {
            _autonomyPreferences.GetAsync(userId, Arg.Any<CancellationToken>())
                .Returns(new AgentAutonomyPreferenceRow { UserId = userId, Level = level });
        }
    }

    private void StubAgentsAndShifts(int agentCount = 1, int shiftCount = 1)
    {
        _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, agentCount).Select(_ => new Client { Id = Guid.NewGuid() }).ToList());
        _shiftScheduleRepository.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((Enumerable.Range(0, shiftCount).Select(_ => new ShiftDayAssignment { ShiftId = Guid.NewGuid() }).ToList(), shiftCount));
    }

    private static Group MakeGroup(PaymentInterval interval, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = DateTime.UtcNow.Date
    };

    [Test]
    public async Task DetectAsync_NoGroups_ReturnsEmpty()
    {
        StubGroups();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_IndividualInterval_AlwaysSkipped()
    {
        StubGroups(MakeGroup(PaymentInterval.Individual));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_OutsideLeadTime_Skips()
    {
        // 2026-01-10 is 22 days before the next period start (2026-02-01) — outside LeadTimeDays.
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_EmailBacklogWithAnalysisEnabled_DefersWholeScan()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubEmailAnalysis(enabled: true, backlogCount: 1);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_EmailAnalysisDisabled_BacklogIsNeverProbed()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubEmailAnalysis(enabled: false, backlogCount: 1);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        await _receivedEmailRepository.DidNotReceiveWithAnyArgs().GetUnprocessedAsync(default);
    }

    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    public async Task DetectAsync_BelowAutonomous_EmitsHintAndNeverStartsAutofill(AutonomyLevel level)
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, level));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var hint = (NextPeriodSchedulingDueTriggerEvent)events[0];
        Assert.That(hint.PeriodStartDate, Is.EqualTo(new DateOnly(2026, 2, 1)));
        Assert.That(hint.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 2, 28)));
        Assert.That(hint.DaysUntilStart, Is.EqualTo(4));
        Assert.That(hint.Kind, Is.EqualTo(AgentTriggerKinds.NextPeriodSchedulingDue));
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [TestCase(AutonomyLevel.Autonomous)]
    [TestCase(AutonomyLevel.FullyAutonomous)]
    public async Task DetectAsync_AutonomousOrHigher_StartsAutofillAndEmitsInfoEvent(AutonomyLevel level)
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubAdmins((AdminId, level));
        var jobId = Guid.NewGuid();
        _autoWizardJobRunner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var started = (NextPeriodAutofillStartedTriggerEvent)events[0];
        Assert.That(started.JobId, Is.EqualTo(jobId));
        Assert.That(started.GroupId, Is.EqualTo(group.Id));
        Assert.That(started.AutoCommitIntended, Is.EqualTo(level == AutonomyLevel.FullyAutonomous));
        await _autoWizardJobRunner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(request =>
                request.GroupId == group.Id
                && request.PeriodFrom == new DateOnly(2026, 2, 1)
                && request.PeriodUntil == new DateOnly(2026, 2, 28)),
            Arg.Any<CancellationToken>());

        if (level == AutonomyLevel.FullyAutonomous)
        {
            _autoCommitService.Received(1).QueueAutoCommit(
                jobId, group.Id, group.Name, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        }
        else
        {
            _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
        }
    }

    [TestCase(AutonomyLevel.Autonomous)]
    [TestCase(AutonomyLevel.FullyAutonomous)]
    public async Task DetectAsync_KillSwitchActive_FallsBackToHintEvenAtAutonomousOrHigher(AutonomyLevel level)
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, level));
        StubKillSwitch(active: true);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
    }

    [Test]
    public async Task DetectAsync_MinAggregation_OneCautiousAdminBlocksAutoStart()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, AutonomyLevel.Propose));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_NoAdmins_DegradesToHint()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_GlobalAutonomyLevelBelowAutonomous_BlocksAutoStartDespiteAdminLevel()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.Autonomous));
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Propose);

        var events = await _sut.DetectAsync();

        // The installation-wide cap throttles the automatic start: at global level 0 the detector
        // falls back to the hint, no matter what the admins have chosen.
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_ScenarioAlreadyCoversNextPeriod_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            FromDate = new DateOnly(2026, 2, 1),
            UntilDate = new DateOnly(2026, 2, 28),
            Status = AnalyseScenarioStatus.Active
        });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_RejectedScenario_DoesNotCountAsPlanned()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            FromDate = new DateOnly(2026, 2, 1),
            UntilDate = new DateOnly(2026, 2, 28),
            Status = AnalyseScenarioStatus.Rejected
        });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
    }

    [Test]
    public async Task DetectAsync_AutofillRunAlreadyInProgress_EmitsNothingForThatGroup()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.Autonomous));
        _autoWizardJobRunner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns<Guid>(_ => throw new AutofillRunConflictException(Guid.NewGuid(), AutofillFamily.AutoWizard));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AutonomousButNoShifts_FallsBackToHint()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous));
        StubAgentsAndShifts(agentCount: 1, shiftCount: 0);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_NextPeriodIsTheComingWeek()
    {
        // Today 2026-01-28 is a Wednesday; with a Monday week start the next period is Mo 2026-02-02
        // to Su 2026-02-08, five days ahead and therefore inside the lead window.
        StubGroups(MakeGroup(PaymentInterval.Weekly));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var hint = (NextPeriodSchedulingDueTriggerEvent)events[0];
        Assert.That(hint.PeriodStartDate, Is.EqualTo(new DateOnly(2026, 2, 2)));
        Assert.That(hint.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 2, 8)));
        Assert.That(hint.DaysUntilStart, Is.EqualTo(5));
    }

    [Test]
    public async Task DetectAsync_GroupWithoutClientsOrShifts_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_NextPeriodWithoutPlannableShift_EmitsNothing()
    {
        _activityProbe.HasPlannableShiftsInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);
        StubGroups(MakeGroup(PaymentInterval.Monthly));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
