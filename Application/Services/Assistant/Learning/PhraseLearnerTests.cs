// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for one round of phrase learning. The rules that must hold: a wording is only kept when the
/// original wish reaches the target because of it, a wording that breaks earlier learning is rolled back,
/// and a failed round leaves the index exactly as it found it. The last one is what stops the loop from
/// slowly filling the index with wordings nobody measured.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class PhraseLearnerTests
{
    private const string Target = "revenue_per_client";
    private const string Excerpt = "Zeige mir die Umsatzstatistik pro Kunde";

    private static readonly Agent DefaultAgent = new() { Id = Guid.NewGuid(), Name = "Klacksy" };
    private static readonly Guid ClusterId = Guid.NewGuid();

    private IAgentRepository _agents = null!;
    private IAgentSkillRepository _skills = null!;
    private ISkillPhraseRepository _phrases = null!;
    private ISkillLearningCandidateRepository _candidates = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private ILearnedArtifactGenerator _generator = null!;
    private ISkillRoutingOracle _oracle = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private PhraseLearner _learner = null!;

    [SetUp]
    public void SetUp()
    {
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(DefaultAgent);

        _skills = Substitute.For<IAgentSkillRepository>();
        _skills.GetByNameAsync(DefaultAgent.Id, Target, Arg.Any<CancellationToken>())
            .Returns(new AgentSkill { Id = Guid.NewGuid(), Name = Target, Description = "Revenue per client." });

        _phrases = Substitute.For<ISkillPhraseRepository>();
        _phrases.GetPhraseTextsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _phrases.TryAddLearnedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        _candidates = Substitute.For<ISkillLearningCandidateRepository>();
        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        _generator = Substitute.For<ILearnedArtifactGenerator>();
        _oracle = Substitute.For<ISkillRoutingOracle>();
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _refresher = Substitute.For<ISkillCatalogRefresher>();

        _learner = new PhraseLearner(
            _agents, _skills, _phrases, _candidates, _goldenCases, _generator, _oracle, _refresher,
            Substitute.For<ILogger<PhraseLearner>>());
    }

    private static SkillLearningClusterContext Cluster(string locale = "de", string? lastError = null) =>
        new(ClusterId, Excerpt, locale, null, null, [], 0, lastError);

    private void GivenPhrases(params string[] phrases) =>
        _generator.GeneratePhrasesAsync(
                Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(phrases);

    private void GivenProbe(string utterance, bool found) =>
        _oracle.ProbeAsync(utterance, Arg.Any<string?>(), Target, Arg.Any<CancellationToken>())
            .Returns(new SkillRoutingProbe(found, found ? [Target] : ["list_clients"]));

    [Test]
    public async Task AWordingThatMakesTheWishReachTheSkill_IsKept()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, true);
        GivenProbe("umsatz pro kunde", true);

        var outcome = await _learner.LearnAsync(Cluster(), Target);

        outcome.Learned.ShouldBeTrue();
        outcome.Phrase.ShouldBe("umsatz pro kunde");
        await _phrases.DidNotReceive().SetStatusAsync(
            Arg.Any<Guid>(), SkillPhraseStatuses.Rejected, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASuccessfulRound_FreezesTheWishAsAGoldenCase()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, true);
        GivenProbe("umsatz pro kunde", true);

        await _learner.LearnAsync(Cluster(), Target);

        await _goldenCases.Received(1).AddAsync(
            Arg.Is<SkillLearningGoldenCase>(c =>
                c.Query == Excerpt && c.ExpectedSourceId == Target && c.ClusterId == ClusterId),
            Arg.Any<CancellationToken>());
    }

    // The probes read the knowledge index. A refresh that only schedules the sync would let them judge
    // the index as it was before the wording was written, so the learner must wait for the sync.
    [Test]
    public async Task TheProbe_RunsOnlyAfterTheIndexSyncWasAwaited()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, true);
        GivenProbe("umsatz pro kunde", true);
        var order = new List<string>();
        _refresher
            .When(r => r.RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("refresh"));
        _oracle
            .When(o => o.ProbeAsync(Excerpt, Arg.Any<string?>(), Target, Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("probe"));

        await _learner.LearnAsync(Cluster(), Target);

        order.Take(2).ShouldBe(["refresh", "probe"]);
        await _refresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Without the rollback a rejected wording would stay in the index for good: it was written before it
    // was judged, because there is no other way to judge it.
    [Test]
    public async Task AWordingThatDoesNotHelp_IsWithdrawnAgain()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, false);
        GivenProbe("umsatz pro kunde", true);

        var outcome = await _learner.LearnAsync(Cluster(), Target);

        outcome.Learned.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldContain("list_clients");
        await _phrases.Received(1).SetStatusAsync(
            Arg.Any<Guid>(), SkillPhraseStatuses.Rejected, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWordingThatBreaksEarlierLearning_IsWithdrawnAgain()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, true);
        GivenProbe("umsatz pro kunde", true);
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([], ["'ferien buchen' no longer reaches 'create_absence'"]);

        var outcome = await _learner.LearnAsync(Cluster(), Target);

        outcome.Learned.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldContain("create_absence");
        await _phrases.Received(1).SetStatusAsync(
            Arg.Any<Guid>(), SkillPhraseStatuses.Rejected, Arg.Any<CancellationToken>());
    }

    // A case that was already red before anything was written is not this wording's fault.
    [Test]
    public async Task AGoldenCaseThatWasAlreadyFailing_IsNotCountedAgainstTheWording()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, true);
        GivenProbe("umsatz pro kunde", true);
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(["already broken"]);

        (await _learner.LearnAsync(Cluster(), Target)).Learned.ShouldBeTrue();
    }

    [Test]
    public async Task TheFirstWordingThatWorks_EndsTheRound()
    {
        GivenPhrases("erste", "zweite");
        _oracle.ProbeAsync(Arg.Any<string>(), Arg.Any<string?>(), Target, Arg.Any<CancellationToken>())
            .Returns(new SkillRoutingProbe(true, [Target]));

        var outcome = await _learner.LearnAsync(Cluster(), Target);

        outcome.Phrase.ShouldBe("erste");
        await _phrases.Received(1).TryAddLearnedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWordingThatIsAlreadyIndexed_IsSkippedWithoutARefresh()
    {
        GivenPhrases("umsatz pro kunde");
        _phrases.TryAddLearnedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var outcome = await _learner.LearnAsync(Cluster(), Target);

        outcome.Learned.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldContain("already indexed");
        await _refresher.DidNotReceive().RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutAGeneratedWording_NothingIsWritten()
    {
        GivenPhrases();

        var outcome = await _learner.LearnAsync(Cluster(), Target);

        outcome.Learned.ShouldBeFalse();
        await _phrases.DidNotReceive().TryAddLearnedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnknownTargetSkill_EndsTheRoundBeforeAnyModelIsAsked()
    {
        _skills.GetByNameAsync(DefaultAgent.Id, "ghost", Arg.Any<CancellationToken>())
            .Returns((AgentSkill?)null);

        var outcome = await _learner.LearnAsync(Cluster(), "ghost");

        outcome.Learned.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldContain("ghost");
        await _generator.DidNotReceive().GeneratePhrasesAsync(
            Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheErrorOfThePreviousRound_SeedsTheGenerator()
    {
        GivenPhrases("umsatz pro kunde");
        GivenProbe(Excerpt, true);
        GivenProbe("umsatz pro kunde", true);

        await _learner.LearnAsync(Cluster(lastError: "list_clients was offered instead"), Target);

        await _generator.Received(1).GeneratePhrasesAsync(
            Arg.Any<SkillLearningClusterContext>(), Target, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), "list_clients was offered instead", Arg.Any<CancellationToken>());
    }

    // skill_phrase holds one language tag per row, so a regional locale has to become its base language;
    // an unknown one must not be guessed into a real language.
    [TestCase("de", "de")]
    [TestCase("de-CH", "de")]
    [TestCase("fr_FR", "fr")]
    [TestCase("??", SkillPhraseLanguages.Undetermined)]
    [TestCase("", SkillPhraseLanguages.Undetermined)]
    public async Task TheLocaleOfTheWish_BecomesTheLanguageOfThePhrase(string locale, string expected)
    {
        GivenPhrases("umsatz pro kunde");
        _oracle.ProbeAsync(Arg.Any<string>(), Arg.Any<string?>(), Target, Arg.Any<CancellationToken>())
            .Returns(new SkillRoutingProbe(true, [Target]));

        await _learner.LearnAsync(Cluster(locale), Target);

        await _phrases.Received(1).TryAddLearnedAsync(
            SkillPhraseOwnerKinds.Skill, Target, expected, SkillPhraseKinds.Synonym, "umsatz pro kunde",
            Arg.Any<CancellationToken>());
    }
}
