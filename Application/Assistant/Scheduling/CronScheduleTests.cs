// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CronSchedule: validation, DST-correct next-occurrence resolution in a named time
/// zone, and local-time formatting.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;

namespace Klacks.UnitTest.Application.Assistant.Scheduling;

[TestFixture]
public class CronScheduleTests
{
    private const string Zurich = "Europe/Zurich";

    [Test]
    public void IsValidExpression_AcceptsStandardFiveField()
    {
        CronSchedule.IsValidExpression("0 8 * * 1").ShouldBeTrue();
    }

    [TestCase("not a cron")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("99 99 * * *")]
    public void IsValidExpression_RejectsInvalid(string? expression)
    {
        CronSchedule.IsValidExpression(expression).ShouldBeFalse();
    }

    [Test]
    public void GetNextOccurrenceUtc_MondayEightLocal_IsSixUtcInSummer()
    {
        var from = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc);

        var next = CronSchedule.GetNextOccurrenceUtc("0 8 * * 1", Zurich, from);

        next.ShouldNotBeNull();
        next!.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        next.Value.Kind.ShouldBe(DateTimeKind.Utc);
        next.Value.Hour.ShouldBe(6);
    }

    [Test]
    public void GetNextOccurrenceUtc_MondayEightLocal_IsSevenUtcInWinter()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = CronSchedule.GetNextOccurrenceUtc("0 8 * * 1", Zurich, from);

        next.ShouldNotBeNull();
        next!.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        next.Value.Hour.ShouldBe(7);
    }

    [Test]
    public void GetNextOccurrenceUtc_ReturnsNull_ForInvalidExpressionOrZone()
    {
        var from = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc);

        CronSchedule.GetNextOccurrenceUtc("nonsense", Zurich, from).ShouldBeNull();
        CronSchedule.GetNextOccurrenceUtc("0 8 * * 1", "Mars/Olympus", from).ShouldBeNull();
    }

    [Test]
    public void FormatLocal_RendersZoneAndLocalHour()
    {
        var utc = new DateTime(2026, 6, 29, 6, 0, 0, DateTimeKind.Utc);

        var text = CronSchedule.FormatLocal(utc, Zurich);

        text.ShouldContain(Zurich);
        text.ShouldContain("08:00");
    }
}
