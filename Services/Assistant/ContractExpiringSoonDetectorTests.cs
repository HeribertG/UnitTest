// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ContractExpiringSoonDetector — covers no-expiring, expiring without
/// follow-up, expiring with follow-up (skip), and severity mapping per daysUntilExpiry.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ContractExpiringSoonDetectorTests
{
    private IClientContractReadRepository _repo = null!;
    private ContractExpiringSoonDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientContractReadRepository>();
        _sut = new ContractExpiringSoonDetector(
            _repo, new FixedCompanyClock(DateTimeOffset.UtcNow), NullLogger<ContractExpiringSoonDetector>.Instance);
    }

    private static ClientContract MakeContract(Guid clientId, DateOnly? untilDate, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = clientId,
        ContractId = Guid.NewGuid(),
        FromDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-365)),
        UntilDate = untilDate,
        IsActive = true,
        Client = new Client { Id = clientId, FirstName = "Max", Name = "Müller" }
    };

    [Test]
    public async Task DetectAsync_NoExpiring_ReturnsEmpty()
    {
        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ExpiringWithoutFollowUp_EmitsEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var clientId = Guid.NewGuid();
        var contract = MakeContract(clientId, today.AddDays(5));

        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _repo.GetContractsForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract }.ToLookup(c => c.ClientId));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var expiring = events.Single() as ContractExpiringSoonTriggerEvent;
        Assert.That(expiring!.DaysUntilExpiry, Is.EqualTo(5));
        Assert.That(expiring.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_ExpiringWithFollowUp_Skips()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var clientId = Guid.NewGuid();
        var expiringContract = MakeContract(clientId, today.AddDays(5));
        var followUp = MakeContract(clientId, today.AddDays(400));
        followUp.FromDate = today.AddDays(5);

        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { expiringContract });
        _repo.GetContractsForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { expiringContract, followUp }.ToLookup(c => c.ClientId));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ExpiringIn25Days_HasMediumSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var clientId = Guid.NewGuid();
        var contract = MakeContract(clientId, today.AddDays(25));

        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _repo.GetContractsForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract }.ToLookup(c => c.ClientId));

        var events = await _sut.DetectAsync();

        var expiring = events.Single() as ContractExpiringSoonTriggerEvent;
        Assert.That(expiring!.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). A contract expiring on 2026-07-02 is 4 days
        // out under the (correct) company day but 5 days out under the (wrong) UTC day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new ContractExpiringSoonDetector(_repo, clock, NullLogger<ContractExpiringSoonDetector>.Instance);
        var clientId = Guid.NewGuid();
        var contract = MakeContract(clientId, new DateOnly(2026, 7, 2));
        _repo.GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _repo.GetContractsForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract }.ToLookup(c => c.ClientId));

        var events = await _sut.DetectAsync();

        var expiring = events.Single() as ContractExpiringSoonTriggerEvent;
        Assert.That(expiring!.DaysUntilExpiry, Is.EqualTo(4),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }
}
