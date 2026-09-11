// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard against DateTime.Now / DateTime.Today in the database-facing layers of
/// Klacks.Api. Those factories yield DateTimeKind.Local, and 414 of the 415 timestamp columns in the
/// schema are "timestamp with time zone": once the Npgsql switch EnableLegacyTimestampBehavior was
/// removed, a non-UTC DateTime reaching a query parameter throws ArgumentException, and a local value
/// used in a calendar comparison is off by the UTC offset (a whole day around midnight).
///
/// Scope note — what this guard does NOT cover:
/// - new DateTime(...) and DateOnly.ToDateTime(...) without an explicit DateTimeKind, which produce
///   Kind=Unspecified and fail the same way.
/// - the raw-SQL seed path, where values are string-interpolated instead of bound as parameters, so
///   no ArgumentException can occur and only the missing UTC offset in the literal shifts the value.
/// The complementary check (asserting Kind==Utc on tracked entities in OnBeforeSaving) is deliberately
/// not built here; this guard is a source scan, not a runtime assertion.
/// </summary>

using System.Text;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class DateTimeKindGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const int MinimumScannedFiles = 400;

    private static readonly string[] ForbiddenPatterns = ["DateTime.Now", "DateTime.Today"];

    private static readonly string[] GuardedDirectories =
    [
        "Infrastructure/Repositories",
        "Infrastructure/Services",
        "Infrastructure/Persistence/Seed",
        "Domain/Services",
        "Application"
    ];

    private static readonly IReadOnlyDictionary<string, (int Count, string Reason)> AllowedOccurrences =
        new Dictionary<string, (int, string)>
        {
            ["Infrastructure/Persistence/Seed/FakeDataSeed.cs"] =
                (1, "Only the .Year int is read and passed as a seed generation parameter; no DateTime " +
                    "value reaches a column.")
        };

    [Test]
    public void DatabaseFacingLayers_MustNotUseLocalDateTimeFactories()
    {
        var (occurrences, scannedFiles) = ScanGuardedDirectories();

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedFiles,
            $"Only {scannedFiles} source files were scanned. The guard cannot have inspected the real " +
            "source tree, so a green result would be meaningless.");

        var violations = occurrences
            .Where(o => !AllowedOccurrences.ContainsKey(o.Key))
            .ToList();

        var report = new StringBuilder();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation.Key}: {violation.Value.Count} occurrence(s) " +
                              $"at line(s) {string.Join(", ", violation.Value.Lines)}");
        }

        violations.ShouldBeEmpty(
            "DateTime.Now/DateTime.Today are forbidden in the database-facing layers because every " +
            "timestamp column is 'timestamp with time zone'. Use DateTime.UtcNow, or ICompanyClock " +
            "when the company's calendar date is meant. If a hit is provably harmless, add it to " +
            $"AllowedOccurrences with a reason.{Environment.NewLine}{report}");
    }

    [Test]
    public void AllowedOccurrences_MustStillMatchTheSource()
    {
        var (occurrences, scannedFiles) = ScanGuardedDirectories();

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles);

        var stale = new StringBuilder();
        foreach (var (path, (expectedCount, _)) in AllowedOccurrences)
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

    private static (Dictionary<string, (int Count, List<int> Lines)> Occurrences, int ScannedFiles) ScanGuardedDirectories()
    {
        var apiRoot = LocateApiProject();
        var occurrences = new Dictionary<string, (int Count, List<int> Lines)>();
        var scannedFiles = 0;

        foreach (var guardedDirectory in GuardedDirectories)
        {
            var absoluteDirectory = Path.Combine(apiRoot, guardedDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absoluteDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Guarded directory '{guardedDirectory}' does not exist under '{apiRoot}'. " +
                    "The guard would silently pass, so this is treated as a failure.");
            }

            foreach (var file in Directory.EnumerateFiles(absoluteDirectory, SourceFilePattern, SearchOption.AllDirectories))
            {
                scannedFiles++;

                var lines = File.ReadAllLines(file);
                var hits = new List<int>();

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ForbiddenPatterns.Any(pattern => line.Contains(pattern, StringComparison.Ordinal)))
                    {
                        hits.Add(i + 1);
                    }
                }

                if (hits.Count > 0)
                {
                    occurrences[ToRelativeKey(apiRoot, file)] = (hits.Count, hits);
                }
            }
        }

        return (occurrences, scannedFiles);
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
