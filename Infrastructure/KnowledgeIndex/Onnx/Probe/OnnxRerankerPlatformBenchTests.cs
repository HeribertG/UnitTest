// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using System.Text.Json;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NUnit.Framework;
using Tokenizers.DotNet;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Latency bench for one reranker runtime variant: model file, IntraOp thread count, batch size and
/// candidate set are all read from environment variables so a shell loop can drive the matrix with one
/// process per variant. Measures the production provider for the headline latency and, on the same
/// inputs, an inline replica that splits tokenization from Session.Run. Reporting only, never asserts
/// on numbers.
/// </summary>
/// <param name="BENCH_MODEL_DIR">Directory holding the model file and tokenizer.json.</param>
/// <param name="BENCH_THREADS">IntraOpNumThreads for the session.</param>
/// <param name="BENCH_BATCH">Batch size used by the replica; the provider always uses the shipped constant.</param>
/// <param name="BENCH_CANDIDATES">JSON array of candidate texts; empty falls back to the synthetic probe set.</param>
[TestFixture]
[Explicit("Latency bench over a cached ONNX reranker model; one variant per process.")]
[Category("MemoryProbe")]
public class OnnxRerankerPlatformBenchTests
{
    private const string ModelDirVariable = "BENCH_MODEL_DIR";
    private const string ThreadsVariable = "BENCH_THREADS";
    private const string BatchVariable = "BENCH_BATCH";
    private const string RoundsVariable = "BENCH_ROUNDS";
    private const string CandidatesVariable = "BENCH_CANDIDATES";
    private const string OutputVariable = "BENCH_OUT";
    private const string LabelVariable = "BENCH_LABEL";
    private const string ParallelVariable = "BENCH_PARALLEL";
    private const string GateVariable = "BENCH_GATE";
    private const string LoadVariable = "BENCH_LOAD";
    private const string SpinningVariable = "BENCH_SPINNING";

    private const int DefaultParallel = 1;
    private const int DefaultLoad = 0;
    private const int DefaultSpinning = 1;

    // onnxruntime kOrtSessionOptionsConfigAllowIntraOpSpinning. "0" makes intra-op workers block on a
    // condition variable between ops instead of busy-waiting at the barrier. Declared here and not in
    // Klacks.Api.OnnxRuntimeConfigKeys because this bench must not touch production code.
    private const string AllowIntraOpSpinningKey = "session.intra_op.allow_spinning";
    private const string SpinningOff = "0";
    private const string SpinningOn = "1";

    private const int DefaultRounds = 15;
    private const string DefaultOutputFileName = "onnx-reranker-platform-bench.jsonl";
    private const string WarmupCandidate = "warmup candidate text";
    private const string PairSeparator = " </s></s> ";
    private const long PadTokenId = 1;
    private const int MaxSequenceLength = 512;
    private const string LogitsOutputName = "logits";
    private const double P50 = 0.50;
    private const double P95 = 0.95;
    private const int JitterRoundStride = 13;
    private const int JitterIndexStride = 3;
    private const int JitterMaxChars = 60;
    private const int TopKForOrderCheck = 3;

    private sealed record BenchResult(
        string Label,
        string ModelDir,
        string ModelFile,
        string CandidateSet,
        int Threads,
        int Batch,
        int Rounds,
        int Parallel,
        int Gate,
        int BackgroundLoadThreads,
        int AllowSpinning,
        string Platform,
        string BuildConfiguration,
        int ProcessorCount,
        string[] InputNames,
        string[] OutputNames,
        int CandidateCount,
        int CandidateChars,
        int TokenMin,
        int TokenMedian,
        int TokenMax,
        int TokensAtCap,
        int PaddedTokenCells,
        int UnpaddedTokenCells,
        double ProviderP50Ms,
        double ProviderP95Ms,
        double ProviderMinMs,
        double ProviderMaxMs,
        double ReplicaP50Ms,
        double ReplicaTokenizeP50Ms,
        double ReplicaRunP50Ms,
        double SessionBuildMs,
        bool Failed,
        string? Error,
        double[] Round0Scores,
        int[] Round0Top3);

