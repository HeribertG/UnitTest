// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using Shouldly;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class NavigationTargetCatalogTests
{
    private INavigationTargetCacheService _cache = null!;
    private NavigationTargetCatalog _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Substitute.For<INavigationTargetCacheService>();
        _sut = new NavigationTargetCatalog(_cache);
    }

    [Test]
    public void GetByRoute_maps_targetId_and_synonyms_and_drops_route_and_labelKey()
    {
        var target = new NavigationTarget
        {
            TargetId = "macros",
            Route = "/workplace/settings",
            LabelKey = "settings.macros",
            Synonyms = new Dictionary<string, string[]> { ["de"] = new[] { "Makros" } }
        };
        _cache.GetByRoute("/workplace/settings").Returns(new[] { target });

        var result = _sut.GetByRoute("/workplace/settings");

        result.Count.ShouldBe(1);
        result[0].TargetId.ShouldBe("macros");
        result[0].Synonyms["de"].ShouldBe(new[] { "Makros" });
    }

    [Test]
    public void GetByRoute_returns_empty_when_cache_has_no_targets_for_the_route()
    {
        _cache.GetByRoute("/workplace/unknown").Returns(Array.Empty<NavigationTarget>());

        var result = _sut.GetByRoute("/workplace/unknown");

        result.ShouldBeEmpty();
    }
}
