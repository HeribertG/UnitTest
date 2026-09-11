// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetCompanyClockQueryHandler: maps ICompanyClock's resolved zone, source and today's date
/// onto CompanyClockResource without a second resolution pass.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Settings.CompanyClock;

using Klacks.Api.Application.Handlers.Settings.CompanyClock;
using Klacks.Api.Application.Queries.Settings.CompanyClock;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetCompanyClockQueryHandlerTests
{
    [Test]
    public async Task Handle_KolkataZoneFromSetting_ReturnsMatchingResource()
    {
        var companyClock = new FixedCompanyClock(
            DateTimeOffset.Parse("2026-06-27T20:00:00Z"),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"),
            CompanyTimeZoneSource.Setting);
        var handler = new GetCompanyClockQueryHandler(companyClock);

        var result = await handler.Handle(new GetCompanyClockQuery(), CancellationToken.None);

        result.TimeZone.ShouldBe("Asia/Kolkata");
        result.Today.ShouldBe(new DateOnly(2026, 6, 28));
        result.Source.ShouldBe(nameof(CompanyTimeZoneSource.Setting));
    }

    [Test]
    public async Task Handle_UtcFallback_ReturnsUtcSource()
    {
        var companyClock = new FixedCompanyClock(
            DateTimeOffset.Parse("2026-06-27T20:00:00Z"),
            TimeZoneInfo.Utc,
            CompanyTimeZoneSource.Utc);
        var handler = new GetCompanyClockQueryHandler(companyClock);

        var result = await handler.Handle(new GetCompanyClockQuery(), CancellationToken.None);

        result.TimeZone.ShouldBe("UTC");
        result.Source.ShouldBe(nameof(CompanyTimeZoneSource.Utc));
    }

    [Test]
    public async Task Handle_WindowsZoneIdFromSetting_ReturnsTheIanaIdNotTheWindowsId()
    {
        var windowsZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var companyClock = new FixedCompanyClock(
            DateTimeOffset.Parse("2026-06-27T20:00:00Z"),
            windowsZone,
            CompanyTimeZoneSource.Setting);
        var handler = new GetCompanyClockQueryHandler(companyClock);

        var result = await handler.Handle(new GetCompanyClockQuery(), CancellationToken.None);

        result.TimeZone.ShouldBe("Europe/Berlin");
        result.TimeZone.ShouldNotBe(windowsZone.Id);
    }
}
