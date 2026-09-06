// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class OnnxSessionIdleUnloadServiceTests
{
    // Deliberately different from DefaultIdleUnloadMinutes, otherwise the threshold test would pass
    // just as well against a service that ignores configuration entirely. It must also stay at or above
    // MinimumIdleUnloadMinutes: below it the constructor logs a clamp warning, and the tests that assert
    // an empty log would fail for a reason that has nothing to do with what they check.
    private const int ConfiguredMinutes = 7;

    // Below MinimumIdleUnloadMinutes and above IdleUnloadDisabled: the one range that gets raised.
    private const int TooSmallMinutes = 1;

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private RecordingLogger<OnnxSessionIdleUnloadService> _logger = null!;
    private FakeProcessHeapTrimmer _heapTrimmer = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new RecordingLogger<OnnxSessionIdleUnloadService>();
        _heapTrimmer = new FakeProcessHeapTrimmer();
    }

    [Test]
    public async Task SweepAsync_SessionIdleLongerThanThreshold_UnloadsIt()
    {
        // Arrange
        var session = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: true);
        var sut = Create(ConfiguredMinutes, session);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        session.ReceivedThresholds.Count.ShouldBe(1);
        _logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Information && e.Message.Contains(session.SessionName));
    }

    [Test]
    public async Task SweepAsync_SessionReportsBusy_DoesNotLogAnUnload()
    {
        // Arrange
        var session = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: false);
        var sut = Create(ConfiguredMinutes, session);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        session.ReceivedThresholds.Count.ShouldBe(1);
        _logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public async Task SweepAsync_SessionNotLoaded_IsNeverAsked()
    {
        // Arrange
        var session = new FakeUnloadableInferenceSession("embedding", isLoaded: false, unloadResult: true);
        var sut = Create(ConfiguredMinutes, session);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        session.ReceivedThresholds.ShouldBeEmpty();
        _logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public async Task SweepAsync_OneSessionThrows_StillSweepsTheOther()
    {
        // Arrange - the throwing session comes first, otherwise the surviving one would have been
        // swept before the exception could possibly have stopped it.
        var failing = new FakeUnloadableInferenceSession(
            "embedding", isLoaded: true, unloadResult: true, throwOnUnload: new InvalidOperationException("boom"));
        var healthy = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: true);
        var sut = Create(ConfiguredMinutes, failing, healthy);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        healthy.ReceivedThresholds.Count.ShouldBe(1);
        _logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(failing.SessionName));
        _logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Information && e.Message.Contains(healthy.SessionName));
    }

    [Test]
    public async Task SweepAsync_PassesConfiguredThresholdToEverySession()
    {
        // Arrange
        var first = new FakeUnloadableInferenceSession("embedding", isLoaded: true, unloadResult: true);
        var second = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: true);
        var sut = Create(ConfiguredMinutes, first, second);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        first.ReceivedThresholds.ShouldBe([TimeSpan.FromMinutes(ConfiguredMinutes)]);
        second.ReceivedThresholds.ShouldBe([TimeSpan.FromMinutes(ConfiguredMinutes)]);
    }

    // A one-minute window would let a user who spaces questions out force a 555 MB reload on every one
    // of them, and that reload blocks every other user on the provider's init lock. Zero still means
    // off; only the range in between is raised.
    [Test]
    public async Task SweepAsync_ConfiguredBelowTheMinimum_UsesTheMinimumAndWarns()
    {
        // Arrange
        var session = new FakeUnloadableInferenceSession("embedding", isLoaded: true, unloadResult: true);
        var sut = Create(TooSmallMinutes, session);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        session.ReceivedThresholds.ShouldBe(
            [TimeSpan.FromMinutes(KnowledgeIndexConstants.MinimumIdleUnloadMinutes)]);
        _logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(KnowledgeIndexConstants.IdleUnloadMinutesConfigKey));
    }

    // Disposing the session only hands the memory back to the allocator; the trim is what returns it to
    // the kernel. Asserted as "called exactly once", never as an RSS drop: on a host without glibc the
    // trimmer is a no-op that correctly reports false.
    [Test]
    public async Task SweepAsync_AfterASuccessfulUnload_TrimsTheProcessHeapOnce()
    {
        // Arrange
        var session = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: true);
        var sut = Create(ConfiguredMinutes, session);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        _heapTrimmer.TrimCount.ShouldBe(1);
    }

    [Test]
    public async Task SweepAsync_SessionWasNotUnloaded_DoesNotTrimTheProcessHeap()
    {
        // Arrange - one busy session and one that holds nothing, so neither path may reach the trim.
        var busy = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: false);
        var unloaded = new FakeUnloadableInferenceSession("embedding", isLoaded: false, unloadResult: true);
        var sut = Create(ConfiguredMinutes, busy, unloaded);

        // Act
        await sut.SweepAsync(CancellationToken.None);

        // Assert
        _heapTrimmer.TrimCount.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_IdleUnloadMinutesZero_NeverTouchesAnySession()
    {
        // Arrange
        var session = new FakeUnloadableInferenceSession("reranker", isLoaded: true, unloadResult: true);
        var sut = Create(KnowledgeIndexConstants.IdleUnloadDisabled, session);

        // Act - awaiting ExecuteTask before StopAsync: stopping the service straight after starting it
        // raced the stopping token against the body and ended the task Canceled with nothing logged in
        // 2 of 6 runs. Established pattern, see GoalReflectionBackgroundServiceTests.
        await sut.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(sut.ExecuteTask!, Task.Delay(CompletionTimeout));
        await sut.StopAsync(CancellationToken.None);

        // Assert
        finished.ShouldBe(sut.ExecuteTask, "a zero idle window must return immediately instead of polling");
        session.ReceivedThresholds.ShouldBeEmpty();
        _logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Information
            && e.Message.Contains(KnowledgeIndexConstants.IdleUnloadMinutesConfigKey));
    }

    private OnnxSessionIdleUnloadService Create(int minutes, params IUnloadableInferenceSession[] sessions)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnowledgeIndexConstants.IdleUnloadMinutesConfigKey] =
                    minutes.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

        return new OnnxSessionIdleUnloadService(
            sessions, configuration, _heapTrimmer, new SettableTimeProvider(DateTime.UtcNow), _logger);
    }

    private sealed class FakeProcessHeapTrimmer : IProcessHeapTrimmer
    {
        public int TrimCount { get; private set; }

        public bool TryTrim()
        {
            TrimCount++;
            return false;
        }
    }

    private sealed class FakeUnloadableInferenceSession : IUnloadableInferenceSession
    {
        private readonly bool _unloadResult;
        private readonly Exception? _throwOnUnload;

        public FakeUnloadableInferenceSession(
            string sessionName, bool isLoaded, bool unloadResult, Exception? throwOnUnload = null)
        {
            SessionName = sessionName;
            IsLoaded = isLoaded;
            _unloadResult = unloadResult;
            _throwOnUnload = throwOnUnload;
        }

        public string SessionName { get; }

        public bool IsLoaded { get; }

        public int LoadCount => 1;

        public List<TimeSpan> ReceivedThresholds { get; } = [];

        public Task<bool> TryUnloadIfIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken)
        {
            ReceivedThresholds.Add(idleFor);

            if (_throwOnUnload is not null)
            {
                throw _throwOnUnload;
            }

            return Task.FromResult(_unloadResult);
        }
    }
}
