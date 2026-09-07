// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RestoreWorkCommandHandler: the undo of a soft-deleted Work re-runs the seal guards
/// (client-based like a create, plus shift-based because the deleted client has no live work to bind
/// him to a group), the write guard (sporadic capacity, hard-blocking conflicts), restores the container
/// children and the softening rows that were cascaded away by the same delete, and replays the
/// create-side commit sequence (restore, commit, overtime successors, period hours, created-notifications).
/// A foreign delete (non-admin, not the deleting user) is answered exactly like "not found".
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class RestoreWorkCommandHandlerTests
{
    private const string DeletedBy = "user-1";
    private const string ConnectionId = "connection-1";

    private IWorkRepository _workRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private IWorkSofteningRepository _softeningRepository = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IDayLockService _dayLockService = null!;
    private IWorkWriteGuard _writeGuard = null!;
    private IWorkRestoreAuthorizer _authorizer = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private RestoreWorkCommandHandler _handler = null!;

    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 10);
    private readonly DateOnly _periodStart = new(2027, 3, 1);
    private readonly DateOnly _periodEnd = new(2027, 3, 31);
    private readonly DateTime _deletedTime = new(2027, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private Work _work = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _softeningRepository = Substitute.For<IWorkSofteningRepository>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _dayLockService = Substitute.For<IDayLockService>();
        _writeGuard = Substitute.For<IWorkWriteGuard>();
        _authorizer = Substitute.For<IWorkRestoreAuthorizer>();
        _authorizer.Resolve(Arg.Any<Work>()).Returns(WorkRestoreAccess.Owner);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();

        _work = new Work
        {
            Id = _workId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = _date,
            IsDeleted = true,
            DeletedTime = _deletedTime,
            CurrentUserDeleted = DeletedBy
        };
        _workRepository.GetDeletedAsync(_workId, Arg.Any<CancellationToken>()).Returns(_work);

        _periodHoursService.GetPeriodBoundariesAsync(_date).Returns((_periodStart, _periodEnd));
        _notificationFacade.GetConnectionId().Returns(ConnectionId);
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns((List<Guid>?)null);
        _completionService.SaveAndTrackAsync(_clientId, _date, _periodStart, _periodEnd, null)
            .Returns(new PeriodHoursResource { Hours = 8 });
        _scheduleEntriesService.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(
            [
                new ScheduleCell { ClientId = _clientId, EntryId = _shiftId },
                new ScheduleCell { ClientId = Guid.NewGuid(), EntryId = _shiftId },
            ]));

        _handler = new RestoreWorkCommandHandler(
            _workRepository,
            new ScheduleMapper(),
            _periodHoursService,
            _scheduleEntriesService,
            _completionService,
            _notificationFacade,
            _cascadeService,
            _softeningRepository,
            _groupContextResolver,
            _dayLockService,
            _writeGuard,
            _authorizer,
            _unitOfWork,
            _overtimeCascadeService,
            Substitute.For<ILogger<RestoreWorkCommandHandler>>());
    }

    [Test]
    public async Task Handle_ThrowsKeyNotFound_WhenNoDeletedWorkExists()
    {
        var unknownId = Guid.NewGuid();
        _workRepository.GetDeletedAsync(unknownId, Arg.Any<CancellationToken>()).Returns((Work?)null);

        var act = async () => await _handler.Handle(new RestoreWorkCommand(unknownId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
    }

    [Test]
    public async Task Handle_ThrowsKeyNotFound_ForAForeignDelete_BeforeAnyGuardRuns()
    {
        _authorizer.Resolve(_work).Returns(WorkRestoreAccess.Hidden);

        var act = async () => await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedAsync(default, default, default, default);
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureNotLockedForShiftAsync(default, default, default, default);
        await _writeGuard.DidNotReceiveWithAnyArgs().EnsureNoSporadicConflictAsync(default!, default);
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
    }

    [Test]
    public async Task Handle_RestoresForAdmin_WhoIsNotTheDeletingUser()
    {
        _authorizer.Resolve(_work).Returns(WorkRestoreAccess.Admin);

        var result = await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        result.ShouldNotBeNull();
        await _workRepository.Received(1).RestoreAsync(_work);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenTheDeleteStampIsMissing()
    {
        _work.DeletedTime = null;

        var act = async () => await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<InvalidRequestException>();
        exception.Message.ShouldBe(RestoreWorkCommandHandler.MissingDeleteStampMessage);
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
        await _cascadeService.DidNotReceiveWithAnyArgs().RestoreChildrenAsync(default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_RestoresWork_AndReturnsPeriodHoursWithClientScopedThreeDayWindow()
    {
        var result = await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PeriodHours!.Hours.ShouldBe(8);
        result.ScheduleEntries.Count.ShouldBe(1);
        await _workRepository.Received(1).RestoreAsync(_work);
        _scheduleEntriesService.Received(1).GetScheduleEntriesQuery(_date.AddDays(-1), _date.AddDays(1), null, null);
    }

    [Test]
    public async Task Handle_RestoresChildrenAndSoftenings_WithTheOriginalDeleteStamp()
    {
        await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await _cascadeService.Received(1).RestoreChildrenAsync(_workId, _deletedTime, DeletedBy);
        await _softeningRepository.Received(1).RestoreForClientDayAsync(
            _clientId, _date, null, _deletedTime, DeletedBy, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ChecksShiftBasedSeal_AndWritesNothingWhenSealed()
    {
        _dayLockService.EnsureNotLockedForShiftAsync(_date, _shiftId, null, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidRequestException("Day 2027-03-10 is sealed and cannot be modified."));

        var act = async () => await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await act.ShouldThrowAsync<InvalidRequestException>();
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
        await _cascadeService.DidNotReceiveWithAnyArgs().RestoreChildrenAsync(default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_ChecksClientBasedSeal_LikeACreateWould()
    {
        _dayLockService.EnsureNotLockedAsync(_date, _clientId, null, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidRequestException("Day 2027-03-10 is sealed and cannot be modified."));

        var act = async () => await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await act.ShouldThrowAsync<InvalidRequestException>();
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_PropagatesSporadicConflict_AndWritesNothing()
    {
        _writeGuard.EnsureNoSporadicConflictAsync(_work, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ConflictException("Sporadic shift 'X' is fully booked on 2027-03-10 (1/1 employees)."));

        var act = async () => await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await act.ShouldThrowAsync<ConflictException>();
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_PropagatesHardBlockingConflict_AndWritesNothing()
    {
        _writeGuard.EnsureNoHardBlockingConflictAsync(_work, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ConflictException("Work blocked."));

        var act = async () => await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await act.ShouldThrowAsync<ConflictException>();
        await _workRepository.DidNotReceiveWithAnyArgs().RestoreAsync(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_RunsRestoreCommitReprocessAndTrack_InThatOrder()
    {
        await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        Received.InOrder(() =>
        {
            _dayLockService.EnsureNotLockedAsync(_date, _clientId, null, Arg.Any<CancellationToken>());
            _dayLockService.EnsureNotLockedForShiftAsync(_date, _shiftId, null, Arg.Any<CancellationToken>());
            _writeGuard.EnsureNoSporadicConflictAsync(_work, Arg.Any<CancellationToken>());
            _writeGuard.EnsureNoHardBlockingConflictAsync(_work, Arg.Any<CancellationToken>());
            _cascadeService.RestoreChildrenAsync(_workId, _deletedTime, DeletedBy);
            _workRepository.RestoreAsync(_work);
            _unitOfWork.CompleteAsync();
            _overtimeCascadeService.ReprocessSuccessorsAsync(_work);
            _completionService.SaveAndTrackAsync(_clientId, _date, _periodStart, _periodEnd, null);
        });
    }

    [Test]
    public async Task Handle_NotifiesAsCreated_NeverAsDeleted()
    {
        await _handler.Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        await _notificationFacade.Received(1).NotifyWorkCreatedAsync(_work, ConnectionId, _periodStart, _periodEnd);
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyWorkDeletedAsync(default!, default!, default, default);
        await _notificationFacade.Received(1).NotifyPeriodHoursUpdatedAsync(
            _clientId, _periodStart, _periodEnd, Arg.Any<PeriodHoursResource>(), ConnectionId, null);
        await _notificationFacade.Received(1).NotifyShiftStatsAsync(
            _shiftId, _date, ConnectionId, null, Arg.Any<CancellationToken>());
    }
}
