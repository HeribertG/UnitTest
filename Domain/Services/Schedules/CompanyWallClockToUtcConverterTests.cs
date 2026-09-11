// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CompanyWallClockToUtcConverter: DST-safe conversion of a company-local wall-clock
/// value to UTC, covering the ordinary case plus both Europe/Zurich 2026 transition edge cases -
/// the spring-forward gap (29.03.2026, clocks jump 02:00 -> 03:00) and the fall-back ambiguity
/// (25.10.2026, 02:00-03:00 occurs twice).
/// </summary>

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class CompanyWallClockToUtcConverterTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Test]
    public void ConvertToUtc_OrdinaryWinterTime_UsesStandardOffset()
    {
        var wallClock = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Unspecified);

        var utc = CompanyWallClockToUtcConverter.ConvertToUtc(wallClock, Zurich);

        utc.ShouldBe(new DateTime(2026, 1, 15, 7, 0, 0, DateTimeKind.Utc));
        utc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void ConvertToUtc_OrdinarySummerTime_UsesDaylightOffset()
    {
        var wallClock = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Unspecified);

        var utc = CompanyWallClockToUtcConverter.ConvertToUtc(wallClock, Zurich);

        utc.ShouldBe(new DateTime(2026, 7, 15, 6, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void ConvertToUtc_SpringForwardGap_AdvancesToNextValidLocalTime()
    {
        // 2026-03-29 02:30 does not exist in Europe/Zurich: clocks jump from 02:00 CET straight to
        // 03:00 CEST. The converter must advance to the next valid local time (03:00) instead of
        // throwing, then convert that (now-valid, CEST/+2) time to UTC.
        var wallClock = new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Unspecified);
        Zurich.IsInvalidTime(wallClock).ShouldBeTrue("test setup: 2026-03-29 02:30 must be an invalid Zurich local time");

        var utc = CompanyWallClockToUtcConverter.ConvertToUtc(wallClock, Zurich);

        utc.ShouldBe(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void ConvertToUtc_FallBackAmbiguity_ResolvesToTheEarlierDaylightSavingOccurrence()
    {
        // 2026-10-25 02:30 occurs twice in Europe/Zurich: first at 02:30 CEST (+2), then again at
        // 02:30 CET (+1) after the clocks fall back at 03:00 CEST -> 02:00 CET. The converter must
        // resolve to the first (daylight-saving, +2) occurrence, i.e. the earlier UTC instant.
        var wallClock = new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Unspecified);
        Zurich.IsAmbiguousTime(wallClock).ShouldBeTrue("test setup: 2026-10-25 02:30 must be an ambiguous Zurich local time");

        var utc = CompanyWallClockToUtcConverter.ConvertToUtc(wallClock, Zurich);

        utc.ShouldBe(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc));
    }

    [Test]
    public void ConvertToUtc_LocalKind_Throws()
    {
        var wallClock = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Local);

        Should.Throw<ArgumentException>(() => CompanyWallClockToUtcConverter.ConvertToUtc(wallClock, Zurich));
    }

    [Test]
    public void ConvertToUtc_UtcKind_Throws()
    {
        var wallClock = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);

        Should.Throw<ArgumentException>(() => CompanyWallClockToUtcConverter.ConvertToUtc(wallClock, Zurich));
    }
}
