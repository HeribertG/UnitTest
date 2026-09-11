// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the round that turns a composable wish into a recipe. Everything here circles one fact: a
/// recipe row is live on every instance the moment it exists, because the engine reads the table on
/// every call without a cache and forces the step skill ahead of any function calling. So the order in
/// which this class writes matters more than what it writes, and the tests are written against that
/// order rather than against the happy path.
/// The nastiest case is the one that has no happy path at all: an exception after the insert. Nothing
/// else in the system would ever remove that row again - a later run reopens the cluster, not the
/// orphaned recipe - so the withdrawal has to happen here or never.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class CapabilityLearnerTests
{
    private const string Skill = "list_open_shifts";
    private const string DraftName = "open-shift-report";
    private static readonly string RecipeName = SkillLearningDefaults.LearnedRecipeNamePrefix + DraftName;

    private IAgentRecipeRepository _recipes = null!;
    private ISkillPhraseRepository _phrases = null!;
    private ISkillLearningCandidateRepository _candidates = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private ILearnedArtifactGenerator _generator = null!;
    private IRecipeDraftValidator _validator = null!;
    private ISkillExecutionOracle _oracle = null!;
    private ISkillRegistry _registry = null!;
    private ISkillRiskClassifier _classifier = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private RecipeEngineService _engine = null!;
    private CapabilityLearner _learner = null!;

    [SetUp]
    public void SetUp()
    {
        _recipes = Substitute.For<IAgentRecipeRepository>();
        _recipes.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns([]);

        _phrases = Substitute.For<ISkillPhraseRepository>();
        _candidates = Substitute.For<ISkillLearningCandidateRepository>();
        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _cases.ListByClusterAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        _generator = Substitute.For<ILearnedArtifactGenerator>();
        _validator = Substitute.For<IRecipeDraftValidator>();
        _oracle = Substitute.For<ISkillExecutionOracle>();
        _registry = Substitute.For<ISkillRegistry>();
        _classifier = Substitute.For<ISkillRiskClassifier>();
        _refresher = Substitute.For<ISkillCatalogRefresher>();

        // NUnit keeps one fixture instance for the whole class, so the recipes a previous test made the
        // engine resolve would still be there. Two tests deliberately use the same trigger, so leaving
        // the list filled makes the outcome depend on execution order.
        _resolvedRecipes.Clear();
        _engine = NewEngine();

        GivenComposableSkill();
        GivenDrafts(Draft());
        GivenVerdict(RecipeDraftVerdict.Accepted(RecipeName, Trigger()));
        GivenProbe(SkillExecutionProbe.Passed(true, []));

        _learner = new CapabilityLearner(
            _recipes, _phrases, _candidates, _cases, _goldenCases, _generator, _validator, _oracle,
            _engine, _registry, _classifier, _refresher, Substitute.For<ILogger<CapabilityLearner>>());
    }

    [Test]
    public async Task ARejectedDraft_IsNeverWrittenToTheRecipeTable()
    {
        GivenVerdict(RecipeDraftVerdict.Rejected("It would swallow 'create-shift-order'."));

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeFalse();
        outcome.Inconclusive.ShouldBeFalse();
        outcome.Error.ShouldContain("create-shift-order");
        await _recipes.DidNotReceive().AddAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
    }

    // The execution oracle must have spoken before anything is written, not after.
    [Test]
    public async Task ACompositionTheExecutionOracleRejected_IsNeverWritten()
    {
        GivenProbe(SkillExecutionProbe.Rejected("Step 1 ('list_open_shifts') did not work: timeout", []));

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeFalse();
        outcome.Inconclusive.ShouldBeFalse();
        await _recipes.DidNotReceive().AddAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
        await _candidates.Received(1).UpdateVerdictAsync(
            Arg.Any<Guid>(), SkillLearningCandidateStatuses.ExecutionFailed,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    // An identity nobody could mint is not a verdict about the composition, and the round must say so:
    // the loop hands an unjudged round back without spending the cluster's attempt budget.
    [Test]
    public async Task ACompositionTheOracleCouldNotJudge_IsReportedAsUnjudgedAndStopsTheRound()
    {
        GivenDrafts(Draft(), Draft("second-variant"));
        GivenProbe(SkillExecutionProbe.Inconclusive("The owner's token was refused."));

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Inconclusive.ShouldBeTrue();
        outcome.Learned.ShouldBeFalse();
        await _recipes.DidNotReceive().AddAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
        await _oracle.Received(1).ProbeAsync(
            Arg.Any<IReadOnlyList<RecipeStep>>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAcceptedComposition_IsWrittenWithTheLearnedOriginAndTheLowestPriority()
    {
        GivenEngineResolvesTo(RecipeName);

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeTrue();
        outcome.RecipeName.ShouldBe(RecipeName);
        await _recipes.Received(1).AddAsync(
            Arg.Is<AgentRecipe>(recipe =>
                recipe.Name == RecipeName
                && recipe.Origin == AgentRecipeOrigins.Learned
                && recipe.IsEnabled
                && recipe.SortOrder == SkillLearningDefaults.LearnedRecipeSortOrder),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AComposedCapabilityTheOracleCouldNotRunEndToEnd_OwesItsFirstUse()
    {
        GivenEngineResolvesTo(RecipeName);
        GivenProbe(SkillExecutionProbe.Passed(false, []));

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeTrue();
        outcome.NeedsFirstUse.ShouldBeTrue();
    }

    // The one check that genuinely needs the row to exist. If the live engine gives the wish to somebody
    // else, the row has to disappear again - it would sit there stealing turns otherwise.
    [Test]
    public async Task ARecipeTheLiveEngineDoesNotPick_IsWithdrawnAgain()
    {
        GivenEngineResolvesTo("create-shift-order");

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeFalse();
        outcome.Error.ShouldContain("create-shift-order");
        await _recipes.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // The regression for the review blocker: before the fix an exception anywhere between the insert and
    // the confirmation left the recipe enabled on every instance with no path that would ever remove it.
    [Test]
    public async Task AnExceptionAfterTheInsert_StillWithdrawsTheRecipe()
    {
        _refresher
            .RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("index sync exploded"));

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeFalse();
        outcome.Inconclusive.ShouldBeTrue();
        outcome.Error.ShouldContain("index sync exploded");
        await _recipes.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // A cancellation is the case that makes the orphan permanent: a deployment lands mid-refresh, and
    // cleaning up with the very token that caused the abort would skip the cleanup.
    [Test]
    public async Task ACancellationAfterTheInsert_StillWithdrawsTheRecipe()
    {
        using var source = new CancellationTokenSource();
        _refresher
            .RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ =>
            {
                source.Cancel();
                throw new OperationCanceledException(source.Token);
            });

        var outcome = await _learner.LearnAsync(Cluster(), [Skill], source.Token);

        outcome.Learned.ShouldBeFalse();
        await _recipes.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // The composition pool is what retrieval already offered for this wish. A skill the classifier does
    // not let anybody compose must not even be shown to the generator.
    [Test]
    public async Task ASkillTheClassifierRefuses_IsNotOfferedAsABuildingBlock()
    {
        _classifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.Sensitive);

        var outcome = await _learner.LearnAsync(Cluster(), [Skill]);

        outcome.Learned.ShouldBeFalse();
        outcome.Error.ShouldContain("may be composed");
        await _generator.DidNotReceive().GenerateCapabilitiesAsync(
            Arg.Any<SkillLearningClusterContext>(),
            Arg.Any<IReadOnlyList<CapabilityBuildingBlock>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EveryVariant_IsRecordedAsACandidateEvenWhenItIsRejected()
    {
        GivenDrafts(Draft(), Draft("second-variant"));
        GivenVerdict(RecipeDraftVerdict.Rejected("collides"));

        await _learner.LearnAsync(Cluster(), [Skill]);

        await _candidates.Received(2).AddAsync(
            Arg.Is<SkillLearningCandidate>(c => c.Kind == SkillLearningCandidateKinds.Capability),
            Arg.Any<CancellationToken>());
    }

    // The excerpt deliberately contains the trigger stem. The engine only falls back to semantic
    // matching when no keyword trigger fires, and that path needs the whole retrieval stack; a test
    // about activation order has no business exercising it.
    private static SkillLearningClusterContext Cluster() =>
        new(Guid.NewGuid(), "melde den dienstbericht der woche", "de", null, null, [], 0, null);

    private static RecipeTrigger Trigger() =>
        new() { AllOf = [new RecipeCondition { AnyWordStart = ["dienstbericht"] }] };

    private static LearnedRecipeDraft Draft(string name = DraftName) =>
        new(
            name,
            "Report the open shifts",
            new Dictionary<string, string> { ["de"] = "x", ["en"] = "x", ["fr"] = "x", ["it"] = "x" },
            Trigger(),
            [new RecipeStep { Kind = RecipeStepKinds.Search, Skill = Skill }]);

    private void GivenComposableSkill()
    {
        var descriptor = new SkillDescriptor(Skill, Skill, SkillCategory.Query, [], [], [], null);
        _registry.GetSkillByName(Skill).Returns(descriptor);
        _classifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.ReadOnly);
    }

    private void GivenDrafts(params LearnedRecipeDraft[] drafts) =>
        _generator
            .GenerateCapabilitiesAsync(
                Arg.Any<SkillLearningClusterContext>(),
                Arg.Any<IReadOnlyList<CapabilityBuildingBlock>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(drafts);

    private void GivenVerdict(RecipeDraftVerdict verdict) =>
        _validator
            .Validate(
                Arg.Any<LearnedRecipeDraft>(),
                Arg.Any<IReadOnlyList<AgentRecipe>>(),
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>())
            .Returns(verdict);

    private void GivenProbe(SkillExecutionProbe probe) =>
        _oracle
            .ProbeAsync(
                Arg.Any<IReadOnlyList<RecipeStep>>(),
                Arg.Any<string?>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(probe);

    // The engine is a concrete class, so it is driven through the repository it resolves recipes from
    // rather than substituted: what comes back is decided by which rows the scoped repository reports.
    private void GivenEngineResolvesTo(string recipeName) =>
        _resolvedRecipes.Add(new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = recipeName,
            Goal = "goal",
            IsEnabled = true,
            TriggerJson = System.Text.Json.JsonSerializer.Serialize(
                Trigger(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            StepsJson = System.Text.Json.JsonSerializer.Serialize(
                new[] { new RecipeStep { Kind = RecipeStepKinds.Search, Skill = Skill } },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        });

    private readonly List<AgentRecipe> _resolvedRecipes = [];

    private RecipeEngineService NewEngine()
    {
        var scopedRepository = Substitute.For<IAgentRecipeRepository>();
        scopedRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _resolvedRecipes);

        // Every service the engine resolves from its own scope has to be present, not only the one the
        // happy path needs: it calls GetRequiredService for the competing-intent detector, the margin
        // evaluator and the retrieval service too, and a missing registration surfaces as a DI message
        // in the outcome text rather than as a failed resolution - which is exactly how these tests
        // first went red.
        var competingIntent = Substitute.For<ICompetingSkillIntentDetector>();
        competingIntent
            .FindCompetingSkillNamesAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<RecipeTrigger>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(scopedRepository);
        services.AddSingleton(competingIntent);
        services.AddSingleton(Substitute.For<IRecipeSkillMarginEvaluator>());
        services.AddSingleton(Substitute.For<IKnowledgeRetrievalService>());

        var provider = services.BuildServiceProvider();

        return new RecipeEngineService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IPendingRecipeStore>(),
            NullLogger<RecipeEngineService>.Instance);
    }
}
