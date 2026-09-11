// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CoverAbsenceCommandHandler after the engine refactor: it clones + flushes, builds a
/// recovery snapshot, delegates replacement selection to the real (pure, deterministic)
/// <see cref="LocalRepairEngine"/>, records the absence (Break), and materialises each reassignment delta
/// as a Replacement WorkChange on the matching cloned work. Locked / uncoverable slots are reported. The
/// snapshot builder, scenario service, conflict checker and mediator are substituted; the engine and the
/// CompliancePartitionService (wired over the substituted checker and override authorizer) are real.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using Klacks.UnitTest.ScheduleRecovery;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class CoverAbsenceCommandHandlerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid CandidateId = Guid.NewGuid();
    private static readonly Guid CrossGroupId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid ClonedWorkId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 10);

    private IAnalyseScenarioRepository _scenarioRepo = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IScheduleEntriesService _scheduleEntries = null!;
    private IRecoverySnapshotBuilder _snapshotBuilder = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IEscalationChainService _escalationChainService = null!;
    private ICompanyClock _companyClock = null!;
    private CoverAbsenceCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _scenarioRepo = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepo.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());

        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _scenarioService.CloneScenarioDataWithMapsAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<Guid, Guid>(), new Dictionary<Guid, Guid> { [WorkId] = ClonedWorkId }));

        _scheduleEntries = Substitute.For<IScheduleEntriesService>();
        SetAbsentSlots(WorkSlot());

        _snapshotBuilder = Substitute.For<IRecoverySnapshotBuilder>();
        UseSnapshot(WithFreeCandidate());

        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkBreaksResponse());
        _mediator.Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkChangeResource());

        _unitOfWork = Substitute.For<IUnitOfWork>();

        _escalationChainService = Substitute.For<IEscalationChainService>();
        _escalationChainService.StartChainAsync(Arg.Any<StartEscalationChainRequest>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)Guid.NewGuid());

        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

        var partitionService = new CompliancePartitionService(
            _conflictChecker,
            _overrideAuthorizer,
            _httpContextAccessor,
            Substitute.For<ILogger<CompliancePartitionService>>());

        _handler = new CoverAbsenceCommandHandler(
            _scenarioRepo, _scenarioService, _scheduleEntries, _snapshotBuilder, new LocalRepairEngine(),
            partitionService, _mediator, _unitOfWork, _escalationChainService, _companyClock,
            NullLogger<CoverAbsenceCommandHandler>.Instance);
    }

    private void SetAbsentSlots(params ScheduleCell[] cells)
        => _scheduleEntries.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(cells));

    private void UseSnapshot(RecoverySnapshot snapshot)
        => _snapshotBuilder.BuildAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(snapshot);

    private static RecoverySnapshot WithFreeCandidate(bool locked = false, bool candidateEligible = true)
        => new SnapshotBuilder()
            .Days(Date)
            .Agent(ClientId, "Absent")
            .Agent(CandidateId, "Bob", blacklistedShiftIds: candidateEligible ? null : [ShiftId])
            .Work(ClientId, Date, ShiftId, ShiftCategory.Early,
                Date.ToDateTime(new TimeOnly(8, 0)), Date.ToDateTime(new TimeOnly(16, 0)), 8m, locked, WorkId)
            .Build();

    private static ScheduleCell WorkSlot() => new()
    {
        EntryType = (int)ScheduleEntryType.Work,
        ClientId = ClientId,
        SourceId = WorkId,
        EntryId = ShiftId,
        EntryDate = Date.ToDateTime(TimeOnly.MinValue),
        StartTime = new TimeSpan(8, 0, 0),
        EndTime = new TimeSpan(16, 0, 0),
        LockLevel = 0
    };

    private Task<CoverAbsenceOutcome> Cover()
        => _handler.Handle(new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId), CancellationToken.None);

    [Test]
    public async Task CoveredSlot_WritesReplacementWorkChange_OnClonedWork_AndRecordsAbsence()
    {
        var outcome = await Cover();

        outcome.Covered.Count.ShouldBe(1);
        outcome.Covered[0].ReplacementClientId.ShouldBe(CandidateId);
        outcome.Covered[0].ShiftId.ShouldBe(ShiftId);
        outcome.Uncovered.ShouldBeEmpty();

        await _scenarioRepo.Received(1).Add(Arg.Any<AnalyseScenario>());
        await _mediator.Received(1).Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c =>
                c.Resource.Type == WorkChangeType.ReplacementWithin
                && c.Resource.ReplaceClientId == CandidateId
                && c.Resource.WorkId == ClonedWorkId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LockedSlot_Reported_NotCovered()
    {
        UseSnapshot(WithFreeCandidate(locked: true));

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(1);
        outcome.Uncovered[0].Reason.ShouldBe("locked");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoEligibleCandidate_ReportedAsUnderCoverage()
    {
        UseSnapshot(WithFreeCandidate(candidateEligible: false));

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(1);
        outcome.Uncovered[0].Reason.ShouldBe("no eligible candidate");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoSlots_StillRecordsAbsence()
    {
        UseSnapshot(new SnapshotBuilder().Days(Date).Agent(ClientId, "Absent").Agent(CandidateId, "Bob").Build());
        SetAbsentSlots();

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.ShouldBeEmpty();
        await _mediator.Received(1).Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CrossGroupCover_MaterialisesTemporaryMembership_AndReassignsToBorrowedAgent()
    {
        // No in-group cover (the only in-group candidate is blacklisted), so the real engine borrows the
        // cross-group candidate, producing a MembershipDelta that the handler must materialise.
        var snapshot = new SnapshotBuilder()
            .Days(Date)
            .ReceivingGroup(GroupId)
            .Agent(ClientId, "Absent")
            .Agent(CandidateId, "InGroupBlacklisted", blacklistedShiftIds: [ShiftId])
            .Agent(CrossGroupId, "Borrowed", isInGroup: false)
            .Work(ClientId, Date, ShiftId, ShiftCategory.Early,
                Date.ToDateTime(new TimeOnly(8, 0)), Date.ToDateTime(new TimeOnly(16, 0)), 8m, false, WorkId)
            .Build();
        UseSnapshot(snapshot);

        var outcome = await Cover();

        outcome.Covered.Count.ShouldBe(1);
        outcome.Covered[0].ReplacementClientId.ShouldBe(CrossGroupId);

        await _scenarioService.Received(1).AddScenarioMembershipAsync(
            Arg.Any<Guid>(), CrossGroupId, GroupId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c =>
                c.Resource.ReplaceClientId == CrossGroupId && c.Resource.WorkId == ClonedWorkId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BlockedByComplianceRule_NoOverride_ReportedAsUncovered_NotMaterialised()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(OverridableBlockingCheck());
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(1);
        outcome.Uncovered[0].Reason.ShouldBe("blocked");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BlockedByComplianceRule_WithAuthorizedOverride_MaterialisesAndCoversSlot()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(OverridableBlockingCheck());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        var outcome = await _handler.Handle(
            new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId, OverrideBlock: true), CancellationToken.None);

        outcome.Covered.Count.ShouldBe(1);
        outcome.Uncovered.ShouldBeEmpty();
        outcome.ComplianceWarnings.ShouldContain(w =>
            w.Comment == "schedule.error-list.rest-violation" && w.Type == ScheduleValidationType.Error);
        await _mediator.Received(1).Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WarnModeConflicts_AreMaterialised_AndSurfacedInOutcome()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(
            [
                new ScheduleValidationNotificationDto
                {
                    Type = ScheduleValidationType.Warning,
                    ClientId = CandidateId,
                    Date = Date,
                    Comment = "schedule.error-list.rest-violation",
                }
            ]));

        var outcome = await Cover();

        outcome.Covered.Count.ShouldBe(1);
        outcome.Uncovered.ShouldBeEmpty();
        var warning = outcome.ComplianceWarnings.ShouldHaveSingleItem();
        warning.Comment.ShouldBe("schedule.error-list.rest-violation");
        warning.Type.ShouldBe(ScheduleValidationType.Warning);
        await _mediator.Received(1).Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StructuralBlock_NeverOverridable_ReportedAsUncovered_EvenWhenOverrideAuthorized()
    {
        var structuralCheck = new PreCommitCheckResult(
        [
            new ScheduleValidationNotificationDto
            {
                Type = ScheduleValidationType.Error,
                ClientId = CandidateId,
                Date = Date,
                Comment = "schedule.error-list.collision",
            }
        ]);
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(structuralCheck);
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(true);

        var outcome = await _handler.Handle(
            new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId, OverrideBlock: true), CancellationToken.None);

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(1);
        outcome.Uncovered[0].Reason.ShouldBe("blocked");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    private static PreCommitCheckResult OverridableBlockingCheck() => new(
    [
        new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = CandidateId,
            Date = Date,
            Comment = "schedule.error-list.rest-violation",
            CommentParams = new Dictionary<string, string> { ["enforcementRule"] = "minRestHours" },
        }
    ]);

    [Test]
    public async Task ClonesAndFlushes_BeforeBuildingSnapshot()
    {
        var order = new List<string>();
        _scenarioService.When(s => s.CloneScenarioDataWithMapsAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("clone"));
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => order.Add("complete"));
        _snapshotBuilder.When(s => s.BuildAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("snapshot"));

        await Cover();

        order.IndexOf("clone").ShouldBeLessThan(order.IndexOf("complete"));
        order.IndexOf("complete").ShouldBeLessThan(order.IndexOf("snapshot"));
    }

    [Test]
    public async Task CoveredSlot_CarriesTheEscalationTierItNeeded()
    {
        var outcome = await Cover();

        outcome.Covered.Count.ShouldBe(1);
        // A free in-group candidate is the cheapest tier; the UI shows how far the engine had to reach.
        outcome.Covered[0].Tier.ShouldBe(0);
        outcome.HighestTier.ShouldBe(0);
    }

    [Test]
    public async Task HighestTier_ReportsUncovered_WhenSomethingStayedOpen()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(OverridableBlockingCheck());
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        var outcome = await Cover();

        outcome.Uncovered.ShouldNotBeEmpty();
        // Tier 4 == Uncovered: a blocked option must never look like a completed cover.
        outcome.HighestTier.ShouldBe(4);
    }

    [Test]
    public async Task MultiDayAbsence_RecordsEveryDayOfThePeriod()
    {
        var until = Date.AddDays(2);

        var outcome = await _handler.Handle(
            new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId, until), CancellationToken.None);

        outcome.ShouldNotBeNull();
        await _mediator.Received().Send(
            Arg.Is<BulkAddBreaksCommand>(c => c.Request.Breaks.Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CoveredSlot_ShiftStartUtc_IsConvertedFromCompanyWallClock_NotRawUtc()
    {
        // Date is 2026-03-10 (before the EU spring-forward on 2026-03-29), so Zurich is CET (UTC+1):
        // the 08:00 local shift start must arrive at the escalation chain as 07:00 UTC, not 08:00 UTC.
        var zurichClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var partitionService = new CompliancePartitionService(
            _conflictChecker, _overrideAuthorizer, _httpContextAccessor,
            Substitute.For<ILogger<CompliancePartitionService>>());
        var handler = new CoverAbsenceCommandHandler(
            _scenarioRepo, _scenarioService, _scheduleEntries, _snapshotBuilder, new LocalRepairEngine(),
            partitionService, _mediator, _unitOfWork, _escalationChainService, zurichClock,
            NullLogger<CoverAbsenceCommandHandler>.Instance);

        await handler.Handle(new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId), CancellationToken.None);

        await _escalationChainService.Received(1).StartChainAsync(
            Arg.Is<StartEscalationChainRequest>(r =>
                r.ShiftStartUtc == new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvertedPeriod_IsRejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(
            new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId, Date.AddDays(-1)), CancellationToken.None));
    }
}

