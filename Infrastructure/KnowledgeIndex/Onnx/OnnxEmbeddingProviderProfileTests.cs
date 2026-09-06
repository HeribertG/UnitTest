// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Microsoft.ML.OnnxRuntime;
using NUnit.Framework;
using Shouldly;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
[Category("SlowModelLoad")]
public class OnnxEmbeddingProviderProfileTests
{
    private const string Query = "Wie lege ich einen neuen Mitarbeiter an?";
    private const double ComponentTolerance = 1e-5;
    private const double MinimumCosine = 0.999999;
    private const int ParallelCallers = 4;

    // cpus: 1.5 in the production compose file rounds up to two cores.
    private const int ProductionCoreCount = 2;

    // fp16 against fp32 sat at cosine 0.999999 and flipped one golden case in 877; the thread-order
    // noise measured at ten threads is 0.9999954, so 0.99999 leaves the same order of headroom.
    private const double MinimumCosineAcrossThreadCounts = 0.99999;

    // Tighter than ComponentTolerance and asserted without the cosine fallback: the arena shrinkage run
    // option must not move a single component, so the claim under test is bit-identity and not
    // "close enough to retrieve the same rows".
    private const double ShrinkComponentTolerance = 1e-6;

    // More than two EmbeddingBatchSize chunks, so the shrinkage run option is exercised repeatedly
    // rather than once, which is the way KnowledgeIndexSynchronizer drives it.
    private const int BulkTextCount = 40;

    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.EmbeddingModelName);

    private static OnnxEmbeddingProvider Create(OnnxEmbeddingRuntimeProfile profile) =>
        new(new ModelLoader(new HttpClient()), CacheDir, profile);

    // The fp16 export only loads at ORT_ENABLE_BASIC, so the optimization level is pinned here and
    // only the arena, the memory pattern and the thread count are varied - the three knobs that
    // change where activations live, never what is computed.
    private static SessionOptions Session(bool arena, bool pattern, int threads) => new()
    {
        EnableCpuMemArena = arena,
        EnableMemoryPattern = pattern,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
        InterOpNumThreads = 1,
        IntraOpNumThreads = threads,
    };

    // The reference in the neutrality tests is the pre-2026-09-06 profile (arena off, one thread), so
    // the shipped Default (arena on, bulk shrink, gate) keeps being proven against it. The thread
    // count is pinned to the production container's two cores here: with ten intra-op threads on the
    // arm64 dev host the summation order moves components by up to 4e-4 (cosine 0.9999954), which is
    // well inside retrieval tolerance but not the identity this test is about.
    [Test]
    public async Task EmbedQueryAsync_DefaultProfileAtProductionThreadCount_ProducesSameVectorAsLegacy()
    {
        float[] expected;
        await using (var reference = Create(OnnxEmbeddingRuntimeProfile.Legacy))
        {
            expected = await reference.EmbedQueryAsync(Query, CancellationToken.None);
        }

        float[] actual;
        await using (var shipped = Create(OnnxEmbeddingRuntimeProfile.Default with
        {
            CreateSessionOptions = () => Session(arena: true, pattern: true, threads: ProductionCoreCount),
        }))
        {
            actual = await shipped.EmbedQueryAsync(Query, CancellationToken.None);
        }

        AssertSameVector(expected, actual);
    }

    [Test]
    public async Task EmbedQueryAsync_DefaultProfileAtHostThreadCount_StaysWithinRetrievalTolerance()
    {
        float[] expected;
        await using (var reference = Create(OnnxEmbeddingRuntimeProfile.Legacy))
        {
            expected = await reference.EmbedQueryAsync(Query, CancellationToken.None);
        }

        float[] actual;
        await using (var shipped = Create(OnnxEmbeddingRuntimeProfile.Default))
        {
            actual = await shipped.EmbedQueryAsync(Query, CancellationToken.None);
        }

        Cosine(expected, actual).ShouldBeGreaterThanOrEqualTo(MinimumCosineAcrossThreadCounts);
    }

    [Test]
    public async Task EmbedQueryAsync_GatedToOne_ParallelCallersGetSequentialVectors()
    {
        await using var provider = Create(OnnxEmbeddingRuntimeProfile.Default with { MaxConcurrentRuns = 1 });

        var sequential = await provider.EmbedQueryAsync(Query, CancellationToken.None);

        var parallel = await Task.WhenAll(Enumerable.Range(0, ParallelCallers)
            .Select(_ => Task.Run(() => provider.EmbedQueryAsync(Query, CancellationToken.None))));

        foreach (var vector in parallel)
        {
            AssertSameVector(sequential, vector);
        }
    }

    [Test]
    public async Task EmbedBatchAsync_ArenaShrinkAfterBulkRun_ProducesSameVectorsAsLegacy()
    {
        var texts = BulkTexts();

        float[][] expected;
        await using (var reference = Create(OnnxEmbeddingRuntimeProfile.Legacy))
        {
            expected = await reference.EmbedBatchAsync(texts, CancellationToken.None);
        }

        float[][] actual;
        await using (var shrinking = Create(ArenaShrinkingProfile()))
        {
            actual = await shrinking.EmbedBatchAsync(texts, CancellationToken.None);
        }

        actual.Length.ShouldBe(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            AssertIdenticalVector(expected[i], actual[i], i);
        }
    }

    [Test]
    public async Task EmbedQueryAsync_ArenaShrinkAfterBulkRun_LeavesQueryPathUnchanged()
    {
        float[] expected;
        await using (var reference = Create(OnnxEmbeddingRuntimeProfile.Legacy))
        {
            expected = await reference.EmbedQueryAsync(Query, CancellationToken.None);
        }

        float[] actual;
        await using (var shrinking = Create(ArenaShrinkingProfile()))
        {
            actual = await shrinking.EmbedQueryAsync(Query, CancellationToken.None);
        }

        AssertIdenticalVector(expected, actual, index: 0);
    }

    private static OnnxEmbeddingRuntimeProfile ArenaShrinkingProfile() =>
        OnnxEmbeddingRuntimeProfile.Legacy with
        {
            CreateSessionOptions = () => Session(arena: true, pattern: true, threads: 1),
            ShrinkArenaAfterBulkRun = true,
        };

    /// <summary>
    /// Distinct short passages rather than the probe corpus: the shrinkage claim is about the run
    /// option, not about sequence length, and 40 padded 512-token rows would make this a multi-minute
    /// test for no additional coverage.
    /// </summary>
    private static string[] BulkTexts() =>
        [.. Enumerable.Range(0, BulkTextCount)
            .Select(i => $"passage {i}: Dienstplan, Abwesenheit, Spesen und Gruppen im Betrieb {i}.")];

    private static void AssertIdenticalVector(float[] expected, float[] actual, int index)
    {
        actual.Length.ShouldBe(expected.Length);

        var maxDelta = expected.Zip(actual, (e, a) => (double)Math.Abs(e - a)).Max();
        maxDelta.ShouldBeLessThanOrEqualTo(
            ShrinkComponentTolerance,
            $"vector {index} maxComponentDelta={maxDelta:E3}");
    }

    private static double Cosine(float[] expected, float[] actual)
    {
        var dot = expected.Zip(actual, (e, a) => (double)e * a).Sum();
        var expectedNorm = Math.Sqrt(expected.Sum(e => (double)e * e));
        var actualNorm = Math.Sqrt(actual.Sum(a => (double)a * a));
        return dot / (expectedNorm * actualNorm);
    }

    private static void AssertSameVector(float[] expected, float[] actual)
    {
        actual.Length.ShouldBe(expected.Length);

        var maxDelta = expected.Zip(actual, (e, a) => Math.Abs(e - a)).Max();
        if (maxDelta <= ComponentTolerance)
        {
            return;
        }

        var cosine = Cosine(expected, actual);

        cosine.ShouldBeGreaterThanOrEqualTo(
            MinimumCosine,
            $"maxComponentDelta={maxDelta:E3} cosine={cosine:F9}");
    }
}
