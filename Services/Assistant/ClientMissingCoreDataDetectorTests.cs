// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientMissingCoreDataDetector — covers the empty result, the defensive
/// skip when nothing is missing, one event per missing field with the contracted severity
/// and dedup key, and the per-tick emission cap.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ClientMissingCoreDataDetectorTests
{
    private IClientCoreDataReadRepository _repo = null!;
    private ClientMissingCoreDataDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientCoreDataReadRepository>();
        _sut = CreateSut(new DateOnly(2026, 1, 15));
    }

    private ClientMissingCoreDataDetector CreateSut(DateOnly today)
    {
        var clock = new FixedCompanyClock(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new ClientMissingCoreDataDetector(_repo, NullLogger<ClientMissingCoreDataDetector>.Instance, clock);
    }

    private void StubStatuses(params ClientCoreDataStatus[] statuses)
    {
        _repo.GetActiveClientsWithMissingCoreDataAsync(Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(statuses.ToList());
    }

    [Test]
    public async Task DetectAsync_NoClientsWithGaps_ReturnsEmpty()
    {
        StubStatuses();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _repo.Received(1).GetActiveClientsWithMissingCoreDataAsync(
            new DateOnly(2026, 1, 15), ClientMissingCoreDataDetector.MaxFindingsPerTick, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_ClientWithCompleteCoreData_EmitsNothing()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", true, true));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ClientWithoutActiveAddress_EmitsMediumAddressEvent()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", false, true));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (ClientMissingCoreDataTriggerEvent)events[0];
        Assert.That(evt.MissingField, Is.EqualTo(ClientMissingCoreDataTriggerEvent.AddressField));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(evt.DedupKey, Is.EqualTo($"{clientId}:address"));
        Assert.That(evt.SummaryParams["name"], Is.EqualTo("Max Müller"));
    }

    [Test]
    public async Task DetectAsync_ClientWithoutEmailAndPhone_EmitsLowContactEvent()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", true, false));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (ClientMissingCoreDataTriggerEvent)events[0];
        Assert.That(evt.MissingField, Is.EqualTo(ClientMissingCoreDataTriggerEvent.ContactField));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
        Assert.That(evt.DedupKey, Is.EqualTo($"{clientId}:contact"));
    }

    [Test]
    public async Task DetectAsync_ClientMissingBoth_EmitsTwoEventsWithDistinctDedupKeys()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", false, false));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events.Select(e => e.DedupKey).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task DetectAsync_MoreFindingsThanCap_EmitsAtMostMaxFindingsPerTick()
    {
        var statuses = Enumerable.Range(0, ClientMissingCoreDataDetector.MaxFindingsPerTick)
            .Select(i => new ClientCoreDataStatus(Guid.NewGuid(), "Max", $"Müller{i}", false, false))
            .ToArray();
        StubStatuses(statuses);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(ClientMissingCoreDataDetector.MaxFindingsPerTick));
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). Passing the company day, not the UTC day,
        // as the reference date is what this test pins.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new ClientMissingCoreDataDetector(_repo, NullLogger<ClientMissingCoreDataDetector>.Instance, clock);
        StubStatuses();

        await _sut.DetectAsync();

        await _repo.Received(1).GetActiveClientsWithMissingCoreDataAsync(
            new DateOnly(2026, 6, 28), ClientMissingCoreDataDetector.MaxFindingsPerTick, Arg.Any<CancellationToken>());
    }
}
