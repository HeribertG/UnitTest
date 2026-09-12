// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the global agent rule seed semantics: missing rules are inserted, unmodified
/// seed rules are refreshed when the shipped default text changes, and admin-edited rules
/// (source != "seed") are never overwritten.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class GlobalAgentRuleSeedServiceTests
{
    private const string RuleName = "PAGE_EXPLANATIONS";
    private const string SeedSource = "seed";

    private IGlobalAgentRuleRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IGlobalAgentRuleRepository>();
        _repository.UpsertRuleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GlobalAgentRule());
    }

    private GlobalAgentRuleSeedService CreateService()
    {
        return new GlobalAgentRuleSeedService(
            _repository,
            Substitute.For<ILogger<GlobalAgentRuleSeedService>>());
    }

    [Test]
    public async Task SeedAsync_MissingRules_AreInsertedWithSeedSource()
    {
        _repository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>());

        await CreateService().SeedAsync();

        await _repository.Received(1).UpsertRuleAsync(
            RuleName, Arg.Any<string>(), Arg.Any<int>(),
            SeedSource, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedAsync_ChangedSeedRule_IsUpdated()
    {
        _repository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>
            {
                new() { Name = RuleName, Content = "outdated seed text", Source = SeedSource, IsActive = true }
            });

        await CreateService().SeedAsync();

        await _repository.Received(1).UpsertRuleAsync(
            RuleName, Arg.Is<string>(c => c != "outdated seed text"), Arg.Any<int>(),
            SeedSource, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedAsync_AdminEditedRule_IsNeverOverwritten()
    {
        _repository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>
            {
                new() { Name = RuleName, Content = "custom admin text", Source = null, IsActive = true }
            });

        await CreateService().SeedAsync();

        await _repository.DidNotReceive().UpsertRuleAsync(
            RuleName, Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedAsync_NewPageExplanationsVoiceRule_IsInsertedWithSeedSource()
    {
        _repository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>
            {
                new() { Name = RuleName, Content = "some text", Source = SeedSource, IsActive = true }
            });

        await CreateService().SeedAsync();

        await _repository.Received(1).UpsertRuleAsync(
            "PAGE_EXPLANATIONS_VOICE", Arg.Any<string>(), Arg.Any<int>(),
            SeedSource, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedAsync_UiElementMapRule_CarriesNoHardCodedRouteList()
    {
        string? uiElementMapContent = null;
        _repository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>());
        _repository.UpsertRuleAsync(
                GlobalAgentRuleNames.UiElementMap,
                Arg.Do<string>(c => uiElementMapContent = c),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GlobalAgentRule());

        await CreateService().SeedAsync();

        uiElementMapContent.ShouldNotBeNull();
        uiElementMapContent!.ShouldNotContain(
            "/workplace/",
            Case.Sensitive,
            "The UI map must not carry a second, hand-maintained route list — it drifts from " +
            "app-routing.module.ts. The allowed pages are the navigate_to 'page' enum values.");
    }

    [Test]
    public async Task SeedAsync_UnchangedSeedRule_IsSkipped()
    {
        string? shippedContent = null;
        _repository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>());
        _repository.UpsertRuleAsync(
                RuleName,
                Arg.Do<string>(c => shippedContent = c),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GlobalAgentRule());

        await CreateService().SeedAsync();
        shippedContent.ShouldNotBeNull();

        var secondRepository = Substitute.For<IGlobalAgentRuleRepository>();
        secondRepository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>
            {
                new() { Name = RuleName, Content = shippedContent!, Source = SeedSource, IsActive = true }
            });

        var secondService = new GlobalAgentRuleSeedService(
            secondRepository,
            Substitute.For<ILogger<GlobalAgentRuleSeedService>>());
        await secondService.SeedAsync();

        await secondRepository.DidNotReceive().UpsertRuleAsync(
            RuleName, Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
