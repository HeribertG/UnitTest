// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Deterministic ICompanyClock test double that returns a settable instant in a settable time zone.
/// Tests must use this instead of Substitute.For&lt;ICompanyClock&gt;() for the newer methods
/// (GetTimeZoneAsync/GetNowAsync/GetTodayDateAsync): an un-stubbed NSubstitute call returns a default
/// value (null TimeZoneInfo, default DateTimeOffset/DateOnly) instead of throwing, which makes a test
/// pass vacuously - green without exercising the real time-zone logic at all.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Settings;

public sealed class FixedCompanyClock : ICompanyClock
{
    public DateTimeOffset Now { get; set; }

    public TimeZoneInfo TimeZone { get; set; }

    public CompanyTimeZoneSource Source { get; set; }

    public FixedCompanyClock(DateTimeOffset now, TimeZoneInfo? timeZone = null, CompanyTimeZoneSource source = CompanyTimeZoneSource.Setting)
    {
        Now = now;
        TimeZone = timeZone ?? TimeZoneInfo.Utc;
        Source = source;
    }

    public Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TimeZone);

    public Task<CompanyTimeZoneResolution> GetTimeZoneResolutionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CompanyTimeZoneResolution(TimeZone, Source));

    public Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TimeZoneInfo.ConvertTime(Now, TimeZone));

    public Task<DateOnly> GetTodayDateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZone).Date));

    public Task<DateTime> GetTodayAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(Now, TimeZone).Date, DateTimeKind.Utc));
}
