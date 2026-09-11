// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GreetingComposer — verifies it composes a greeting from grounded facts via the
/// cheap LLM, and returns null (so the caller falls back to the template) when there is nothing
/// ambient to say.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class GreetingComposerTests
{
    private static readonly DateOnly FixedToday = new(2026, 9, 11);

    private ILLMProviderFactory _providerFactory = null!;
    private ILLMRepository _llmRepository = null!;
    private IOpenMeteoClient _weatherClient = null!;
    private IPublicHolidayProvider _holidayProvider = null!;
    private IWebSearchProviderFactory _searchFactory = null!;
    private IIdentityContextProvider _identityProvider = null!;
    private IAgentRepository _agentRepository = null!;
    private ICompanyLocationProvider _companyLocation = null!;
    private ISettingsReader _settingsReader = null!;
    private FixedCompanyClock _companyClock = null!;
    private ILLMProvider _provider = null!;
    private GreetingComposer _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _providerFactory = Substitute.For<ILLMProviderFactory>();
        _llmRepository = Substitute.For<ILLMRepository>();
        _weatherClient = Substitute.For<IOpenMeteoClient>();
        _holidayProvider = Substitute.For<IPublicHolidayProvider>();
        _searchFactory = Substitute.For<IWebSearchProviderFactory>();
        _identityProvider = Substitute.For<IIdentityContextProvider>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _companyLocation = Substitute.For<ICompanyLocationProvider>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _companyClock = new FixedCompanyClock(FixedToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = Guid.NewGuid() });
        _identityProvider.GetIdentityPromptAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("You are Klacksy.");
        _searchFactory.CreateAsync(Arg.Any<CancellationToken>()).Returns((IWebSearchProvider?)null);
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        _holidayProvider.GetUpcomingHolidayAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((UpcomingHoliday?)null);

        var model = new LLMModel { ModelId = "m", ApiModelId = "m" };
        _llmRepository.GetModelsAsync(true).Returns(new List<LLMModel> { model });
        _provider = Substitute.For<ILLMProvider>();
        _providerFactory.GetProviderForModelAsync("m").Returns(_provider);

        _sut = new GreetingComposer(
            _providerFactory, _llmRepository, _weatherClient, _holidayProvider, _searchFactory,
            _identityProvider, _agentRepository, _companyLocation, _settingsReader, _companyClock,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<GreetingComposer>.Instance);
    }

    [Test]
    public async Task ComposeAsync_WithWeather_ReturnsLlmGreeting()
    {
        _weatherClient.GetCurrentWeatherAsync(10.0, 20.0, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new WeatherSnapshot { Condition = "Clear sky", TemperatureCelsius = 18 });
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Good morning, Max!" });

        var result = await _sut.ComposeAsync(new GreetingContext("u1", "en", "Max", "morning", 10.0, 20.0, "CH"));

        result.ShouldBe("Good morning, Max!");
    }

    [Test]
    public async Task ComposeAsync_AnswerOnlyFromReasoningChannel_ReturnsNull()
    {
        GivenWeather();
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse
            {
                Success = true,
                Content = "We need to write a short, warm greeting in German...",
                ContentFromReasoning = true
            });

        var result = await _sut.ComposeAsync(new GreetingContext("u3", "de", "admin", "afternoon", 10.0, 20.0, "CH"));

        result.ShouldBeNull();
    }

    [Test]
    public async Task ComposeAsync_MultiLineChainOfThought_ReturnsNull()
    {
        GivenWeather();
        var leak = string.Join("\n", Enumerable.Repeat("So: \"Guten Nachmittag, admin!\" But keep it short.", 12));
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = true, Content = leak });

        var result = await _sut.ComposeAsync(new GreetingContext("u4", "de", "admin", "afternoon", 10.0, 20.0, "CH"));

        result.ShouldBeNull();
    }

    [Test]
    public async Task ComposeAsync_TwoLineGreeting_IsAccepted()
    {
        GivenWeather();
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Guten Nachmittag, admin!\nSchön, dass du da bist." });

        var result = await _sut.ComposeAsync(new GreetingContext("u5", "de", "admin", "afternoon", 10.0, 20.0, "CH"));

        result.ShouldBe("Guten Nachmittag, admin!\nSchön, dass du da bist.");
    }

    private void GivenWeather()
        => _weatherClient.GetCurrentWeatherAsync(10.0, 20.0, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new WeatherSnapshot { Condition = "Clear sky", TemperatureCelsius = 31 });

    [Test]
    public async Task ComposeAsync_NoAmbientFacts_ReturnsNullWithoutLlmCall()
    {
        _weatherClient.GetCurrentWeatherAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((WeatherSnapshot?)null);

        var result = await _sut.ComposeAsync(new GreetingContext("u2", "en", "Max", "morning", 10.0, 20.0, "CH"));

        result.ShouldBeNull();
        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>());
    }
}
