// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the English fallback of feature-plugin translations. A plugin used to ship only de/en/fr/it,
/// and GetTranslations silently skipped every other language, so the UI showed raw i18n keys there.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Services.Plugins;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Klacks.Api.Infrastructure.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Settings = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class FeaturePluginServiceTranslationFallbackTests
{
    private const string PluginName = "messaging";
    private const string TitleKey = "settings.user-messengers.title";
    private const string HintKey = "settings.user-messengers.hint";

    private FeaturePluginService _service = null!;
    private string _pluginRoot = null!;

    [SetUp]
    public async Task SetUp()
    {
        _pluginRoot = Path.Combine(Path.GetTempPath(), "klacks-plugins-i18n-" + Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(_pluginRoot, PluginName);
        var i18nDirectory = Path.Combine(pluginDirectory, "i18n");
        Directory.CreateDirectory(i18nDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(new { name = PluginName, displayName = "Messaging", minKlacksVersion = "1.0.0" }));
        File.WriteAllText(
            Path.Combine(i18nDirectory, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { [TitleKey] = "My messengers", [HintKey] = "Connect a messenger" }));
        File.WriteAllText(
            Path.Combine(i18nDirectory, "de.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { [TitleKey] = "Meine Messenger" }));

        var settings = new List<Settings>();
        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSettingsList().Returns(_ => settings);
        settingsRepository.GetSetting(Arg.Any<string>())
            .Returns(call => settings.FirstOrDefault(s => s.Type == call.Arg<string>()));
        settingsRepository.AddSetting(Arg.Any<Settings>())
            .Returns(call =>
            {
                var setting = call.Arg<Settings>();
                settings.Add(setting);
                return setting;
            });

        var seedLoader = Substitute.For<SkillSeedLoader>(
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            Substitute.For<ISkillPhraseRepository>(),
            Substitute.For<Klacks.Api.Application.Interfaces.Plugins.IFeaturePluginService>(),
            Substitute.For<IWebHostEnvironment>(),
            Substitute.For<ILogger<SkillSeedLoader>>());

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISettingsRepository)).Returns(settingsRepository);
        provider.GetService(typeof(IUnitOfWork)).Returns(Substitute.For<IUnitOfWork>());
        provider.GetService(typeof(SkillSeedLoader)).Returns(seedLoader);
        provider.GetService(typeof(ISkillCatalogRefresher)).Returns(Substitute.For<ISkillCatalogRefresher>());
        provider.GetService(typeof(IAgentSkillRepository)).Returns(Substitute.For<IAgentSkillRepository>());
        provider.GetService(typeof(IAgentRepository)).Returns(Substitute.For<IAgentRepository>());

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FeaturePlugins:Directory"] = _pluginRoot })
            .Build();

        _service = new FeaturePluginService(scopeFactory, configuration, Substitute.For<ILogger<FeaturePluginService>>());
        await _service.InitializeAsync();
        (await _service.InstallAsync(PluginName)).ShouldBeTrue();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_pluginRoot))
        {
            Directory.Delete(_pluginRoot, recursive: true);
        }
    }

    [Test]
    public void Language_without_a_plugin_file_falls_back_to_english()
    {
        var translations = _service.GetTranslations("ko");

        translations.ShouldNotBeNull();
        translations![TitleKey].ShouldBe("My messengers");
    }

    [Test]
    public void Partial_translation_falls_back_to_english_per_missing_key()
    {
        var translations = _service.GetTranslations("de");

        translations.ShouldNotBeNull();
        translations![TitleKey].ShouldBe("Meine Messenger");
        translations[HintKey].ShouldBe("Connect a messenger");
    }
}
