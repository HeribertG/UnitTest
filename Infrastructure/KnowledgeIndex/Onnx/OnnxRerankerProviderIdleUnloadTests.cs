// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;
using Shouldly;
using Tokenizers.DotNet;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
public class OnnxRerankerProviderIdleUnloadTests
{
    private const string Query = "Wie lege ich einen neuen Mitarbeiter an?";
    private const double Tolerance = 1e-9;
    private const int ParallelCallers = 4;
    private const int CallsPerCaller = 4;
    private const string BrokenTokenizerContent = "not json";
    private const string BrokenModelDirectoryPrefix = "klacks-broken-tokenizer-";

    private static readonly string[] Candidates =
    [
        "create_employee. Legt einen neuen Mitarbeiter an.",
        "list_contracts. Listet alle Vertraege auf.",
        "open_schedule. Oeffnet den Dienstplan.",
    ];

    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.RerankerModelName);

    private static OnnxRerankerProvider Create() => new(new ModelLoader(new HttpClient()), CacheDir);

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
    public async Task ScoreAsync_AfterUnload_ReloadsAndProducesIdenticalScores()
    {
        await using var provider = Create();

        var expected = await provider.ScoreAsync(Query, Candidates, CancellationToken.None);
        provider.IsLoaded.ShouldBeTrue();
        provider.LoadCount.ShouldBe(1);

        var unloaded = await provider.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);

        unloaded.ShouldBeTrue();
        provider.IsLoaded.ShouldBeFalse();

        var actual = await provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        provider.LoadCount.ShouldBe(2);
        for (var i = 0; i < expected.Length; i++)
        {
            actual[i].ShouldBe(expected[i], Tolerance);
        }
    }

    [Test]
    [Category("SlowModelLoad")]
    public async Task TryUnloadIfIdleAsync_ThresholdNotReached_KeepsSessionLoaded()
    {
        await using var provider = Create();

        await provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        var unloaded = await provider.TryUnloadIfIdleAsync(TimeSpan.FromHours(1), CancellationToken.None);

        unloaded.ShouldBeFalse();
        provider.IsLoaded.ShouldBeTrue();
        provider.LoadCount.ShouldBe(1);
    }

    // A half-built session is worse than none: LoadAsync creates the InferenceSession before the
    // tokenizer, and AcquireSessionAsync only rebuilds while the session field is null - so a tokenizer
    // that fails after the session succeeded would leave every later call dereferencing a null tokenizer
    // until some sweep happened to clear the session. A corrupted tokenizer.json next to a real model
    // file is the only way to reach that ordering from outside the class: the model has to load, or the
    // failure happens one line earlier and the case is never exercised.
    [Test]
    [Category("SlowModelLoad")]
    public async Task ScoreAsync_TokenizerFailsAfterTheSessionWasBuilt_LeavesNothingLoaded()
    {
        var modelSource = Path.Combine(CacheDir, KnowledgeIndexConstants.RerankerModelFileName);
        if (!File.Exists(modelSource))
        {
            Assert.Ignore($"Model not found under {CacheDir}.");
        }

        var brokenDirectory = Path.Combine(
            Path.GetTempPath(), BrokenModelDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(brokenDirectory);

        try
        {
            File.Copy(modelSource, Path.Combine(brokenDirectory, KnowledgeIndexConstants.RerankerModelFileName));
            File.WriteAllText(
                Path.Combine(brokenDirectory, KnowledgeIndexConstants.RerankerTokenizerFileName),
                BrokenTokenizerContent);

            // Empty url and hash: ModelLoader then takes both files as they are. With the production
            // hash it would notice the corrupted tokenizer and download a working one over it, quietly
            // repairing the very thing under test.
            await using var provider = new OnnxRerankerProvider(
                new ModelLoader(new HttpClient()),
                brokenDirectory,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

            // The tokenizer's own exception type rather than a bare Exception: a truncated or missing
            // model file throws OnnxRuntimeException one line earlier and would satisfy a laxer
            // assertion without the session ever having been built.
            await Should.ThrowAsync<TokenizerException>(
                () => provider.ScoreAsync(Query, Candidates, CancellationToken.None));

            provider.IsLoaded.ShouldBeFalse();
            provider.LoadCount.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(brokenDirectory, recursive: true);
        }
    }

    // The failure this guards against is not a wrong number but a native use-after-free: disposing an
    // InferenceSession while Run() is executing on it faults the process instead of throwing. The
    // LoadCount assertion is what keeps the test honest - without it the run passes just as happily
    // when the unloader never managed to unload anything at all.
    [Test]
    [Category("SlowModelLoad")]
    public async Task ScoreAsync_ParallelCallersWhileUnloaderRuns_NeverFaultsAndReloadsAtLeastOnce()
    {
        await using var provider = Create();

        var reference = await provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        using var callersDone = new CancellationTokenSource();

        var unloader = Task.Run(async () =>
        {
            while (!callersDone.IsCancellationRequested)
            {
                await provider.TryUnloadIfIdleAsync(TimeSpan.Zero, CancellationToken.None);
                await Task.Yield();
            }
        });

        var callers = Enumerable.Range(0, ParallelCallers)
            .Select(_ => Task.Run(async () =>
            {
                var results = new List<double[]>();
                for (var i = 0; i < CallsPerCaller; i++)
                {
                    results.Add(await provider.ScoreAsync(Query, Candidates, CancellationToken.None));
                }

                return results;
            }))
            .ToArray();

        var scored = await Task.WhenAll(callers);
        await callersDone.CancelAsync();
        await unloader;

        foreach (var scores in scored.SelectMany(r => r))
        {
            for (var i = 0; i < reference.Length; i++)
            {
                scores[i].ShouldBe(reference[i], Tolerance);
            }
        }

        provider.LoadCount.ShouldBeGreaterThan(1);
    }
}
