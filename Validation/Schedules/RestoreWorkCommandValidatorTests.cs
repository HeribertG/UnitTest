// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RestoreWorkCommandValidator: the deleting user may undo his own delete only inside the
/// undo window and only while the Work is not sealed; an Admin restores any delete at any time, a sealed
/// Work included. A foreign delete is NOT judged here at all - the validator passes it through unchanged
/// so the handler answers 404 and no 400 message can reveal that the Work exists. A missing or
/// not-deleted Work passes for the same reason.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Validation.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Validation.Schedules;

[TestFixture]
public class RestoreWorkCommandValidatorTests
{
    private static readonly DateTime Now = new(2027, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private RestoreWorkCommandValidator _validator = null!;
    private IWorkRepository _workRepository = null!;
    private IWorkRestoreAuthorizer _authorizer = null!;
    private SettableTimeProvider _timeProvider = null!;
    private readonly Guid _workId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _authorizer = Substitute.For<IWorkRestoreAuthorizer>();
        _timeProvider = new SettableTimeProvider(Now);
        _validator = new RestoreWorkCommandValidator(
            _workRepository,
            new WorkLockLevelService(),
            _authorizer,
            _timeProvider);
    }

    [Test]
    public async Task Validate_WorkNotFoundOrNotDeleted_ShouldBeValid()
    {
        _workRepository.GetDeletedAsync(_workId, Arg.Any<CancellationToken>()).Returns((Work?)null);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeTrue();
        _authorizer.DidNotReceiveWithAnyArgs().Resolve(default!);
    }

    [Test]
    public async Task Validate_OwnDeleteInsideWindow_ShouldBeValid()
    {
        GivenDeletedWork(Now.AddSeconds(-(WorkRestoreDefaults.UndoWindowSeconds - 1)), WorkRestoreAccess.Owner);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public async Task Validate_OwnDeleteAtWindowEdge_ShouldBeValid()
    {
        GivenDeletedWork(Now.AddSeconds(-WorkRestoreDefaults.UndoWindowSeconds), WorkRestoreAccess.Owner);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public async Task Validate_OwnDeleteAfterWindow_ShouldBeInvalid()
    {
        GivenDeletedWork(Now.AddSeconds(-(WorkRestoreDefaults.UndoWindowSeconds + 1)), WorkRestoreAccess.Owner);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == RestoreWorkCommandValidator.UndoWindowExpiredMessage);
    }

    [Test]
    [TestCase(WorkLockLevel.None)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_ForeignDelete_PassesThroughWithoutAnyMessage_SealedOrNot(WorkLockLevel level)
    {
        GivenDeletedWork(Now.AddDays(-30), WorkRestoreAccess.Hidden, level);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Test]
    public async Task Validate_AdminAfterWindow_ShouldBeValid()
    {
        GivenDeletedWork(Now.AddDays(-30), WorkRestoreAccess.Admin);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_SealedWork_OwnerInsideWindow_ShouldBeInvalid(WorkLockLevel level)
    {
        GivenDeletedWork(Now.AddSeconds(-1), WorkRestoreAccess.Owner, level);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == RestoreWorkCommandValidator.SealedWorkMessage);
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_SealedWork_Admin_ShouldBeValid(WorkLockLevel level)
    {
        GivenDeletedWork(Now.AddSeconds(-1), WorkRestoreAccess.Admin, level);

        var result = await _validator.ValidateAsync(new RestoreWorkCommand(_workId));

        result.IsValid.ShouldBeTrue();
    }

    private void GivenDeletedWork(DateTime deletedTime, WorkRestoreAccess access, WorkLockLevel level = WorkLockLevel.None)
    {
        var work = new Work
        {
            Id = _workId,
            IsDeleted = true,
            DeletedTime = deletedTime,
            CurrentUserDeleted = "user-1",
            LockLevel = level
        };
        _workRepository.GetDeletedAsync(_workId, Arg.Any<CancellationToken>()).Returns(work);
        _authorizer.Resolve(work).Returns(access);
    }
}
