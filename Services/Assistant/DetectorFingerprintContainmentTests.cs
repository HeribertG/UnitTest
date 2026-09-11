// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the one invariant that makes IAgentConditionFingerprintSource safe to hand to
/// MarkResolvedAsync: for every detector that implements it, the fingerprints of the events
/// DetectAsync returns must all be contained in the set GetActiveFingerprintsAsync returns. The
/// dangerous direction is a fingerprint set NARROWER than the event set - the tick would then resolve
/// rows it opened moments earlier and re-arm them as new ones on the next tick, which is exactly the
/// flapping the ledger exists to prevent. A superset is fine and expected, because DetectAsync is
/// capped and the fingerprint scan is not.
///
/// Every fixture here therefore seeds MORE findings than the detector's cap, so the two paths really
/// do return different sizes and the containment assertion has something to prove. Substituted
/// repositories mimic their real counterparts' cap semantics (Take(maxResults) / RowCount over unique
/// shift ids) rather than ignoring the argument, otherwise the capped path would silently return
/// everything and the test would pass vacuously.
///
/// Scope note - what this does NOT cover: it cannot prove the two paths agree against the real
/// database, only that they agree over the same in-memory candidate set. A predicate that EF Core
/// translates differently in the two shapes would slip through. The IntegrationTest suite is the place
/// for that, and it does not cover it today.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.DTOs.Assistant;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class DetectorFingerprintContainmentTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Test]
    public async Task OpenOrderDetector_EmittedEventsAreAllCoveredByTheFingerprintScan()
    {
        var repository = Substitute.For<IShiftRepository>();
        var orders = Enumerable.Range(0, OpenOrderDetector.MaxCandidatesToScan + 5)
            .Select(offset => new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Order",
                Status = ShiftStatus.OriginalOrder,
                FromDate = Today.AddDays(offset % 60)
            })
            .ToList();
        repository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(orders));

        var sut = new OpenOrderDetector(repository, ShiftGroupScopeReaderStub.WithoutAnyGroups(), FixedClock(), NullLogger<OpenOrderDetector>.Instance);

        await AssertContainmentAsync(sut, sut, expectedCappedCount: OpenOrderDetector.MaxCandidatesToScan);
    }

    [Test]
    public async Task UncutFullDayShiftDetector_EmittedEventsAreAllCoveredByTheFingerprintScan()
    {
        var repository = Substitute.For<IShiftRepository>();
        var time = new TimeOnly(7, 0);
        var shifts = Enumerable.Range(0, UncutFullDayShiftDetector.MaxFindingsPerTick + 40)
            .Select(offset => new Shift
            {
                Id = Guid.NewGuid(),
                Name = "24h",
                Abbreviation = "24H",
                Status = ShiftStatus.OriginalShift,
                ShiftType = ShiftType.IsTask,
                FromDate = Today.AddDays(offset),
                StartShift = time,
                EndShift = time
            })
            .ToList();
        repository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(shifts));

        var sut = new UncutFullDayShiftDetector(repository, ShiftGroupScopeReaderStub.WithoutAnyGroups(), FixedClock(), NullLogger<UncutFullDayShiftDetector>.Instance);

        await AssertContainmentAsync(sut, sut, expectedCappedCount: UncutFullDayShiftDetector.MaxFindingsPerTick);
    }

    [Test]
    public async Task EmptyContainerDetector_EmittedEventsAreAllCoveredByTheFingerprintScan()
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        var templateRepository = Substitute.For<IContainerTemplateRepository>();

        var containers = Enumerable.Range(0, EmptyContainerDetector.MaxFindingsPerTick + 10)
            .Select(offset => new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container",
                Status = ShiftStatus.OriginalShift,
                ShiftType = ShiftType.IsContainer,
                FromDate = Today.AddDays(offset)
            })
            .ToList();

        var containerWithTemplate = containers[0];
        shiftRepository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(containers));
        templateRepository.GetQuery().Returns(new TestAsyncEnumerable<ContainerTemplate>(
            new List<ContainerTemplate> { new() { Id = Guid.NewGuid(), ContainerId = containerWithTemplate.Id } }));

        // All candidates already open in the ledger: isolates this test to the first slice's own cap,
        // which is what expectedCappedCount below asserts -- the second slice's ledger-exclusion
        // behaviour has its own coverage in EmptyContainerDetectorTests.
        var agentConditionRepository = Substitute.For<IAgentConditionRepository>();
        agentConditionRepository.GetOpenByKindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(containers.Select(container => new AgentCondition { EntityId = container.Id }).ToList());

        var sut = new EmptyContainerDetector(
            shiftRepository, templateRepository, ShiftGroupScopeReaderStub.WithoutAnyGroups(),
            agentConditionRepository, FixedClock(), NullLogger<EmptyContainerDetector>.Instance);

        var fingerprints = await AssertContainmentAsync(
            sut, sut, expectedCappedCount: EmptyContainerDetector.MaxFindingsPerTick);

        fingerprints.ShouldNotContain(
            AgentConditionLedgerPolicy.FingerprintFor(
                sut.Kind, EmptyContainerTriggerEvent.DedupKeyFor(containerWithTemplate.Id)),
            "The anti-join against ContainerTemplate must apply to the fingerprint scan too, "
            + "or a container that already has a template would keep a ledger row alive forever.");
    }

    [Test]
    public async Task UnstaffedShift7dDetector_EmittedEventsAreAllCoveredByTheFingerprintScan()
    {
        const int cappedShiftCount = 3;
        var repository = Substitute.For<IShiftScheduleRepository>();
        var assignments = Enumerable.Range(0, 20)
            .Select(offset => new ShiftDayAssignment
            {
                ShiftId = Guid.NewGuid(),
                Date = Today.AddDays(offset % 7),
                Quantity = 2,
                SumEmployees = 0
            })
            .ToList();

        repository.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var rowCount = call.ArgAt<ShiftScheduleFilter>(0).RowCount;
                var pagedShiftIds = assignments
                    .Select(assignment => assignment.ShiftId)
                    .Distinct()
                    .Take(rowCount == int.MaxValue ? int.MaxValue : cappedShiftCount)
                    .ToHashSet();

                return (assignments.Where(a => pagedShiftIds.Contains(a.ShiftId)).ToList(), pagedShiftIds.Count);
            });

        var sut = new UnstaffedShift7dDetector(repository, ShiftGroupScopeReaderStub.WithoutAnyGroups(), FixedClock(), NullLogger<UnstaffedShift7dDetector>.Instance);

        await AssertContainmentAsync(sut, sut, expectedCappedCount: cappedShiftCount);
    }

    [Test]
    public async Task AvailabilityGapDetector_EmittedEventsAreAllCoveredByTheFingerprintScan()
    {
        var repository = Substitute.For<IClientAvailabilityReadRepository>();
        repository.AnyAvailabilityEntriesExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        var clients = Enumerable.Range(0, AvailabilityGapDetector.MaxFindingsPerTick + 12)
            .Select(index => new PlannableClientInfo(Guid.NewGuid(), "First" + index, "Last" + index))
            .ToList();

        repository.GetPlannableClientsWithoutAvailabilityAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => clients.Take(call.ArgAt<int>(2)).ToList());

        var sut = new AvailabilityGapDetector(
            repository, NullLogger<AvailabilityGapDetector>.Instance, FixedClock());

        await AssertContainmentAsync(sut, sut, expectedCappedCount: AvailabilityGapDetector.MaxFindingsPerTick);
    }

    [Test]
    public async Task ClientMissingCoreDataDetector_EmittedEventsAreAllCoveredByTheFingerprintScan()
    {
        var repository = Substitute.For<IClientCoreDataReadRepository>();

        var statuses = Enumerable.Range(0, ClientMissingCoreDataDetector.MaxFindingsPerTick + 12)
            .Select(index => new ClientCoreDataStatus(Guid.NewGuid(), "First" + index, "Last" + index, false, false))
            .ToList();

        repository.GetActiveClientsWithMissingCoreDataAsync(
                Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => statuses.Take(call.ArgAt<int>(1)).ToList());

        var sut = new ClientMissingCoreDataDetector(
            repository, NullLogger<ClientMissingCoreDataDetector>.Instance, FixedClock());

        var fingerprints = await AssertContainmentAsync(
            sut, sut, expectedCappedCount: ClientMissingCoreDataDetector.MaxFindingsPerTick);

        fingerprints.Count.ShouldBe(
            statuses.Count * 2,
            "Each seeded client is missing BOTH core data fields, so the uncapped scan must spell out two "
            + "fingerprints per client exactly as DetectAsync emits two events per client.");
    }

    private static FixedCompanyClock FixedClock() =>
        new(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

    private static async Task<IReadOnlySet<string>> AssertContainmentAsync(
        IAgentTriggerDetector detector,
        IAgentConditionFingerprintSource fingerprintSource,
        int expectedCappedCount)
    {
        var events = await detector.DetectAsync();
        var fingerprints = await fingerprintSource.GetActiveFingerprintsAsync();

        events.Count.ShouldBe(
            expectedCappedCount,
            $"{detector.GetType().Name}: the fixture must seed past the cap, otherwise both paths return "
            + "the same set and the containment assertion below proves nothing.");

        fingerprints.Count.ShouldBeGreaterThan(
            events.Count,
            $"{detector.GetType().Name}: the uncapped fingerprint scan must see more than the capped "
            + "DetectAsync, otherwise the cap is not actually being bypassed.");

        var uncovered = events
            .Select(AgentConditionLedgerPolicy.FingerprintFor)
            .Where(fingerprint => !fingerprints.Contains(fingerprint))
            .ToList();

        uncovered.ShouldBeEmpty(
            $"{detector.GetType().Name}: DetectAsync emitted findings whose fingerprints the scan does not "
            + "know. MarkResolvedAsync would resolve the rows this very tick opened and re-arm them on the "
            + "next one. The two paths have drifted apart - either in their predicates or in how they "
            + "spell the dedup key.");

        return fingerprints;
    }
}
