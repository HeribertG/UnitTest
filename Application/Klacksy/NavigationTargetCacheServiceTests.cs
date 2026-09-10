// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using Shouldly;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class NavigationTargetCacheServiceTests
{
    private static IServiceScopeFactory BuildScopeFactory(INavigationTargetSynonymRepository repo)
    {
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(INavigationTargetSynonymRepository)).Returns(repo);
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    [Test]
    public void FindBySynonym_returns_targets_matching_synonym_from_db()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [{
          "targetId":"llm-provider","route":"/settings","labelKey":"settings.llm",
          "synonyms":{},"synonymStatus":"reviewed"
        }]
        """);

        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            new List<NavigationTargetSynonym>
            {
                new() { TargetId = "llm-provider", Language = "de", Keyword = "ki anbieter" },
                new() { TargetId = "llm-provider", Language = "de", Keyword = "llm provider" }
            });

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));

        sut.FindBySynonym("ki anbieter", "de").Count().ShouldBe(1);
        sut.FindBySynonym("unknown", "de").ShouldBeEmpty();
        sut.GetById("llm-provider").ShouldNotBeNull();
    }

    [Test]
    public void FindBySynonym_returns_empty_when_no_synonyms_in_db()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [{
          "targetId":"absence","route":"/workplace/absence","labelKey":"nav.absence",
          "synonyms":{"de":["abwesenheit"]},"synonymStatus":"pending"
        }]
        """);

        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>());

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));

        sut.FindBySynonym("abwesenheit", "de").ShouldBeEmpty();
        sut.GetById("absence").ShouldNotBeNull();
    }

    [Test]
    public void GetByRoute_returns_only_targets_of_that_route()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [
          {"targetId":"macros","route":"/workplace/settings","labelKey":"settings.macros","synonyms":{}},
          {"targetId":"company-rules","route":"/workplace/settings","labelKey":"settings.companyRules","synonyms":{}},
          {"targetId":"client-search-bar","route":"/workplace/client","labelKey":"client.search","synonyms":{}}
        ]
        """);

        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>());

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));

        var settingsTargets = sut.GetByRoute("/workplace/settings");

        settingsTargets.Select(t => t.TargetId).ShouldBe(new[] { "macros", "company-rules" }, ignoreOrder: true);
        sut.GetByRoute("/workplace/client").Select(t => t.TargetId).ShouldBe(new[] { "client-search-bar" });
        sut.GetByRoute("/workplace/does-not-exist").ShouldBeEmpty();
    }

    [Test]
    public void Invalidate_clears_snapshot_so_next_call_reloads()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [{
          "targetId":"t1","route":"/t1","labelKey":"t1",
          "synonyms":{}
        }]
        """);

        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>
        {
            new() { TargetId = "t1", Language = "en", Keyword = "test" }
        });

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));
        sut.FindBySynonym("test", "en").Count().ShouldBe(1);

        sut.Invalidate();

        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>());
        sut.FindBySynonym("test", "en").ShouldBeEmpty();
    }

    [Test]
    public void First_lookup_without_warm_up_sees_empty_snapshot_when_repository_is_asynchronous()
    {
        var tempFile = WriteSingleTargetManifest();
        var pendingLoad = new TaskCompletionSource<IReadOnlyList<NavigationTargetSynonym>>();
        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(pendingLoad.Task);

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));

        sut.FindBySynonym("uploadfläche", "de").ShouldBeEmpty();
        sut.GetById("erp-drop-points").ShouldBeNull();

        pendingLoad.SetResult(new List<NavigationTargetSynonym>());
    }

    [Test]
    public async Task WarmUpAsync_fills_snapshot_before_first_lookup_when_repository_is_asynchronous()
    {
        var tempFile = WriteSingleTargetManifest();
        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => LoadAfterYieldAsync());

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));
        await sut.WarmUpAsync();

        sut.FindBySynonym("uploadfläche", "de").Select(t => t.TargetId).ShouldBe(new[] { "erp-drop-points" });
        sut.GetByRoute("/workplace/settings").ShouldNotBeEmpty();
    }

    [Test]
    public async Task WarmUpAsync_reloads_even_when_snapshot_is_still_fresh()
    {
        var tempFile = WriteSingleTargetManifest();
        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>());

        var sut = new NavigationTargetCacheService(tempFile, BuildScopeFactory(synonymRepo));
        sut.FindBySynonym("uploadfläche", "de").ShouldBeEmpty();

        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>
        {
            new() { TargetId = "erp-drop-points", Language = "de", Keyword = "uploadfläche" }
        });
        await sut.WarmUpAsync();

        sut.FindBySynonym("uploadfläche", "de").Count.ShouldBe(1);
    }

    private static async Task<IReadOnlyList<NavigationTargetSynonym>> LoadAfterYieldAsync()
    {
        await Task.Yield();
        return new List<NavigationTargetSynonym>
        {
            new() { TargetId = "erp-drop-points", Language = "de", Keyword = "uploadfläche" }
        };
    }

    private static string WriteSingleTargetManifest()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [{
          "targetId":"erp-drop-points","route":"/workplace/settings","labelKey":"settings.erpDropPoints",
          "synonyms":{}
        }]
        """);
        return tempFile;
    }
}
