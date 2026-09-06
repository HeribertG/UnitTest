// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using System.Text.Json;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;
using Tokenizers.DotNet;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Measures resident memory of one embedding runtime variant on the BULK path: a single
/// EmbedBatchAsync call over the whole index corpus, which is how KnowledgeIndexSynchronizer embeds an
/// empty index at startup and the shape the original OOM kill came from. Exactly one variant per
/// process, because the ORT arena and the VmHWM counter are both process-global. The same corpus runs
/// several times so a high-water mark that keeps rising on identical input shapes is visible as
/// allocator behaviour. Reporting only; never asserts on numbers.
/// </summary>
[TestFixture]
[Explicit("Memory probe over the cached ONNX embedding model; one variant per process, run via scripts/onnx-memory-probe.sh BULK.")]
[Category("MemoryProbe")]
public class OnnxEmbeddingBulkMemoryProbeTests
{
    private const string VariantVariable = "PROBE_VARIANT";
    private const string ThreadsVariable = "PROBE_THREADS";
    private const string GateVariable = "PROBE_GATE";
    private const string CorpusVariable = "PROBE_CORPUS";
    private const string BulksVariable = "PROBE_BULKS";
    private const string OutputVariable = "PROBE_OUT";
    private const string ModelsRootVariable = "PROBE_MODELS_ROOT";

    private const int DefaultGate = OnnxEmbeddingRuntimeProfile.UnlimitedConcurrency;
    private const int DefaultBulks = 2;
    private const int SingleCaller = 1;
    private const string DefaultOutputFileName = "onnx-memory-probe.jsonl";
    private const string WarmupText = "warmup passage";
    private const string PassagePrefix = "passage: ";
    private const int MaxSequenceLength = 512;
    private const double P50 = 0.50;
    private const double P95 = 0.95;

