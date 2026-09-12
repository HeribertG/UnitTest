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
    private static IServiceScopeFactory BuildScopeFactory(
        INavigationTargetSynonymRepository repo, IFeatureAvailabilityService? featureAvailability = null)
    {
        var availability = featureAvailability ?? Substitute.For<IFeatureAvailabilityService>();
        if (featureAvailability == null)
        {
            availability.IsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(INavigationTargetSynonymRepository)).Returns(repo);
        provider.GetService(typeof(IFeatureAvailabilityService)).Returns(availability);
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

    // The matcher behind the chat fast-path does not ask a second time, so a feature-gated target that
    // survives into the snapshot is a navigation the Angular guard then bounces to /no-access.
    [Test]
    public void Targets_of_an_unavailable_feature_are_dropped_from_the_snapshot()
    {
        var sut = new NavigationTargetCacheService(
            WriteFeatureGatedManifest(), BuildScopeFactory(EmptySynonymRepo(), AvailabilityOf(messaging: false)));

        sut.GetById("messaging").ShouldBeNull();
        sut.GetByRoute("/workplace/messaging").ShouldBeEmpty();
        sut.FindBySynonym("nachrichten", "de").ShouldBeEmpty();
        sut.GetById("dashboard").ShouldNotBeNull();
    }

    [Test]
    public void Targets_of_an_available_feature_stay_in_the_snapshot()
    {
        var sut = new NavigationTargetCacheService(
            WriteFeatureGatedManifest(), BuildScopeFactory(EmptySynonymRepo(), AvailabilityOf(messaging: true)));

        sut.GetById("messaging").ShouldNotBeNull();
        sut.GetByRoute("/workplace/messaging").Select(t => t.TargetId).ShouldBe(new[] { "messaging" });
        sut.FindBySynonym("nachrichten", "de").Select(t => t.TargetId).ShouldBe(new[] { "messaging" });
        sut.GetById("dashboard").ShouldNotBeNull();
    }

    [Test]
    public void Feature_availability_is_asked_once_per_distinct_feature_per_reload()
    {
        var availability = AvailabilityOf(messaging: true);
        var sut = new NavigationTargetCacheService(
            WriteFeatureGatedManifest(), BuildScopeFactory(EmptySynonymRepo(), availability));

        sut.GetById("dashboard").ShouldNotBeNull();

        availability.Received(1).IsAvailableAsync("messaging", Arg.Any<CancellationToken>());
    }

    private static IFeatureAvailabilityService AvailabilityOf(bool messaging)
    {
        var availability = Substitute.For<IFeatureAvailabilityService>();
        availability.IsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        availability.IsAvailableAsync("messaging", Arg.Any<CancellationToken>()).Returns(messaging);
        return availability;
    }

    private static INavigationTargetSynonymRepository EmptySynonymRepo()
    {
        var repo = Substitute.For<INavigationTargetSynonymRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<NavigationTargetSynonym>
        {
            new() { TargetId = "messaging", Language = "de", Keyword = "nachrichten" }
        });
        return repo;
    }

    private static string WriteFeatureGatedManifest()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        [
          {"targetId":"messaging","route":"/workplace/messaging","labelKey":"nav.messaging",
           "category":"page","requiredFeature":"messaging","synonyms":{}},
          {"targetId":"dashboard","route":"/workplace/dashboard","labelKey":"nav.dashboard",
           "category":"page","synonyms":{}}
        ]
        """);
        return tempFile;
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
