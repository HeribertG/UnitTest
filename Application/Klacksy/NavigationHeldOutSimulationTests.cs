// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using System.Diagnostics;
using System.Text.Json;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

/// <summary>
/// Reproducible simulation against the REAL NavigationTargetMatcher, NavigationTargetCacheService and
/// UtteranceNormalizer. Two modes:
/// <para>
/// Leave-one-out (default, NAV_PROBE_MANIFEST_PATH unset): for every (active target, synonym) of the
/// selected locale in the DATA manifest, the phrase is removed from the synonym set (all other
/// targets/locales unchanged), the real cache is re-warmed, and the held-out phrase is matched as if
/// it were a fresh user utterance. This is the acceptance metric for synonym work.
/// </para>
/// <para>
/// Fixed probe (NAV_PROBE_MANIFEST_PATH set): the utterances come from the PROBE manifest's synonyms
/// instead of the DATA manifest's, so the same fixed phrase set can be re-matched against a DIFFERENT
/// synonym data set — the comparison leave-one-out alone cannot make, because leave-one-out draws its
/// phrases from whichever manifest is under test. For each probe (target, phrase): if the target is
/// obsolete or missing in the data manifest it is classified "target gone" and never matched; otherwise
/// if the data manifest still contains that exact (target, locale, phrase) row it is held out exactly
/// like leave-one-out (so DATA identical to PROBE reproduces leave-one-out's numbers exactly); otherwise
/// it is matched straight against the unmodified data (the phrase simply is not there to remove).
/// </para>
/// <para>
/// [Explicit] because a full run rebuilds the cache once per phrase (thousands of phrases per locale)
/// and is meant to be run manually, not on every build. Select via:
/// <c>dotnet test --no-build --filter "FullyQualifiedName~NavigationHeldOutSimulationTests"</c>
/// with environment variables NAV_MANIFEST_PATH (absolute path, default: the built
/// Application/Skills/Definitions/navigation-targets.json next to the test assembly), NAV_LOCALE
/// (default "de") and optionally NAV_PROBE_MANIFEST_PATH (absolute path; switches to fixed-probe mode)
/// set before invoking dotnet test. Optional NAV_REPORT_PATH (absolute file path) additionally writes
/// the same table as UTF-8 text, because TestContext.Out's captured console output can mangle
/// non-ASCII characters (German umlauts) depending on the runner's code page.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Long-running simulation over the full navigation-targets.json manifest; run manually with NAV_MANIFEST_PATH/NAV_LOCALE/NAV_PROBE_MANIFEST_PATH env vars set.")]
public class NavigationHeldOutSimulationTests
{
    private const string ManifestEnvVar = "NAV_MANIFEST_PATH";
    private const string LocaleEnvVar = "NAV_LOCALE";
    private const string ProbeManifestEnvVar = "NAV_PROBE_MANIFEST_PATH";
    private const string ReportPathEnvVar = "NAV_REPORT_PATH";
    private const string DefaultLocale = "de";
    private const string SynonymSource = "held-out-simulation";
    private const int TopExamplesCount = 15;

