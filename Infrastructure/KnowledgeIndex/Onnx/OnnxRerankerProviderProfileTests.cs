// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
[Category("SlowModelLoad")]
public class OnnxRerankerProviderProfileTests
{
    private const string Query = "Wie lege ich einen neuen Mitarbeiter an?";
    private const double Tolerance = 1e-6;
    private const int ParallelCallers = 4;

    private static readonly string[] Candidates =
    [
        "create_employee. Legt einen neuen Mitarbeiter an.",
        "list_contracts. Listet alle Vertraege auf.",
        "open_schedule. Oeffnet den Dienstplan.",
        "delete_group. Loescht eine Gruppe.",
        "add_expense. Erfasst eine Spesenposition.",
    ];

    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.RerankerModelName);

    private static OnnxRerankerProvider Create(OnnxRerankerRuntimeProfile profile) =>
        new(new ModelLoader(new HttpClient()), CacheDir, profile: profile);

    [Test]
    public async Task ScoreAsync_ShrinkProfile_ProducesSameScoresAsDefault()
    {
        await using var reference = Create(OnnxRerankerRuntimeProfile.Default);
        await using var shrinking = Create(OnnxRerankerRuntimeProfile.Default with { ShrinkArenaAfterRun = true });

        var expected = await reference.ScoreAsync(Query, Candidates, CancellationToken.None);
        var actual = await shrinking.ScoreAsync(Query, Candidates, CancellationToken.None);

        for (var i = 0; i < expected.Length; i++)
        {
            actual[i].ShouldBe(expected[i], Tolerance);
        }
    }

    [Test]
    public async Task ScoreAsync_GatedToOne_ParallelCallersGetSequentialScores()
    {
        await using var provider = Create(OnnxRerankerRuntimeProfile.Default with { MaxConcurrentRuns = 1 });

        var sequential = await provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        var parallel = await Task.WhenAll(Enumerable.Range(0, ParallelCallers)
            .Select(_ => provider.ScoreAsync(Query, Candidates, CancellationToken.None)));

        foreach (var scores in parallel)
        {
            for (var i = 0; i < sequential.Length; i++)
            {
                scores[i].ShouldBe(sequential[i], Tolerance);
            }
        }
    }

    // The arena and memory-pattern switches only change where activations live, never what is
    // computed. Graph optimization level and thread count are deliberately kept identical here:
    // ORT_ENABLE_BASIC + one thread (CreateMemoryFrugal) moves scores by up to ~0.01 on this int8
    // model, so those two are not score-neutral and must not be swapped without a golden-set run.
    [Test]
    public async Task ScoreAsync_ArenaDisabled_ProducesSameScoresAsDefault()
    {
        await using var reference = Create(OnnxRerankerRuntimeProfile.Default);
        await using var arenaDisabled = Create(OnnxRerankerRuntimeProfile.Default with
        {
            CreateSessionOptions = () =>
            {
                var options = OnnxSessionOptionsFactory.CreateThroughput();
                options.EnableCpuMemArena = false;
                options.EnableMemoryPattern = false;
                return options;
            },
        });

        var expected = await reference.ScoreAsync(Query, Candidates, CancellationToken.None);
        var actual = await arenaDisabled.ScoreAsync(Query, Candidates, CancellationToken.None);

        var maxDelta = expected.Zip(actual, (e, a) => Math.Abs(e - a)).Max();
        maxDelta.ShouldBeLessThanOrEqualTo(
            Tolerance,
            $"default={string.Join(", ", expected.Select(s => s.ToString("F6")))} arenaOff={string.Join(", ", actual.Select(s => s.ToString("F6")))}");
    }
}
