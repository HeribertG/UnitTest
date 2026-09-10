// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for validate_address (read-only): geocodes the supplied address and reports exact
/// match, partial match (ZIP/city only) or not found; missing postal code / city yields guidance,
/// a postal code embedded in the city string is recovered, and geocoding failures degrade gracefully.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.RouteOptimization;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ValidateAddressSkillTests
{
    private IGeocodingService _geocodingService = null!;
    private ICountryResolver _countryResolver = null!;
    private ValidateAddressSkill _skill = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static Countries Switzerland() => new()
    {
        Abbreviation = "CH",
        Name = new MultiLanguage { De = "Schweiz", En = "Switzerland" }
    };

    [SetUp]
    public void SetUp()
    {
        _geocodingService = Substitute.For<IGeocodingService>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((Countries?)null);
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(Switzerland());
        _skill = new ValidateAddressSkill(_geocodingService, _countryResolver);
    }

    [Test]
    public async Task MissingPostalCodeAndCity_ReturnsGuidance_WithoutGeocoding()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Bahnhofstrasse 1"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("postal code");
        await _geocodingService.DidNotReceive()
            .ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ExactMatch_ReportsValidatedAddress()
    {
        _geocodingService.ValidateExactAddressAsync("Kirchstrasse 52", "3097", "Liebefeld", "Schweiz")
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                ExactMatch = true,
                State = "BE",
                ReturnedAddress = "Kirchstrasse 52, 3097 Liebefeld",
                Latitude = 46.93,
                Longitude = 7.42,
                MatchType = "exact"
            });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Kirchstrasse 52",
            ["zip"] = "3097",
            ["city"] = "Liebefeld"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("Address validated successfully");
        result.Message!.ShouldContain("BE");
    }

    [Test]
    public async Task FoundButNotExact_ReportsZipAndCityCorrect_StreetUnverified()
    {
        _geocodingService.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                ExactMatch = false,
                State = "ZH",
                ReturnedAddress = "8001 Zürich",
                MatchType = "postcode"
            });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Nonexistent Lane 999",
            ["zip"] = "8001",
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("could not be verified");
        result.Message!.ShouldContain("8001");
    }

    [Test]
    public async Task NotFound_ReportsAddressNotFound()
    {
        _geocodingService.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult
            {
                Found = false,
                ExactMatch = false,
                State = null,
                MatchType = "none"
            });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Nowhere 1",
            ["zip"] = "9999",
            ["city"] = "Atlantis"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("not found");
        result.Message!.ShouldContain("could not be mapped");
    }

    [Test]
    public async Task PostalCodeEmbeddedInCity_IsRecovered()
    {
        _geocodingService.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult { Found = true, ExactMatch = true, State = "BE" });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Kirchstrasse 52",
            ["city"] = "3097 Liebefeld"
        });

        result.Success.ShouldBeTrue();
        await _geocodingService.Received(1)
            .ValidateExactAddressAsync("Kirchstrasse 52", "3097", "Liebefeld", Arg.Any<string>());
    }

    [Test]
    public async Task GeocodingThrows_ReturnsGracefulFailure()
    {
        _geocodingService.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<GeocodingValidationResult>(_ => throw new HttpRequestException("Nominatim unreachable"));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Kirchstrasse 52",
            ["zip"] = "3097",
            ["city"] = "Liebefeld"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("Could not validate address");
    }

    [Test]
    public async Task GeocoderUnavailable_IsReportedAsNotChecked_NotAsNotFound()
    {
        _geocodingService.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult { Found = false, ServiceUnavailable = true, MatchType = "error" });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["street"] = "Kirchstrasse 52",
            ["zip"] = "3097",
            ["city"] = "Liebefeld"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("Could not validate address");
        result.Message!.ShouldNotContain("not found");
    }
}
