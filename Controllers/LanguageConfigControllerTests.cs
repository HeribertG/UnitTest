// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the language config endpoint: default language resolution from the
/// DEFAULT_LANGUAGE setting with validation against the supported languages and fallback to "en",
/// plus the admin-only knowledge index sync status endpoint.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class LanguageConfigControllerTests
{
    private ILanguagePluginService _languagePluginService = null!;
    private IFeaturePluginService _featurePluginService = null!;
    private IMarketplaceClientService _marketplaceClient = null!;
    private ISettingsReader _settingsReader = null!;
    private IKnowledgeIndexSyncScheduler _knowledgeIndexSyncScheduler = null!;
    private CapturingLogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _languagePluginService = Substitute.For<ILanguagePluginService>();
        _languagePluginService.GetInstalledPluginCodes().Returns(new List<string>());
        _languagePluginService.GetPlugin(Arg.Any<string>()).Returns((LanguagePluginInfo?)null);

        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _marketplaceClient = Substitute.For<IMarketplaceClientService>();

        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsModel?)null);

        _knowledgeIndexSyncScheduler = Substitute.For<IKnowledgeIndexSyncScheduler>();
        _logger = new CapturingLogger();
    }

    [Test]
    public async Task GetLanguages_NoDefaultLanguageSetting_ReturnsFallbackWithoutWarning()
    {
        var controller = CreateController();

        var response = await InvokeGetLanguages(controller);

        response.DefaultLanguage.ShouldBe(LanguageConfig.DefaultLanguageFallback);
        _logger.Warnings.ShouldBe(0);
    }

    [Test]
    public async Task GetLanguages_SupportedDefaultLanguageSetting_ReturnsConfiguredValue()
    {
        StubDefaultLanguageSetting("de");
        var controller = CreateController();

        var response = await InvokeGetLanguages(controller);

        response.DefaultLanguage.ShouldBe("de");
        _logger.Warnings.ShouldBe(0);
    }

    [Test]
    public async Task GetLanguages_InstalledPluginAsDefaultLanguage_ReturnsConfiguredValue()
    {
        _languagePluginService.GetInstalledPluginCodes().Returns(new List<string> { "pl" });
        _languagePluginService.GetPlugin("pl").Returns(new LanguagePluginInfo { Code = "pl" });
        StubDefaultLanguageSetting("pl");
        var controller = CreateController();

        var response = await InvokeGetLanguages(controller);

        response.DefaultLanguage.ShouldBe("pl");
        _logger.Warnings.ShouldBe(0);
    }

    [Test]
    public async Task GetLanguages_UnsupportedDefaultLanguageSetting_FallsBackWithWarning()
    {
        StubDefaultLanguageSetting("xx");
        var controller = CreateController();

        var response = await InvokeGetLanguages(controller);

        response.DefaultLanguage.ShouldBe(LanguageConfig.DefaultLanguageFallback);
        _logger.Warnings.ShouldBe(1);
    }

    [Test]
    public async Task GetLanguages_EmptyDefaultLanguageSetting_ReturnsFallbackWithoutWarning()
    {
        StubDefaultLanguageSetting("   ");
        var controller = CreateController();

        var response = await InvokeGetLanguages(controller);

        response.DefaultLanguage.ShouldBe(LanguageConfig.DefaultLanguageFallback);
        _logger.Warnings.ShouldBe(0);
    }

    [Test]
    public void GetKnowledgeIndexSyncStatus_ReturnsTheSchedulerStatus()
    {
        var status = new KnowledgeIndexSyncStatus(
            true, true, null, null, "installing language plugin 'pl'", null);
        _knowledgeIndexSyncScheduler.Status.Returns(status);
        var controller = CreateController();

        var result = controller.GetKnowledgeIndexSyncStatus();

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBe(status);
    }

    [Test]
    public void GetKnowledgeIndexSyncStatus_IsAdminOnlyAndPinsTheJwtScheme()
    {
        var authorize = typeof(LanguageConfigController)
            .GetMethod(nameof(LanguageConfigController.GetKnowledgeIndexSyncStatus))!
            .GetCustomAttribute<AuthorizeAttribute>();

        authorize.ShouldNotBeNull();
        authorize!.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        authorize.Roles.ShouldBe(Roles.Admin);
    }

    private void StubDefaultLanguageSetting(string value)
    {
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.DefaultLanguage, Value = value });
    }

    private LanguageConfigController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new LanguageConfigController(
            configuration,
            _languagePluginService,
            _featurePluginService,
            _marketplaceClient,
            _settingsReader,
            _knowledgeIndexSyncScheduler,
            _logger);
    }

    private static async Task<LanguageConfigResponse> InvokeGetLanguages(LanguageConfigController controller)
    {
        var actionResult = await controller.GetLanguages();
        var okResult = actionResult.Result.ShouldBeOfType<OkObjectResult>();
        return okResult.Value.ShouldBeOfType<LanguageConfigResponse>();
    }

    private sealed class CapturingLogger : ILogger<LanguageConfigController>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
