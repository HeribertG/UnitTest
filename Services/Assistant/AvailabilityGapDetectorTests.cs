// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AvailabilityGapDetector — covers the anti-spam guard (no availability
/// entries in the system), the empty result, severity per distance to month start, the
/// next-month window computation and the per-tick emission cap.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AvailabilityGapDetectorTests
{
    private IClientAvailabilityReadRepository _repo = null!;
    private AvailabilityGapDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientAvailabilityReadRepository>();
        _repo.AnyAvailabilityEntriesExistAsync(Arg.Any<CancellationToken>()).Returns(true);
        _sut = CreateSut(new DateOnly(2026, 1, 15));
    }

    private AvailabilityGapDetector CreateSut(DateOnly today)
    {
        var clock = new FixedCompanyClock(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new AvailabilityGapDetector(_repo, NullLogger<AvailabilityGapDetector>.Instance, clock);
    }

    private void StubClients(params PlannableClientInfo[] clients)
    {
        _repo.GetPlannableClientsWithoutAvailabilityAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(clients.ToList());
    }

    [Test]
    public async Task DetectAsync_NoAvailabilityEntriesInSystem_ReturnsEmptyWithoutClientScan()
    {
        _repo.AnyAvailabilityEntriesExistAsync(Arg.Any<CancellationToken>()).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _repo.DidNotReceiveWithAnyArgs().GetPlannableClientsWithoutAvailabilityAsync(default, default, default, default);
    }

    [Test]
    public async Task DetectAsync_NoClientsWithoutAvailability_ReturnsEmpty()
    {
        StubClients();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_MidMonth_EmitsMediumSeverityEventForNextCalendarMonth()
    {
        var clientId = Guid.NewGuid();
        StubClients(new PlannableClientInfo(clientId, "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (AvailabilityGapTriggerEvent)events[0];
        Assert.That(evt.PeriodStart, Is.EqualTo(new DateOnly(2026, 2, 1)));
        Assert.That(evt.PeriodEnd, Is.EqualTo(new DateOnly(2026, 2, 28)));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(evt.DedupKey, Is.EqualTo($"{clientId}:2026-02"));
        Assert.That(evt.SummaryParams["name"], Is.EqualTo("Max Müller"));
        await _repo.Received(1).GetPlannableClientsWithoutAvailabilityAsync(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), AvailabilityGapDetector.MaxFindingsPerTick, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_WithinSevenDaysOfMonthStart_EmitsHighSeverity()
    {
        _sut = CreateSut(new DateOnly(2026, 1, 27));
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). Next calendar month is July either way, so
        // daysUntilPeriodStart distinguishes the two: 4 under (wrong) UTC day, 3 under the (correct)
        // company day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new AvailabilityGapDetector(_repo, NullLogger<AvailabilityGapDetector>.Instance, clock);
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (AvailabilityGapTriggerEvent)events[0];
        Assert.That(evt.PeriodStart, Is.EqualTo(new DateOnly(2026, 7, 1)));
        Assert.That(evt.DaysUntilPeriodStart, Is.EqualTo(3),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }

    [Test]
    public async Task DetectAsync_MoreFindingsThanCap_EmitsAtMostMaxFindingsPerTick()
    {
        var clients = Enumerable.Range(0, AvailabilityGapDetector.MaxFindingsPerTick + 5)
            .Select(i => new PlannableClientInfo(Guid.NewGuid(), "Max", $"Müller{i}"))
            .ToArray();
        StubClients(clients);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(AvailabilityGapDetector.MaxFindingsPerTick));
    }
}