    // Only the leading components of the first corpus vector are stored, as in the query probe: 768
    // floats per row would dominate the JSONL, and a drift in the session configuration shows up in the
    // first components just as it would anywhere else in the vector.
    private const int StoredVectorComponents = 8;

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
        double[] Round0Scores,
        int CorpusSize,
        int Bulks,
        double PeakAfterBulk1Mb,
        double[] BulkMs,
        double[] BulkRssMb,
        double[] BulkPeakMb,
        double HostAvailableMbAtStart,
        int ChunkCount,
        int ChunksAtCap,
        int TokenMin,
        int TokenMedian,
        int TokenMax,
        int PaddedTokenTotal,
        double RssAfterTrimMb,
        bool HeapTrimmed);

    [Test]
    public async Task MeasureBulkVariant()
    {
        var variant = Environment.GetEnvironmentVariable(VariantVariable) ?? EmbeddingProbeVariantCatalog.Frugal;
        var threads = ReadInt(ThreadsVariable, Environment.ProcessorCount);
        var gate = ReadInt(GateVariable, DefaultGate);
        var corpusSize = ReadInt(CorpusVariable, EmbeddingBulkProbeCorpus.DefaultSize);
        var bulks = ReadInt(BulksVariable, DefaultBulks);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable)
            ?? Path.Combine(Path.GetTempPath(), DefaultOutputFileName);
        var modelDir = ResolveModelDirectory();

        TestContext.WriteLine($"variant={variant} threads={threads} gate={gate} corpus={corpusSize} bulks={bulks} model={modelDir}");

        // Built once and reused for every pass on purpose: bulk N exists to answer whether the arena
        // holds its high-water mark, and that question is only answerable if the input shapes are
        // identical between passes.
        var corpus = EmbeddingBulkProbeCorpus.Build(corpusSize);
        var shapes = MeasureShapes(modelDir, corpus);
        TestContext.WriteLine($"shapes: chunks={shapes.ChunkCount} atCap={shapes.ChunksAtCap} tokens min/median/max={shapes.TokenMin}/{shapes.TokenMedian}/{shapes.TokenMax} paddedTokens={shapes.PaddedTokenTotal}");

        var baselineSample = ProcessMemoryProbe.Read();
        var hostAvailable = ReadHostAvailableMb();
        var durations = new List<double>();
        var bulkRss = new List<double>();
        var bulkPeak = new List<double>();
        double[] referenceComponents = [];
        var failed = false;
        string? error = null;
        var heapTrimmed = false;
        ProcessMemoryProbe.Sample afterLoad = baselineSample, afterFirstBulk = baselineSample, afterLastBulk = baselineSample, afterGc = baselineSample, afterTrim = baselineSample;

        try
        {
            var profile = EmbeddingProbeVariantCatalog.Resolve(variant, threads, gate);
            await using var provider = new OnnxEmbeddingProvider(new ModelLoader(new HttpClient()), modelDir, profile);

            // Session build is measured after embedding one short passage, the same way
            // OnnxWarmupService brings the session up in production, so the bulk numbers below are the
            // cost of the bulk itself and not of the 555 MB weight load.
            await provider.EmbedBatchAsync([WarmupText], CancellationToken.None);
            afterLoad = ProcessMemoryProbe.Read();
            TestContext.WriteLine($"round 0: rss={afterLoad.ResidentMb:F0} MB peak={afterLoad.PeakResidentMb:F0} MB (after load)");

            for (var bulk = 1; bulk <= bulks; bulk++)
            {
                var watch = Stopwatch.StartNew();
                var vectors = await provider.EmbedBatchAsync(corpus, CancellationToken.None);
                watch.Stop();

                var sample = ProcessMemoryProbe.Read();
                durations.Add(watch.Elapsed.TotalMilliseconds);
                bulkRss.Add(sample.ResidentMb);
                bulkPeak.Add(sample.PeakResidentMb);

                if (bulk == 1)
                {
                    referenceComponents = vectors[0].Take(StoredVectorComponents).Select(value => (double)value).ToArray();
                    afterFirstBulk = sample;
                }

                afterLastBulk = sample;

                // Written per pass and not only at the end: a process the OOM killer takes writes no
                // JSONL line at all, so the console series is the only surviving record of how far it
                // got and how the high-water mark moved.
                TestContext.WriteLine($"round {bulk}: rss={sample.ResidentMb:F0} MB peak={sample.PeakResidentMb:F0} MB bulk={watch.Elapsed.TotalSeconds:F1} s");
            }

            // Measured while the session is still alive: what a managed GC gives back with the native
            // ORT allocations untouched. The provider is disposed only after this sample.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            afterGc = ProcessMemoryProbe.Read();

            // Separates "the arena never released anything" from "the arena released to glibc and glibc
            // kept the pages". Arena shrinkage hands regions back to the CPU device allocator, which is
            // malloc; whether that reaches the kernel is a glibc decision, and malloc_trim is the only
            // way to ask. Without this sample an unchanged RSS cannot be attributed to either cause.
            heapTrimmed = new GlibcHeapTrimmer().TryTrim();
            afterTrim = ProcessMemoryProbe.Read();
            TestContext.WriteLine($"after trim: rss={afterTrim.ResidentMb:F0} MB trimmed={heapTrimmed}");
        }
        catch (Exception ex)
        {
            failed = true;
            error = ex.GetType().Name + ": " + ex.Message;
            TestContext.WriteLine($"FAILED: {error}");
        }

        var sorted = durations.OrderBy(x => x).ToArray();
        var result = new ProbeResult(
            variant, SingleCaller, gate, threads, bulks,
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other",
            Environment.ProcessorCount,
            baselineSample.ResidentMb,
            afterLoad.ResidentMb,
            afterFirstBulk.ResidentMb,
            afterLastBulk.ResidentMb,
            afterGc.ResidentMb,
            afterGc.PeakResidentMb,
            Percentile(sorted, P50),
            Percentile(sorted, P95),
            sorted.Length == 0 ? 0 : sorted[^1],
            failed,
            error,
            referenceComponents,
            corpusSize,
            bulks,
            afterFirstBulk.PeakResidentMb,
            [.. durations],
            [.. bulkRss],
            [.. bulkPeak],
            hostAvailable,
            shapes.ChunkCount,
            shapes.ChunksAtCap,
            shapes.TokenMin,
            shapes.TokenMedian,
            shapes.TokenMax,
            shapes.PaddedTokenTotal,
            afterTrim.ResidentMb,
            heapTrimmed);

        var line = JsonSerializer.Serialize(result);
        File.AppendAllText(outputPath, line + Environment.NewLine);
        TestContext.WriteLine(line);

        Assert.Pass();
    }

    private sealed record CorpusShapes(
        int ChunkCount,
        int ChunksAtCap,
        int TokenMin,
        int TokenMedian,
        int TokenMax,
        int PaddedTokenTotal);

    /// <summary>
    /// Reproduces the tokenization and chunking OnnxEmbeddingProvider performs, so the report can state
    /// the sequence shapes the session actually saw. The corpus lengths are character counts while
    /// padding is by token, and EmbedBatchAsync does not length-sort its input, so each chunk's maxLen
    /// is set by whichever long passage happened to land in it.
    /// </summary>
    /// <param name="modelDirectory">Directory holding the cached tokenizer.json.</param>
    /// <param name="corpus">Passages exactly as they are handed to EmbedBatchAsync, without the prefix.</param>
    private static CorpusShapes MeasureShapes(string modelDirectory, string[] corpus)
    {
        var tokenizerPath = Path.Combine(modelDirectory, KnowledgeIndexConstants.EmbeddingTokenizerFileName);
        using var tokenizer = new Tokenizer(vocabPath: tokenizerPath);

        var lengths = corpus
            .Select(text => Math.Min(tokenizer.Encode(PassagePrefix + text).Length, MaxSequenceLength))
            .ToArray();

        var batchSize = KnowledgeIndexConstants.EmbeddingBatchSize;
        var chunkCount = 0;
        var chunksAtCap = 0;
        var paddedTokens = 0;
        for (var start = 0; start < lengths.Length; start += batchSize)
        {
            var end = Math.Min(start + batchSize, lengths.Length);
            var maxLen = lengths[start..end].Max();
            chunkCount++;
            paddedTokens += (end - start) * maxLen;
            if (maxLen >= MaxSequenceLength) chunksAtCap++;
        }

        var ordered = lengths.OrderBy(x => x).ToArray();
        return new CorpusShapes(chunkCount, chunksAtCap, ordered[0], ordered[ordered.Length / 2], ordered[^1], paddedTokens);
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

    private const string MemInfoPath = "/proc/meminfo";
    private const string AvailableKey = "MemAvailable:";
    private const double KilobytesPerMegabyte = 1024.0;

    private static double ReadHostAvailableMb()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(MemInfoPath)) return 0;

        foreach (var line in File.ReadLines(MemInfoPath))
        {
            if (!line.StartsWith(AvailableKey, StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) / KilobytesPerMegabyte;
        }

        return 0;
    }

    private static string ResolveModelDirectory()
    {
        var root = Environment.GetEnvironmentVariable(ModelsRootVariable)
            ?? Path.Combine(Path.GetTempPath(), "klacks-test-models");
        var dir = Path.Combine(root, KnowledgeIndexConstants.EmbeddingModelName);
        if (!File.Exists(Path.Combine(dir, KnowledgeIndexConstants.EmbeddingModelFileName)))
            Assert.Ignore($"Embedding model not found under {dir}. Set {ModelsRootVariable}.");
        return dir;
    }
}
