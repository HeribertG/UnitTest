// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodCloseDueDetector — covers period-end computation per PaymentInterval
/// and the "already sealed" skip path.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseDueDetectorTests
{
    private IGroupRepository _groupRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private PeriodCloseDueDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _activityProbe.HasWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        StubWeekStart(DayOfWeek.Monday);
        _sut = CreateSut(new DateOnly(2026, 1, 10));
    }

    private void StubWeekStart(DayOfWeek weekStartDay)
    {
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var date = ci.Arg<DateOnly>();
                var offset = ((int)date.DayOfWeek - (int)weekStartDay + 7) % 7;
                return date.AddDays(-offset);
            });
    }

    private PeriodCloseDueDetector CreateSut(DateOnly today)
    {
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new PeriodCloseDueDetector(_groupRepository, _sealedDayRepository, _weekConfiguration,
            _activityProbe, NullLogger<PeriodCloseDueDetector>.Instance, tp);
    }

    private void StubGroups(List<Group> groups)
    {
        _groupRepository.List().Returns(groups);
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(groups.Select(group => group.Id).ToList());
    }

    private static Group MakeGroup(PaymentInterval interval, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = DateTime.UtcNow.Date
    };

    [Test]
    public async Task DetectAsync_NoGroups_ReturnsEmpty()
    {
        StubGroups(new List<Group>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_IndividualInterval_AlwaysSkipped()
    {
        StubGroups(new List<Group> { MakeGroup(PaymentInterval.Individual) });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _sealedDayRepository.DidNotReceiveWithAnyArgs().GetRangeAsync(default, default, default, default);
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_NotInWindow_Skips()
    {
        // SetUp date is 2026-01-10, which is 21 days before month-end — outside the 3-day warn window
        StubGroups(new List<Group> { MakeGroup(PaymentInterval.Monthly) });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_DefaultMondayWeekStart_PeriodEndsOnSunday()
    {
        var group = MakeGroup(PaymentInterval.Weekly);
        StubGroups(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodCloseDueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 11)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(1));
        Assert.That(evt.ActionRoute, Is.EqualTo(ProactiveActionRoutes.PeriodClosing));
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_ConfiguredSundayWeekStart_PeriodEndsOnSaturday()
    {
        var group = MakeGroup(PaymentInterval.Weekly);
        StubGroups(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        StubWeekStart(DayOfWeek.Sunday);
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodCloseDueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 10)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(0));
    }

    [Test]
    public async Task DetectAsync_MonthlyTargetHoursGroup_WithinWindow_EmitsSamePeriodEndAsMonthly()
    {
        var group = MakeGroup(PaymentInterval.MonthlyTargetHours);
        StubGroups(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _sut = CreateSut(new DateOnly(2026, 1, 29));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodCloseDueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 31)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(2));
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_AlreadySealedAtEnd_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), group.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Id = Guid.NewGuid() } });
        _sut = CreateSut(new DateOnly(2026, 1, 28));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_GroupWithoutClientsOrShifts_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        _sut = CreateSut(new DateOnly(2026, 1, 30));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_PeriodWithoutAnyWork_EmitsNothing()
    {
        _activityProbe.HasWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        StubGroups(new List<Group> { MakeGroup(PaymentInterval.Monthly) });
        _sut = CreateSut(new DateOnly(2026, 1, 30));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
