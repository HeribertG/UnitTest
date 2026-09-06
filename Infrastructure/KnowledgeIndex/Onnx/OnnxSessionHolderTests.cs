// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
public class OnnxSessionHolderTests
{
    private const int ParallelCallers = 4;
    private const int CallsPerCaller = 50;
    private const int SingleSlot = 1;
    private const int SlotProbeMilliseconds = 100;
    private const int IdleGapMilliseconds = 1;
    private const string BuildFailure = "build failed";

    private sealed class LeaseState
    {
        public int Disposed;
    }

    private readonly record struct FakeLease(LeaseState State) : IDisposable
    {
        public void Dispose() => Interlocked.Increment(ref State.Disposed);
    }

    private sealed class Builder
    {
        public int Calls;
        public bool FailNext;
        public readonly List<LeaseState> Built = [];

        public Task<FakeLease> BuildAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException(BuildFailure);
            }

            var state = new LeaseState();
            Built.Add(state);
            return Task.FromResult(new FakeLease(state));
        }
    }

    private static (OnnxSessionHolder<FakeLease> Holder, Builder Builder) Create(
        int maxConcurrentRuns = OnnxSessionHolder<FakeLease>.UnlimitedConcurrency)
    {
        var builder = new Builder();
        return (new OnnxSessionHolder<FakeLease>(builder.BuildAsync, maxConcurrentRuns), builder);
    }

    [Test]
    public async Task TryUnloadIfIdleAsync_NeverAcquired_ReturnsFalseWithoutBuilding()
    {
        var (holder, builder) = Create();
        await using var _ = holder;

        var unloaded = await holder.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);

        unloaded.ShouldBeFalse();
        holder.IsLoaded.ShouldBeFalse();
        holder.LoadCount.ShouldBe(0);
        builder.Calls.ShouldBe(0);
    }

    [Test]
    public async Task AcquireAsync_BuildsOnceAndReusesTheLease()
    {
        var (holder, builder) = Create();
        await using var _ = holder;

        var first = await holder.AcquireAsync(CancellationToken.None);
        holder.Release();
        var second = await holder.AcquireAsync(CancellationToken.None);
        holder.Release();

        builder.Calls.ShouldBe(1);
        holder.LoadCount.ShouldBe(1);
        holder.IsLoaded.ShouldBeTrue();
        ReferenceEquals(first.State, second.State).ShouldBeTrue();
    }

    [Test]
    public async Task TryUnloadIfIdleAsync_WhileARunIsActive_RefusesAndKeepsTheLease()
    {
        var (holder, builder) = Create();
        await using var _ = holder;

        await holder.AcquireAsync(CancellationToken.None);
        var unloaded = await holder.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);
        holder.Release();

        unloaded.ShouldBeFalse();
        holder.IsLoaded.ShouldBeTrue();
        builder.Built[0].Disposed.ShouldBe(0);
    }

    [Test]
    public async Task TryUnloadIfIdleAsync_ThresholdNotReached_KeepsTheLease()
    {
        var (holder, builder) = Create();
        await using var _ = holder;

        await holder.AcquireAsync(CancellationToken.None);
        holder.Release();

        var unloaded = await holder.TryUnloadIfIdleAsync(TimeSpan.FromHours(1), CancellationToken.None);

        unloaded.ShouldBeFalse();
        holder.IsLoaded.ShouldBeTrue();
        builder.Built[0].Disposed.ShouldBe(0);
    }

    [Test]
    public async Task TryUnloadIfIdleAsync_IdleLease_DisposesItAndTheNextAcquireRebuilds()
    {
        var (holder, builder) = Create();
        await using var _ = holder;

        await holder.AcquireAsync(CancellationToken.None);
        holder.Release();

        var unloaded = await holder.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);

        unloaded.ShouldBeTrue();
        holder.IsLoaded.ShouldBeFalse();
        builder.Built[0].Disposed.ShouldBe(1);

        await holder.AcquireAsync(CancellationToken.None);
        holder.Release();

        holder.LoadCount.ShouldBe(2);
        builder.Calls.ShouldBe(2);
        holder.IsLoaded.ShouldBeTrue();
    }

    // The builder is the only place a half-built session could leak from. A holder that recorded a
    // load before the builder returned would hand every later caller a lease that never existed.
    [Test]
    public async Task AcquireAsync_BuilderThrows_LeavesNothingLoadedAndRetriesNextTime()
    {
        var (holder, builder) = Create();
        await using var _ = holder;
        builder.FailNext = true;

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => holder.AcquireAsync(CancellationToken.None));

        failure.Message.ShouldBe(BuildFailure);
        holder.IsLoaded.ShouldBeFalse();
        holder.LoadCount.ShouldBe(0);

        await holder.AcquireAsync(CancellationToken.None);
        holder.Release();

        holder.LoadCount.ShouldBe(1);
        builder.Calls.ShouldBe(2);
    }

    [Test]
    public async Task DisposeAsync_CalledTwice_DisposesTheLeaseOnceAndDoesNotThrow()
    {
        var (holder, builder) = Create();

        await holder.AcquireAsync(CancellationToken.None);
        holder.Release();

        await holder.DisposeAsync();
        await holder.DisposeAsync();

        builder.Built[0].Disposed.ShouldBe(1);
        holder.IsLoaded.ShouldBeFalse();
    }

    [Test]
    public async Task WaitForSlotAsync_SingleSlot_SecondCallerWaitsUntilTheSlotIsReleased()
    {
        var (holder, _) = Create(SingleSlot);
        await using var __ = holder;

        await holder.WaitForSlotAsync(CancellationToken.None);
        var second = holder.WaitForSlotAsync(CancellationToken.None);

        (await Task.WhenAny(second, Task.Delay(SlotProbeMilliseconds))).ShouldNotBeSameAs(second);

        holder.ReleaseSlot();
        await second;
        holder.ReleaseSlot();
    }

    [Test]
    public async Task WaitForSlotAsync_UnlimitedConcurrency_NeverBlocks()
    {
        var (holder, _) = Create();
        await using var __ = holder;

        for (var i = 0; i < ParallelCallers; i++)
        {
            await holder.WaitForSlotAsync(CancellationToken.None);
        }

        for (var i = 0; i < ParallelCallers; i++)
        {
            holder.ReleaseSlot();
        }
    }

    // The failure this guards against is a lease disposed while a caller still works on it. The
    // LoadCount assertion keeps the test honest - without it the run passes just as happily when the
    // unloader never managed to unload anything at all.
    [Test]
    public async Task AcquireAsync_ParallelCallersWhileUnloaderRuns_NeverHandsOutADisposedLease()
    {
        var (holder, _) = Create();
        await using var __ = holder;

        using var callersDone = new CancellationTokenSource();

        var unloader = Task.Run(async () =>
        {
            while (!callersDone.IsCancellationRequested)
            {
                await holder.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);
                await Task.Yield();
            }
        });

        var callers = Enumerable.Range(0, ParallelCallers)
            .Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < CallsPerCaller; i++)
                {
                    var lease = await holder.AcquireAsync(CancellationToken.None);
                    try
                    {
                        await Task.Yield();
                        Volatile.Read(ref lease.State.Disposed).ShouldBe(0);
                    }
                    finally
                    {
                        holder.Release();
                    }

                    // A fake lease makes each call microseconds long; without a gap four callers keep
                    // the refcount above zero almost permanently and the unloader never gets its turn.
                    await Task.Delay(IdleGapMilliseconds);
                }
            }))
            .ToArray();

        await Task.WhenAll(callers);
        callersDone.Cancel();
        await unloader;

        holder.LoadCount.ShouldBeGreaterThan(1);
    }
}
