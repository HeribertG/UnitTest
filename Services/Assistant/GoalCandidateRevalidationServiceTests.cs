// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GoalCandidateRevalidationService — covers the expiry of a candidate whose
/// observation stopped, the survival of one still being observed, the grace period for freshly
/// written candidates, the per-user scoping of the observation lookup and the refusal to act on a
/// truncated dispatch window.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class GoalCandidateRevalidationServiceTests
{
    private const string UserId = "planner-1";
    private const string OtherUserId = "planner-2";
    private const int DispatchRowCap = 2000;

    private static readonly DateTime NowUtc = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private TimeProvider _timeProvider = null!;
    private GoalCandidateRevalidationService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _timeProvider = Substitute.For<TimeProvider>();
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(NowUtc));
        StubDispatches();
        _sut = new GoalCandidateRevalidationService(
            _dispatchRepository,
            _goalCandidateRepository,
            _timeProvider,
            NullLogger<GoalCandidateRevalidationService>.Instance);
    }

    private void StubCandidates(params GoalCandidate[] candidates) =>
        _goalCandidateRepository.GetOpenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(candidates.ToList());

    private void StubDispatches(params ProactiveTriggerDispatchRow[] rows) =>
        _dispatchRepository.GetSinceAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rows.ToList());

    private static GoalCandidate MakeCandidate(string goalType, int ageDays = 10, string userId = UserId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        GoalType = goalType,
        Status = GoalCandidateStatus.Proposed,
        CreateTime = NowUtc.AddDays(-ageDays)
    };

    private static ProactiveTriggerDispatchRow MakeDispatch(string triggerKind, string userId = UserId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TriggerKind = triggerKind,
        CreateTime = NowUtc.AddDays(-1)
    };

    [Test]
    public async Task RunRevalidationCycleAsync_ObservationStopped_ExpiresCandidate()
    {
        var candidate = MakeCandidate(AgentTriggerKinds.PeriodCloseDue);
        StubCandidates(candidate);

        var expired = await _sut.RunRevalidationCycleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(expired, Is.EqualTo(1));
            Assert.That(candidate.Status, Is.EqualTo(GoalCandidateStatus.Expired));
            Assert.That(candidate.DecidedUtc, Is.EqualTo(NowUtc));
        });
    }

    [Test]
    public async Task RunRevalidationCycleAsync_StillObserved_KeepsCandidate()
    {
        var candidate = MakeCandidate(AgentTriggerKinds.EmptyContainer);
        StubCandidates(candidate);
        StubDispatches(MakeDispatch(AgentTriggerKinds.EmptyContainer));

        var expired = await _sut.RunRevalidationCycleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(expired, Is.Zero);
            Assert.That(candidate.Status, Is.EqualTo(GoalCandidateStatus.Proposed));
        });
    }

    [Test]
    public async Task RunRevalidationCycleAsync_CandidateWithinGracePeriod_IsNeverExpired()
    {
        var candidate = MakeCandidate(AgentTriggerKinds.PeriodOverdue, ageDays: 0);
        StubCandidates(candidate);

        var expired = await _sut.RunRevalidationCycleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(expired, Is.Zero);
            Assert.That(candidate.Status, Is.EqualTo(GoalCandidateStatus.Proposed));
        });
    }

    [Test]
    public async Task RunRevalidationCycleAsync_ObservationOfAnotherUser_DoesNotKeepCandidate()
    {
        var candidate = MakeCandidate(AgentTriggerKinds.PeriodOverdue);
        StubCandidates(candidate);
        StubDispatches(MakeDispatch(AgentTriggerKinds.PeriodOverdue, OtherUserId));

        var expired = await _sut.RunRevalidationCycleAsync();

        Assert.That(expired, Is.EqualTo(1));
    }

    [Test]
    public async Task RunRevalidationCycleAsync_TruncatedDispatchWindow_ExpiresNothing()
    {
        var candidate = MakeCandidate(AgentTriggerKinds.PeriodCloseDue);
        StubCandidates(candidate);
        StubDispatches(Enumerable
            .Range(0, DispatchRowCap)
            .Select(_ => MakeDispatch(AgentTriggerKinds.TargetHoursDrift))
            .ToArray());

        var expired = await _sut.RunRevalidationCycleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(expired, Is.Zero);
            Assert.That(candidate.Status, Is.EqualTo(GoalCandidateStatus.Proposed));
        });
    }

    [Test]
    public async Task RunRevalidationCycleAsync_NoOpenCandidates_SkipsTheDispatchQuery()
    {
        StubCandidates();

        var expired = await _sut.RunRevalidationCycleAsync();

        Assert.That(expired, Is.Zero);
        await _dispatchRepository.DidNotReceive().GetSinceAsync(
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
