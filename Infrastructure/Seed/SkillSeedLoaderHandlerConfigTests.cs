// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that a skill seed version bump only writes the handler config when the seed definition
/// carries one. navigate_to ships with "handlerConfig": null while its stored config holds the routes
/// every installed feature plugin registered for itself, and the scanner bumps the version on every
/// description change — an unconditional overwrite with "{}" silently unregistered all plugin pages.
/// Keeping the config is only half of it: ParametersJson is still rewritten from the seed, so the page
/// keys derived from the kept routes have to be unioned back into the 'page' enum, which is validated
/// hard before a navigation runs.
/// </summary>

using System.Text.Json.Nodes;
using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class SkillSeedLoaderHandlerConfigTests
{
    private const string SkillName = "navigate_to";
    private const string RegisteredRoutes = "{\"routes\":{\"messaging\":\"/workplace/messaging\"}}";
    private const string FloorPlanRoutes = "{\"routes\":{\"floor-plan\":\"/workplace/floor-plan\"}}";

    private string _contentRoot = null!;
    private string _seedFilePath = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IFeaturePluginService _featurePluginService = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-skill-seed-hc-" + Guid.NewGuid().ToString("N"));
        var definitionsDir = Path.Combine(_contentRoot, "Application", "Skills", "Definitions");
        Directory.CreateDirectory(definitionsDir);
        _seedFilePath = Path.Combine(definitionsDir, "skill-seeds.json");

        _agent = new Agent { Id = Guid.NewGuid(), Name = "klacks-default", IsDefault = true };

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);

        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _featurePluginService.GetAllPluginsAsync().Returns(new List<FeaturePluginInfo>());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task VersionBumpReseed_DefinitionWithNullHandlerConfig_KeepsRegisteredPluginRoutes()
    {
        var existing = GivenExistingSkill(RegisteredRoutes);
        WriteSeedFile(version: 2, handlerConfigJson: "null");

        await CreateLoader().LoadAsync();

        await _skillRepository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        Assert.That(existing.Version, Is.EqualTo(2), "the version bump must be applied");
        Assert.That(existing.HandlerConfig, Is.EqualTo(RegisteredRoutes),
            "a seed definition without a handler config must not wipe the registered plugin routes");
    }

    [Test]
    public async Task VersionBumpReseed_DefinitionWithoutHandlerConfigProperty_KeepsRegisteredPluginRoutes()
    {
        var existing = GivenExistingSkill(RegisteredRoutes);
        WriteSeedFile(version: 2, handlerConfigJson: null);

        await CreateLoader().LoadAsync();

        Assert.That(existing.HandlerConfig, Is.EqualTo(RegisteredRoutes));
    }

    [Test]
    public async Task VersionBumpReseed_DefinitionWithHandlerConfig_OverwritesTheStoredOne()
    {
        var existing = GivenExistingSkill(RegisteredRoutes);
        WriteSeedFile(version: 2, handlerConfigJson: "{\"method\":\"List\"}");

        await CreateLoader().LoadAsync();

        Assert.That(existing.HandlerConfig, Is.EqualTo("{\"method\":\"List\"}"),
            "an explicit handler config in the seed definition is the truth");
    }

    [Test]
    public async Task VersionBumpReseed_UnionsTheKeptRouteKeysBackIntoThePageEnum()
    {
        var existing = GivenExistingSkill(FloorPlanRoutes, BuildPageParametersJson("a", "b"));
        WriteSeedFile(version: 2, handlerConfigJson: null, pageEnumValues: ["a", "b", "c"]);

        await CreateLoader().LoadAsync();

        Assert.That(ReadPageEnumValues(existing), Is.EqualTo(new[] { "a", "b", "c", "floor-plan" }),
            "the seed brings the new built-in page, the kept routes bring the plugin page back");
        Assert.That(existing.HandlerConfig, Is.EqualTo(FloorPlanRoutes),
            "restoring the enum must not touch the stored routes");
    }

    [Test]
    public async Task VersionBumpReseed_WithoutStoredRoutes_LeavesThePageEnumAsTheSeedWroteIt()
    {
        var existing = GivenExistingSkill("{}", BuildPageParametersJson("a", "b"));
        WriteSeedFile(version: 2, handlerConfigJson: null, pageEnumValues: ["a", "b", "c"]);

        await CreateLoader().LoadAsync();

        Assert.That(ReadPageEnumValues(existing), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public async Task VersionBumpReseed_WithAnExplicitHandlerConfig_UnionsTheRoutesOfThatConfig()
    {
        var existing = GivenExistingSkill(RegisteredRoutes, BuildPageParametersJson("a"));
        WriteSeedFile(version: 2, handlerConfigJson: FloorPlanRoutes, pageEnumValues: ["a"]);

        await CreateLoader().LoadAsync();

        Assert.That(ReadPageEnumValues(existing), Is.EqualTo(new[] { "a", "floor-plan" }),
            "the seed's own handler config is the truth, so its routes are the ones projected");
    }

    [Test]
    public async Task NewSkillWithoutHandlerConfig_IsInsertedWithTheEmptyDefault()
    {
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());
        WriteSeedFile(version: 1, handlerConfigJson: "null");

        await CreateLoader().LoadAsync();

        await _skillRepository.Received(1).AddAsync(
            Arg.Is<AgentSkill>(s => s.Name == SkillName && s.HandlerConfig == "{}"),
            Arg.Any<CancellationToken>());
    }

    private AgentSkill GivenExistingSkill(string handlerConfig, string? parametersJson = null)
    {
        var existing = new AgentSkill
        {
            AgentId = _agent.Id,
            Name = SkillName,
            Description = "old description",
            Version = 1,
            HandlerConfig = handlerConfig,
            ParametersJson = parametersJson ?? "[]"
        };

        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill> { existing });

        return existing;
    }

    private static string BuildPageParametersJson(params string[] pageKeys)
    {
        var values = string.Join(",", pageKeys.Select(key => $"\"{key}\""));
        return "[{\"name\":\"page\",\"description\":\"The page to navigate to\",\"type\":\"Enum\"," +
               $"\"required\":true,\"defaultValue\":null,\"enumValues\":[{values}]}}]";
    }

    private static List<string> ReadPageEnumValues(AgentSkill skill)
    {
        var parameters = JsonNode.Parse(skill.ParametersJson)!.AsArray();
        var page = parameters.Single(p => p!["name"]!.GetValue<string>() == "page")!.AsObject();
        return [.. page["enumValues"]!.AsArray().Select(v => v!.GetValue<string>())];
    }

    private SkillSeedLoader CreateLoader()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);

        return new SkillSeedLoader(
            _skillRepository,
            _agentRepository,
            Substitute.For<ISkillPhraseRepository>(),
            _featurePluginService,
            environment,
            NullLogger<SkillSeedLoader>.Instance);
    }

    private void WriteSeedFile(int version, string? handlerConfigJson, string[]? pageEnumValues = null)
    {
        var handlerConfigProperty = handlerConfigJson == null
            ? string.Empty
            : $"\"handlerConfig\":{handlerConfigJson},";

        var parametersProperty = pageEnumValues == null
            ? string.Empty
            : $"\"parameters\":{BuildPageParametersJson(pageEnumValues)},";

        var json =
            "{\"version\":1,\"skills\":[{" +
            $"\"name\":\"{SkillName}\"," +
            "\"description\":\"new description\"," +
            "\"category\":\"UI\"," +
            "\"executionType\":\"Skill\"," +
            "\"isEnabled\":true," +
            parametersProperty +
            handlerConfigProperty +
            $"\"version\":{version}" +
            "}]}";

        File.WriteAllText(_seedFilePath, json);
    }
}
