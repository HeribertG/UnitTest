// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the per-language skill-synonym contract: every skill in skill-seeds.json or in a feature
/// plugin's skill-seeds.json that ships non-empty synonyms must have a matching entry with at least
/// one non-blank phrase in every Plugins/Languages/&lt;locale&gt;/skill-synonyms.json pack, or that
/// language silently loses the deterministic keyword-based skill match for that skill without anyone
/// noticing. Locales are discovered dynamically from the Plugins/Languages directory, so a newly added
/// language pack is checked automatically. Feature-plugin skills are included since 2026-09-11: the
/// messaging skills had been missing from all 21 packs unnoticed, because only the core seeds were
/// required. A second guard enforces that CJK/Thai packs contain no phrase shorter than
/// SkillMatchingEngine's MinMatchLength (4 chars), since such entries can never match and are dead data.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class LanguagePackSkillSynonymCoverageTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillSynonymsFileName = "skill-synonyms.json";
    private const int MaxReportedMissingSkills = 20;
    private const int MinPhraseLength = 4;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly string[] CjkOrThaiLocales = ["ja", "ko", "th", "zh-CN", "zh-TW"];

    public static IEnumerable<string> LocaleDirectories() =>
        Directory.GetDirectories(LocatePluginsLanguagesDir())
            .Select(d => new DirectoryInfo(d).Name)
            .OrderBy(name => name, StringComparer.Ordinal);

    public static IEnumerable<string> CjkOrThaiLocaleDirectories() =>
        LocaleDirectories().Where(CjkOrThaiLocales.Contains);

    [TestCaseSource(nameof(LocaleDirectories))]
    public void SkillSynonyms_MustCoverEverySkillThatHasSynonymsInSeeds(string locale)
    {
        var requiredSkills = LoadSkillNamesWithNonEmptySynonyms();
        var pack = LoadSkillSynonymPack(locale);

        var missing = requiredSkills
            .Where(name => !pack.TryGetValue(name, out var phrases) ||
                           phrases is null ||
                           !phrases.Any(p => !string.IsNullOrWhiteSpace(p)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(BuildMissingSkillsMessage(locale, missing));
    }

    [TestCaseSource(nameof(CjkOrThaiLocaleDirectories))]
    public void CjkAndThaiPacks_MustNotContainPhrasesShorterThanMinMatchLength(string locale)
    {
        var pack = LoadSkillSynonymPack(locale);

        var violations = pack
            .SelectMany(kv => (kv.Value ?? new List<string>())
                .Where(phrase => !string.IsNullOrWhiteSpace(phrase) && phrase.Trim().Length < MinPhraseLength)
                .Select(phrase => $"{kv.Key}: '{phrase}' ({phrase.Trim().Length} chars)"))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            $"{locale}/{SkillSynonymsFileName} contains phrase(s) shorter than SkillMatchingEngine.MinMatchLength " +
            $"({MinPhraseLength} chars); these can never match and are dead data. Violations: " +
            string.Join("; ", violations));
    }

    private static string BuildMissingSkillsMessage(string locale, List<string> missing)
    {
        if (missing.Count == 0)
        {
            return string.Empty;
        }

        var shown = missing.Take(MaxReportedMissingSkills).ToList();
        var suffix = missing.Count > MaxReportedMissingSkills
            ? $", ... and {missing.Count - MaxReportedMissingSkills} more"
            : string.Empty;

        return $"{locale}/{SkillSynonymsFileName} is missing a non-empty synonym entry for {missing.Count} " +
            $"skill(s): {string.Join(", ", shown)}{suffix}. Every skill with non-empty synonyms in " +
            $"{SkillSeedsFileName} (core or feature plugin) must have at least one non-blank phrase here, or this language loses the " +
            "deterministic keyword-based skill match for it.";
    }

    private static List<string> LoadSkillNamesWithNonEmptySynonyms()
    {
        var names = new List<string>();

        using (var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName))))
        {
            names.AddRange(SelectNamesWithNonEmptySynonyms(document.RootElement.GetProperty("skills")));
        }

        var featuresDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (featuresDir != null)
        {
            foreach (var pluginDir in Directory.GetDirectories(featuresDir))
            {
                var seedFile = Path.Combine(pluginDir, SkillSeedsFileName);
                if (!File.Exists(seedFile))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
                names.AddRange(SelectNamesWithNonEmptySynonyms(document.RootElement));
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> SelectNamesWithNonEmptySynonyms(JsonElement skills) =>
        skills.EnumerateArray()
            .Where(s => s.TryGetProperty("synonyms", out var synonyms) &&
                        synonyms.ValueKind == JsonValueKind.Object &&
                        synonyms.EnumerateObject().Any())
            .Select(s => s.GetProperty("name").GetString() ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();

    private static Dictionary<string, List<string>> LoadSkillSynonymPack(string locale)
    {
        var file = Path.Combine(LocatePluginsLanguagesDir(), locale, SkillSynonymsFileName);
        if (!File.Exists(file))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))
            ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    private static string LocatePluginsLanguagesDir() =>
        TryLocateDir(PluginsLanguagesRelativePath)
        ?? throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', PluginsLanguagesRelativePath)} by walking up from the test base directory.");

    private static string? TryLocateDir(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");
    }
}
