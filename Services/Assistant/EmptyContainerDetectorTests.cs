// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmptyContainerDetector -- covers the empty-database case, the positive
/// no-template-at-all case, the negative case with at least one template (which proves
/// empty_container is disjoint from unstaffed_shift rather than a subset of it), the
/// non-container (Task) exclusion, scenario-copy exclusion, soft-delete exclusion on both
/// Shift and ContainerTemplate, dedup-key stability, the active-period severity rule, and
/// the MaxFindingsPerTick emission cap.
/// Uses a real EF Core InMemory DataBaseContext with the real ShiftRepository and
/// ContainerTemplateRepository (as ContainerAvailableTasksServiceTests.cs does) because the
/// detector composes IQueryable via GetQuery() and awaits ToListAsync(), which a plain
/// NSubstitute-mocked IQueryable cannot support.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class EmptyContainerDetectorTests
{
    private static readonly DateOnly BulkFromDate = new(2025, 1, 1);

    private const int ClockAdvancePollMilliseconds = 5;

    private DataBaseContext _context = null!;
    private ShiftRepository _shiftRepository = null!;
    private ContainerTemplateRepository _containerTemplateRepository = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private AgentConditionRepository _agentConditionRepository = null!;
    private EmptyContainerDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        var shiftLogger = Substitute.For<ILogger<Shift>>();
        var containerTemplateLogger = Substitute.For<ILogger<ContainerTemplate>>();
        var detectorLogger = Substitute.For<ILogger<EmptyContainerDetector>>();

        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var shiftValidator = Substitute.For<IShiftValidator>();
        var queryPipeline = Substitute.For<IShiftQueryPipelineService>();
        var groupManagementService = Substitute.For<IShiftGroupManagementService>();
        var scheduleMapper = new ScheduleMapper();

        _shiftRepository = new ShiftRepository(
            _context,
            shiftLogger,
            queryPipeline,
            groupManagementService,
            collectionUpdateService,
            shiftValidator,
            scheduleMapper,
            new FixedCompanyClock(DateTimeOffset.UtcNow));

        var containerTemplateServiceLogger = Substitute.For<ILogger<ContainerTemplateService>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var containerTemplateService = new ContainerTemplateService(unitOfWork, containerTemplateServiceLogger);

        _containerTemplateRepository = new ContainerTemplateRepository(
            _context,
            containerTemplateLogger,
            collectionUpdateService,
            containerTemplateService);

        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _agentConditionRepository = new AgentConditionRepository(_context);

        _sut = new EmptyContainerDetector(
            _shiftRepository, _containerTemplateRepository, _groupScopeReader, _agentConditionRepository,
            new FixedCompanyClock(DateTimeOffset.UtcNow), detectorLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    /// <summary>
    /// CreateTime is stamped by OnBeforeSaving from DateTime.UtcNow, whose granularity can be coarser than
    /// the time two SaveChanges calls take. Waiting for the clock to actually move is what makes "created
    /// later" mean later, instead of relying on the two batches landing in different ticks by luck.
    /// </summary>
    private static async Task WaitForTheClockToAdvanceAsync()
    {
        var before = DateTime.UtcNow;

        while (DateTime.UtcNow <= before)
        {
            await Task.Delay(ClockAdvancePollMilliseconds);
        }
    }

    private static Shift MakeContainer(
        DateOnly fromDate,
        DateOnly? untilDate = null,
        Guid? analyseToken = null,
        Guid? scenarioSourceShiftId = null,
        bool isDeleted = false,
        ShiftStatus status = ShiftStatus.OriginalShift,
        ShiftType shiftType = ShiftType.IsContainer) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Container",
        Abbreviation = "CNT",
        ShiftType = shiftType,
        Status = status,
        FromDate = fromDate,
        UntilDate = untilDate,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AnalyseToken = analyseToken,
        ScenarioSourceShiftId = scenarioSourceShiftId,
        IsDeleted = isDeleted
    };

    private static ContainerTemplate MakeTemplate(Guid containerId, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = containerId,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(16, 0),
        Weekday = 1,
        IsDeleted = isDeleted
    };

    /// <summary>
    /// Marks the given containers as already having an open ledger row for empty_container, the way a
    /// prior tick's UpsertDetectedAsync would have left them -- exactly what NotYetOpenInLedgerAsync
    /// excludes from its second slice.
    /// </summary>
    private async Task MarkOpenInLedgerAsync(params Guid[] containerIds)
    {
        foreach (var containerId in containerIds)
        {
            await _context.AgentConditions.AddAsync(new AgentCondition
            {
                TriggerKind = AgentTriggerKinds.EmptyContainer,
                Fingerprint = $"empty_container:{containerId}:{Guid.NewGuid()}",
                EntityId = containerId,
                Severity = AgentTriggerSeverity.Medium,
                Status = AgentConditionStatus.Reported,
                DetectedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task DetectAsync_EmptyDatabase_ReturnsEmpty()
    {
        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ContainerWithoutAnyTemplate_EmitsEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var emptyContainerEvent = events.Single() as EmptyContainerTriggerEvent;
        Assert.That(emptyContainerEvent, Is.Not.Null);
        Assert.That(emptyContainerEvent!.ShiftId, Is.EqualTo(container.Id));
        Assert.That(emptyContainerEvent.Kind, Is.EqualTo(AgentTriggerKinds.EmptyContainer));
    }

    [Test]
    public async Task DetectAsync_ContainerWithAtLeastOneTemplate_ReturnsEmpty_DisjointFromUnstaffedShift()
    {
        // empty_container reports MISSING slot DEFINITIONS on the container itself. A container
        // that already has at least one ContainerTemplate is, at most, an unstaffed_shift concern
        // (missing employees on an existing slot) -- the two trigger kinds are disjoint, never a
        // subset of one another, so this container must NOT raise empty_container.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.ContainerTemplate.AddAsync(MakeTemplate(container.Id));
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_TaskShiftInsteadOfContainer_ReturnsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var task = MakeContainer(today.AddDays(-1), today.AddDays(30), shiftType: ShiftType.IsTask);
        await _context.Shift.AddAsync(task);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_NonOriginalShiftStatus_ReturnsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sealedOrder = MakeContainer(today.AddDays(-1), today.AddDays(30), status: ShiftStatus.SealedOrder);
        await _context.Shift.AddAsync(sealedOrder);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioClone_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scenarioContainer = MakeContainer(today.AddDays(-1), today.AddDays(30), analyseToken: Guid.NewGuid());
        await _context.Shift.AddAsync(scenarioContainer);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioSourceCopy_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var scenarioSourceCopy = MakeContainer(today.AddDays(-1), today.AddDays(30), scenarioSourceShiftId: Guid.NewGuid());
        await _context.Shift.AddAsync(scenarioSourceCopy);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_SoftDeletedShift_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var deletedContainer = MakeContainer(today.AddDays(-1), today.AddDays(30), isDeleted: true);
        await _context.Shift.AddAsync(deletedContainer);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_OnlySoftDeletedTemplate_StillCountsAsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.ContainerTemplate.AddAsync(MakeTemplate(container.Id, isDeleted: true));
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var emptyContainerEvent = events.Single() as EmptyContainerTriggerEvent;
        Assert.That(emptyContainerEvent!.ShiftId, Is.EqualTo(container.Id));
    }

    [Test]
    public async Task DetectAsync_DedupKey_IsStableAcrossRepeatedScans()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var firstScan = await _sut.DetectAsync();
        var secondScan = await _sut.DetectAsync();

        Assert.That(firstScan.Single().DedupKey, Is.EqualTo(secondScan.Single().DedupKey));
        Assert.That(firstScan.Single().DedupKey, Is.EqualTo(container.Id.ToString()));
    }

    [Test]
    public async Task DetectAsync_PeriodCurrentlyActive_IsHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(1));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_PeriodNotYetActive_IsLowerThanHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(10), today.AddDays(40));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_PeriodAlreadyEnded_IsLowerThanHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-40), today.AddDays(-10));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_MoreEmptyContainersThanCap_StopsAtMaxFindingsPerTick()
    {
        // Comfortably past both the cap and the second slice's own limit, so nothing being open in
        // the ledger yet still leaves a real cap to prove.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var totalContainers =
            EmptyContainerDetector.MaxFindingsPerTick + EmptyContainerDetector.RecentlyCreatedSlots + 5;
        var containers = Enumerable.Range(0, totalContainers)
            .Select(_ => MakeContainer(today.AddDays(-1), today.AddDays(30)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(
            EmptyContainerDetector.MaxFindingsPerTick + EmptyContainerDetector.RecentlyCreatedSlots));
    }

    [Test]
    public async Task DetectAsync_MoreEmptyContainersThanCap_SelectsOldestFromDateFirst()
    {
        // Regression test for the starvation bug: without an explicit OrderBy before Take, the cap
        // picks from whatever order the query happens to return (effectively physical storage order),
        // so the same subset is chosen on every tick regardless of which containers are actually
        // oldest -- once dispatched, the other containers are permanently starved because dedup has
        // no TTL. Inserting in an order that contradicts FromDate order proves the selection tracks
        // FromDate, not insertion/storage order.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var totalContainers = EmptyContainerDetector.MaxFindingsPerTick + 5;
        var containers = Enumerable.Range(0, totalContainers)
            .Select(i => MakeContainer(today.AddDays(-(totalContainers - i)), today.AddDays(365)))
            .ToList();
        var insertionOrder = containers.OrderByDescending(c => c.FromDate).ToList();
        await _context.Shift.AddRangeAsync(insertionOrder);
        await _context.SaveChangesAsync();

        var expectedIds = containers
            .OrderBy(c => c.FromDate)
            .Take(EmptyContainerDetector.MaxFindingsPerTick)
            .Select(c => c.Id)
            .ToHashSet();

        // Mark the leftover newest containers as already open, isolating this test to the first
        // slice's FromDate ordering -- the second slice's own behaviour has its own tests below.
        var leftoverIds = containers.Select(c => c.Id).Except(expectedIds).ToArray();
        await MarkOpenInLedgerAsync(leftoverIds);

        var events = (await _sut.DetectAsync()).Cast<EmptyContainerTriggerEvent>().ToList();
        var actualIds = events.Select(e => e.ShiftId).ToHashSet();

        Assert.That(actualIds, Is.EqualTo(expectedIds));
    }

    [Test]
    public async Task DetectAsync_CapIsFullAndBacklogSharesOneFromDate_SecondTickReportsWhatFirstTickStarved()
    {
        // The degenerate case plain FromDate order cannot handle, and the one the reference
        // installation actually has: every backlog container carries the same FromDate, so the cap's
        // oldest-first selection collapses onto the random-GUID tiebreaker and would pick the same
        // fixed subset forever under the old "CreateTime strictly greater" second slice, because a
        // shared CreateTime across the whole backlog makes that floor unbeatable. Excluding by ledger
        // membership instead has no such degenerate case: whatever the cap and the first tick's second
        // slice did not reach is still eligible next tick, once the ledger has recorded what was
        // already reported -- mirroring what AgentConditionLedgerService.UpsertDetectedAsync does
        // after every real tick.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backlog = Enumerable.Range(0, EmptyContainerDetector.MaxFindingsPerTick + 20)
            .Select(_ => MakeContainer(BulkFromDate, today.AddDays(365)))
            .ToList();
        await _context.Shift.AddRangeAsync(backlog);
        await _context.SaveChangesAsync();

        var firstTick = (await _sut.DetectAsync()).Cast<EmptyContainerTriggerEvent>().ToList();
        Assert.That(firstTick, Has.Count.EqualTo(
            EmptyContainerDetector.MaxFindingsPerTick + EmptyContainerDetector.RecentlyCreatedSlots));

        var stillStarved = backlog
            .Select(container => container.Id)
            .Except(firstTick.Select(e => e.ShiftId))
            .ToHashSet();
        Assert.That(stillStarved, Is.Not.Empty, "the fixture must actually starve someone on tick 1, otherwise this proves nothing");

        await MarkOpenInLedgerAsync(firstTick.Select(e => e.ShiftId).ToArray());

        var secondTick = (await _sut.DetectAsync()).Cast<EmptyContainerTriggerEvent>().ToList();

        // The first slice re-reports its same oldest-first 50 every tick regardless of ledger status
        // -- by design, so a row being re-observed keeps having its payload refreshed. The point of
        // this test is the second slice: everyone tick 1 starved must be among tick 2's findings.
        Assert.That(secondTick.Select(e => e.ShiftId), Is.SupersetOf(stillStarved));
    }

    [Test]
    public async Task DetectAsync_RepeatedTicksWithLedgerUpdates_EventuallyReportsEveryStarvedCandidate()
    {
        // Same degenerate shape at a size that needs several ticks to fully drain, proving the fix
        // converges rather than only clearing a single leftover.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backlogSize = EmptyContainerDetector.MaxFindingsPerTick + EmptyContainerDetector.RecentlyCreatedSlots * 3;
        var backlog = Enumerable.Range(0, backlogSize)
            .Select(_ => MakeContainer(BulkFromDate, today.AddDays(365)))
            .ToList();
        await _context.Shift.AddRangeAsync(backlog);
        await _context.SaveChangesAsync();

        var seenIds = new HashSet<Guid>();
        const int maxTicks = 20;
        for (var tick = 0; tick < maxTicks && seenIds.Count < backlogSize; tick++)
        {
            var events = (await _sut.DetectAsync()).Cast<EmptyContainerTriggerEvent>().ToList();
            var newIds = events.Select(e => e.ShiftId).Where(id => seenIds.Add(id)).ToArray();
            await MarkOpenInLedgerAsync(newIds);
        }

        Assert.That(seenIds, Has.Count.EqualTo(backlogSize),
            "every backlog candidate must eventually reach the ledger, even when they all share one FromDate");
    }

    [Test]
    public async Task DetectAsync_FewerCandidatesThanTheCap_ReportsEachExactlyOnce()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var containers = Enumerable.Range(0, 5)
            .Select(_ => MakeContainer(BulkFromDate, today.AddDays(365)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers);
        await _context.SaveChangesAsync();

        await WaitForTheClockToAdvanceAsync();

        var createdLater = MakeContainer(today, today.AddDays(365));
        await _context.Shift.AddAsync(createdLater);
        await _context.SaveChangesAsync();

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(containers.Count + 1));
    }

    [Test]
    public async Task DetectAsync_ContainerInOneGroup_CarriesThatGroup()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();
        var groupId = Guid.NewGuid();
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (container.Id, new[] { groupId }));

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(emptyContainerEvent.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ContainerInTwoGroups_CarriesBoth_NotOnlyTheFirst()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (container.Id, new[] { firstGroupId, secondGroupId }));

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.GroupIds, Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task DetectAsync_ContainerWithoutAnyGroup_CarriesNoGroup_AndStaysGroupScopeRequired()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var container = MakeContainer(today.AddDays(-1), today.AddDays(30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.GroupIds, Is.Empty);
        Assert.That(emptyContainerEvent.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ManyContainers_ResolvesGroupsInOneBatchedLookup_NeverOnePerContainer()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var containers = Enumerable.Range(0, 10)
            .Select(_ => MakeContainer(today.AddDays(-1), today.AddDays(30)))
            .ToList();
        await _context.Shift.AddRangeAsync(containers);
        await _context.SaveChangesAsync();

        await _sut.DetectAsync();

        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). A container starting 2026-06-28 is already
        // active under the (correct) company day but not yet active under the (wrong) UTC day -
        // crossing IsPeriodActive's High/Medium severity boundary.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new EmptyContainerDetector(
            _shiftRepository, _containerTemplateRepository, _groupScopeReader, _agentConditionRepository,
            clock, Substitute.For<ILogger<EmptyContainerDetector>>());
        var container = MakeContainer(new DateOnly(2026, 6, 28), new DateOnly(2026, 6, 30));
        await _context.Shift.AddAsync(container);
        await _context.SaveChangesAsync();

        var emptyContainerEvent = (EmptyContainerTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(emptyContainerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.High),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }
}
