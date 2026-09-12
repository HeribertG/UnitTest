// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins that installing or uninstalling a feature plugin only adds or removes that one page key in the
/// navigate_to 'page' enum. The enum used to be replaced wholesale by the registered plugin routes, so
/// after installing a single plugin the model saw exactly one page it could navigate to and every
/// built-in page was gone until the next skill reseed. Enabling and disabling are covered here too:
/// they used to leave both the route and the page key in place, so a disabled plugin stayed navigable
/// while the frontend route guard refused the page. The startup sync is pinned here too: it heals an
/// installation whose stored route map is empty, and it must stay silent on a healthy one.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Services.Plugins;

using System.Text.Json;
using System.Text.Json.Nodes;
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
public class FeaturePluginServiceNavigationEnumTests
{
    private const string PluginName = "floor-plan";
    private const string PluginRoute = "/workplace/floor-plan";
    private const string NavigateToSkillName = "navigate_to";

    private static readonly string[] BuiltInPageKeys = ["dashboard", "client-list", "settings"];

    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private AgentSkill _navigateToSkill = null!;
    private FeaturePluginService _service = null!;
    private string _pluginRoot = null!;
    private List<Settings> _settings = null!;

    [SetUp]
    public async Task SetUp()
    {
        _pluginRoot = Path.Combine(Path.GetTempPath(), "klacks-plugins-nav-" + Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(_pluginRoot, PluginName);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                name = PluginName,
                displayName = "Floor Plan",
                minKlacksVersion = "1.0.0",
                navigation = new { route = PluginRoute }
            }));

        _settings = [];
        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSettingsList().Returns(_ => _settings);
        settingsRepository.GetSetting(Arg.Any<string>())
            .Returns(call => _settings.FirstOrDefault(s => s.Type == call.Arg<string>()));
        settingsRepository.AddSetting(Arg.Any<Settings>())
            .Returns(call =>
            {
                var setting = call.Arg<Settings>();
                _settings.Add(setting);
                return setting;
            });

        var seedLoader = Substitute.For<SkillSeedLoader>(
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            Substitute.For<ISkillPhraseRepository>(),
            Substitute.For<Klacks.Api.Application.Interfaces.Plugins.IFeaturePluginService>(),
            Substitute.For<IWebHostEnvironment>(),
            Substitute.For<ILogger<SkillSeedLoader>>());
        seedLoader.GetPluginSkillNamesAsync(PluginName, Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        var defaultAgent = new Agent { Id = Guid.NewGuid(), Name = "klacks-default", IsDefault = true };
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(defaultAgent);

        _navigateToSkill = new AgentSkill
        {
            Id = Guid.NewGuid(),
            AgentId = defaultAgent.Id,
            Name = NavigateToSkillName,
            ParametersJson = BuildParametersJson(BuiltInPageKeys)
        };
        _skillRepository.GetByNameAsync(defaultAgent.Id, NavigateToSkillName, Arg.Any<CancellationToken>())
            .Returns(_navigateToSkill);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISettingsRepository)).Returns(settingsRepository);
        provider.GetService(typeof(IUnitOfWork)).Returns(Substitute.For<IUnitOfWork>());
        provider.GetService(typeof(SkillSeedLoader)).Returns(seedLoader);
        provider.GetService(typeof(ISkillCatalogRefresher)).Returns(Substitute.For<ISkillCatalogRefresher>());
        provider.GetService(typeof(ILanguagePluginService)).Returns(Substitute.For<ILanguagePluginService>());
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
    public async Task InstallAsync_AppendsThePluginPageKeyAndKeepsTheBuiltInOnes()
    {
        (await _service.InstallAsync(PluginName)).ShouldBeTrue();

        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", PluginName]);
    }

    [Test]
    public async Task UninstallAsync_RemovesOnlyThePluginPageKey()
    {
        await _service.InstallAsync(PluginName);

        (await _service.UninstallAsync(PluginName)).ShouldBeTrue();

        ReadPageEnumValues().ShouldBe(BuiltInPageKeys);
    }

    [Test]
    public async Task InstallAsync_CalledTwice_DoesNotDuplicateThePluginPageKey()
    {
        await _service.InstallAsync(PluginName);

        (await _service.InstallAsync(PluginName)).ShouldBeTrue();

        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", PluginName]);
    }

    [Test]
    public async Task DisableAsync_RemovesThePageKeyAndTheRoute()
    {
        await _service.InstallAsync(PluginName);

        (await _service.DisableAsync(PluginName)).ShouldBeTrue();

        ReadPageEnumValues().ShouldBe(BuiltInPageKeys);
        ReadRoutes().ShouldNotContainKey(PluginName);
    }

    [Test]
    public async Task EnableAsync_AfterDisable_PutsThePageKeyAndTheRouteBack()
    {
        await _service.InstallAsync(PluginName);
        await _service.DisableAsync(PluginName);

        (await _service.EnableAsync(PluginName)).ShouldBeTrue();

        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", PluginName]);
        ReadRoutes()[PluginName].ShouldBe(PluginRoute);
    }

    [Test]
    public async Task EnableAsync_OnAnAlreadyEnabledPlugin_DoesNotDuplicateThePageKey()
    {
        await _service.InstallAsync(PluginName);

        (await _service.EnableAsync(PluginName)).ShouldBeTrue();

        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", PluginName]);
    }

    [Test]
    public async Task InstallAsync_LeavesEveryOtherParameterUntouched()
    {
        await _service.InstallAsync(PluginName);

        var parameters = JsonNode.Parse(_navigateToSkill.ParametersJson)!.AsArray();
        var entityId = parameters.Single(p => p!["name"]!.GetValue<string>() == "entityId")!.AsObject();
        entityId["description"]!.GetValue<string>().ShouldBe("The entity id");
        entityId["required"]!.GetValue<bool>().ShouldBeFalse();
        entityId.ContainsKey("defaultValue").ShouldBeTrue();
        entityId["defaultValue"].ShouldBeNull();
        entityId.ContainsKey("enumValues").ShouldBeTrue();
        entityId["enumValues"].ShouldBeNull();
    }

    // A manifest is installer-supplied data. An unvalidated route was stored verbatim and then handed to
    // the client as a navigation instruction by the navigate_to skill.
    [Test]
    public async Task InstallAsync_WithAnAbsoluteUrlRoute_RegistersNeitherPageKeyNorRoute()
    {
        const string pluginName = "absolute-url-plugin";

        await InstallPluginWithRouteAsync(pluginName, "https://evil.example/x");

        ReadPageEnumValues().ShouldBe(BuiltInPageKeys);
        ReadRoutes().ShouldNotContainKey(pluginName);
    }

    [Test]
    public async Task InstallAsync_WithATraversalRoute_RegistersNeitherPageKeyNorRoute()
    {
        const string pluginName = "traversal-plugin";

        await InstallPluginWithRouteAsync(pluginName, "/workplace/../settings");

        ReadPageEnumValues().ShouldBe(BuiltInPageKeys);
        ReadRoutes().ShouldNotContainKey(pluginName);
    }

    [Test]
    public async Task InstallAsync_WithAWellFormedRoute_RegistersPageKeyAndRoute()
    {
        const string pluginName = "second-plan";
        const string route = "/workplace/second-plan";

        await InstallPluginWithRouteAsync(pluginName, route);

        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", pluginName]);
        ReadRoutes()[pluginName].ShouldBe(route);
    }

    // Registration used to happen only at the moment of installing or enabling. An installation whose
    // plugin predates that code - or whose HandlerConfig a skill seed version bump wiped - therefore
    // kept an enabled plugin page unreachable for the assistant until someone reinstalled the plugin.
    [Test]
    public async Task SyncNavigationRoutesAsync_WithAnEnabledPluginAndAnEmptyHandlerConfig_RegistersRouteAndPageKey()
    {
        await _service.InstallAsync(PluginName);
        _navigateToSkill.HandlerConfig = "{}";
        _navigateToSkill.ParametersJson = BuildParametersJson(BuiltInPageKeys);

        await _service.SyncNavigationRoutesAsync();

        ReadRoutes()[PluginName].ShouldBe(PluginRoute);
        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", PluginName]);
    }

    [Test]
    public async Task SyncNavigationRoutesAsync_RunTwice_WritesNothingTheSecondTime()
    {
        await _service.InstallAsync(PluginName);
        _navigateToSkill.HandlerConfig = "{}";
        _navigateToSkill.ParametersJson = BuildParametersJson(BuiltInPageKeys);
        await _service.SyncNavigationRoutesAsync();
        _skillRepository.ClearReceivedCalls();

        await _service.SyncNavigationRoutesAsync();

        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        ReadRoutes()[PluginName].ShouldBe(PluginRoute);
        ReadPageEnumValues().ShouldBe(["dashboard", "client-list", "settings", PluginName]);
    }

    [Test]
    public async Task SyncNavigationRoutesAsync_WithADisabledPlugin_RegistersNothing()
    {
        await _service.InstallAsync(PluginName);
        await _service.DisableAsync(PluginName);
        _navigateToSkill.HandlerConfig = "{}";
        _navigateToSkill.ParametersJson = BuildParametersJson(BuiltInPageKeys);

        await _service.SyncNavigationRoutesAsync();

        ReadRoutes().ShouldNotContainKey(PluginName);
        ReadPageEnumValues().ShouldBe(BuiltInPageKeys);
    }

    [Test]
    public async Task SyncNavigationRoutesAsync_WithAnUninstalledPlugin_RegistersNothing()
    {
        _navigateToSkill.HandlerConfig = "{}";

        await _service.SyncNavigationRoutesAsync();

        ReadRoutes().ShouldNotContainKey(PluginName);
        ReadPageEnumValues().ShouldBe(BuiltInPageKeys);
    }

    private async Task InstallPluginWithRouteAsync(string pluginName, string route)
    {
        var pluginDirectory = Path.Combine(_pluginRoot, pluginName);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                name = pluginName,
                displayName = pluginName,
                minKlacksVersion = "1.0.0",
                navigation = new { route }
            }));

        await _service.RefreshPluginsAsync();
        (await _service.InstallAsync(pluginName)).ShouldBeTrue();
    }

    private Dictionary<string, string> ReadRoutes()
    {
        var routes = JsonNode.Parse(_navigateToSkill.HandlerConfig)?["routes"]?.AsObject();
        return routes == null
            ? []
            : routes.ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>());
    }

    private List<string> ReadPageEnumValues()
    {
        var parameters = JsonNode.Parse(_navigateToSkill.ParametersJson)!.AsArray();
        var page = parameters.Single(p => p!["name"]!.GetValue<string>() == "page")!.AsObject();
        return [.. page["enumValues"]!.AsArray().Select(v => v!.GetValue<string>())];
    }

    private static string BuildParametersJson(IEnumerable<string> pageKeys)
    {
        return JsonSerializer.Serialize(new object[]
        {
            new
            {
                name = "page",
                description = "The page to navigate to",
                type = "Enum",
                required = true,
                defaultValue = (string?)null,
                enumValues = pageKeys
            },
            new
            {
                name = "entityId",
                description = "The entity id",
                type = "String",
                required = false,
                defaultValue = (string?)null,
                enumValues = (string[]?)null
            }
        });
    }
}
