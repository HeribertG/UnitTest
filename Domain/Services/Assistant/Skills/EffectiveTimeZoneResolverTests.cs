// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EffectiveTimeZoneResolver: a valid explicit override wins, while an empty or invalid
/// one falls through to the company's configured zone (ICompanyClock) - never a hard-coded regional
/// default and never TimeZoneInfo.Local.
/// </summary>

using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class EffectiveTimeZoneResolverTests
{
    private static EffectiveTimeZoneResolver CreateSut(string companyZoneId)
        => new(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(companyZoneId)));

    [Test]
    public async Task ValidExplicitOverride_WinsOverTheCompanyZone()
    {
        var sut = CreateSut("Europe/Zurich");

        var (zone, id) = await sut.ResolveAsync("Asia/Tokyo");

        id.ShouldBe("Asia/Tokyo");
        zone.Id.ShouldBe("Asia/Tokyo");
    }

    [Test]
    public async Task EmptyOverride_FallsThroughToTheCompanyZone()
    {
        var sut = CreateSut("Asia/Kolkata");

        var (zone, id) = await sut.ResolveAsync(string.Empty);

        id.ShouldBe("Asia/Kolkata");
        zone.Id.ShouldBe("Asia/Kolkata");
    }

    [Test]
    public async Task NullOverride_FallsThroughToTheCompanyZone()
    {
        var sut = CreateSut("America/St_Johns");

        var (zone, id) = await sut.ResolveAsync(null);

        id.ShouldBe("America/St_Johns");
        zone.Id.ShouldBe("America/St_Johns");
    }

    [Test]
    public async Task InvalidOverride_FallsThroughToTheCompanyZone()
    {
        var sut = CreateSut("Pacific/Auckland");

        var (zone, id) = await sut.ResolveAsync("Not/AZone");

        id.ShouldBe("Pacific/Auckland");
        zone.Id.ShouldBe("Pacific/Auckland");
    }

    [Test]
    public async Task WindowsCompanyZone_ReturnsTheIanaId_NotTheWindowsId()
    {
        var sut = new EffectiveTimeZoneResolver(
            new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time")));

        var (_, id) = await sut.ResolveAsync(null);

        id.ShouldBe("Europe/Berlin");
    }

    [Test]
    public async Task WindowsExplicitOverride_ReturnsTheIanaId_NotTheWindowsId()
    {
        var sut = CreateSut("Asia/Kolkata");

        var (_, id) = await sut.ResolveAsync("W. Europe Standard Time");

        id.ShouldBe("Europe/Berlin");
    }
}
