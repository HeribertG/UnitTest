// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard against hard-coded Europe/Zurich or Europe/Berlin time zone literals in
/// Klacks.Api source and configuration. Klacks is developed in Switzerland but sold internationally;
/// one configured company time zone per installation (ICompanyClock, resolved from the
/// APP_ADDRESS_TIMEZONE / APP_ADDRESS_COUNTRY settings) is the single source of truth - never a
/// hard-coded regional default baked into runtime code or shipped configuration.
///
/// Scans every *.cs file under Klacks.Api (skipping bin/obj/Migrations, where generated or historical
/// code is out of scope) and every appsettings*.json file (skipping bin/obj, where a stale build output
/// copy would otherwise produce a false positive against a file nobody edits directly). Lines starting
/// with `//` (including XML-doc `///`) are skipped, matching DateTimeKindGuardTests and
/// ForbidChallengeSchemeGuardTests - documentation examples ("e.g. Europe/Zurich") are not runtime
/// defaults.
///
/// Scope note - what this guard does NOT cover:
/// - Klacks.Api/Plugins/** contains no .cs files (language packs are JSON data, not code) and no
///   appsettings*.json, so it is scanned but contributes nothing; checked manually 2026-09-11 and
///   confirmed clean of both literals.
/// - There is no deploy/ directory in this repository (checked manually 2026-09-11); if one is added
///   later it is out of scope here (not Klacks.Api source/config).
/// - Other projects (Klacks.UnitTest, Klacks.IntegrationTest, Klacks.Ui, ...) are not scanned.
/// </summary>

