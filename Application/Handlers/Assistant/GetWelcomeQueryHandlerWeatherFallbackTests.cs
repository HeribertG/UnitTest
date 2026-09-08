// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetWelcomeQueryHandlerWeatherFallbackTests
{
    private ISuggestionsRanker _suggestionsRanker = null!;
    private IOpenMeteoClient _weatherClient = null!;
    private ICompanyLocationProvider _companyLocationProvider = null!;
    private IOnboardingService _onboardingService = null!;
    private IPublicHolidayProvider _holidayProvider = null!;
    private IGreetingComposer _greetingComposer = null!;
    private IConfiguration _configuration = null!;
    private IWelcomeFocusResolver _welcomeFocusResolver = null!;
    private GetWelcomeQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _suggestionsRanker = Substitute.For<ISuggestionsRanker>();
        _suggestionsRanker
            .RankAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new List<string>());
        _weatherClient = Substitute.For<IOpenMeteoClient>();
        _companyLocationProvider = Substitute.For<ICompanyLocationProvider>();
        _onboardingService = Substitute.For<IOnboardingService>();
        _holidayProvider = Substitute.For<IPublicHolidayProvider>();
        _holidayProvider.GetUpcomingHolidayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UpcomingHoliday?)null);
        _greetingComposer = Substitute.For<IGreetingComposer>();
        _greetingComposer.ComposeAsync(Arg.Any<GreetingContext>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _configuration = new ConfigurationBuilder().Build();
        _welcomeFocusResolver = Substitute.For<IWelcomeFocusResolver>();
        _welcomeFocusResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WelcomeFocusResource?)null);
        _handler = new GetWelcomeQueryHandler(_suggestionsRanker, _weatherClient, _companyLocationProvider, _onboardingService, _holidayProvider, _greetingComposer, _configuration, _welcomeFocusResolver);
    }

    private GetWelcomeQueryHandler HandlerWith(IConfiguration configuration)
        => new(_suggestionsRanker, _weatherClient, _companyLocationProvider, _onboardingService, _holidayProvider, _greetingComposer, configuration, _welcomeFocusResolver);

    [Test]
    public async Task Handle_RequestHasBrowserCoordinates_UsesThemAndSkipsCompanyFallback()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(10.0, 20.0, Arg.Any<CancellationToken>()).Returns("weather.clear");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.WeatherKey.ShouldBe("weather.clear");
        await _companyLocationProvider.DidNotReceive().GetCompanyLocationAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoBrowserCoordinates_FallsBackToCompanyLocation()
    {
        var request = BuildRequest(latitude: null, longitude: null);
        _companyLocationProvider.GetCompanyLocationAsync(Arg.Any<CancellationToken>()).Returns(((double Latitude, double Longitude)?)(47.0, 8.0));
        _weatherClient.GetWeatherKeyAsync(47.0, 8.0, Arg.Any<CancellationToken>()).Returns("weather.company");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.WeatherKey.ShouldBe("weather.company");
    }

    [Test]
    public async Task Handle_NoBrowserCoordinatesAndNoCompanyLocation_LeavesWeatherKeyEmpty()
    {
        var request = BuildRequest(latitude: null, longitude: null);
        _companyLocationProvider.GetCompanyLocationAsync(Arg.Any<CancellationToken>()).Returns(((double Latitude, double Longitude)?)null);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.WeatherKey.ShouldBe(string.Empty);
        await _weatherClient.DidNotReceive().GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_PopulatesOnboardingFromService()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");
        var onboarding = new OnboardingResource { ShouldOffer = true, ShowCard = true, Status = "pending" };
        _onboardingService.GetStateAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(onboarding);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.Onboarding.ShouldNotBeNull();
        result.Onboarding!.ShouldOffer.ShouldBeTrue();
        result.Onboarding.Status.ShouldBe("pending");
    }

    [Test]
    public async Task Handle_HolidayTomorrow_SetsAmbientKeyAndName()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");
        _holidayProvider.GetUpcomingHolidayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UpcomingHoliday("Auffahrt", IsToday: false));

        var result = await _handler.Handle(request, CancellationToken.None);

        result.AmbientKey.ShouldBe("klacksy.welcome.ambient.holiday_tomorrow");
        result.AmbientHolidayName.ShouldBe("Auffahrt");
    }

    [Test]
    public async Task Handle_NoHoliday_LeavesAmbientEmpty()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.AmbientKey.ShouldBe(string.Empty);
        result.AmbientHolidayName.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_NoHolidayButPoorAir_SetsAirAmbientKey()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");
        _weatherClient.GetAirQualityAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns((int?)130);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.AmbientKey.ShouldBe("klacksy.welcome.ambient.air_poor");
    }

    [Test]
    public async Task Handle_NoHolidayAndGoodAir_LeavesAmbientEmpty()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");
        _weatherClient.GetAirQualityAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns((int?)15);

        var result = await _handler.Handle(request, CancellationToken.None);

        result.AmbientKey.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_GreetingLlmEnabled_SetsGreetingTextFromComposer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:Greeting:LlmEnabled"] = "true" })
            .Build();
        _greetingComposer.ComposeAsync(Arg.Any<GreetingContext>(), Arg.Any<CancellationToken>())
            .Returns("Guten Morgen, Max! Grauer Montag, aber die Luft ist frisch.");
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");

        var result = await HandlerWith(config).Handle(request, CancellationToken.None);

        result.GreetingText.ShouldBe("Guten Morgen, Max! Grauer Montag, aber die Luft ist frisch.");
    }

    [Test]
    public async Task Handle_GreetingLlmDisabledByDefault_LeavesGreetingTextNull()
    {
        var request = BuildRequest(latitude: 10.0, longitude: 20.0);
        _weatherClient.GetWeatherKeyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns("weather.clear");

        var result = await _handler.Handle(request, CancellationToken.None);

        result.GreetingText.ShouldBeNull();
        await _greetingComposer.DidNotReceive().ComposeAsync(Arg.Any<GreetingContext>(), Arg.Any<CancellationToken>());
    }

    private static GetWelcomeQuery BuildRequest(double? latitude, double? longitude)
    {
        return new GetWelcomeQuery
        {
            Lang = "en",
            LocalHour = 9,
            Weekday = 1,
            IsReopen = false,
            Latitude = latitude,
            Longitude = longitude,
            UserId = Guid.NewGuid().ToString(),
        };
    }

    [Test]
    public async Task SuggestionRoutes_ContainsRouteForEachKnownNavigationKey()
    {
        var knownKeys = new Dictionary<string, string>
        {
            ["klacksy.welcome.suggestion.edit_settings"] = "/workplace/settings",
            ["klacksy.welcome.suggestion.manage_providers"] = "/workplace/settings/llm",
            ["klacksy.welcome.suggestion.view_schedule"] = "/workplace/schedule",
            ["klacksy.welcome.suggestion.review_absences"] = "/workplace/absence",
            ["klacksy.welcome.suggestion.create_employee"] = "/workplace/new-employee",
            ["klacksy.welcome.suggestion.find_person"] = "/workplace/client",
        };

        _suggestionsRanker
            .RankAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)knownKeys.Keys.ToList());

        var result = await _handler.Handle(BuildQuery(), CancellationToken.None);

        result.SuggestionRoutes.ShouldNotBeNull();
        foreach (var (key, expectedRoute) in knownKeys)
        {
            result.SuggestionRoutes.ShouldContainKey(key);
            result.SuggestionRoutes[key].ShouldBe(expectedRoute);
        }
    }

    [Test]
    public async Task SuggestionRoutes_DoesNotContainRouteForNonNavigationKey()
    {
        _suggestionsRanker
            .RankAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new List<string> { "klacksy.welcome.suggestion.show_help" });

        var result = await _handler.Handle(BuildQuery(), CancellationToken.None);

        result.SuggestionRoutes.ShouldNotContainKey("klacksy.welcome.suggestion.show_help");
    }

    private GetWelcomeQuery BuildQuery() => new()
    {
        Lang = "de",
        LocalHour = 15,
        Weekday = 2,
        IsReopen = false,
        UserId = Guid.NewGuid().ToString(),
    };

    [Test]
    public async Task Handle_FocusResolverReturnsAFocus_ItIsCarriedOnTheWelcome()
    {
        var focus = new WelcomeFocusResource
        {
            Kind = "period_overdue",
            PromptKey = "klacksy.focus.period-overdue.prompt",
            ActionKind = "navigate",
            ActionLabelKey = "klacksy.focus.period-overdue.action",
            ActionRoute = "/workplace/period-closing"
        };
        _welcomeFocusResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(focus);

        var result = await _handler.Handle(BuildQuery(), CancellationToken.None);

        result.Focus.ShouldNotBeNull();
        result.Focus!.PromptKey.ShouldBe("klacksy.focus.period-overdue.prompt");
    }

    [Test]
    public async Task Handle_FocusResolverReturnsNull_LeavesFocusNull()
    {
        var result = await _handler.Handle(BuildQuery(), CancellationToken.None);

        result.Focus.ShouldBeNull();
    }

    [Test]
    public async Task Handle_ReopenGreeting_StillResolvesTheFocus()
    {
        var query = BuildQuery();
        query.IsReopen = true;

        await _handler.Handle(query, CancellationToken.None);

        await _welcomeFocusResolver.Received(1).ResolveAsync(query.UserId, Arg.Any<CancellationToken>());
    }
}
