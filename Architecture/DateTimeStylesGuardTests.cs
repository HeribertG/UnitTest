// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard against DateTime.Parse/TryParse and DateTimeOffset.Parse/TryParse calls that do
/// not pass a DateTimeStyles argument - or pass only DateTimeStyles.None, which is not an explicit style
/// in the sense this guard requires (it still yields Kind=Unspecified/Local depending on the input) - in
/// Klacks.Api/Application and Klacks.Api/Domain/Services. Every timestamp column is 'timestamp with time
/// zone': a value parsed without a real style either keeps a non-Utc Kind (rejected outright by Npgsql
/// once it reaches a query parameter) or, for a calendar-boundary value such as a birthdate/validFrom,
/// silently drifts by the caller's local UTC offset. This is a statement-level scan (walks from the
/// call's opening paren to its matching closing paren, not line-by-line) because several correct call
/// sites split the DateTimeStyles argument onto its own line.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class DateTimeStylesGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const string DateTimeStylesToken = "DateTimeStyles";
    private const string DateTimeStylesNoneToken = "DateTimeStyles.None";
    private const int MinimumScannedFiles = 2000;

    /// <summary>
    /// A fixed sample fed directly to <see cref="FindViolationLines"/> (not the real source tree) so the
    /// scanner's correctness does not depend on how many real call sites happen to exist right now - a
    /// prior version of this guard asserted a specific minimum count against the live scan, which made
    /// the test brittle to unrelated code changes instead of anti-vacuous. Line 1 passes a proper
    /// DateTimeStyles argument and must NOT be flagged; line 2 passes none and MUST be flagged.
    /// </summary>
    private const string ScannerSelfTestSample =
        "var a = DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value);\n"
        + "var b = DateTime.TryParse(s, out var other);\n";

    private static readonly Regex ForbiddenCallPattern =
        new(@"\b(DateTime|DateTimeOffset)\.(Parse|TryParse)\(", RegexOptions.Compiled);

    private static readonly string[] GuardedDirectories = ["Application", "Domain/Services"];

    private static readonly IReadOnlyDictionary<string, (int Count, string Reason)> AllowedOccurrences =
        new Dictionary<string, (int, string)>
        {
            ["Application/Constants/MyVersion.cs"] =
                (1, "Only formats the build timestamp for display in the version string - never reaches " +
                    "a database column, so the parsed value's Kind is irrelevant to a plain ToString call."),
            ["Domain/Services/Assistant/Skills/SkillParameterTypeValidator.cs"] =
                (1, "Only the boolean TryParse result is used (out _ discards the parsed value) to check " +
                    "whether an LLM-supplied skill parameter LOOKS LIKE a date before dispatch - the value " +
                    "itself never reaches a database column or any caller, so its Kind is irrelevant.")
        };

    /// <summary>
    /// Real multi-line call sites that DO pass DateTimeStyles (split onto their own line, which is why a
    /// naive line-by-line scan would misflag them). Asserted to be both scanned (anti-vacuous: the guard
    /// must actually inspect these files) and NOT flagged (proves the statement-level scan, not a
    /// coincidence of the allowlist).
    /// </summary>
    private static readonly string[] MultiLineCorrectSites =
    [
        "Application/Skills/GetErpImportStatusSkill.cs",
        "Application/Services/Assistant/SlackOwnerBridgeService.cs",
        "Application/Services/Imports/ErpOrderImportRunner.cs"
    ];

    [Test]
    public void DateTimeParseCalls_MustPassDateTimeStyles()
    {
        var (occurrences, scannedFiles, _, _) = ScanGuardedDirectories();

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
            "DateTime.Parse/TryParse and DateTimeOffset.Parse/TryParse must pass an explicit " +
            "DateTimeStyles argument (e.g. AssumeUniversal | AdjustToUniversal for a true instant, or " +
            "use SkillUtcDateTimeParser for a calendar-boundary value). If a hit is provably harmless, " +
            $"add it to AllowedOccurrences with a reason.{Environment.NewLine}{report}");
    }

    [Test]
    public void AllowedOccurrences_MustStillMatchTheSource()
    {
        var (occurrences, scannedFiles, _, _) = ScanGuardedDirectories();

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

    [Test]
    public void MultiLineCorrectSites_AreScannedAndNeverFlagged()
    {
        var (occurrences, _, _, scannedRelativePaths) = ScanGuardedDirectories();

        var missing = MultiLineCorrectSites.Where(path => !scannedRelativePaths.Contains(path)).ToList();
        missing.ShouldBeEmpty(
            "These known multi-line DateTimeStyles call sites were not found under the scanned " +
            $"directories at all - the anti-vacuous check itself is broken.{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing));

        var flagged = MultiLineCorrectSites.Where(occurrences.ContainsKey).ToList();
        flagged.ShouldBeEmpty(
            "These call sites split DateTimeStyles onto its own line and must be recognised by the " +
            $"statement-level scan, not misflagged by a line-by-line one.{Environment.NewLine}" +
            string.Join(Environment.NewLine, flagged));
    }

    [Test]
    public void ForbiddenCallPattern_ActuallyMatchesRealCallSites()
    {
        var (_, scannedFiles, totalMatches, _) = ScanGuardedDirectories();

        scannedFiles.ShouldBeGreaterThan(MinimumScannedFiles);
        totalMatches.ShouldBeGreaterThan(
            0,
            "The DateTime/DateTimeOffset Parse/TryParse pattern matched no call sites at all in the real " +
            "source tree - a broken regex could produce a vacuous green guard. The scanner's actual " +
            "detection logic is proven independently by ScannerSelfTest_DetectsStyledCallAsCorrectAndUnstyledCallAsViolation.");
    }

    [Test]
    public void ScannerSelfTest_DetectsStyledCallAsCorrectAndUnstyledCallAsViolation()
    {
        var (violationLines, matchCount) = FindViolationLines(ScannerSelfTestSample);

        matchCount.ShouldBe(
            2,
            "The regex must match both call sites in the fixed sample - a broken regex could produce a " +
            "vacuous green guard.");
        violationLines.ShouldBe(
            new List<int> { 2 },
            "Only the unstyled call on line 2 must be flagged; the styled call on line 1 passes " +
            "DateTimeStyles and must not be.");
    }

    [Test]
    public void ScannerSelfTest_FlagsDateTimeStylesNone_EvenThoughTheTokenTechnicallyAppears()
    {
        const string sample = "var a = DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value);\n";

        var (violationLines, matchCount) = FindViolationLines(sample);

        matchCount.ShouldBe(1);
        violationLines.ShouldBe(
            new List<int> { 1 },
            "DateTimeStyles.None yields Kind=Unspecified/Local depending on the input - exactly the " +
            "hazard this guard exists to catch. A naive substring check for the token \"DateTimeStyles\" " +
            "would wrongly treat this call as styled and correct.");
    }

    [Test]
    public void ScannerSelfTest_DoesNotFlagDateTimeStylesNoneCombinedWithAnotherFlag()
    {
        const string sample =
            "var a = DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None | DateTimeStyles.AssumeUniversal, out var value);\n";

        var (violationLines, matchCount) = FindViolationLines(sample);

        matchCount.ShouldBe(1);
        violationLines.ShouldBeEmpty(
            "DateTimeStyles.None is the integer value 0, so combined with another flag via '|' it " +
            "contributes nothing and the call is styled by AssumeUniversal alone - a substring check for " +
            "\"DateTimeStyles.None\" would wrongly flag this as None-only and correct callers would have " +
            "to work around a guard bug.");
    }

    private static (
        Dictionary<string, (int Count, List<int> Lines)> Occurrences,
        int ScannedFiles,
        int TotalMatches,
        HashSet<string> ScannedRelativePaths) ScanGuardedDirectories()
    {
        var apiRoot = LocateApiProject();
        var occurrences = new Dictionary<string, (int Count, List<int> Lines)>();
        var scannedFiles = 0;
        var totalMatches = 0;
        var scannedRelativePaths = new HashSet<string>(StringComparer.Ordinal);

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
                var relativeKey = ToRelativeKey(apiRoot, file);
                scannedRelativePaths.Add(relativeKey);

                var text = File.ReadAllText(file);
                var (hits, matchCount) = FindViolationLines(text);
                totalMatches += matchCount;

                if (hits.Count > 0)
                {
                    occurrences[relativeKey] = (hits.Count, hits);
                }
            }
        }

        return (occurrences, scannedFiles, totalMatches, scannedRelativePaths);
    }

    private static (List<int> ViolationLines, int MatchCount) FindViolationLines(string text)
    {
        var violations = new List<int>();
        var matchCount = 0;

        foreach (Match match in ForbiddenCallPattern.Matches(text))
        {
            if (IsOnACommentLine(text, match.Index))
            {
                continue;
            }

            matchCount++;

            var openParenIndex = match.Index + match.Length - 1;
            var closeParenIndex = FindMatchingCloseParen(text, openParenIndex);
            var span = closeParenIndex >= 0
                ? text.Substring(openParenIndex, closeParenIndex - openParenIndex + 1)
                : text[openParenIndex..];

            var hasAnExplicitStyle = span.Contains(DateTimeStylesToken, StringComparison.Ordinal);
            var isNoneOnly = IsNoneOnlyStyleArgument(span);
            if (!hasAnExplicitStyle || isNoneOnly)
            {
                violations.Add(CountLineNumber(text, match.Index));
            }
        }

        return (violations, matchCount);
    }

    /// <summary>
    /// True only when the single call argument that carries "DateTimeStyles" is EXACTLY
    /// <c>DateTimeStyles.None</c> with nothing combined into it via '|'. DateTimeStyles.None is the
    /// integer value 0, so a plain substring search for the token cannot tell "None alone" apart from
    /// "None combined with a real flag" (e.g. <c>None | AssumeUniversal</c>, which is a fully explicit,
    /// correct style) - only comparing the whole argument text catches that distinction.
    /// </summary>
    private static bool IsNoneOnlyStyleArgument(string span)
    {
        var argsText = span.Length >= 2 && span[0] == '(' && span[^1] == ')' ? span[1..^1] : span.TrimStart('(');

        foreach (var argument in SplitTopLevelArguments(argsText))
        {
            var normalized = string.Concat(argument.Where(c => !char.IsWhiteSpace(c)));
            if (!normalized.Contains(DateTimeStylesToken, StringComparison.Ordinal))
            {
                continue;
            }

            return string.Equals(normalized, DateTimeStylesNoneToken, StringComparison.Ordinal);
        }

        return false;
    }

    private static IEnumerable<string> SplitTopLevelArguments(string argsText)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < argsText.Length; i++)
        {
            switch (argsText[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return argsText[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return argsText[start..];
    }

    private static bool IsOnACommentLine(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        var lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        var line = text[lineStart..lineEnd];
        return line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal);
    }

    private static int FindMatchingCloseParen(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int CountLineNumber(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
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