using System.Text;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class HardCodedTimeZoneGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string CsFilePattern = "*.cs";
    private const string AppSettingsFilePattern = "appsettings*.json";
    private const string LineCommentPrefix = "//";
    // Actual count under Klacks.Api (excluding bin/obj/Migrations) was ~4731 .cs files on 2026-09-11;
    // the floor is set to ~88% of that so ordinary file churn does not make this test flaky while a
    // scan that silently inspected the wrong (near-empty) directory still fails loudly.
    private const int MinimumScannedCsFiles = 4200;
    private const int MinimumScannedJsonFiles = 1;

    private static readonly string[] ForbiddenPatterns = ["Europe/Zurich", "Europe/Berlin"];

    private static readonly string[] SkippedCsDirectorySegments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"
    ];

    private static readonly string[] SkippedJsonDirectorySegments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"
    ];

    private static readonly IReadOnlyDictionary<string, int> AllowedCsOccurrences = new Dictionary<string, int>
    {
        ["Application/Constants/CountryTimeZones.cs"] = 2,
        ["Infrastructure/Persistence/Seed/SeedGenerator.cs"] = 1
    };

    [Test]
    public void ApiSourceFiles_MustNotHardCodeZurichOrBerlinTimeZone()
    {
        var (occurrences, scannedFiles) = ScanCsFiles();

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedCsFiles,
            $"Only {scannedFiles} .cs files were scanned. The guard cannot have inspected the real " +
            "Klacks.Api source tree, so a green result would be meaningless.");

        var violations = occurrences
            .Where(o => !AllowedCsOccurrences.ContainsKey(o.Key))
            .ToList();

        var report = new StringBuilder();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation.Key}: {violation.Value.Count} occurrence(s) at line(s) " +
                               $"{string.Join(", ", violation.Value.Lines)}");
        }

        violations.ShouldBeEmpty(
            "Europe/Zurich and Europe/Berlin are forbidden as runtime defaults - Klacks installs are " +
            "sold internationally with exactly one configured company time zone (ICompanyClock). Use " +
            "the company's configured zone instead of a hard-coded one. If a hit is provably a country " +
            $"lookup table entry, add it to AllowedCsOccurrences with its exact count.{Environment.NewLine}{report}");
    }

    [Test]
    public void AllowedCsOccurrences_MustStillMatchTheSource()
    {
        var (occurrences, scannedFiles) = ScanCsFiles();

        scannedFiles.ShouldBeGreaterThan(MinimumScannedCsFiles);

        var stale = new StringBuilder();
        foreach (var (path, expectedCount) in AllowedCsOccurrences)
        {
            if (!occurrences.TryGetValue(path, out var actual))
            {
                stale.AppendLine($"  {path}: listed as an exception but contains no occurrence anymore.");
                continue;
            }

            if (actual.Count != expectedCount)
            {
                stale.AppendLine($"  {path}: expected {expectedCount} occurrence(s) but found {actual.Count} " +
                                  $"at line(s) {string.Join(", ", actual.Lines)}.");
            }
        }

        stale.Length.ShouldBe(
            0,
            "The exception list is stale. It must not silently decay into a permanent allowlist: remove " +
            "entries that were fixed, and review any entry whose count changed instead of raising the " +
            $"number.{Environment.NewLine}{stale}");
    }

    [Test]
    public void AppSettingsFiles_MustNotHardCodeZurichOrBerlinTimeZone()
    {
        var (occurrences, scannedFiles) = ScanAppSettingsFiles();

        scannedFiles.ShouldBeGreaterThanOrEqualTo(
            MinimumScannedJsonFiles,
            $"Only {scannedFiles} appsettings*.json files were scanned. The guard cannot have inspected " +
            "real configuration, so a green result would be meaningless.");

        var report = new StringBuilder();
        foreach (var violation in occurrences)
        {
            report.AppendLine($"  {violation.Key}: {violation.Value.Count} occurrence(s) at line(s) " +
                               $"{string.Join(", ", violation.Value.Lines)}");
        }

        occurrences.ShouldBeEmpty(
            "Europe/Zurich and Europe/Berlin must not ship as a default in appsettings*.json - the " +
            $"company time zone is configured per installation, never baked into the shipped config.{Environment.NewLine}{report}");
    }

    private static (Dictionary<string, (int Count, List<int> Lines)> Occurrences, int ScannedFiles) ScanCsFiles()
    {
        var apiRoot = LocateApiProject();
        var occurrences = new Dictionary<string, (int Count, List<int> Lines)>();
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, CsFilePattern, SearchOption.AllDirectories))
        {
            if (SkippedCsDirectorySegments.Any(segment => file.Contains(segment, StringComparison.Ordinal)))
            {
                continue;
            }

            scannedFiles++;
            var hits = ScanFileLines(file);
            if (hits.Count > 0)
            {
                occurrences[ToRelativeKey(apiRoot, file)] = (hits.Count, hits);
            }
        }

        return (occurrences, scannedFiles);
    }

    private static (Dictionary<string, (int Count, List<int> Lines)> Occurrences, int ScannedFiles) ScanAppSettingsFiles()
    {
        var apiRoot = LocateApiProject();
        var occurrences = new Dictionary<string, (int Count, List<int> Lines)>();
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, AppSettingsFilePattern, SearchOption.AllDirectories))
        {
            if (SkippedJsonDirectorySegments.Any(segment => file.Contains(segment, StringComparison.Ordinal)))
            {
                continue;
            }

            scannedFiles++;
            var hits = ScanFileLines(file, treatDoubleSlashAsComment: false);
            if (hits.Count > 0)
            {
                occurrences[ToRelativeKey(apiRoot, file)] = (hits.Count, hits);
            }
        }

        return (occurrences, scannedFiles);
    }

    private static List<int> ScanFileLines(string file, bool treatDoubleSlashAsComment = true)
    {
        var lines = File.ReadAllLines(file);
        var hits = new List<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (treatDoubleSlashAsComment && line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (ForbiddenPatterns.Any(pattern => line.Contains(pattern, StringComparison.Ordinal)))
            {
                hits.Add(i + 1);
            }
        }

        return hits;
    }

    private static string ToRelativeKey(string apiRoot, string file)
    {
        return Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, "Domain", "Services")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
