// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TargetHoursDriftDetector — verifies threshold gating, severity mapping
/// from drift magnitude (≥12h medium, ≥24h high), that the scanned period is the last
/// completed calendar month rather than the running one, and that GetActiveFingerprintsAsync
/// stays in lockstep with DetectAsync since this detector carries no cap.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TargetHoursDriftDetectorTests
{
    private IClientRepository _clientRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private TargetHoursDriftDetector _sut = null!;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _activityProbe.HasAnyWorkInRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = CreateSut(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
    }

    private TargetHoursDriftDetector CreateSut(DateTimeOffset now) =>
        new(_clientRepository, _workRepository, _activityProbe,
            NullLogger<TargetHoursDriftDetector>.Instance, new FixedTimeProvider(now));

    private static Client MakeClient(string firstName = "Anna", EntityTypeEnum type = EntityTypeEnum.Employee) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = firstName,
        Name = "Müller",
        Type = type
    };

    private void SetupClients(params Client[] clients) =>
        _clientRepository.GetQuery().Returns(new TestAsyncEnumerable<Client>(clients));

    [Test]
    public async Task DetectAsync_NoClients_ReturnsEmpty()
    {
        SetupClients();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WithinThreshold_Skips()
    {
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 152, GuaranteedHours = 160 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_NegativeDrift_OverThreshold_EmitsHigh()
    {
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 130, GuaranteedHours = 160 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var drift = events.Single() as TargetHoursDriftTriggerEvent;
        Assert.That(drift!.DriftHours, Is.EqualTo(-30m));
        Assert.That(drift.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_CustomerWithDrift_Skips()
    {
        var customer = MakeClient("Clara", EntityTypeEnum.Customer);
        SetupClients(customer);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [customer.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_MixedRoster_EmitsForStaffOnly()
    {
        var customer = MakeClient("Clara", EntityTypeEnum.Customer);
        var employee = MakeClient("Anna");
        var externEmp = MakeClient("Matteo", EntityTypeEnum.ExternEmp);
        SetupClients(customer, employee, externEmp);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [customer.Id] = new() { Hours = 0, GuaranteedHours = 170 },
                [employee.Id] = new() { Hours = 0, GuaranteedHours = 170 },
                [externEmp.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        var clientIds = events.Cast<TargetHoursDriftTriggerEvent>().Select(e => e.ClientId).ToList();
        Assert.That(clientIds, Is.EquivalentTo(new[] { employee.Id, externEmp.Id }));
    }

    [Test]
    public async Task DetectAsync_StaffWithoutContract_StillEmits()
    {
        var employee = MakeClient();
        SetupClients(employee);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [employee.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DetectAsync_NoGuaranteedHours_Skips()
    {
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 100, GuaranteedHours = 0 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScansLastCompletedMonth_NotTheRunningOne()
    {
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        await _workRepository.Received(1).GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(),
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        var drift = events.Single() as TargetHoursDriftTriggerEvent;
        Assert.That(drift!.PeriodLabel, Is.EqualTo("2026-07"));
    }

    [Test]
    public async Task DetectAsync_InJanuary_ScansDecemberOfPreviousYear()
    {
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });
        var sut = CreateSut(new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.Zero));

        var events = await sut.DetectAsync();

        await _workRepository.Received(1).GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(),
            new DateOnly(2025, 12, 1),
            new DateOnly(2025, 12, 31),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        var drift = events.Single() as TargetHoursDriftTriggerEvent;
        Assert.That(drift!.PeriodLabel, Is.EqualTo("2025-12"));
    }

    [Test]
    public async Task DetectAsync_DeletedClient_IsExcludedFromFingerprintsToo()
    {
        var deleted = MakeClient();
        deleted.IsDeleted = true;
        SetupClients(deleted);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [deleted.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(events, Is.Empty);
        Assert.That(fingerprints, Is.Empty);
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NoClients_ReturnsEmpty()
    {
        SetupClients();

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_MatchesDetectAsync_SinceThisDetectorHasNoCap()
    {
        var overThreshold = MakeClient("Anna");
        var withinThreshold = MakeClient("Bruno");
        var customer = MakeClient("Clara", EntityTypeEnum.Customer);
        SetupClients(overThreshold, withinThreshold, customer);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [overThreshold.Id] = new() { Hours = 100, GuaranteedHours = 160 },
                [withinThreshold.Id] = new() { Hours = 158, GuaranteedHours = 160 },
                [customer.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        var expectedFingerprints = events
            .Select(AgentConditionLedgerPolicy.FingerprintFor)
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(fingerprints, Is.EquivalentTo(expectedFingerprints));
        Assert.That(fingerprints, Has.Count.EqualTo(1));
        Assert.That(fingerprints.Single(), Is.EqualTo(
            AgentConditionLedgerPolicy.FingerprintFor(
                AgentTriggerKinds.TargetHoursDrift,
                TargetHoursDriftTriggerEvent.DedupKeyFor(overThreshold.Id, "2026-07"))));
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_UsesSameLastCompletedMonthAsDetectAsync()
    {
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        await _sut.GetActiveFingerprintsAsync();

        await _workRepository.Received(1).GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(),
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_PeriodWithoutAnyWork_EmitsNothing()
    {
        _activityProbe.HasAnyWorkInRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_PeriodWithoutAnyWork_ReportsNothing()
    {
        _activityProbe.HasAnyWorkInRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var client = MakeClient();
        SetupClients(client);
        _workRepository.GetPeriodHoursForClients(
            Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [client.Id] = new() { Hours = 0, GuaranteedHours = 170 }
            });

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }
}