    [Test]
    public void MeasureVariant()
    {
        var label = Environment.GetEnvironmentVariable(LabelVariable) ?? "unlabelled";
        var modelDir = Environment.GetEnvironmentVariable(ModelDirVariable)
            ?? Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.RerankerModelName);
        var threads = ReadInt(ThreadsVariable, Environment.ProcessorCount);
        var batch = ReadInt(BatchVariable, KnowledgeIndexConstants.RerankBatchSize);
        var rounds = ReadInt(RoundsVariable, DefaultRounds);
        var parallel = ReadInt(ParallelVariable, DefaultParallel);
        var gate = ReadInt(GateVariable, Environment.ProcessorCount);
        var load = ReadInt(LoadVariable, DefaultLoad);
        var spinning = ReadInt(SpinningVariable, DefaultSpinning);
        var candidatesPath = Environment.GetEnvironmentVariable(CandidatesVariable);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable)
            ?? Path.Combine(Path.GetTempPath(), DefaultOutputFileName);

        var modelPath = Path.Combine(modelDir, KnowledgeIndexConstants.RerankerModelFileName);
        var tokenizerPath = Path.Combine(modelDir, KnowledgeIndexConstants.RerankerTokenizerFileName);
        if (!File.Exists(modelPath))
            Assert.Ignore($"Model not found at {modelPath}. Set {ModelDirVariable}.");

        var baseCandidates = candidatesPath is { Length: > 0 }
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(candidatesPath))!
            : RerankerProbeCandidates.Build(0);
        var candidateSet = candidatesPath is { Length: > 0 } ? Path.GetFileName(candidatesPath) : "synthetic";

        TestContext.WriteLine(
            $"label={label} model={modelPath} threads={threads} batch={batch} rounds={rounds} " +
            $"parallel={parallel} gate={gate} load={load} spinning={spinning} set={candidateSet}");

        // Emulates the CPU pressure a live host puts on the session: the app process runs the
        // embedding session, the learning loop, EF Core and ASP.NET beside the reranker, and the ORT
        // intra-op pool has no priority over any of it.
        using var loadStop = new CancellationTokenSource();
        var loadThreads = StartBackgroundLoad(load, loadStop.Token);

        var failed = false;
        string? error = null;
        double[] round0Scores = [];
        double[] providerLatencies = [];
        var replica = new ReplicaMeasurement();
        string[] inputNames = [];
        string[] outputNames = [];
        var tokenStats = new TokenStats();
        double sessionBuildMs = 0;

        try
        {
            var profile = new OnnxRerankerRuntimeProfile(
                () => ThroughputOptions(threads, spinning), ShrinkArenaAfterRun: false, MaxConcurrentRuns: gate);

            var buildWatch = Stopwatch.StartNew();
            using (var metaOptions = ThroughputOptions(threads, spinning))
            using (var metaSession = new InferenceSession(modelPath, metaOptions))
            {
                sessionBuildMs = buildWatch.Elapsed.TotalMilliseconds;
                inputNames = metaSession.InputMetadata.Keys.ToArray();
                outputNames = metaSession.OutputMetadata.Keys.ToArray();
            }

            TestContext.WriteLine($"inputs=[{string.Join(",", inputNames)}] outputs=[{string.Join(",", outputNames)}] sessionBuild={sessionBuildMs:F0} ms");

            providerLatencies = MeasureProvider(modelDir, profile, baseCandidates, rounds, parallel, out round0Scores);
            replica = MeasureReplica(modelPath, tokenizerPath, threads, spinning, batch, baseCandidates, rounds, out tokenStats);
        }
        catch (Exception ex)
        {
            failed = true;
            error = ex.GetType().Name + ": " + ex.Message;
            TestContext.WriteLine($"FAILED: {error}");
        }

        loadStop.Cancel();
        foreach (var thread in loadThreads)
            thread.Join();

        var sortedProvider = providerLatencies.OrderBy(x => x).ToArray();
        var result = new BenchResult(
            label,
            modelDir,
            KnowledgeIndexConstants.RerankerModelFileName,
            candidateSet,
            threads,
            batch,
            rounds,
            parallel,
            gate,
            load,
            spinning,
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other",
            BuildConfiguration(),
            Environment.ProcessorCount,
            inputNames,
            outputNames,
            baseCandidates.Length,
            baseCandidates.Sum(c => c.Length),
            tokenStats.Min,
            tokenStats.Median,
            tokenStats.Max,
            tokenStats.AtCap,
            tokenStats.PaddedCells,
            tokenStats.UnpaddedCells,
            Percentile(sortedProvider, P50),
            Percentile(sortedProvider, P95),
            sortedProvider.Length == 0 ? 0 : sortedProvider[0],
            sortedProvider.Length == 0 ? 0 : sortedProvider[^1],
            replica.TotalP50Ms,
            replica.TokenizeP50Ms,
            replica.RunP50Ms,
            sessionBuildMs,
            failed,
            error,
            round0Scores,
            Top3(round0Scores));

        var line = JsonSerializer.Serialize(result);
        File.AppendAllText(outputPath, line + Environment.NewLine);
        TestContext.WriteLine(line);

        Assert.Pass();
    }

    private static Thread[] StartBackgroundLoad(int count, CancellationToken ct)
    {
        var threads = new Thread[Math.Max(0, count)];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                var sink = 0.0;
                while (!ct.IsCancellationRequested)
                    sink += Math.Sqrt(sink + 1.0);
                GC.KeepAlive(sink);
            })
            { IsBackground = true };
            threads[i].Start();
        }

        return threads;
    }

    private static double[] MeasureProvider(
        string modelDir,
        OnnxRerankerRuntimeProfile profile,
        string[] baseCandidates,
        int rounds,
        int parallel,
        out double[] round0Scores)
    {
        using var http = new HttpClient();
        var provider = new OnnxRerankerProvider(
            new ModelLoader(http), modelDir, string.Empty, string.Empty, string.Empty, string.Empty, profile);

        try
        {
            provider.ScoreAsync(RerankerProbeCandidates.Query, [WarmupCandidate], CancellationToken.None)
                .GetAwaiter().GetResult();

            round0Scores = provider
                .ScoreAsync(RerankerProbeCandidates.Query, baseCandidates, CancellationToken.None)
                .GetAwaiter().GetResult();

            var latencies = new List<double>();
            for (var round = 1; round <= rounds; round++)
            {
                var candidates = Jitter(baseCandidates, round);

                // Task.Run is what makes the callers concurrent: ScoreAsync has no real suspension
                // point once the session exists, so plain async lambdas would run one after another.
                var calls = Enumerable.Range(0, parallel).Select(_ => Task.Run(async () =>
                {
                    var watch = Stopwatch.StartNew();
                    await provider.ScoreAsync(RerankerProbeCandidates.Query, candidates, CancellationToken.None);
                    return watch.Elapsed.TotalMilliseconds;
                })).ToArray();

                var roundLatencies = Task.WhenAll(calls).GetAwaiter().GetResult();
                latencies.AddRange(roundLatencies);
                TestContext.WriteLine($"provider round {round}: {string.Join(" / ", roundLatencies.Select(l => l.ToString("F0")))} ms");
            }

            return latencies.ToArray();
        }
        finally
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private readonly record struct ReplicaMeasurement(double TotalP50Ms, double TokenizeP50Ms, double RunP50Ms);

    private readonly record struct TokenStats(int Min, int Median, int Max, int AtCap, int PaddedCells, int UnpaddedCells);

    private static ReplicaMeasurement MeasureReplica(
        string modelPath,
        string tokenizerPath,
        int threads,
        int spinning,
        int batch,
        string[] baseCandidates,
        int rounds,
        out TokenStats stats)
    {
        using var options = ThroughputOptions(threads, spinning);
        using var session = new InferenceSession(modelPath, options);
        using var tokenizer = new Tokenizer(vocabPath: tokenizerPath);

        RunOnce(session, tokenizer, [WarmupCandidate], batch, out _, out _);
        RunOnce(session, tokenizer, baseCandidates, batch, out _, out _);
        stats = Profile(tokenizer, baseCandidates, batch);

        var totals = new List<double>();
        var tokenizeTimes = new List<double>();
        var runTimes = new List<double>();
        for (var round = 1; round <= rounds; round++)
        {
            var candidates = Jitter(baseCandidates, round);
            var watch = Stopwatch.StartNew();
            RunOnce(session, tokenizer, candidates, batch, out var tokenizeMs, out var runMs);
            totals.Add(watch.Elapsed.TotalMilliseconds);
            tokenizeTimes.Add(tokenizeMs);
            runTimes.Add(runMs);
        }

        TestContext.WriteLine(
            $"replica: total p50={Percentile([.. totals.OrderBy(x => x)], P50):F0} " +
            $"tokenize p50={Percentile([.. tokenizeTimes.OrderBy(x => x)], P50):F0} " +
            $"run p50={Percentile([.. runTimes.OrderBy(x => x)], P50):F0} ms");

        return new ReplicaMeasurement(
            Percentile([.. totals.OrderBy(x => x)], P50),
            Percentile([.. tokenizeTimes.OrderBy(x => x)], P50),
            Percentile([.. runTimes.OrderBy(x => x)], P50));
    }

    // Mirrors OnnxRerankerProvider.ScoreAsync exactly: one Encode per candidate over the whole pair
    // string, truncation to the cap afterwards, length-sorted batches, padding to the batch maximum.
    private static double[] RunOnce(
        InferenceSession session,
        Tokenizer tokenizer,
        string[] candidates,
        int batch,
        out double tokenizeMs,
        out double runMs)
    {
        var tokenizeWatch = Stopwatch.StartNew();
        var encoded = new long[candidates.Length][];
        for (var i = 0; i < candidates.Length; i++)
        {
            encoded[i] = tokenizer.Encode(RerankerProbeCandidates.Query + PairSeparator + candidates[i])
                .Select(id => (long)id)
                .Take(MaxSequenceLength)
                .ToArray();
        }

        var order = Enumerable.Range(0, candidates.Length).OrderBy(i => encoded[i].Length).ToArray();
        tokenizeMs = tokenizeWatch.Elapsed.TotalMilliseconds;

        var runWatch = Stopwatch.StartNew();
        var scores = new double[candidates.Length];
        for (var start = 0; start < order.Length; start += batch)
        {
            var end = Math.Min(start + batch, order.Length);
            var chunk = new long[end - start][];
            for (var i = start; i < end; i++)
                chunk[i - start] = encoded[order[i]];

            var chunkScores = RunBatch(session, chunk);
            for (var i = 0; i < chunkScores.Length; i++)
                scores[order[start + i]] = chunkScores[i];
        }

        runMs = runWatch.Elapsed.TotalMilliseconds;
        return scores;
    }

    private static double[] RunBatch(InferenceSession session, IReadOnlyList<long[]> encoded)
    {
        var maxLen = encoded.Max(e => e.Length);
        var batchSize = encoded.Count;
        var inputIds = new long[batchSize * maxLen];
        var attentionMask = new long[batchSize * maxLen];

        for (var i = 0; i < batchSize; i++)
        {
            var row = encoded[i];
            for (var j = 0; j < row.Length; j++)
            {
                inputIds[i * maxLen + j] = row[j];
                attentionMask[i * maxLen + j] = 1;
            }

            for (var j = row.Length; j < maxLen; j++)
                inputIds[i * maxLen + j] = PadTokenId;
        }

        var dims = new[] { batchSize, maxLen };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dims))
        };

        using var outputs = session.Run(inputs);
        var logits = outputs.First(o => o.Name == LogitsOutputName).AsTensor<float>();

        var scores = new double[batchSize];
        for (var i = 0; i < batchSize; i++)
        {
            var logit = logits.Rank == 2 ? logits[i, 0] : logits[i];
            scores[i] = 1.0 / (1.0 + Math.Exp(-logit));
        }

        return scores;
    }

    private static TokenStats Profile(Tokenizer tokenizer, string[] candidates, int batch)
    {
        var lengths = candidates
            .Select(c => Math.Min(MaxSequenceLength, tokenizer.Encode(RerankerProbeCandidates.Query + PairSeparator + c).Length))
            .ToArray();
        var sorted = lengths.OrderBy(x => x).ToArray();

        var padded = 0;
        for (var start = 0; start < sorted.Length; start += batch)
        {
            var end = Math.Min(start + batch, sorted.Length);
            padded += (end - start) * sorted[end - 1];
        }

        TestContext.WriteLine(
            $"tokens: min={sorted[0]} median={sorted[sorted.Length / 2]} max={sorted[^1]} " +
            $"atCap={sorted.Count(l => l >= MaxSequenceLength)} paddedCells={padded} unpaddedCells={sorted.Sum()}");

        return new TokenStats(
            sorted[0], sorted[sorted.Length / 2], sorted[^1],
            sorted.Count(l => l >= MaxSequenceLength), padded, sorted.Sum());
    }

    private static string[] Jitter(string[] candidates, int round)
    {
        return candidates.Select((text, index) =>
        {
            var trim = (round * JitterRoundStride + index * JitterIndexStride) % JitterMaxChars;
            return text[..Math.Max(1, text.Length - trim)];
        }).ToArray();
    }

    private static SessionOptions ThroughputOptions(int threads, int allowSpinning)
    {
        var options = new SessionOptions
        {
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = threads,
        };

        options.AddSessionConfigEntry(
            AllowIntraOpSpinningKey, allowSpinning == 0 ? SpinningOff : SpinningOn);
        return options;
    }

    private static int[] Top3(double[] scores) =>
        scores.Length == 0
            ? []
            : Enumerable.Range(0, scores.Length)
                .OrderByDescending(i => scores[i])
                .Take(TopKForOrderCheck)
                .ToArray();

    private static string BuildConfiguration() =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

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
}
