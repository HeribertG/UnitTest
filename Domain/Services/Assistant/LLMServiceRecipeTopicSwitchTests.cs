// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression guard for the topic-switch bug: while a recipe waits on an ask step, an independent
/// question from the user must not be raw-filled into the pending slot. Live incident 2026-09-08 — an
/// ERP-import question asked mid-flow of setup-consultation was silently written into the hour-tracking
/// ask slot, and the recipe's tool-less ask-step call (which has no skills available) then falsely told
/// the user no ERP import exists. ResolveOrResumeRecipeAsync is covered directly for the slot-bag/plan
/// bookkeeping; ExecuteMultiTurnLoopAsync is covered end to end (real LLMFunctionExecutor, ILLMSkillBridge
/// substituted one layer deeper, matching LLMServiceRecipeGateHoldTests) to prove the actual behavior: a
/// topic-switch turn runs with the full toolset, answers the question, and re-asks the still-open recipe
/// question in the same response — while an ordinary slot reply keeps working exactly as before.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BridgeLLMFunctionCall = Klacks.Api.Domain.Services.Assistant.Providers.LLMFunctionCall;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceRecipeTopicSwitchTests
{
    private const string ConversationId = "conv-topic-switch";
    private const string ErpSkill = "explain_page_settings_erp_drop_points";
    private const string TrackingSlot = "tracking";
    private const string CustomersSlot = "customers";
    private const string TrackingPromptEn = "How should hour tracking work?";
    private const string TrackingPromptDe = "Wie soll die Stundenzurechnung erfolgen?";
    private const string CustomersPromptDe = "Hast du Kundenbestellungen?";
    private const string ErpAnswer = "Der ERP-Import läuft über einen Drop-Point in den Einstellungen.";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe SetupConsultationLike = new()
    {
        Id = Guid.NewGuid(),
        Name = "setup-consultation",
        Goal = "Advise on setup.",
        TriggerJson = """{"allOf":[{"anyWordStart":["fange"]}],"noneOf":[]}""",
        StepsJson =
            "[" +
            "{\"kind\":\"ask\",\"slot\":\"" + TrackingSlot + "\",\"prompt\":\"" + TrackingPromptEn +
            "\",\"promptTranslations\":{\"de\":\"" + TrackingPromptDe + "\"}}," +
            "{\"kind\":\"ask\",\"slot\":\"" + CustomersSlot +
            "\",\"prompt\":\"Do you have customer orders?\",\"promptTranslations\":{\"de\":\"" + CustomersPromptDe + "\"}}" +
            "]",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private ILLMSkillBridge _skillBridge = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { SetupConsultationLike });
        _recipeRepository.GetByNameAsync(SetupConsultationLike.Name, Arg.Any<CancellationToken>())
            .Returns(SetupConsultationLike);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _skillBridge = Substitute.For<ILLMSkillBridge>();

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        scopedProvider.GetService(typeof(IKnowledgeRetrievalService))
            .Returns(Substitute.For<IKnowledgeRetrievalService>());
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        // A null default agent leaves the executor's skill cache empty, so GetExecutionTypeAsync
        // returns the plain "Skill" default and every call reaches the bridge (same trick as
        // LLMServiceRecipeGateHoldTests).
        var agentRepository = Substitute.For<IAgentRepository>();
        agentRepository.GetDefaultAgentAsync().Returns((Agent?)null);

        var functionExecutor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            Substitute.For<IAgentSkillRepository>(),
            agentRepository,
            Substitute.For<IPendingConfirmationStore>(),
            _skillBridge);

        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: functionExecutor,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            pendingConfirmationStore: Substitute.For<IPendingConfirmationStore>(),
            recipeEngine: recipeEngine,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            slotExtractor: new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!);
    }

    private static LLMContext Context(string message, string? language = "de") => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        Language = language,
        AvailableFunctions = new List<LLMFunction> { new() { Name = ErpSkill } }
    };

    private static MultiTurnContext BuildContext(LLMContext context, ILLMProvider provider) => new(
        context,
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew());

    private void ResumeAtTrackingAskStep() =>
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = SetupConsultationLike.Name,
            AwaitingConfirmation = false,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

    private void BridgeAnswersTheErpQuestion() =>
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<BridgeLLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, ResultType = "Data", Message = ErpAnswer });

    // ---- ResolveOrResumeRecipeAsync: the resolution/slot-bag seam ----

    [Test]
    public async Task ResolveOrResumeRecipeAsync_IndependentQuestion_LeavesSlotUnfilled_StaysOnSameAskStep()
    {
        ResumeAtTrackingAskStep();
        var message = "Ich habe eine xml Datei mit allen Bestellungen drin. Wie kann ich es einbinden?";

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.TopicSwitchThisTurn.ShouldBeTrue();
        plan.IsActive.ShouldBeTrue();
        plan.CurrentIsAsk.ShouldBeTrue();
        plan.CurrentStep!.Slot.ShouldBe(TrackingSlot);
        plan.Slots.ShouldNotContainKey(TrackingSlot);
    }

    [Test]
    public async Task ResolveOrResumeRecipeAsync_OrdinaryReply_StillFillsSlotAndAdvances_RegressionGuard()
    {
        ResumeAtTrackingAskStep();

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context("Wir sind ein Spital, also eher ohne Kunden"),
            Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.TopicSwitchThisTurn.ShouldBeFalse();
        plan.Slots[TrackingSlot].ShouldBe("Wir sind ein Spital, also eher ohne Kunden");
        plan.CurrentStep!.Slot.ShouldBe(CustomersSlot);
    }

    // ---- ExecuteMultiTurnLoopAsync: the actual chat-loop behavior ----

    [Test]
    public async Task TopicSwitch_RunsFullToolsetTurn_AnswersTheQuestion_ThenReasksTheOpenRecipeQuestion()
    {
        ResumeAtTrackingAskStep();
        BridgeAnswersTheErpQuestion();
        var message = "Ich habe eine xml Datei mit allen Bestellungen drin. Wie kann ich es einbinden?";

        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new LLMProviderResponse
                {
                    Success = true,
                    Content = string.Empty,
                    FunctionCalls = new List<BridgeLLMFunctionCall>
                    {
                        new() { FunctionName = ErpSkill, Parameters = new Dictionary<string, object>() }
                    }
                },
                _ => new LLMProviderResponse
                {
                    Success = true,
                    Content = ErpAnswer,
                    FunctionCalls = new List<BridgeLLMFunctionCall>()
                });

        var (responseContent, _, _, allFunctionCalls, askedSlot) = await _service.ExecuteMultiTurnLoopAsync(
            BuildContext(Context(message), provider));

        allFunctionCalls.ShouldContain(c => c.FunctionName == ErpSkill && c.Success);
        responseContent.ShouldContain(ErpAnswer);
        responseContent.ShouldContain(TrackingPromptDe);
        askedSlot.ShouldBe(TrackingSlot);

        _pendingRecipeStore.Received(1).Save(Arg.Is<PendingRecipe>(p =>
            p.RecipeName == SetupConsultationLike.Name
            && p.StepIndex == 0
            && !p.Slots.ContainsKey(TrackingSlot)));
        _pendingRecipeStore.DidNotReceive().Clear(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task OrdinaryReply_StillPausesOnAskWithoutAnyToolCall_RegressionGuard()
    {
        ResumeAtTrackingAskStep();
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = CustomersPromptDe });

        var (responseContent, _, _, allFunctionCalls, askedSlot) = await _service.ExecuteMultiTurnLoopAsync(
            BuildContext(Context("Wir sind ein Spital, also eher ohne Kunden"), provider));

        responseContent.ShouldBe(CustomersPromptDe);
        allFunctionCalls.ShouldBeEmpty();
        askedSlot.ShouldBe(CustomersSlot);
        _pendingRecipeStore.Received(1).Save(Arg.Is<PendingRecipe>(p =>
            p.StepIndex == 1 && p.Slots[TrackingSlot] == "Wir sind ein Spital, also eher ohne Kunden"));
    }
}
