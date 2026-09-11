// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that a feature plugin's skills follow its lifecycle without a restart. Until 2026-08-06 the
/// plugin seed loader only ran at startup and only for installed and enabled plugins: a plugin
/// installed at runtime had no skills until the next boot, and an uninstalled one kept its skills in
/// the catalogue and in the knowledge index indefinitely. Since 2026-09-11 install and enable also pull
/// the synonyms of every installed language pack into the plugin's skills: a pack installed while the
/// plugin was off only reached the skills that were enabled at that time.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Services.Plugins;

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
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
public class FeaturePluginServiceSkillLifecycleTests
{
    private const string PluginName = "messaging";
    private const string NavigateToSkillName = "navigate_to";
    private const string PluginRoute = "/messaging";
    private const string PluginSkillName = "send_message";

    private ISettingsRepository _settingsRepository = null!;
    private SkillSeedLoader _seedLoader = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private ILanguagePluginService _languagePluginService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private FeaturePluginService _service = null!;
    private string _pluginRoot = null!;
    private List<Settings> _settings = null!;

    [SetUp]
    public async Task SetUp()
    {
        _pluginRoot = Path.Combine(Path.GetTempPath(), "klacks-plugins-" + Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(_pluginRoot, PluginName);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                name = PluginName,
                displayName = "Messaging",
                minKlacksVersion = "1.0.0"
            }));

        _settings = [];
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.GetSettingsList().Returns(_ => _settings);
        _settingsRepository.GetSetting(Arg.Any<string>())
            .Returns(call => _settings.FirstOrDefault(s => s.Type == call.Arg<string>()));
        _settingsRepository.AddSetting(Arg.Any<Settings>())
            .Returns(call =>
            {
                var setting = call.Arg<Settings>();
                _settings.Add(setting);
                return setting;
            });

        _seedLoader = Substitute.For<SkillSeedLoader>(
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            Substitute.For<ISkillPhraseRepository>(),
            Substitute.For<Klacks.Api.Application.Interfaces.Plugins.IFeaturePluginService>(),
            Substitute.For<IWebHostEnvironment>(),
            Substitute.For<ILogger<SkillSeedLoader>>());
        _seedLoader.GetPluginSkillNamesAsync(PluginName, Arg.Any<CancellationToken>())
            .Returns(new List<string> { PluginSkillName });
        _refresher = Substitute.For<ISkillCatalogRefresher>();
        _languagePluginService = Substitute.For<ILanguagePluginService>();

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISettingsRepository)).Returns(_settingsRepository);
        provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        provider.GetService(typeof(SkillSeedLoader)).Returns(_seedLoader);
        provider.GetService(typeof(ISkillCatalogRefresher)).Returns(_refresher);
        provider.GetService(typeof(ILanguagePluginService)).Returns(_languagePluginService);
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(IAgentRepository)).Returns(_agentRepository);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FeaturePlugins:Directory"] = _pluginRoot })
            .Build();

        _service = new FeaturePluginService(
            scopeFactory, configuration, Substitute.For<ILogger<FeaturePluginService>>());
        await _service.InitializeAsync();
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
    public async Task InstallAsync_SeedsTheSkillsAndRefreshesTheCatalogue()
    {
        var installed = await _service.InstallAsync(PluginName);

        installed.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _seedLoader.SeedPluginSkillsAsync(PluginName, Arg.Any<CancellationToken>());
            _seedLoader.SetPluginSkillsEnabledAsync(PluginName, true, Arg.Any<CancellationToken>());
            _languagePluginService.ApplyInstalledSkillSynonymsAsync(
                Arg.Is<IReadOnlyCollection<string>>(names => names.SequenceEqual(new[] { PluginSkillName })));
            _refresher.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }

    // A language pack installed while the plugin was disabled skipped the plugin's skills, because the
    // pack install only writes into enabled skills. Enabling the plugin has to pull those synonyms.
    [Test]
    public async Task EnableAsync_AfterDisable_AppliesTheInstalledLanguagePackSynonymsToThePluginSkills()
    {
        await _service.InstallAsync(PluginName);
        await _service.DisableAsync(PluginName);
        _languagePluginService.ClearReceivedCalls();

        var enabled = await _service.EnableAsync(PluginName);

        enabled.ShouldBeTrue();
        await _languagePluginService.Received(1).ApplyInstalledSkillSynonymsAsync(
            Arg.Is<IReadOnlyCollection<string>>(names => names.SequenceEqual(new[] { PluginSkillName })));
    }

    [Test]
    public async Task DisableAsync_DoesNotTouchTheLanguagePackSynonyms()
    {
        await _service.InstallAsync(PluginName);
        _languagePluginService.ClearReceivedCalls();

        await _service.DisableAsync(PluginName);

        await _languagePluginService.DidNotReceive().ApplyInstalledSkillSynonymsAsync(
            Arg.Any<IReadOnlyCollection<string>>());
    }

    // Applying the pack synonyms is best effort: if it fails, the catalogue refresh that makes the
    // plugin's skills reachable must still run.
    [Test]
    public async Task InstallAsync_ApplyingSynonymsThrows_StillRefreshesTheCatalogue()
    {
        _languagePluginService.ApplyInstalledSkillSynonymsAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns<Task>(_ => throw new InvalidOperationException("pack file unreadable"));

        var installed = await _service.InstallAsync(PluginName);

        installed.ShouldBeTrue();
        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UninstallAsync_DisablesTheSkillsAndRefreshesTheCatalogue()
    {
        await _service.InstallAsync(PluginName);
        _seedLoader.ClearReceivedCalls();
        _refresher.ClearReceivedCalls();

        var uninstalled = await _service.UninstallAsync(PluginName);

        uninstalled.ShouldBeTrue();
        await _seedLoader.Received(1).SetPluginSkillsEnabledAsync(
            PluginName, false, Arg.Any<CancellationToken>());
        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisableAsync_TakesTheSkillsOutWithoutSeedingThemAgain()
    {
        await _service.InstallAsync(PluginName);
        _seedLoader.ClearReceivedCalls();

        var disabled = await _service.DisableAsync(PluginName);

        disabled.ShouldBeTrue();
        await _seedLoader.Received(1).SetPluginSkillsEnabledAsync(
            PluginName, false, Arg.Any<CancellationToken>());
        await _seedLoader.DidNotReceive().SeedPluginSkillsAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Re-enabling has to flip the flag explicitly. The seed alone cannot do it: its version gate
    // skips a definition whose version did not change, which is exactly the case here.
    [Test]
    public async Task EnableAsync_AfterDisable_TurnsTheSkillsBackOn()
    {
        await _service.InstallAsync(PluginName);
        await _service.DisableAsync(PluginName);
        _seedLoader.ClearReceivedCalls();
        _refresher.ClearReceivedCalls();

        var enabled = await _service.EnableAsync(PluginName);

        enabled.ShouldBeTrue();
        await _seedLoader.Received(1).SetPluginSkillsEnabledAsync(
            PluginName, true, Arg.Any<CancellationToken>());
        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A broken seed file must not make an install that did happen report failure.
    [Test]
    public async Task InstallAsync_SeedingThrows_StillReportsSuccess()
    {
        _seedLoader.SeedPluginSkillsAsync(PluginName, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("malformed skill-seeds.json"));

        var installed = await _service.InstallAsync(PluginName);

        installed.ShouldBeTrue();
        _service.IsEnabled(PluginName).ShouldBeTrue();
    }

    // The refresh rebuilds the knowledge index from the database, so it must not run before the
    // settings write is committed.
    [Test]
    public async Task InstallAsync_CompletesTheUnitOfWorkBeforeRefreshingTheCatalogue()
    {
        var installed = await _service.InstallAsync(PluginName);

        installed.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _refresher.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }

    // SetUp's manifest has no navigation entry, so RegisterPluginNavigationAsync would return early
    // on every other test in this fixture. This test gives the plugin a route and rediscovers it so
    // the navigation branch actually runs, then pins that the navigate_to update happens before the
    // refresh that follows it in the same install flow.
    [Test]
    public async Task InstallAsync_RegistersNavigationBeforeRefreshingTheCatalogue()
    {
        File.WriteAllText(
            Path.Combine(_pluginRoot, PluginName, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                name = PluginName,
                displayName = "Messaging",
                minKlacksVersion = "1.0.0",
                navigation = new { route = PluginRoute }
            }));
        await _service.RefreshPluginsAsync();

        var defaultAgent = new Agent { Id = Guid.NewGuid() };
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(defaultAgent);
        var navigateToSkill = new AgentSkill { Id = Guid.NewGuid(), AgentId = defaultAgent.Id, Name = NavigateToSkillName };
        _skillRepository.GetByNameAsync(defaultAgent.Id, NavigateToSkillName, Arg.Any<CancellationToken>())
            .Returns(navigateToSkill);

        var installed = await _service.InstallAsync(PluginName);

        installed.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _skillRepository.UpdateAsync(navigateToSkill, Arg.Any<CancellationToken>());
            _refresher.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        });
    }
}
