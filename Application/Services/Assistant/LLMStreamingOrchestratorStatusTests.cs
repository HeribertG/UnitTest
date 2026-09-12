// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class LLMStreamingOrchestratorStatusTests
{
    private const string ModelId = "model-x";
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string Message = "Show me the clients";
    private const int ShortIdLength = 8;
    private const long SelfClockElapsedToleranceMs = 50;
    private const double BackdatedTurnStartSeconds = 1.0;
    private const long BackdatedTurnStartMs = 900;

    private ISkillToolsetAssembler _assembler = null!;
    private ILLMService _llmService = null!;
    private ISkillCacheService _skillCache = null!;
    private LLMStreamingOrchestrator _orchestrator = null!;

    private bool _assembleCalled;
    private string? _correlationSeenByAssembler;

    [SetUp]
    public void SetUp()
    {
        _assembleCalled = false;
        _correlationSeenByAssembler = null;

        _assembler = Substitute.For<ISkillToolsetAssembler>();
        _assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _assembleCalled = true;
                _correlationSeenByAssembler = TurnCorrelation.Current;
                return new SkillToolsetResult();
            });

        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = Guid.NewGuid(), Name = "Klacksy" });

        _llmService = Substitute.For<ILLMService>();
        _llmService.ProcessStreamAsync(Arg.Any<LLMContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(SseChunk.Content("hi")));

        var providerOrchestrator = new LLMProviderOrchestrator(
            NullLogger<LLMProviderOrchestrator>.Instance,
            Substitute.For<ILLMProviderFactory>(),
            Substitute.For<ILLMRepository>());

        _orchestrator = new LLMStreamingOrchestrator(
            _llmService,
            _skillCache,
            _assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            Substitute.For<IContextBudgetPolicy>(),
            Substitute.For<ILogger<LLMStreamingOrchestrator>>());
    }

    private static async IAsyncEnumerable<SseChunk> Stream(params SseChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static LLMStreamRequest Request() => new()
    {
        Message = Message,
        UserId = UserId,
        ModelId = ModelId,
        UserRights = new List<string>()
    };

    // The whole point of the status event: it must reach the browser BEFORE the assembly the user is
    // waiting on runs, not after it.
    [Test]
    public async Task FirstChunk_IsAssemblingToolsetStatus_BeforeAssemblyRuns()
    {
        // Arrange
        var enumerator = _orchestrator.ProcessStreamAsync(Request()).GetAsyncEnumerator();

        // Act
        var moved = await enumerator.MoveNextAsync();

        // Assert
        moved.ShouldBeTrue();
        enumerator.Current.Type.ShouldBe(SseChunkType.Status);
        enumerator.Current.Stage.ShouldBe(SseStatusStages.AssemblingToolset);
        // Without a caller-supplied timestamp the orchestrator starts the clock one statement earlier,
        // so the elapsed time is sub-millisecond rather than exactly zero.
        enumerator.Current.ElapsedMs!.Value.ShouldBeLessThan(SelfClockElapsedToleranceMs);
        _assembleCalled.ShouldBeFalse();

        await enumerator.DisposeAsync();
    }

    // The caller's clock wins, so the elapsed times of a turn count from when the request arrived and
    // not from the point the orchestrator happens to be reached.
    [Test]
    public async Task ElapsedMs_CountsFromTheCallerSuppliedTurnStart()
    {
        // Arrange
        var request = Request();
        request.TurnStartTimestamp =
            Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * BackdatedTurnStartSeconds);
        var enumerator = _orchestrator.ProcessStreamAsync(request).GetAsyncEnumerator();

        // Act
        await enumerator.MoveNextAsync();

        // Assert
        enumerator.Current.ElapsedMs!.Value.ShouldBeGreaterThanOrEqualTo(BackdatedTurnStartMs);

        await enumerator.DisposeAsync();
    }

    // The set inside the iterator body covers the assembly segment only: an AsyncLocal written between
    // two yields is dropped again when the consumer's execution context is restored. That is exactly
    // why ChatController publishes the turn id from its own flow instead of relying on this one.
    [Test]
    public async Task TurnCorrelationSetInsideTheIterator_DoesNotReachTheConsumer()
    {
        // Arrange
        var beforeEnumeration = TurnCorrelation.Current;
        var enumerator = _orchestrator.ProcessStreamAsync(Request()).GetAsyncEnumerator();

        // Act
        await enumerator.MoveNextAsync();
        await enumerator.MoveNextAsync();

        // Assert
        _correlationSeenByAssembler.ShouldNotBeNull();
        TurnCorrelation.Current.ShouldBe(beforeEnumeration);

        await enumerator.DisposeAsync();
    }

    // The retrieval log is written deep inside the assembly (and inside child DI scopes of it), so the
    // ambient turn id has to be visible there for its lines to be joinable to this turn.
    [Test]
    public async Task TurnCorrelation_IsVisibleInsideTheAssembly()
    {
        // Arrange & Act
        await Drain(_orchestrator.ProcessStreamAsync(Request()));

        // Assert
        _assembleCalled.ShouldBeTrue();
        _correlationSeenByAssembler.ShouldNotBeNull();
        _correlationSeenByAssembler!.Length.ShouldBe(ShortIdLength);
    }

    // Mirrors the production path: the consumer (the controller) owns the turn id and publishes it
    // from its own async flow, because an AsyncLocal set between two yields does not survive the
    // return to the consumer. The orchestrator must then adopt that id instead of minting a second one.
    [Test]
    public async Task TurnIdSuppliedByTheCaller_IsAdoptedAndSeenByTheAssembler()
    {
        // Arrange
        var turnId = Guid.NewGuid();
        TurnCorrelation.Set(turnId);
        var request = Request();
        request.TurnId = turnId;

        LLMContext? handedToLlmService = null;
        _llmService.ProcessStreamAsync(
                Arg.Do<LLMContext>(c => handedToLlmService = c), Arg.Any<CancellationToken>())
            .Returns(_ => Stream());

        // Act
        await Drain(_orchestrator.ProcessStreamAsync(request));

        // Assert
        _correlationSeenByAssembler.ShouldBe(TurnCorrelation.Format(turnId));
        handedToLlmService!.TurnId.ShouldBe(turnId);
    }

    [Test]
    public async Task ChunksOfTheLlmService_ArePassedThroughAfterTheStatus()
    {
        // Arrange & Act
        var chunks = await Drain(_orchestrator.ProcessStreamAsync(Request()));

        // Assert
        chunks[0].Type.ShouldBe(SseChunkType.Status);
        chunks[^1].Type.ShouldBe(SseChunkType.Content);
        chunks[^1].Text.ShouldBe("hi");
    }

    [Test]
    public async Task TurnIdOfTheContext_MatchesTheCorrelationTheAssemblerSaw()
    {
        // Arrange
        LLMContext? handedToLlmService = null;
        _llmService.ProcessStreamAsync(
                Arg.Do<LLMContext>(c => handedToLlmService = c), Arg.Any<CancellationToken>())
            .Returns(_ => Stream());

        // Act
        await Drain(_orchestrator.ProcessStreamAsync(Request()));

        // Assert
        handedToLlmService.ShouldNotBeNull();
        handedToLlmService!.TurnId.ShouldNotBeNull();
        TurnCorrelation.Format(handedToLlmService.TurnId!.Value).ShouldBe(_correlationSeenByAssembler);
        handedToLlmService.TurnStartTimestamp.ShouldNotBeNull();
    }

    private static async Task<List<SseChunk>> Drain(IAsyncEnumerable<SseChunk> source)
    {
        var chunks = new List<SseChunk>();
        await foreach (var chunk in source)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }
}
