// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
public class OnnxEmbeddingProviderIdleUnloadTests
{
    private const string Query = "Wie lege ich einen neuen Mitarbeiter an?";
    private const int ParallelCallers = 4;
    private const int CallsPerCaller = 2;

    // More than two batch chunks, so the loop inside EmbedBatchAsync is wide enough for the unloader
    // to look at the provider several times while a single lease is held.
    private const int BatchTextCount = KnowledgeIndexConstants.EmbeddingBatchSize * 2 + 8;

    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.EmbeddingModelName);

    private static OnnxEmbeddingProvider Create() => new(new ModelLoader(new HttpClient()), CacheDir);

    private static string[] BatchTexts() => Enumerable.Range(0, BatchTextCount)
        .Select(i => $"Dienstplan Eintrag Nummer {i} fuer die Schicht am Morgen.")
        .ToArray();

    // Deliberately without the SlowModelLoad category: the whole point of this case is that an
    // uninitialised provider answers without ever touching a model file.
    [Test]
    public async Task TryUnloadIfIdleAsync_NeverInitialised_ReturnsFalse()
    {
        await using var provider = Create();

        var unloaded = await provider.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);

        unloaded.ShouldBeFalse();
        provider.IsLoaded.ShouldBeFalse();
        provider.LoadCount.ShouldBe(0);
    }

    [Test]
    [Category("SlowModelLoad")]
    public async Task EmbedQueryAsync_AfterUnload_ReloadsAndProducesIdenticalVectors()
    {
        await using var provider = Create();

        var expected = await provider.EmbedQueryAsync(Query, CancellationToken.None);
        provider.IsLoaded.ShouldBeTrue();
        provider.LoadCount.ShouldBe(1);

        var unloaded = await provider.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);

        unloaded.ShouldBeTrue();
        provider.IsLoaded.ShouldBeFalse();

        var actual = await provider.EmbedQueryAsync(Query, CancellationToken.None);

        provider.LoadCount.ShouldBe(2);

        // Bit-identical, not merely close: the reload rebuilds the same graph with the same session
        // options on the same machine, so any drift here would mean the unload changed what is
        // computed rather than only where the weights live.
        actual.Length.ShouldBe(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            actual[i].ShouldBe(expected[i]);
        }
    }

    [Test]
    [Category("SlowModelLoad")]
    public async Task TryUnloadIfIdleAsync_ThresholdNotReached_KeepsSessionLoaded()
    {
        await using var provider = Create();

        await provider.EmbedQueryAsync(Query, CancellationToken.None);

        var unloaded = await provider.TryUnloadIfIdleAsync(TimeSpan.FromHours(1), CancellationToken.None);

        unloaded.ShouldBeFalse();
        provider.IsLoaded.ShouldBeTrue();
        provider.LoadCount.ShouldBe(1);
    }

    // The failure this guards against is not a wrong number but a native use-after-free: disposing an
    // InferenceSession while Run() is executing on it faults the process instead of throwing. The
    // LoadCount assertion is what keeps the test honest - without it the run passes just as happily
    // when the unloader never managed to unload anything at all.
    //
    // Two bursts, not one fixed set of calls: a single burst only *hopes* every caller happens to be
    // between calls at the same instant the unloader samples activeRuns == 0 (OnnxSessionHolder.cs),
    // which is a bet on the thread scheduler - under load the callers can run continuously enough that
    // the window never opens, and the run fails with "LoadCount should be greater than 1 but was 1"
    // despite nothing being broken. Task.WhenAll on the first burst guarantees activeRuns == 0 the
    // moment it returns, so the idle window is arranged rather than awaited: the poll loop below waits
    // for the already-running unloader to observe that real window, and the second burst's first call
    // is then a structural reload, not a probabilistic one.
    [Test]
    [Category("SlowModelLoad")]
    public async Task EmbedQueryAsync_ParallelCallersWhileUnloaderRuns_NeverFaultsAndReloadsAtLeastOnce()
    {
        await using var provider = Create();

        var reference = await provider.EmbedQueryAsync(Query, CancellationToken.None);

        using var callersDone = new CancellationTokenSource();

        var unloader = Task.Run(async () =>
        {
            while (!callersDone.IsCancellationRequested)
            {
                await provider.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);
                await Task.Yield();
            }
        });

        async Task<float[][]> RunBurstAsync()
        {
            var callers = Enumerable.Range(0, ParallelCallers)
                .Select(_ => Task.Run(async () =>
                {
                    var results = new List<float[]>();
                    for (var i = 0; i < CallsPerCaller; i++)
                    {
                        results.Add(await provider.EmbedQueryAsync(Query, CancellationToken.None));
                    }

                    return results;
                }))
                .ToArray();

            var embedded = await Task.WhenAll(callers);
            return embedded.SelectMany(r => r).ToArray();
        }

        var firstBurst = await RunBurstAsync();
        var loadCountAfterFirstBurst = provider.LoadCount;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (provider.IsLoaded && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, CancellationToken.None);
        }

        provider.IsLoaded.ShouldBeFalse(
            "The unloader never reclaimed the session during the guaranteed idle window between the " +
            "two bursts (all callers had returned, so activeRuns was 0) - either TryUnloadIfIdleAsync " +
            "stopped seeing an idle session, or something is holding a lease open.");

        var secondBurst = await RunBurstAsync();

        await callersDone.CancelAsync();
        await unloader;

        foreach (var vector in firstBurst.Concat(secondBurst))
        {
            vector.Length.ShouldBe(reference.Length);
            for (var i = 0; i < reference.Length; i++)
            {
                vector[i].ShouldBe(reference[i]);
            }
        }

        provider.LoadCount.ShouldBeGreaterThan(loadCountAfterFirstBurst);
    }

    // A bulk pass spans several chunks of EmbeddingBatchSize and must hold ONE lease across all of
    // them, so the sweep cannot pull the session out from under the loop.
    // The provider is deliberately NOT warmed up first: a session that already exists before the batch
    // starts can legitimately be unloaded in the gap between Task.Run and the batch taking its lease,
    // and that unrelated release would be indistinguishable from the failure under test. With nothing
    // loaded beforehand, the batch's own load is the only one that may ever happen - so a load count of
    // one at the end, after thousands of zero-threshold unload attempts, says the session was held for
    // the entire loop. A lease taken per chunk instead would let the sweep in between two chunks, and
    // both the successful-unload counter and the load count would show it.
    [Test]
    [Category("SlowModelLoad")]
    public async Task EmbedBatchAsync_WhileBatchIsRunning_SessionIsNeverUnloadedMidLoop()
    {
        await using var provider = Create();

        var texts = BatchTexts();
        var batch = Task.Run(() => provider.EmbedBatchAsync(texts, CancellationToken.None));

        var unloadsWhileRunning = 0;
        var refusalsWhileRunning = 0;
        while (!batch.IsCompleted)
        {
            // LoadCount is incremented as the LAST statement of the load, after both the session and
            // the 17 MB tokenizer are built, so a non-zero count means the seconds-long load window is
            // over. From there on the session is non-null and no elapsed time is ever below
            // TimeSpan.Zero, which leaves a running call as the reason for a refusal - the two
            // statements between the counter and the lock release are the one other possibility, and
            // they are nanoseconds against a poll every millisecond over a multi-second batch. Without
            // this guard a refusal could come from the load window itself, and the assertion would say
            // nothing about the chunk loop.
            var running = provider.LoadCount >= 1 && provider.IsLoaded;
            if (await provider.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None))
            {
                unloadsWhileRunning++;
            }
            else if (running)
            {
                refusalsWhileRunning++;
            }

            await Task.Delay(1);
        }

        var vectors = await batch;

        vectors.Length.ShouldBe(texts.Length);
        unloadsWhileRunning.ShouldBe(0);
        refusalsWhileRunning.ShouldBeGreaterThan(0);
        provider.LoadCount.ShouldBe(1);
    }
}
