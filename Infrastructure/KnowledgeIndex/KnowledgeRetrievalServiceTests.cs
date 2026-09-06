// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeRetrievalServiceTests
{
    private IEmbeddingProvider _embeddings = null!;
    private IRerankerProvider _reranker = null!;
    private IKnowledgeIndexRepository _repo = null!;
    private KnowledgeRetrievalService _service = null!;

    [SetUp]
    public void Setup()
    {
        _embeddings = Substitute.For<IEmbeddingProvider>();
        _reranker = Substitute.For<IRerankerProvider>();
        _repo = Substitute.For<IKnowledgeIndexRepository>();

        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        _service = new KnowledgeRetrievalService(_embeddings, _reranker, _repo);
    }

    // The preload only pays off when it overlaps the query embedding, and it is only safe when the
    // scores are not asked for before it has finished: an uncompleted preload must hold ScoreAsync back.
    [Test]
    public async Task RetrieveAsync_StartsTheRerankerPreloadEarlyAndWaitsForItBeforeScoring()
    {
        var preload = new TaskCompletionSource();
        _reranker.EnsureLoadedAsync(Arg.Any<CancellationToken>()).Returns(preload.Task);
        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "s", Text = "s. Skill." }]);
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        var retrieval = _service.RetrieveAsync("open shifts", [], false, 5, currentRoute: null, CancellationToken.None);

        await _reranker.Received(1).EnsureLoadedAsync(Arg.Any<CancellationToken>());
        await _repo.Received(1).FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        retrieval.IsCompleted.ShouldBeFalse();
        await _reranker.DidNotReceive().ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        preload.SetResult();
        var result = await retrieval;

        result.Candidates.Count.ShouldBe(1);
        await _reranker.Received(1).ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    // A failed head start is not a failed retrieval: whatever broke the preload breaks ScoreAsync too,
    // and that is where it belongs. A query without candidates must not fail on a task nobody needed.
    [Test]
    public async Task RetrieveAsync_RerankerPreloadFails_StillScoresAndReturnsTheResult()
    {
        _reranker.EnsureLoadedAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("model download failed")));
        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "s", Text = "s. Skill." }]);
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        var result = await _service.RetrieveAsync("open shifts", [], false, 5, currentRoute: null, CancellationToken.None);

        result.Candidates.Count.ShouldBe(1);
    }

    // The whole point of the [retrieval] line is that it emits at runtime. A stopwatch that never
    // reaches a log is indistinguishable from no instrumentation at all, and the numbers it reports
    // are the only ones this chain has - so the wiring is pinned here rather than assumed.
    [Test]
    public async Task RetrieveAsync_EmitsOneRetrievalLineCarryingCandidateCountAndPassOrdinal()
    {
        var logger = new CapturingLogger();
        var counter = new RetrievalCallCounter();
        var service = new KnowledgeRetrievalService(_embeddings, _reranker, _repo, logger, counter);

        var skill = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "ListOpenShifts",
            Text = "ListOpenShifts. Returns open shifts."
        };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([skill]);
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        await service.RetrieveAsync("open shifts", [], false, 5, currentRoute: null, CancellationToken.None);
        await service.RetrieveAsync("open shifts", [], false, 5, currentRoute: null, CancellationToken.None);

        var lines = logger.Messages.Where(m => m.Contains("[retrieval]")).ToList();

        lines.Count.ShouldBe(2);
        lines[0].ShouldContain("call=1");
        lines[1].ShouldContain("call=2");
        lines[0].ShouldContain("cands=1");
        lines[0].ShouldContain($"chars={skill.Text.Length}");
    }

    // The query must never reach the log in clear text - it carries whatever the user typed.
    [Test]
    public async Task RetrieveAsync_RetrievalLineHashesTheQueryInsteadOfLoggingIt()
    {
        var logger = new CapturingLogger();
        var service = new KnowledgeRetrievalService(_embeddings, _reranker, _repo, logger, new RetrievalCallCounter());

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "s", Text = "t" }]);
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        await service.RetrieveAsync("streng geheime anfrage", [], false, 5, currentRoute: null, CancellationToken.None);

        var line = logger.Messages.Single(m => m.Contains("[retrieval]"));
        line.ShouldNotContain("streng geheime anfrage");
        line.ShouldContain("query=");
    }

    // The kind predicate must reach the KNN query itself: the index holds ~450 skills against ~24
    // recipes, so a kind-blind top-N is almost always all skills and a recipe caller filtering
    // afterwards is usually left with nothing. The mock only answers the kind-filtered call, so a
    // service that stopped forwarding the predicate would come back empty here.
    [Test]
    public async Task RetrieveAsync_WithKindFilter_ForwardsThePredicateToTheKnnQuery()
    {
        var recipe = new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "r", Text = "recipe text" };
        _repo.FindNearestAsync(
                Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>(), KnowledgeEntryKind.Recipe)
            .Returns([recipe]);
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        var result = await _service.RetrieveAsync(
            "plan a recipe", [], false, 3, currentRoute: null, CancellationToken.None, KnowledgeEntryKind.Recipe);

        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Entry.SourceId.ShouldBe("r");
        await _repo.Received(1).FindNearestAsync(
            Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>(), KnowledgeEntryKind.Recipe);
    }

    private sealed class CapturingLogger : ILogger<KnowledgeRetrievalService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Test]
    public async Task RetrieveAsync_DropsEndpointWhenWrappingSkillIsInResult()
    {
        var skill = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "ListOpenShifts",
            ExposedEndpointKey = "GET /api/backend/shifts",
            Text = "ListOpenShifts. Returns open shifts."
        };
        var endpoint = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Endpoint,
            SourceId = "GET /api/backend/shifts",
            Text = "GET /api/backend/shifts. Lists shifts."
        };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([skill, endpoint]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        var result = await _service.RetrieveAsync("open shifts", [], false, 5, currentRoute: null, CancellationToken.None);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Entry.SourceId.ShouldBe("ListOpenShifts");
    }

    [Test]
    public async Task RetrieveAsync_ScoreBelowCutoff_ReturnsEmpty()
    {
        var entry = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "SomeSkill",
            Text = "Some skill text."
        };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { KnowledgeIndexConstants.DefaultScoreCutoff - 0.01 });

        var result = await _service.RetrieveAsync("query", [], false, 5, currentRoute: null, CancellationToken.None);

        result.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public async Task RetrieveAsync_AdminBypass_ForwardedToRepository()
    {
        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<KnowledgeEntry>());

        await _service.RetrieveAsync("query", [], isAdmin: true, 5, currentRoute: null, CancellationToken.None);

        await _repo.Received(1).FindNearestAsync(
            Arg.Any<float[]>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Is<bool>(bypass => bypass),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RetrieveAsync_WhitespaceQuery_ReturnsEmpty()
    {
        var result = await _service.RetrieveAsync("   ", [], false, 5, currentRoute: null, CancellationToken.None);

        result.IsEmpty.ShouldBeTrue();
        await _repo.DidNotReceive().FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RetrieveAsync_CandidatesRankedByScore()
    {
        var high = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "HighScore", Text = "High" };
        var low = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "LowScore", Text = "Low" };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([high, low]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.5, 0.9 });

        var result = await _service.RetrieveAsync("query", [], false, 5, currentRoute: null, CancellationToken.None);

        result.Candidates.Count().ShouldBe(2);
        result.Candidates[0].Entry.SourceId.ShouldBe("LowScore");
        result.Candidates[1].Entry.SourceId.ShouldBe("HighScore");
    }

    [Test]
    public async Task RetrieveAsync_CurrentRouteMatchesBoostTable_LiftsMatchingSkillAboveHigherRawScore()
    {
        var pageSkill = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "create_shift", Text = "Creates a shift." };
        var unrelated = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "unrelated_skill", Text = "Something else." };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pageSkill, unrelated]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.5, 0.53 });

        var result = await _service.RetrieveAsync(
            "query", [], false, 5, "/workplace/schedule/week", CancellationToken.None);

        result.Candidates[0].Entry.SourceId.ShouldBe("create_shift");
    }

    [Test]
    public async Task RetrieveAsync_CurrentRouteNotInBoostTable_NoBoostApplied()
    {
        var pageSkill = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "create_shift", Text = "Creates a shift." };
        var unrelated = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "unrelated_skill", Text = "Something else." };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pageSkill, unrelated]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.5, 0.53 });

        var result = await _service.RetrieveAsync(
            "query", [], false, 5, "/workplace/unmapped-page", CancellationToken.None);

        result.Candidates[0].Entry.SourceId.ShouldBe("unrelated_skill");
    }

    [Test]
    public async Task RetrieveAsync_RouteBoost_NeverFiltersOutNonMatchingSkills()
    {
        var pageSkill = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "create_shift", Text = "Creates a shift." };
        var unrelated = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "unrelated_skill", Text = "Something else." };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pageSkill, unrelated]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.5, 0.53 });

        var result = await _service.RetrieveAsync(
            "query", [], false, 5, "/workplace/schedule/week", CancellationToken.None);

        result.Candidates.Count().ShouldBe(2);
    }

    [Test]
    public async Task RetrieveAsync_RouteBoost_NeverLiftsSubCutoffSkillIntoTheResult()
    {
        var pageSkill = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "create_shift", Text = "Creates a shift." };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pageSkill]);

        // Just below the cutoff, not far below: any boost factor above 1.12 would carry this score over
        // the cutoff. So the entry can only stay out if the filter runs on the raw score — which is the
        // invariant this test is named for. A score of cutoff/2 would pass even with the order reversed.
        var subCutoffScore = KnowledgeIndexConstants.DefaultScoreCutoff * 0.9;

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { subCutoffScore });

        var result = await _service.RetrieveAsync(
            "query", [], false, 5, "/workplace/schedule/week", CancellationToken.None);

        result.IsEmpty.ShouldBeTrue();
    }

    // Both scores are measured cross-encoder output from the hard golden set: correct targets can sit
    // deep in the reranker's lower mode and still be ranked correctly. A route boost must not reorder
    // across that gap — the boosted entry here scores seven times lower than the entry it displaces.
    [Test]
    public async Task RetrieveAsync_RouteBoost_DoesNotLiftAMuchWeakerSkillOverAStrongerOne()
    {
        var pageSkill = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "create_absence", Text = "Creates an absence." };
        var stronger = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "get_period_hours", Text = "Returns period hours." };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([pageSkill, stronger]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.002, 0.0139 });

        var result = await _service.RetrieveAsync(
            "query", [], false, 5, "/workplace/absence", CancellationToken.None);

        result.Candidates[0].Entry.SourceId.ShouldBe("get_period_hours");
    }
}
