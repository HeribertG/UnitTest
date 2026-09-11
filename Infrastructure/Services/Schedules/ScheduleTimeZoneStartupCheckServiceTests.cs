// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleTimeZoneStartupCheckService: it must warn once at startup when
/// Schedule:DstAware is enabled and Schedule:TimeZoneId disagrees with the company's own configured
/// zone, stay silent when they agree or DstAware is off, and never let a resolution failure escape and
/// block application startup.
/// </summary>

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ScheduleTimeZoneStartupCheckServiceTests
{
    private static IServiceScopeFactory ScopeFactoryFor(ICompanyClock companyClock)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => companyClock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Test]
    public async Task DstAwareWithADifferentScheduleZone_LogsAWarning()
    {
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var options = Options.Create(new ScheduleTimeOptions { DstAware = true, TimeZoneId = "Asia/Kolkata" });
        var logger = new RecordingLogger<ScheduleTimeZoneStartupCheckService>();
        var service = new ScheduleTimeZoneStartupCheckService(ScopeFactoryFor(companyClock), options, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Asia/Kolkata", StringComparison.Ordinal)
            && e.Message.Contains("Europe/Zurich", StringComparison.Ordinal));
    }

    [Test]
    public async Task DstAwareWithTheSameScheduleZone_LogsNoWarning()
    {
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var options = Options.Create(new ScheduleTimeOptions { DstAware = true, TimeZoneId = "Europe/Zurich" });
        var logger = new RecordingLogger<ScheduleTimeZoneStartupCheckService>();
        var service = new ScheduleTimeZoneStartupCheckService(ScopeFactoryFor(companyClock), options, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Test]
    public async Task DstAwareDisabled_NeverOpensAScope()
    {
        var options = Options.Create(new ScheduleTimeOptions { DstAware = false, TimeZoneId = "Asia/Kolkata" });
        var logger = new RecordingLogger<ScheduleTimeZoneStartupCheckService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = new ScheduleTimeZoneStartupCheckService(scopeFactory, options, logger);

        await service.StartAsync(CancellationToken.None);

        scopeFactory.DidNotReceive().CreateScope();
        logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public async Task ScopeResolutionThrows_IsSwallowed_AndNeverBlocksStartup()
    {
        var options = Options.Create(new ScheduleTimeOptions { DstAware = true, TimeZoneId = "Asia/Kolkata" });
        var logger = new RecordingLogger<ScheduleTimeZoneStartupCheckService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("scope unavailable"));
        var service = new ScheduleTimeZoneStartupCheckService(scopeFactory, options, logger);

        await service.StartAsync(CancellationToken.None);

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning);
    }
}
