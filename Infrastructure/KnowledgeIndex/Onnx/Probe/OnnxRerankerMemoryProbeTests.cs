// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using System.Text.Json;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Measures resident memory and latency of one reranker runtime variant in this process and appends
/// the result as a JSON line. Exactly one variant per process: the ORT arena and any OrtEnv allocator
/// are process-global, and so is the peak-RSS counter. Driven entirely by environment variables so a
/// shell loop can run the matrix. Reporting only; never asserts on numbers.
/// </summary>
[TestFixture]
[Explicit("Memory probe over the cached ONNX reranker model; one variant per process, run via scripts/onnx-memory-probe.sh.")]
[Category("MemoryProbe")]
public class OnnxRerankerMemoryProbeTests
{
    private const string VariantVariable = "PROBE_VARIANT";
    private const string ParallelVariable = "PROBE_PARALLEL";
    private const string GateVariable = "PROBE_GATE";
    private const string RoundsVariable = "PROBE_ROUNDS";
    private const string ThreadsVariable = "PROBE_THREADS";
    private const string OutputVariable = "PROBE_OUT";
    private const string ModelsRootVariable = "PROBE_MODELS_ROOT";

    private const int DefaultParallel = 1;
    private const int DefaultGate = OnnxRerankerRuntimeProfile.UnlimitedConcurrency;
    private const int DefaultRounds = 10;
    private const string DefaultOutputFileName = "onnx-memory-probe.jsonl";
    private const string WarmupCandidate = "warmup candidate text";
    private const double P50 = 0.50;
    private const double P95 = 0.95;

    private sealed record ProbeResult(
        string Variant,
        int Parallel,
        int Gate,
        int Threads,
        int Rounds,
        string Platform,
        int ProcessorCount,
        double RssBaselineMb,
        double RssAfterLoadMb,
        double RssAfterWarmupMb,
        double RssAfterRoundsMb,
        double RssAfterGcMb,
        double PeakRssMb,
        double LatencyP50Ms,
        double LatencyP95Ms,
        double LatencyMaxMs,
        bool Failed,
        string? Error,
        double[] Round0Scores);

    [Test]
    public async Task MeasureVariant()
    {
        var variant = Environment.GetEnvironmentVariable(VariantVariable) ?? ProbeVariantCatalog.Baseline;
        var parallel = ReadInt(ParallelVariable, DefaultParallel);
        var gate = ReadInt(GateVariable, DefaultGate);
        var rounds = ReadInt(RoundsVariable, DefaultRounds);
        var threads = ReadInt(ThreadsVariable, Environment.ProcessorCount);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable)
            ?? Path.Combine(Path.GetTempPath(), DefaultOutputFileName);
        var modelDir = ResolveModelDirectory();

        TestContext.WriteLine($"variant={variant} parallel={parallel} gate={gate} rounds={rounds} threads={threads} model={modelDir}");

        var baselineSample = ProcessMemoryProbe.Read();
        var latencies = new List<double>();
        double[] round0Scores = [];
        var failed = false;
        string? error = null;
        ProcessMemoryProbe.Sample afterLoad = baselineSample, afterWarmup = baselineSample, afterRounds = baselineSample, afterGc = baselineSample;

        try
        {
            var profile = ProbeVariantCatalog.Resolve(variant, threads, gate);
            await using var provider = new OnnxRerankerProvider(
                new ModelLoader(new HttpClient()), modelDir, string.Empty, string.Empty, string.Empty, string.Empty, profile);

            // Session build is measured after scoring one short pair, the same way OnnxWarmupService
            // brings the session up in production.
            await provider.ScoreAsync(RerankerProbeCandidates.Query, [WarmupCandidate], CancellationToken.None);
            afterLoad = ProcessMemoryProbe.Read();

            round0Scores = await provider.ScoreAsync(RerankerProbeCandidates.Query, RerankerProbeCandidates.Build(0), CancellationToken.None);
            afterWarmup = ProcessMemoryProbe.Read();

            for (var round = 1; round <= rounds; round++)
            {
                var candidates = RerankerProbeCandidates.Build(round);

                // Task.Run is what makes the callers concurrent: once the session exists, ScoreAsync
                // has no real suspension point, so plain async lambdas would run one after another
                // on the calling thread. Production callers arrive on separate request threads.
                var calls = Enumerable.Range(0, parallel).Select(_ => Task.Run(async () =>
                {
                    var watch = Stopwatch.StartNew();
                    await provider.ScoreAsync(RerankerProbeCandidates.Query, candidates, CancellationToken.None);
                    return watch.Elapsed.TotalMilliseconds;
                })).ToArray();
                latencies.AddRange(await Task.WhenAll(calls));

                var sample = ProcessMemoryProbe.Read();
                TestContext.WriteLine($"round {round}: rss={sample.ResidentMb:F0} MB peak={sample.PeakResidentMb:F0} MB");
            }

            afterRounds = ProcessMemoryProbe.Read();

            // Measured while the session is still alive: what a managed GC gives back with the
            // native ORT allocations untouched. The provider is disposed only after this sample.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            afterGc = ProcessMemoryProbe.Read();
        }
        catch (Exception ex)
        {
            failed = true;
            error = ex.GetType().Name + ": " + ex.Message;
            TestContext.WriteLine($"FAILED: {error}");
        }

        var sorted = latencies.OrderBy(x => x).ToArray();
        var result = new ProbeResult(
            variant, parallel, gate, threads, rounds,
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other",
            Environment.ProcessorCount,
            baselineSample.ResidentMb,
            afterLoad.ResidentMb,
            afterWarmup.ResidentMb,
            afterRounds.ResidentMb,
            afterGc.ResidentMb,
            afterGc.PeakResidentMb,
            Percentile(sorted, P50),
            Percentile(sorted, P95),
            sorted.Length == 0 ? 0 : sorted[^1],
            failed,
            error,
            round0Scores);

        var line = JsonSerializer.Serialize(result);
        File.AppendAllText(outputPath, line + Environment.NewLine);
        TestContext.WriteLine(line);

        Assert.Pass();
    }

    private static int ReadInt(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string ResolveModelDirectory()
    {
        var root = Environment.GetEnvironmentVariable(ModelsRootVariable)
            ?? Path.Combine(Path.GetTempPath(), "klacks-test-models");
        var dir = Path.Combine(root, KnowledgeIndexConstants.RerankerModelName);
        if (!File.Exists(Path.Combine(dir, KnowledgeIndexConstants.RerankerModelFileName)))
            Assert.Ignore($"Reranker model not found under {dir}. Set {ModelsRootVariable}.");
        return dir;
    }
}
