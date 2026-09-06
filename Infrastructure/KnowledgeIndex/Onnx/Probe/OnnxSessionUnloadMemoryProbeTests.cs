// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Measures how much resident memory an idle unload actually returns to the process: build a session,
/// run realistic inference for a few rounds, unload, read RSS before and after. The number decides
/// whether Dispose alone is enough or whether malloc_trim has to be evaluated (design rule: below ~60%
/// of the session size the allocator is keeping the pages). One provider per process, selected via
/// PROBE_SESSION = reranker | embedding; results are appended as a JSON line like the other probes.
/// </summary>
[TestFixture]
[Explicit("Memory probe: loads a real ONNX session, unloads it and reports the RSS delta. Run via dotnet vstest, one provider per process.")]
[Category("MemoryProbe")]
public class OnnxSessionUnloadMemoryProbeTests
{
    private const string SessionVariable = "PROBE_SESSION";
    private const string RoundsVariable = "PROBE_ROUNDS";
    private const string OutputVariable = "PROBE_OUT";
    private const string ModelsRootVariable = "PROBE_MODELS_ROOT";

    private const string RerankerSession = "reranker";
    private const string EmbeddingSession = "embedding";
    private const int DefaultRounds = 5;
    private const int ReloadCycles = 3;
    private const string DefaultOutputFileName = "onnx-unload-probe.jsonl";

    private sealed record UnloadProbeResult(
        string Session,
        string Platform,
        int Rounds,
        int ReloadCycles,
        double RssBaselineMb,
        double RssLoadedMb,
        double RssAfterUnloadMb,
        double FreedMb,
        double FreedPercentOfSession,
        bool MallocTrimReleased,
        double RssAfterTrimMb,
        double FreedWithTrimPercentOfSession,
        double RssAfterReloadCyclesMb,
        double PeakRssMb,
        int LoadCount,
        bool Failed,
        string? Error);

    [Test]
    public async Task UnloadIdleSession_ReportsResidentMemoryDelta()
    {
        var sessionKind = Environment.GetEnvironmentVariable(SessionVariable) ?? RerankerSession;
        var rounds = ReadInt(RoundsVariable, DefaultRounds);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable)
            ?? Path.Combine(Path.GetTempPath(), DefaultOutputFileName);
        var modelsRoot = Environment.GetEnvironmentVariable(ModelsRootVariable)
            ?? Path.Combine(Path.GetTempPath(), "klacks-test-models");

        var baseline = ProcessMemoryProbe.Read();
        double loaded = 0, afterUnload = 0, afterTrim = 0, afterCycles = 0;
        var trimmed = false;
        var loadCount = 0;
        var failed = false;
        string? error = null;

        try
        {
            await using var provider = CreateProvider(sessionKind, modelsRoot, out var run);
            var unloadable = (IUnloadableInferenceSession)provider;

            for (var round = 0; round < rounds; round++)
            {
                await run(round);
            }

            loaded = ProcessMemoryProbe.Read().ResidentMb;

            var unloaded = await unloadable.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);
            if (!unloaded)
            {
                throw new InvalidOperationException("TryUnloadIfIdleAsync returned false on an idle session.");
            }

            afterUnload = ProcessMemoryProbe.Read().ResidentMb;
            TestContext.WriteLine($"{sessionKind}: loaded={loaded:F0} MB unloaded={afterUnload:F0} MB freed={loaded - afterUnload:F0} MB");

            // Design rule: below ~60% of the session size the allocator is keeping the pages, and
            // malloc_trim is the candidate remedy. Measured here rather than assumed.
            trimmed = new GlibcHeapTrimmer().TryTrim();
            afterTrim = ProcessMemoryProbe.Read().ResidentMb;
            TestContext.WriteLine($"{sessionKind}: malloc_trim released={trimmed} rss after trim={afterTrim:F0} MB (further {afterUnload - afterTrim:F0} MB)");

            // Reload/unload cycles: a rising RSS here means each cycle leaks something the dispose
            // does not return, which would turn thrashing into a slow leak in production.
            for (var cycle = 0; cycle < ReloadCycles; cycle++)
            {
                await run(cycle);
                await unloadable.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);
                var sample = ProcessMemoryProbe.Read();
                TestContext.WriteLine($"cycle {cycle + 1}: rss after unload={sample.ResidentMb:F0} MB peak={sample.PeakResidentMb:F0} MB");
            }

            afterCycles = ProcessMemoryProbe.Read().ResidentMb;
            loadCount = unloadable.LoadCount;
        }
        catch (Exception ex)
        {
            failed = true;
            error = ex.GetType().Name + ": " + ex.Message;
            TestContext.WriteLine($"FAILED: {error}");
        }

        var sessionSize = loaded - baseline.ResidentMb;
        var freed = loaded - afterUnload;
        var result = new UnloadProbeResult(
            sessionKind,
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other",
            rounds,
            ReloadCycles,
            baseline.ResidentMb,
            loaded,
            afterUnload,
            freed,
            sessionSize > 0 ? 100.0 * freed / sessionSize : 0,
            trimmed,
            afterTrim,
            sessionSize > 0 ? 100.0 * (loaded - afterTrim) / sessionSize : 0,
            afterCycles,
            ProcessMemoryProbe.Read().PeakResidentMb,
            loadCount,
            failed,
            error);

        var line = System.Text.Json.JsonSerializer.Serialize(result);
        File.AppendAllText(outputPath, line + Environment.NewLine);
        TestContext.WriteLine(line);

        Assert.Pass();
    }

    private static IAsyncDisposable CreateProvider(string sessionKind, string modelsRoot, out Func<int, Task> run)
    {
        var loader = new ModelLoader(new HttpClient());
        switch (sessionKind)
        {
            case RerankerSession:
            {
                var dir = RequireModel(modelsRoot, KnowledgeIndexConstants.RerankerModelName, KnowledgeIndexConstants.RerankerModelFileName);
                var provider = new OnnxRerankerProvider(loader, dir, string.Empty, string.Empty, string.Empty, string.Empty);
                run = round => provider.ScoreAsync(RerankerProbeCandidates.Query, RerankerProbeCandidates.Build(round), CancellationToken.None);
                return provider;
            }

            case EmbeddingSession:
            {
                var dir = RequireModel(modelsRoot, KnowledgeIndexConstants.EmbeddingModelName, KnowledgeIndexConstants.EmbeddingModelFileName);
                var provider = new OnnxEmbeddingProvider(loader, dir);
                run = async round =>
                {
                    for (var index = 0; index < EmbeddingProbeQuestions.Count; index++)
                    {
                        await provider.EmbedQueryAsync(EmbeddingProbeQuestions.Get(round, index), CancellationToken.None);
                    }
                };
                return provider;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(sessionKind), sessionKind, "Unknown probe session");
        }
    }

    private static string RequireModel(string modelsRoot, string modelName, string modelFileName)
    {
        var dir = Path.Combine(modelsRoot, modelName);
        if (!File.Exists(Path.Combine(dir, modelFileName)))
        {
            Assert.Ignore($"Model not found under {dir}. Set {ModelsRootVariable}.");
        }

        return dir;
    }

    private static int ReadInt(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) ? value : fallback;
    }
}
