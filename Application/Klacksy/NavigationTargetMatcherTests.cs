// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using Shouldly;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Domain.Constants;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class NavigationTargetMatcherTests
{
    private INavigationTargetCacheService _cache = null!;
    private NavigationTargetMatcher _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Substitute.For<INavigationTargetCacheService>();
        _sut = new NavigationTargetMatcher(_cache);
    }

    [Test]
    public void Match_returns_fast_path_on_exact_synonym_hit()
    {
        var target = new NavigationTarget { TargetId = "llm-provider", Route = "/settings", LabelKey = "x" };
        _cache.FindBySynonym("llm provider", "de").Returns(new[] { target });

        var result = _sut.Match("llm provider", "de", Array.Empty<string>());

        result.TargetId.ShouldBe("llm-provider");
        result.Score.ShouldBe(1.0);
        result.Tier.ShouldBe(NavigationMatchTier.Exact);
        result.Candidates.Count().ShouldBe(1);
        result.IsFastPath.ShouldBeTrue();
    }

    [Test]
    public void Match_sets_fast_path_when_exact_synonym_resolves_to_exactly_one_allowed_target()
    {
        var target = new NavigationTarget { TargetId = "only-target", Route = "/only", LabelKey = "x" };
        _cache.FindBySynonym("schichten", "de").Returns(new[] { target });

        var result = _sut.Match("schichten", "de", Array.Empty<string>());

        result.TargetId.ShouldBe("only-target");
        result.Candidates.Count().ShouldBe(1);
        result.IsFastPath.ShouldBeTrue();
    }

    [Test]
    public void Match_does_not_set_fast_path_when_exact_synonym_resolves_to_more_than_one_allowed_target()
    {
        var first = new NavigationTarget { TargetId = "shift-plan", Route = "/shifts/plan", LabelKey = "x" };
        var second = new NavigationTarget { TargetId = "shift-order", Route = "/shifts/order", LabelKey = "y" };
        _cache.FindBySynonym("schichten", "de").Returns(new[] { first, second });

        var result = _sut.Match("schichten", "de", Array.Empty<string>());

        result.Score.ShouldBe(1.0);
        result.Tier.ShouldBe(NavigationMatchTier.Exact);
        result.Candidates.Count().ShouldBe(2);
        result.IsFastPath.ShouldBeFalse();
    }

    [Test]
    public void Match_does_not_fast_path_when_synonym_only_exists_in_a_third_locale()
    {
        var target = new NavigationTarget { TargetId = "some-target", Route = "/x", LabelKey = "x" };
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>()).Returns(Array.Empty<NavigationTarget>());
        _cache.FindBySynonymAnyLocale(Arg.Any<string>()).Returns(new[] { target });

        var result = _sut.Match("selesai", "de", Array.Empty<string>());

        result.Tier.ShouldBe(NavigationMatchTier.TokenOverlap);
        result.IsFastPath.ShouldBeFalse();
    }

    [Test]
    public void Match_falls_back_to_english_locale_for_exact_match_when_user_locale_has_no_hit()
    {
        var target = new NavigationTarget { TargetId = "t-en", Route = "/en", LabelKey = "x" };
        _cache.FindBySynonym("overtime", "de").Returns(Array.Empty<NavigationTarget>());
        _cache.FindBySynonym("overtime", "en").Returns(new[] { target });

        var result = _sut.Match("overtime", "de", Array.Empty<string>());

        result.TargetId.ShouldBe("t-en");
        result.Tier.ShouldBe(NavigationMatchTier.Exact);
        result.IsFastPath.ShouldBeTrue();
    }

    [Test]
    public void Match_does_not_query_english_locale_when_locale_synonym_already_matches()
    {
        var target = new NavigationTarget { TargetId = "t-de", Route = "/de", LabelKey = "x" };
        _cache.FindBySynonym("schichten", "de").Returns(new[] { target });

        var result = _sut.Match("schichten", "de", Array.Empty<string>());

        result.TargetId.ShouldBe("t-de");
        _cache.DidNotReceive().FindBySynonym(Arg.Any<string>(), "en");
    }

    [Test]
    public void Match_skips_permission_gated_target_when_user_lacks_right()
    {
        var target = new NavigationTarget { TargetId = "admin-only", Route = "/settings", LabelKey = "x", RequiredPermission = Roles.Admin };
        _cache.FindBySynonym("admin", "de").Returns(new[] { target });

        var result = _sut.Match("admin", "de", Array.Empty<string>());

        result.TargetId.ShouldBeNull();
        result.Candidates.ShouldBeEmpty();
    }

    [Test]
    public void Match_allows_permission_gated_target_for_admin_via_the_shared_permission_helper()
    {
        var target = new NavigationTarget
        {
            TargetId = "admin-only",
            Route = "/settings",
            LabelKey = "x",
            RequiredPermission = Permissions.CanEditSettings
        };
        _cache.FindBySynonym("admin", "de").Returns(new[] { target });

        var result = _sut.Match("admin", "de", new[] { Roles.Admin });

        result.TargetId.ShouldBe("admin-only");
        result.Tier.ShouldBe(NavigationMatchTier.Exact);
    }

    [Test]
    public void Match_requires_every_element_of_a_comma_separated_permission_list()
    {
        var target = new NavigationTarget
        {
            TargetId = "double-gated",
            Route = "/settings",
            LabelKey = "x",
            RequiredPermission = $"{Permissions.CanViewSettings}, {Permissions.CanEditSettings}"
        };
        _cache.FindBySynonym("gated", "de").Returns(new[] { target });

        var partial = _sut.Match("gated", "de", new[] { Permissions.CanViewSettings });
        var complete = _sut.Match("gated", "de", new[] { Permissions.CanViewSettings, Permissions.CanEditSettings });

        partial.TargetId.ShouldBeNull();
        complete.TargetId.ShouldBe("double-gated");
    }

    [Test]
    public void Match_returns_null_when_no_synonyms_hit_any_token()
    {
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>()).Returns(Array.Empty<NavigationTarget>());

        var result = _sut.Match("unknown query here", "de", Array.Empty<string>());

        result.TargetId.ShouldBeNull();
        result.Candidates.ShouldBeEmpty();
    }

    [Test]
    public void Match_accumulates_token_overlap_score_and_picks_top_candidate()
    {
        var target = new NavigationTarget { TargetId = "t1", Route = "/t1", LabelKey = "x" };
        _cache.FindBySynonym("foo", "de").Returns(new[] { target });
        _cache.FindBySynonym("bar", "de").Returns(new[] { target });

        var result = _sut.Match("foo bar", "de", Array.Empty<string>());

        result.TargetId.ShouldBe("t1");
        result.Score.ShouldBe(0.85);
        result.Candidates.Count().ShouldBe(1);
        result.Tier.ShouldBe(NavigationMatchTier.TokenOverlap);
        result.IsFastPath.ShouldBeFalse();
    }

    [Test]
    public void Match_returns_no_fast_path_when_token_overlap_just_below_threshold()
    {
        var target = new NavigationTarget { TargetId = "t1", Route = "/t1", LabelKey = "x" };
        _cache.FindBySynonym("foo", "de").Returns(new[] { target });
        _cache.FindBySynonym("bar", "de").Returns(new[] { target });
        _cache.FindBySynonym(Arg.Is<string>(s => s != "foo" && s != "bar"), "de")
            .Returns(Array.Empty<NavigationTarget>());

        var result = _sut.Match("foo bar baz", "de", Array.Empty<string>());

        result.Score.ShouldBe(2.0 / 3.0, 0.001);
        result.IsFastPath.ShouldBeFalse();
    }

    [Test]
    public void Match_returns_null_when_top_score_below_threshold()
    {
        var target = new NavigationTarget { TargetId = "t1", Route = "/t1", LabelKey = "x" };
        _cache.FindBySynonym("foo", "de").Returns(new[] { target });
        _cache.FindBySynonym(Arg.Is<string>(s => s != "foo"), "de").Returns(Array.Empty<NavigationTarget>());

        var result = _sut.Match("foo bar baz qux", "de", Array.Empty<string>());

        result.TargetId.ShouldBeNull();
        result.Candidates.Count().ShouldBe(1);
        result.Score.ShouldBe(0.25, 0.001);
    }

    [Test]
    public void Match_returns_fuzzy_candidate_when_typo_is_close_to_a_synonym()
    {
        var target = new NavigationTarget
        {
            TargetId = "schedule",
            Route = "/workplace/schedule",
            LabelKey = "nav.schedule",
            Synonyms = new Dictionary<string, string[]>
            {
                ["de"] = new[] { "einsatzplan", "schichtplan", "dienstplan" },
            },
        };
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Array.Empty<NavigationTarget>());
        _cache.FindBySynonymAnyLocale(Arg.Any<string>())
            .Returns(Array.Empty<NavigationTarget>());
        _cache.All.Returns(new[] { target });

        var result = _sut.Match("einsatplan", "de", Array.Empty<string>());

        result.TargetId.ShouldBe("schedule");
        result.Score.ShouldBeGreaterThan(0.6);
        result.Candidates.Count().ShouldBe(1);
        result.Tier.ShouldBe(NavigationMatchTier.Fuzzy);
        result.IsFastPath.ShouldBeFalse();
    }

    [Test]
    public void Match_skips_fuzzy_target_when_user_lacks_permission()
    {
        var target = new NavigationTarget
        {
            TargetId = "schedule",
            Route = "/workplace/schedule",
            LabelKey = "nav.schedule",
            RequiredPermission = "CanViewSchedule",
            Synonyms = new Dictionary<string, string[]>
            {
                ["de"] = new[] { "einsatzplan" },
            },
        };
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Array.Empty<NavigationTarget>());
        _cache.FindBySynonymAnyLocale(Arg.Any<string>())
            .Returns(Array.Empty<NavigationTarget>());
        _cache.All.Returns(new[] { target });

        var result = _sut.Match("einsatplan", "de", Array.Empty<string>());

        result.TargetId.ShouldBeNull();
        result.Candidates.ShouldBeEmpty();
    }

    [Test]
    public void Match_fuzzy_returns_nothing_when_no_synonym_is_similar_enough()
    {
        var target = new NavigationTarget
        {
            TargetId = "schedule",
            Route = "/workplace/schedule",
            LabelKey = "nav.schedule",
            Synonyms = new Dictionary<string, string[]>
            {
                ["de"] = new[] { "einsatzplan" },
            },
        };
        _cache.FindBySynonym(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Array.Empty<NavigationTarget>());
        _cache.FindBySynonymAnyLocale(Arg.Any<string>())
            .Returns(Array.Empty<NavigationTarget>());
        _cache.All.Returns(new[] { target });

        var result = _sut.Match("completely unrelated query", "de", Array.Empty<string>());

        result.TargetId.ShouldBeNull();
    }
}
