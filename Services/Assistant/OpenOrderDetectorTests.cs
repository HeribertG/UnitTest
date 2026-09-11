// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OpenOrderDetector -- covers empty-result, the three severity tiers by FromDate
/// proximity, past-dated exclusion, scenario-clone exclusion (AnalyseToken / ScenarioSourceShiftId),
/// soft-delete exclusion, non-OriginalOrder status exclusion, and DedupKey stability across two
/// scans of the same order. open_order is deliberately not unstaffed_shift: an order can already be
/// fully staffed and still be an open, unsealed draft, so this detector never looks at
/// SumEmployees/Quantity.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class OpenOrderDetectorTests
{
    private IShiftRepository _repo = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private OpenOrderDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IShiftRepository>();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _sut = new OpenOrderDetector(_repo, _groupScopeReader, new FixedCompanyClock(DateTimeOffset.UtcNow), NullLogger<OpenOrderDetector>.Instance);
    }

    private static Shift MakeShift(
        DateOnly fromDate,
        ShiftStatus status = ShiftStatus.OriginalOrder,
        Guid? analyseToken = null,
        Guid? scenarioSourceShiftId = null,
        bool isDeleted = false,
        Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Status = status,
        FromDate = fromDate,
        Name = "Test Order",
        AnalyseToken = analyseToken,
        ScenarioSourceShiftId = scenarioSourceShiftId,
        IsDeleted = isDeleted
    };

    [Test]
    public async Task DetectAsync_NoShifts_ReturnsEmpty()
    {
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift>()));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_OrderStartingWithin7Days_EmitsHighSeverityEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var openOrder = events.Single() as OpenOrderTriggerEvent;
        Assert.That(openOrder, Is.Not.Null);
        Assert.That(openOrder!.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_OrderStartingBetween8And30Days_EmitsMediumSeverityEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(15));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = (await _sut.DetectAsync()).Cast<OpenOrderTriggerEvent>().ToList();

        Assert.That(events.Single().Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_OrderStartingAfter30Days_EmitsLowSeverityEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(45));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = (await _sut.DetectAsync()).Cast<OpenOrderTriggerEvent>().ToList();

        Assert.That(events.Single().Severity, Is.EqualTo(AgentTriggerSeverity.Low));
    }

    [Test]
    public async Task DetectAsync_OrderInThePast_IsNotReported()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(-1));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioCloneWithAnalyseToken_IsNotReported()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3), analyseToken: Guid.NewGuid());
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioCloneWithScenarioSourceShiftId_IsNotReported()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3), scenarioSourceShiftId: Guid.NewGuid());
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_SoftDeletedShift_IsNotReported()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3), isDeleted: true);
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_NonOriginalOrderStatus_IsNotReported()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3), status: ShiftStatus.SealedOrder);
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_CalledTwice_ReturnsStableDedupKeyForSameShift()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var firstKey = (await _sut.DetectAsync()).Cast<OpenOrderTriggerEvent>().Single().DedupKey;
        var secondKey = (await _sut.DetectAsync()).Cast<OpenOrderTriggerEvent>().Single().DedupKey;

        Assert.That(secondKey, Is.EqualTo(firstKey));
    }

    [Test]
    public async Task DetectAsync_MoreOpenOrdersThanCap_SelectsSoonestFromDateFirstUpToCap()
    {
        // Regression test for the missing defensive cap: this detector has no per-tick emission
        // window like its two sibling detectors, so an unbounded table could scan indefinitely.
        // Inserting in an order that contradicts FromDate order proves both that the cap is applied
        // and that the selection tracks FromDate (soonest = highest severity), not insertion order.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var totalOrders = OpenOrderDetector.MaxCandidatesToScan + 5;
        var shifts = Enumerable.Range(0, totalOrders)
            .Select(i => MakeShift(today.AddDays(i)))
            .ToList();
        var insertionOrder = shifts.OrderByDescending(s => s.FromDate).ToList();
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(insertionOrder));

        var events = (await _sut.DetectAsync()).Cast<OpenOrderTriggerEvent>().ToList();

        var expectedIds = shifts
            .OrderBy(s => s.FromDate)
            .Take(OpenOrderDetector.MaxCandidatesToScan)
            .Select(s => s.Id)
            .ToHashSet();
        var actualIds = events.Select(e => e.ShiftId).ToHashSet();

        Assert.That(events, Has.Count.EqualTo(OpenOrderDetector.MaxCandidatesToScan));
        Assert.That(actualIds, Is.EqualTo(expectedIds));
    }

    [Test]
    public async Task DetectAsync_OrderInOneGroup_CarriesThatGroup()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3));
        var groupId = Guid.NewGuid();
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (shift.Id, new[] { groupId }));

        var openOrder = (OpenOrderTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(openOrder.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(openOrder.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_OrderInTwoGroups_CarriesBoth_NotOnlyTheFirst()
    {
        // A shift is a member of MANY groups (GroupItem is a many-to-many join). Keeping only one of
        // them would silently deny the finding to every planner scoped to the other group.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3));
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (shift.Id, new[] { firstGroupId, secondGroupId }));

        var openOrder = (OpenOrderTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(openOrder.GroupIds, Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task DetectAsync_OrderWithoutAnyGroup_CarriesNoGroup_AndStaysGroupScopeRequired()
    {
        // No group means the audience cannot be scoped; RequiresGroupScope is what makes
        // AgentTriggerService route this to Admins instead of broadcasting it to every planner.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeShift(today.AddDays(3));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var openOrder = (OpenOrderTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(openOrder.GroupIds, Is.Empty);
        Assert.That(openOrder.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ManyOrders_ResolvesGroupsInOneBatchedLookup_NeverOnePerOrder()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shifts = Enumerable.Range(0, 20).Select(i => MakeShift(today.AddDays(i))).ToList();
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(shifts));

        await _sut.DetectAsync();

        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). An order starting on 2026-06-30 is 2 days
        // out under the (correct) company day but 3 days out under the (wrong) UTC day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new OpenOrderDetector(_repo, _groupScopeReader, clock, NullLogger<OpenOrderDetector>.Instance);
        var shift = MakeShift(new DateOnly(2026, 6, 30));
        _repo.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { shift }));

        var events = (await _sut.DetectAsync()).Cast<OpenOrderTriggerEvent>().ToList();

        Assert.That(events.Single().DaysUntil, Is.EqualTo(2),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }
}