    [Test]
    public async Task HeldOutSimulation_PrintsResultsTable()
    {
        var manifestPath = Environment.GetEnvironmentVariable(ManifestEnvVar)
            ?? Path.Combine(AppContext.BaseDirectory, "Application", "Skills", "Definitions", "navigation-targets.json");
        var locale = Environment.GetEnvironmentVariable(LocaleEnvVar) ?? DefaultLocale;
        var probeManifestPath = Environment.GetEnvironmentVariable(ProbeManifestEnvVar);
        var probeMode = !string.IsNullOrWhiteSpace(probeManifestPath);

        Assert.That(File.Exists(manifestPath), Is.True, $"Data manifest not found: {manifestPath}");
        if (probeMode)
            Assert.That(File.Exists(probeManifestPath!), Is.True, $"Probe manifest not found: {probeManifestPath}");

        var manifestMTimeUtc = File.GetLastWriteTimeUtc(manifestPath);
        var probeManifestMTimeUtc = probeMode ? File.GetLastWriteTimeUtc(probeManifestPath!) : (DateTime?)null;

        var dataTargets = LoadManifest(manifestPath);
        var activeDataTargets = dataTargets.Where(t => !t.Obsolete).ToList();
        var activeDataTargetIds = activeDataTargets.Select(t => t.TargetId).ToHashSet();

        var master = BuildMasterSynonyms(dataTargets);
        var allPermissions = activeDataTargets
            .Where(t => !string.IsNullOrEmpty(t.RequiredPermission))
            .Select(t => t.RequiredPermission!)
            .Distinct()
            .ToArray();

        var synonymRepo = Substitute.For<INavigationTargetSynonymRepository>();
        synonymRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<NavigationTargetSynonym>>(master));

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(INavigationTargetSynonymRepository)).Returns(synonymRepo);
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var cache = new NavigationTargetCacheService(manifestPath, scopeFactory);
        var matcher = new NavigationTargetMatcher(cache);
        var normalizer = new UtteranceNormalizer();

        var cases = probeMode
            ? BuildCases(LoadManifest(probeManifestPath!).Where(t => !t.Obsolete).ToList(), locale)
            : BuildCases(activeDataTargets, locale);

        var correct = 0;
        var wrong = 0;
        var noMatch = 0;
        var targetGone = 0;
        var triviallyPresent = 0;
        var fastPathCorrect = 0;
        var fastPathWrong = 0;
        var fastPathWrongExamples = new List<(string Phrase, string Normalized, string Intended, string Jumped)>();

        await cache.WarmUpAsync();

        var stopwatch = Stopwatch.StartNew();
        foreach (var (targetId, phrase) in cases)
        {
            if (probeMode && !activeDataTargetIds.Contains(targetId))
            {
                targetGone++;
                continue;
            }

            var normalized = normalizer.Normalize(phrase, locale).Normalized;
            var existsInData = master.Any(s => s.TargetId == targetId && s.Language == locale && s.Keyword == phrase);

            NavigationMatchResult result;
            if (!probeMode || existsInData)
            {
                if (probeMode)
                    triviallyPresent++;

                var removed = master
                    .Where(s => s.TargetId == targetId && s.Language == locale && s.Keyword == phrase)
                    .ToList();
                master.RemoveAll(s => s.TargetId == targetId && s.Language == locale && s.Keyword == phrase);

                await cache.WarmUpAsync();
                result = matcher.Match(normalized, locale, allPermissions);

                master.AddRange(removed);
                if (probeMode)
                    await cache.WarmUpAsync(); // resync the cache with the now-restored full master before the next, possibly non-removing, case
            }
            else
            {
                // Phrase is not in the data manifest for this target at all (nothing to hold out) —
                // match straight against the unmodified data, which the cache already reflects.
                result = matcher.Match(normalized, locale, allPermissions);
            }

            if (result.TargetId is null)
            {
                noMatch++;
            }
            else if (result.TargetId == targetId)
            {
                correct++;
                if (result.IsFastPath)
                    fastPathCorrect++;
            }
            else
            {
                wrong++;
                if (result.IsFastPath)
                {
                    fastPathWrong++;
                    if (fastPathWrongExamples.Count < TopExamplesCount)
                        fastPathWrongExamples.Add((phrase, normalized, targetId, result.TargetId));
                }
            }
        }

        stopwatch.Stop();

        var report = BuildReport(
            manifestPath, manifestMTimeUtc, locale, probeManifestPath, probeManifestMTimeUtc,
            cases.Count, correct, wrong, noMatch, targetGone, triviallyPresent,
            fastPathCorrect, fastPathWrong, fastPathWrongExamples, stopwatch.Elapsed);
        TestContext.Out.WriteLine(report);

        var reportPath = Environment.GetEnvironmentVariable(ReportPathEnvVar);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            // TestContext.Out can mangle non-ASCII phrases depending on the console code page; this
            // optional UTF-8 file keeps German umlauts etc. intact for later inspection.
            File.WriteAllText(reportPath, report, System.Text.Encoding.UTF8);
        }

        Assert.That(cases.Count, Is.GreaterThan(0), "No phrases were generated for this locale/manifest — check NAV_LOCALE and the manifest content.");
    }

    private static List<(string TargetId, string Phrase)> BuildCases(List<NavigationTarget> activeTargets, string locale)
    {
        var cases = new List<(string TargetId, string Phrase)>();
        foreach (var target in activeTargets)
        {
            if (!target.Synonyms.TryGetValue(locale, out var phrases))
                continue;

            foreach (var phrase in phrases.Distinct())
                cases.Add((target.TargetId, phrase));
        }

        return cases;
    }

    private static List<NavigationTarget> LoadManifest(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<NavigationTarget>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static List<NavigationTargetSynonym> BuildMasterSynonyms(List<NavigationTarget> targets)
    {
        var master = new List<NavigationTargetSynonym>();
        foreach (var target in targets)
        {
            foreach (var (language, keywords) in target.Synonyms)
            {
                foreach (var keyword in keywords)
                {
                    master.Add(new NavigationTargetSynonym
                    {
                        TargetId = target.TargetId,
                        Language = language,
                        Keyword = keyword,
                        Source = SynonymSource,
                    });
                }
            }
        }

        return master;
    }

    private static string BuildReport(
        string manifestPath,
        DateTime manifestMTimeUtc,
        string locale,
        string? probeManifestPath,
        DateTime? probeManifestMTimeUtc,
        int total,
        int correct,
        int wrong,
        int noMatch,
        int targetGone,
        int triviallyPresent,
        int fastPathCorrect,
        int fastPathWrong,
        IReadOnlyList<(string Phrase, string Normalized, string Intended, string Jumped)> fastPathWrongExamples,
        TimeSpan elapsed)
    {
        double Pct(int n) => total == 0 ? 0 : 100.0 * n / total;
        var msPerPhrase = total == 0 ? 0 : elapsed.TotalMilliseconds / total;
        var probeMode = probeManifestPath is not null;
        var nonGoneTotal = total - targetGone;

        var lines = new List<string>
        {
            string.Empty,
            $"Navigation simulation -- mode={(probeMode ? "FIXED PROBE" : "LEAVE-ONE-OUT")}, locale={locale}",
            $"Data manifest: {manifestPath}",
            $"Data manifest mtime (UTC): {manifestMTimeUtc:O}",
        };

        if (probeMode)
        {
            lines.Add($"Probe manifest: {probeManifestPath}");
            lines.Add($"Probe manifest mtime (UTC): {probeManifestMTimeUtc:O}");
        }

        lines.Add($"Phrases evaluated: {total}, runtime: {elapsed.TotalSeconds:F1}s ({msPerPhrase:F1} ms/phrase)");
        lines.Add(string.Empty);
        lines.Add("Class          Count    Pct");
        lines.Add($"correct       {correct,6}  {Pct(correct),5:F1}%");
        lines.Add($"wrong         {wrong,6}  {Pct(wrong),5:F1}%");
        lines.Add($"no match      {noMatch,6}  {Pct(noMatch),5:F1}%");
        if (probeMode)
            lines.Add($"target gone   {targetGone,6}  {Pct(targetGone),5:F1}%  (obsolete/missing in data manifest, not matched)");
        lines.Add($"(sum)         {correct + wrong + noMatch + targetGone,6}  {Pct(correct + wrong + noMatch + targetGone),5:F1}%");

        if (probeMode)
        {
            lines.Add(string.Empty);
            lines.Add($"Trivially present in data (exact phrase still there for the same target -> held out like leave-one-out): {triviallyPresent} / {nonGoneTotal} non-gone probe cases ({(nonGoneTotal == 0 ? 0 : 100.0 * triviallyPresent / nonGoneTotal):F1}%)");
            lines.Add("Sanity check: if this equals 100% (data manifest == probe manifest for this locale), the correct/wrong/no-match/fast-path counts above must equal a plain leave-one-out run on that same manifest.");
        }

        lines.Add(string.Empty);
        lines.Add("Fast-path drill-down (subset of correct/wrong above, NOT additive to the sum):");
        lines.Add($"fast-path correct {fastPathCorrect,6}  {Pct(fastPathCorrect),5:F1}%");
        lines.Add($"fast-path wrong   {fastPathWrong,6}  {Pct(fastPathWrong),5:F1}%  <-- dangerous class (silent wrong navigation)");
        lines.Add(string.Empty);
        lines.Add($"Top {fastPathWrongExamples.Count} fast-path-wrong examples (raw phrase | normalized phrase fed to the matcher | intended target | jumped target):");

        foreach (var example in fastPathWrongExamples)
            lines.Add($"  \"{example.Phrase}\" | \"{example.Normalized}\" | {example.Intended} | {example.Jumped}");

        return string.Join(Environment.NewLine, lines);
    }
}
