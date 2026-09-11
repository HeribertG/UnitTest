// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for KnowledgeIndexSyncScheduler: single-flight, coalescing of requests that arrive during a
/// run, failure isolation (status records it, the gate reopens), cancellation through
/// ApplicationStopping, and RunNowAsync waiting for a run that started after the call.
/// </summary>

using System.Collections.Concurrent;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Infrastructure.Services;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexSyncSchedulerTests
{
    private const string Reason = "installing language plugin 'pl'";
    private const int ConcurrentRequestCount = 50;

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(100);
    private static readonly DateTime Now = new(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc);

    private ControllableSynchronizer _synchronizer = null!;
    private CancellationTokenSource _stopping = null!;
    private RecordingLogger<KnowledgeIndexSyncScheduler> _logger = null!;
    private int _scopesCreated;
    private KnowledgeIndexSyncScheduler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _synchronizer = new ControllableSynchronizer();
        _stopping = new CancellationTokenSource();
        _logger = new RecordingLogger<KnowledgeIndexSyncScheduler>();
        _scopesCreated = 0;

        var services = new ServiceCollection();
        services.AddScoped<IKnowledgeIndexSynchronizer>(_ =>
        {
            Interlocked.Increment(ref _scopesCreated);
            return _synchronizer;
        });

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(_stopping.Token);

        _sut = new KnowledgeIndexSyncScheduler(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            new SettableTimeProvider(Now),
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _synchronizer.ReleaseAll();
        _stopping.Dispose();
    }

    [Test]
    public async Task Request_WhenIdle_RunsOnceInAFreshScopeAndRecordsTheCompletion()
    {
        _sut.Request(Reason);
        await WaitUntilIdleAsync();

        _synchronizer.Calls.ShouldBe(1);
        _scopesCreated.ShouldBe(1);
        _sut.Status.ShouldBe(new(false, false, new DateTimeOffset(Now), null, Reason, null));
    }

    [Test]
    public async Task Request_TwoSequentialRuns_EachGetsItsOwnScope()
    {
        _sut.Request(Reason);
        await WaitUntilIdleAsync();
        _sut.Request(Reason);
        await WaitUntilIdleAsync();

        _synchronizer.Calls.ShouldBe(2);
        _scopesCreated.ShouldBe(2);
    }

    [Test]
    public async Task Request_ManyConcurrentRequests_NeverRunInParallelAndCallAtMostTwice()
    {
        _synchronizer.BlockCall(1);

        await Task.WhenAll(Enumerable.Range(0, ConcurrentRequestCount)
            .Select(i => Task.Run(() => _sut.Request($"{Reason} #{i}"))));
        _synchronizer.Release(1);
        await WaitUntilIdleAsync();

        _synchronizer.Calls.ShouldBeInRange(1, 2);
        _synchronizer.MaxConcurrentCalls.ShouldBe(1);
    }

    [Test]
    public async Task Request_DuringARun_CoalescesIntoExactlyOneFollowUpRun()
    {
        _synchronizer.BlockCall(1);
        _sut.Request(Reason);
        await _synchronizer.WaitForCallStartedAsync(1);

        for (var i = 0; i < ConcurrentRequestCount; i++)
        {
            _sut.Request($"{Reason} #{i}");
        }

        _sut.Status.IsRunning.ShouldBeTrue();
        _sut.Status.IsPending.ShouldBeTrue();

        _synchronizer.Release(1);
        await WaitUntilIdleAsync();

        _synchronizer.Calls.ShouldBe(2);
        _synchronizer.MaxConcurrentCalls.ShouldBe(1);
        _sut.Status.LastReason.ShouldBe($"{Reason} #{ConcurrentRequestCount - 1}");
    }

    [Test]
    public async Task Request_RunFails_RecordsTheErrorLogsAWarningAndTheNextRequestRunsAgain()
    {
        _synchronizer.FailCall(1, new InvalidOperationException("embedding provider down"));

        Should.NotThrow(() => _sut.Request(Reason));
        await WaitUntilIdleAsync();

        var failed = _sut.Status;
        failed.IsRunning.ShouldBeFalse();
        failed.LastError.ShouldBe("embedding provider down");
        failed.LastFailedUtc.ShouldBe(new DateTimeOffset(Now));
        failed.LastCompletedUtc.ShouldBeNull();
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);

        _sut.Request(Reason);
        await WaitUntilIdleAsync();

        _synchronizer.Calls.ShouldBe(2);
        _sut.Status.LastCompletedUtc.ShouldBe(new DateTimeOffset(Now));
    }

    [Test]
    public async Task ApplicationStopping_CancelsTheRunningSyncAndReleasesEveryWaiter()
    {
        _synchronizer.BlockCall(1);
        _sut.Request(Reason);
        await _synchronizer.WaitForCallStartedAsync(1);
        var waiter = _sut.RunNowAsync(Reason, CancellationToken.None);

        _stopping.Cancel();

        await waiter.WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync();
        _synchronizer.ReceivedTokens.Single().IsCancellationRequested.ShouldBeTrue();
        _synchronizer.Calls.ShouldBe(1);
        _sut.Status.LastError.ShouldBeNull();
        _sut.Status.LastCompletedUtc.ShouldBeNull();
    }

    [Test]
    public async Task ApplicationStopping_AfterwardsNoFurtherRunStartsAndRunNowAsyncStillReturns()
    {
        _stopping.Cancel();

        _sut.Request(Reason);
        await _sut.RunNowAsync(Reason, CancellationToken.None).WaitAsync(CompletionTimeout);
        await WaitUntilIdleAsync();

        _synchronizer.Calls.ShouldBe(0);
    }

    [Test]
    public async Task RunNowAsync_WhenIdle_ReturnsAfterItsRunCompleted()
    {
        await _sut.RunNowAsync(Reason, CancellationToken.None).WaitAsync(CompletionTimeout);

        _synchronizer.Calls.ShouldBe(1);
        _sut.Status.LastCompletedUtc.ShouldBe(new DateTimeOffset(Now));
    }

    [Test]
    public async Task RunNowAsync_WhileARunIsInFlight_WaitsForARunThatStartedAfterTheCall()
    {
        _synchronizer.BlockCall(1);
        _synchronizer.BlockCall(2);
        _sut.Request(Reason);
        await _synchronizer.WaitForCallStartedAsync(1);

        var waiter = _sut.RunNowAsync(Reason, CancellationToken.None);

        _synchronizer.Release(1);
        await _synchronizer.WaitForCallStartedAsync(2);
        await Task.Delay(ShortWait);
        waiter.IsCompleted.ShouldBeFalse("the run in flight may have read the catalogue before the caller's change");

        _synchronizer.Release(2);
        await waiter.WaitAsync(CompletionTimeout);

        _synchronizer.Calls.ShouldBe(2);
    }

    [Test]
    public async Task RunNowAsync_RunFails_ReturnsWithoutThrowingAndReportsTheFailure()
    {
        _synchronizer.FailCall(1, new InvalidOperationException("database unreachable"));

        await Should.NotThrowAsync(() => _sut.RunNowAsync(Reason, CancellationToken.None).WaitAsync(CompletionTimeout));

        _sut.Status.LastError.ShouldBe("database unreachable");
    }

    [Test]
    public async Task RunNowAsync_CallerCancels_StopsWaitingWhileTheRunContinues()
    {
        _synchronizer.BlockCall(1);
        using var callerCancellation = new CancellationTokenSource();
        var waiter = _sut.RunNowAsync(Reason, callerCancellation.Token);
        await _synchronizer.WaitForCallStartedAsync(1);

        callerCancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => waiter.WaitAsync(CompletionTimeout));
        _synchronizer.ReceivedTokens.Single().IsCancellationRequested.ShouldBeFalse();

        _synchronizer.Release(1);
        await WaitUntilIdleAsync();
        _sut.Status.LastCompletedUtc.ShouldBe(new DateTimeOffset(Now));
    }

    private async Task WaitUntilIdleAsync()
    {
        using var timeout = new CancellationTokenSource(CompletionTimeout);
        while (true)
        {
            var status = _sut.Status;
            if (!status.IsRunning && !status.IsPending)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class ControllableSynchronizer : IKnowledgeIndexSynchronizer
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _blocks = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<int, Exception> _failures = new();
        private int _calls;
        private int _active;
        private int _maxConcurrentCalls;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public ConcurrentQueue<CancellationToken> ReceivedTokens { get; } = new();

        public void BlockCall(int call) =>
            _blocks[call] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(int call) => _blocks[call].TrySetResult();

        public void ReleaseAll()
        {
            foreach (var block in _blocks.Values)
            {
                block.TrySetResult();
            }
        }

        public void FailCall(int call, Exception exception) => _failures[call] = exception;

        public Task WaitForCallStartedAsync(int call) => Started(call).Task.WaitAsync(CompletionTimeout);

        public async Task SyncAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            ReceivedTokens.Enqueue(cancellationToken);
            Started(call).TrySetResult();

            try
            {
                if (_blocks.TryGetValue(call, out var block))
                {
                    await block.Task.WaitAsync(cancellationToken);
                }

                if (_failures.TryGetValue(call, out var failure))
                {
                    throw failure;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private TaskCompletionSource Started(int call) =>
            _started.GetOrAdd(call, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private void UpdateMaximum(int active)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentCalls);
                if (active <= observed)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maxConcurrentCalls, active, observed) != observed);
        }
    }
}
