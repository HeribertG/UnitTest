// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UnstaffedShift7dDetector — covers empty-result, only-staffed,
/// mix of staffed/understaffed, and the daysUntil severity boundary.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UnstaffedShift7dDetectorTests
{
    private IShiftScheduleRepository _repo = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private UnstaffedShift7dDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IShiftScheduleRepository>();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _sut = new UnstaffedShift7dDetector(
            _repo, _groupScopeReader, new FixedCompanyClock(DateTimeOffset.UtcNow), NullLogger<UnstaffedShift7dDetector>.Instance);
    }

    private static ShiftDayAssignment MakeAssignment(DateOnly date, int sum, int quantity, Guid? id = null) => new()
    {
        ShiftId = id ?? Guid.NewGuid(),
        Date = date,
        Quantity = quantity,
        SumEmployees = sum,
        ShiftName = "Test",
        Abbreviation = "T"
    };

    [Test]
    public async Task DetectAsync_NoAssignments_ReturnsEmpty()
    {
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>(), 0));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_OnlyFullyStaffed_ReturnsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(1), sum: 3, quantity: 3),
                MakeAssignment(today.AddDays(2), sum: 2, quantity: 2)
            }, 2));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_UnderstaffedShift_EmitsEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(2), sum: 1, quantity: 3)
            }, 1));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var unstaffed = events.Single() as UnstaffedShiftTriggerEvent;
        Assert.That(unstaffed, Is.Not.Null);
        Assert.That(unstaffed!.DaysUntil, Is.EqualTo(2));
    }

    [Test]
    public async Task DetectAsync_MixedDays_RanksSeverityCorrectly()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(1), sum: 0, quantity: 1),
                MakeAssignment(today.AddDays(5), sum: 0, quantity: 1)
            }, 2));

        var events = (await _sut.DetectAsync()).Cast<UnstaffedShiftTriggerEvent>().ToList();

        Assert.That(events.Single(e => e.DaysUntil == 1).Severity, Is.EqualTo(AgentTriggerSeverity.High));
        Assert.That(events.Single(e => e.DaysUntil == 5).Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_IgnoresZeroQuantitySlots()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(1), sum: 0, quantity: 0)
            }, 1));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ShiftInOneGroup_CarriesThatGroup_AndPreselectsItInTheActionParams()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assignment = MakeAssignment(today.AddDays(1), sum: 0, quantity: 2);
        var groupId = Guid.NewGuid();
        StubAssignments(assignment);
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (assignment.ShiftId, new[] { groupId }));

        var unstaffed = (UnstaffedShiftTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(unstaffed.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(unstaffed.ActionParams![ProactiveActionParamKeys.GroupId], Is.EqualTo(groupId.ToString()));
        Assert.That(unstaffed.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ShiftInTwoGroups_CarriesBoth_NotOnlyTheFirst()
    {
        // ShiftDayAssignment has no group of its own, so the groups are resolved afterwards; keeping
        // only the first would deny the finding to every planner scoped to the second group.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assignment = MakeAssignment(today.AddDays(1), sum: 0, quantity: 2);
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        StubAssignments(assignment);
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (assignment.ShiftId, new[] { firstGroupId, secondGroupId }));

        var unstaffed = (UnstaffedShiftTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(unstaffed.GroupIds, Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task DetectAsync_UngroupedShift_IsStillFound_ButCarriesNoGroup()
    {
        // The scan filter sets ShowUngroupedShifts, so an ungrouped shift must keep being detected;
        // RequiresGroupScope then routes it to Admins instead of every planner.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assignment = MakeAssignment(today.AddDays(1), sum: 0, quantity: 2);
        StubAssignments(assignment);

        var unstaffed = (UnstaffedShiftTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(unstaffed.GroupIds, Is.Empty);
        Assert.That(unstaffed.ActionParams!.ContainsKey(ProactiveActionParamKeys.GroupId), Is.False);
        Assert.That(unstaffed.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ManyFindings_ResolvesGroupsInOneBatchedLookup_NeverOnePerFinding()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assignments = Enumerable.Range(0, 10)
            .Select(_ => MakeAssignment(today.AddDays(1), sum: 0, quantity: 2))
            .ToArray();
        StubAssignments(assignments);

        await _sut.DetectAsync();

        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    private void StubAssignments(params ShiftDayAssignment[] assignments) =>
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((assignments.ToList(), assignments.Length));

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). An unstaffed slot on 2026-06-30 is 2 days
        // out under the (correct) company day but 3 days out under the (wrong) UTC day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new UnstaffedShift7dDetector(_repo, _groupScopeReader, clock, NullLogger<UnstaffedShift7dDetector>.Instance);
        StubAssignments(MakeAssignment(new DateOnly(2026, 6, 30), sum: 0, quantity: 1));

        var events = await _sut.DetectAsync();

        var unstaffed = events.Single() as UnstaffedShiftTriggerEvent;
        Assert.That(unstaffed!.DaysUntil, Is.EqualTo(2),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }
}
