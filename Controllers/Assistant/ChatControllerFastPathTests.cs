// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the conversation-aware navigation fast-path in ChatController:
/// the deterministic fast-path short-circuits the LLM for the FIRST message of a conversation,
/// and mid-conversation only for explicit navigation commands ("öffne …"). Bare answers like
/// "Mitarbeiter" inside a guided flow (e.g. create_employee) must route through the LLM.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class ChatControllerFastPathTests
{
    private IMediator _mediator = null!;
    private IUtteranceNormalizer _normalizer = null!;
    private INavigationTargetMatcher _navMatcher = null!;
    private INavigationTargetCacheService _navCache = null!;
    private INavigationFeedbackLogger _navLogger = null!;
    private INavigationMissDetector _navMissDetector = null!;
    private ILLMRepository _llmRepository = null!;
    private INavigationEntityRouteGuard _entityRouteGuard = null!;
    private ChatController _controller = null!;

    private const string FastPathRoute = "/workplace/edit-address";
    private const string CurrentUserId = "11111111-1111-1111-1111-111111111111";

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _normalizer = Substitute.For<IUtteranceNormalizer>();
        _navMatcher = Substitute.For<INavigationTargetMatcher>();
        _navCache = Substitute.For<INavigationTargetCacheService>();
        _navLogger = Substitute.For<INavigationFeedbackLogger>();
        _navMissDetector = Substitute.For<INavigationMissDetector>();
        _llmRepository = Substitute.For<ILLMRepository>();
        _entityRouteGuard = Substitute.For<INavigationEntityRouteGuard>();

        _controller = new ChatController(
            Substitute.For<ILogger<ChatController>>(),
            _mediator,
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            Substitute.For<ILLMStreamingOrchestrator>(),
            Substitute.For<ISkillCacheService>(),
            _normalizer,
            _navMatcher,
            _navCache,
            _navLogger,
            _navMissDetector,
            _llmRepository,
            Substitute.For<IUserActivityTracker>(),
            _entityRouteGuard)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateHttpContextFor(CurrentUserId)
            }
        };

        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance("mitarbeiter", "mitarbeiter", false, false));

        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = "edit-employee",
                Route = FastPathRoute,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate("edit-employee", FastPathRoute, 1.0) }
            });
    }

    private static DefaultHttpContext CreateHttpContextFor(string userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Test]
    public async Task FastPath_Routes_WhenNoOngoingConversation()
    {
        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = null };

        var result = await _controller.ProcessMessage(request);

        var ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as LLMResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.NavigateTo, Is.EqualTo(FastPathRoute));
        Assert.That(response.ActionPerformed, Is.True);
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessLLMMessageCommand>());
    }

    [Test]
    public async Task FastPath_IsSkipped_WhenTargetRequiresAnEntityId()
    {
        _entityRouteGuard.RequiresEntity("edit-employee", FastPathRoute).Returns(true);
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>()).Returns(new LLMResponse { Message = "llm" });
        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = null };

        var result = await _controller.ProcessMessage(request);

        await _mediator.Received(1).Send(Arg.Any<ProcessLLMMessageCommand>());
        var response = (result.Result as OkObjectResult)?.Value as LLMResponse;
        Assert.That(response?.NavigateTo, Is.Not.EqualTo(FastPathRoute));
    }

    [Test]
    public async Task FastPath_SetsNavigateToTarget_ForInPageCategory()
    {
        const string targetId = "erp-drop-points";
        const string route = "/workplace/settings";
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = targetId,
                Route = route,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate(targetId, route, 1.0) }
            });
        _navCache.GetById(targetId).Returns(new NavigationTarget
        {
            TargetId = targetId,
            Route = route,
            LabelKey = "settings.erpDropPoints",
            Category = "settings.integrations"
        });

        var request = new LLMRequest { Message = "Zeige mir die Uploadfläche", ConversationId = null };

        var result = await _controller.ProcessMessage(request);

        var ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as LLMResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.NavigateTo, Is.EqualTo(route));
        Assert.That(response.NavigateToTarget, Is.EqualTo(targetId));
    }

    [Test]
    public async Task FastPath_OmitsNavigateToTarget_ForPageLevelCategory()
    {
        const string targetId = "dashboard";
        const string route = "/workplace/dashboard";
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = targetId,
                Route = route,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate(targetId, route, 1.0) }
            });
        _navCache.GetById(targetId).Returns(new NavigationTarget
        {
            TargetId = targetId,
            Route = route,
            LabelKey = "dashboard.label",
            Category = NavigationTargetCategories.PageLevel
        });

        var request = new LLMRequest { Message = "Zeige mir das Dashboard", ConversationId = null };

        var result = await _controller.ProcessMessage(request);

        var ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as LLMResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.NavigateTo, Is.EqualTo(route));
        Assert.That(response.NavigateToTarget, Is.Null);
    }

    [Test]
    public async Task FastPath_SetsNavigateToTarget_WhenCategoryIsNull()
    {
        const string targetId = "contract-form.percent";
        const string route = "/workplace/contracts";
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = targetId,
                Route = route,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate(targetId, route, 1.0) }
            });
        _navCache.GetById(targetId).Returns(new NavigationTarget
        {
            TargetId = targetId,
            Route = route,
            LabelKey = "contracts.percent",
            Category = null
        });

        var request = new LLMRequest { Message = "Öffne das Prozent-Feld", ConversationId = null };

        var result = await _controller.ProcessMessage(request);

        var ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as LLMResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.NavigateTo, Is.EqualTo(route));
        Assert.That(response.NavigateToTarget, Is.EqualTo(targetId));
    }

    [Test]
    public async Task FastPath_Suppressed_WhenConversationHasHistory()
    {
        const string conversationId = "conv-1";
        _llmRepository.GetConversationByConversationIdAsync(conversationId, CurrentUserId)
            .Returns(new LLMConversation { MessageCount = 2 });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>())
            .Returns(new LLMResponse { Message = "Wie lautet die Adresse?" });

        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = conversationId };

        var result = await _controller.ProcessMessage(request);

        await _mediator.Received(1).Send(Arg.Any<ProcessLLMMessageCommand>());
        var ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as LLMResponse;
        Assert.That(response!.NavigateTo, Is.Null);
    }

    [Test]
    public async Task FastPath_Routes_MidConversation_WhenMessageIsExplicitNavigationCommand()
    {
        const string conversationId = "conv-1";
        _llmRepository.GetConversationByConversationIdAsync(conversationId, CurrentUserId)
            .Returns(new LLMConversation { MessageCount = 2 });

        var request = new LLMRequest { Message = "Öffne Mitarbeiter", ConversationId = conversationId };

        var result = await _controller.ProcessMessage(request);

        var ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as LLMResponse;
        Assert.That(response!.NavigateTo, Is.EqualTo(FastPathRoute));
        Assert.That(response.ActionPerformed, Is.True);
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessLLMMessageCommand>());
    }

    [Test]
    public async Task FastPath_Suppressed_MidConversation_WhenBareAnswerMatchesTargetExactly()
    {
        const string conversationId = "conv-1";
        _llmRepository.GetConversationByConversationIdAsync(conversationId, CurrentUserId)
            .Returns(new LLMConversation { MessageCount = 2 });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>())
            .Returns(new LLMResponse { Message = "Wie lautet die Adresse?" });

        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = conversationId };

        await _controller.ProcessMessage(request);

        await _mediator.Received(1).Send(Arg.Any<ProcessLLMMessageCommand>());
    }

    [Test]
    public async Task FastPath_Routes_WhenConversationIdHasNoStoredHistory()
    {
        const string conversationId = "conv-new";
        _llmRepository.GetConversationByConversationIdAsync(conversationId, CurrentUserId)
            .Returns((LLMConversation?)null);

        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = conversationId };

        var result = await _controller.ProcessMessage(request);

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as LLMResponse;
        Assert.That(response!.NavigateTo, Is.EqualTo(FastPathRoute));
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessLLMMessageCommand>());
    }

    [Test]
    public async Task LlmPath_SetsMissedTargetId_WhenDetectorFlagsACandidate()
    {
        const string conversationId = "conv-1";
        const string navigatedRoute = "/workplace/settings";
        var suspectedCandidate = new NavigationCandidate("erp-drop-points", navigatedRoute, 0.6);
        _llmRepository.GetConversationByConversationIdAsync(conversationId, CurrentUserId)
            .Returns(new LLMConversation { MessageCount = 2 });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>())
            .Returns(new LLMResponse { Message = "…", NavigateTo = navigatedRoute, NavigateToTarget = null });
        _navMissDetector.DetectSuspectedMiss(Arg.Any<NavigationMatchResult>(), navigatedRoute, null)
            .Returns(suspectedCandidate);

        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = conversationId };

        var result = await _controller.ProcessMessage(request);

        var response = (result.Result as OkObjectResult)!.Value as LLMResponse;
        Assert.That(response!.MissedTargetId, Is.EqualTo(suspectedCandidate.TargetId));
        await _navLogger.Received(1).LogOutcomeAsync(
            Arg.Any<string?>(),
            Arg.Any<string>(),
            suspectedCandidate.TargetId,
            NavigationOutcomeKinds.SuspectedMiss,
            navigatedRoute,
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LlmPath_LeavesMissedTargetIdEmpty_WhenDetectorFindsNothing()
    {
        const string conversationId = "conv-1";
        const string navigatedRoute = "/workplace/settings";
        _llmRepository.GetConversationByConversationIdAsync(conversationId, CurrentUserId)
            .Returns(new LLMConversation { MessageCount = 2 });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>())
            .Returns(new LLMResponse { Message = "…", NavigateTo = navigatedRoute, NavigateToTarget = null });
        _navMissDetector.DetectSuspectedMiss(Arg.Any<NavigationMatchResult>(), navigatedRoute, null)
            .Returns((NavigationCandidate?)null);

        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = conversationId };

        var result = await _controller.ProcessMessage(request);

        var response = (result.Result as OkObjectResult)!.Value as LLMResponse;
        Assert.That(response!.MissedTargetId, Is.Null);
        await _navLogger.DidNotReceive().LogOutcomeAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamFastPath_SetsTargetInMetadata_ForInPageCategory()
    {
        const string targetId = "erp-drop-points";
        const string route = "/workplace/settings";
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = targetId,
                Route = route,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate(targetId, route, 1.0) }
            });
        _navCache.GetById(targetId).Returns(new NavigationTarget
        {
            TargetId = targetId,
            Route = route,
            LabelKey = "settings.erpDropPoints",
            Category = "settings.integrations"
        });

        var body = new MemoryStream();
        _controller.ControllerContext.HttpContext.Response.Body = body;

        var request = new LLMRequest { Message = "Zeige mir die Uploadfläche", ConversationId = null };

        await _controller.ProcessMessageStream(request, CancellationToken.None);

        body.Position = 0;
        var sse = new StreamReader(body).ReadToEnd();
        Assert.That(sse, Does.Contain($"\"target\":\"{targetId}\""));
    }

    // The progress event goes out before the navigation match and its feedback log (a DB write), not
    // after them - that ordering is the whole point of sending it from the controller.
    [Test]
    public async Task Stream_SendsTheFirstStatusEvent_BeforeAnythingElse()
    {
        var body = new MemoryStream();
        _controller.ControllerContext.HttpContext.Response.Body = body;

        var request = new LLMRequest { Message = "Mitarbeiter", ConversationId = null };

        await _controller.ProcessMessageStream(request, CancellationToken.None);

        body.Position = 0;
        var sse = new StreamReader(body).ReadToEnd();
        Assert.That(sse, Does.StartWith("event: status\ndata: "));
        Assert.That(sse, Does.Contain("\"stage\":\"assembling_toolset\""));
        Assert.That(sse.IndexOf("event: metadata", StringComparison.Ordinal),
            Is.GreaterThan(sse.IndexOf("event: status", StringComparison.Ordinal)),
            "the fast path must still send its own events after the status event");
    }

    [Test]
    public async Task StreamFastPath_OmitsTargetInMetadata_ForPageLevelCategory()
    {
        const string targetId = "dashboard";
        const string route = "/workplace/dashboard";
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = targetId,
                Route = route,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate(targetId, route, 1.0) }
            });
        _navCache.GetById(targetId).Returns(new NavigationTarget
        {
            TargetId = targetId,
            Route = route,
            LabelKey = "dashboard.label",
            Category = NavigationTargetCategories.PageLevel
        });

        var body = new MemoryStream();
        _controller.ControllerContext.HttpContext.Response.Body = body;

        var request = new LLMRequest { Message = "Zeige mir das Dashboard", ConversationId = null };

        await _controller.ProcessMessageStream(request, CancellationToken.None);

        body.Position = 0;
        var sse = new StreamReader(body).ReadToEnd();
        Assert.That(sse, Does.Contain($"\"navigateTo\":\"{route}\""),
            "the metadata event itself must still be present - otherwise the missing \"target\" proves nothing");
        Assert.That(sse, Does.Not.Contain("\"target\""));
    }

    [Test]
    public async Task ProcessMessage_WithoutConversationId_KeepsTheIdTheHandlerCreated()
    {
        const string serverConversationId = "44444444-4444-4444-4444-444444444444";
        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance("wie fange ich an", "wie fange ich an", false, false));
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = null,
                Route = null,
                Score = 0.0,
                Tier = NavigationMatchTier.None,
                Candidates = Array.Empty<NavigationCandidate>()
            });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>())
            .Returns(new LLMResponse { Message = "…", ConversationId = serverConversationId });

        var request = new LLMRequest { Message = "Wie fange ich an?", ConversationId = null };

        var result = await _controller.ProcessMessage(request);

        var response = (result.Result as OkObjectResult)!.Value as LLMResponse;
        Assert.That(response!.ConversationId, Is.EqualTo(serverConversationId),
            "The handler already persisted state under its own conversation id; replacing it with a "
            + "fresh GUID hands the client an id nothing is stored under, so the next turn starts an "
            + "empty conversation and any running recipe is lost.");
    }
}
