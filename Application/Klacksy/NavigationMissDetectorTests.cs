// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using Shouldly;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class NavigationMissDetectorTests
{
    private const string Route = "/workplace/edit-address";
    private const string OtherRoute = "/workplace/settings";
    private const string CandidateTargetId = "erp-drop-points";

    private INavigationTargetCacheService _targetCache = null!;
    private NavigationMissDetector _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _targetCache = Substitute.For<INavigationTargetCacheService>();
        _targetCache.GetById(Arg.Any<string>())
            .Returns(new NavigationTarget { TargetId = CandidateTargetId, Route = Route, LabelKey = "x" });
        _sut = new NavigationMissDetector(_targetCache);
    }

    private static NavigationMatchResult MatchWith(params NavigationCandidate[] candidates) => new()
    {
        TargetId = null,
        Route = null,
        Score = 0.0,
        Tier = NavigationMatchTier.TokenOverlap,
        Candidates = candidates
    };

    private static NavigationCandidate Candidate(double score, string route = Route) => new(CandidateTargetId, route, score);

    [Test]
    public void DetectSuspectedMiss_returns_null_when_navigated_route_is_null()
    {
        var match = MatchWith(Candidate(0.6));

        var result = _sut.DetectSuspectedMiss(match, null, null);

        result.ShouldBeNull();
    }

    [Test]
    public void DetectSuspectedMiss_returns_null_when_navigated_route_is_empty()
    {
        var match = MatchWith(Candidate(0.6));

        var result = _sut.DetectSuspectedMiss(match, string.Empty, null);

        result.ShouldBeNull();
    }

    [Test]
    public void DetectSuspectedMiss_returns_null_when_navigated_target_is_set()
    {
        var match = MatchWith(Candidate(0.6));

        var result = _sut.DetectSuspectedMiss(match, Route, CandidateTargetId);

        result.ShouldBeNull();
    }

    [Test]
    public void DetectSuspectedMiss_returns_candidate_between_min_score_and_fast_path_on_same_route()
    {
        var candidate = Candidate(0.6);
        var match = MatchWith(candidate);

        var result = _sut.DetectSuspectedMiss(match, Route, null);

        result.ShouldBe(candidate);
    }

    [Test]
    public void DetectSuspectedMiss_returns_null_when_score_equals_fast_path_threshold()
    {
        var match = MatchWith(Candidate(NavigationMatchThresholds.FastPath));

        var result = _sut.DetectSuspectedMiss(match, Route, null);

        result.ShouldBeNull();
    }

    [Test]
    public void DetectSuspectedMiss_returns_null_when_score_is_below_min_score_for_match()
    {
        var match = MatchWith(Candidate(0.49));

        var result = _sut.DetectSuspectedMiss(match, Route, null);

        result.ShouldBeNull();
    }

    [Test]
    public void DetectSuspectedMiss_returns_null_when_candidate_is_on_a_different_route()
    {
        var match = MatchWith(Candidate(0.6, OtherRoute));

        var result = _sut.DetectSuspectedMiss(match, Route, null);

        result.ShouldBeNull();
    }

    [Test]
    public void DetectSuspectedMiss_matches_entity_route_suffix()
    {
        var candidate = Candidate(0.6);
        var match = MatchWith(candidate);
        var navigatedRoute = Route + "/3e2f1a10-3b1a-4b1a-9c1a-2f1a3b1a4b1a";

        var result = _sut.DetectSuspectedMiss(match, navigatedRoute, null);

        result.ShouldBe(candidate);
    }

    [Test]
    public void DetectSuspectedMiss_ignores_query_string_on_navigated_route()
    {
        var candidate = Candidate(0.6, OtherRoute);
        var match = MatchWith(candidate);
        var navigatedRoute = OtherRoute + "?tab=x";

        var result = _sut.DetectSuspectedMiss(match, navigatedRoute, null);

        result.ShouldBe(candidate);
    }

    [Test]
    public void DetectSuspectedMiss_picks_the_highest_scoring_candidate()
    {
        var weaker = Candidate(0.55);
        var stronger = Candidate(0.7);
        var match = MatchWith(weaker, stronger);

        var result = _sut.DetectSuspectedMiss(match, Route, null);

        result.ShouldBe(stronger);
    }

    [Test]
    public void DetectSuspectedMiss_returns_null_when_candidate_is_page_level()
    {
        _targetCache.GetById(CandidateTargetId)
            .Returns(new NavigationTarget
            {
                TargetId = CandidateTargetId,
                Route = Route,
                LabelKey = "x",
                Category = NavigationTargetCategories.PageLevel
            });
        var match = MatchWith(Candidate(0.6));

        var result = _sut.DetectSuspectedMiss(match, Route, null);

        result.ShouldBeNull();
    }
}
