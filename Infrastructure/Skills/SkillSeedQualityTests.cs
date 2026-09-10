// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards trigger-keyword quality in skill-seeds.json: no two skills may declare the same set of
/// trigger keywords. Word-identical keyword lists make the deterministic keyword guarantee
/// (SkillMatchingEngine) unable to distinguish the skills, so the guarantee cap fills with
/// alphabetically arbitrary picks instead of the skill the user actually asked for. Also guards
/// that every skill carries synonyms in all four core languages (de, en, fr, it), which have no
/// language pack and therefore no other coverage gate.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string KeywordSetSeparator = "\u0001";
    private const string SynonymsProperty = "synonyms";

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    [Test]
    public void TriggerKeywords_NoTwoSkillsShareAnIdenticalKeywordSet()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        var skillsByKeywordSet = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (!skill.TryGetProperty("triggerKeywords", out var keywords))
            {
                continue;
            }

            // Since the keyword rebuild this is an object keyed by language. Reading only arrays here
            // made the gate skip every skill and pass on nothing at all.
            var phrases = keywords.ValueKind switch
            {
                JsonValueKind.Array => keywords.EnumerateArray().ToList(),
                JsonValueKind.Object => keywords.EnumerateObject()
                    .Where(g => g.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(g => g.Value.EnumerateArray())
                    .ToList(),
                _ => []
            };

            var normalizedTerms = phrases
                .Select(k => (k.GetString() ?? string.Empty).Trim().ToLowerInvariant())
                .Where(k => k.Length > 0)
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (normalizedTerms.Count == 0)
            {
                continue;
            }

            var setKey = string.Join(KeywordSetSeparator, normalizedTerms);
            var skillName = skill.GetProperty("name").GetString() ?? string.Empty;

            if (!skillsByKeywordSet.TryGetValue(setKey, out var names))
            {
                names = [];
                skillsByKeywordSet[setKey] = names;
            }

            names.Add(skillName);
        }

        var collisions = skillsByKeywordSet.Values
            .Where(names => names.Count > 1)
            .Select(names => string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal)))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        collisions.ShouldBeEmpty(
            $"{SkillSeedsFileName} contains skills with identical triggerKeywords sets; the deterministic " +
            "keyword guarantee cannot rank them and falls back to alphabetical arbitrariness. Give each " +
            "skill action-specific keywords. Colliding groups: " + string.Join(" | ", collisions));
    }

    [Test]
    public void Synonyms_EverySkillMustCoverAllCoreLanguages_WithNonEmptyLists()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        var offenders = new List<string>();

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var skillName = skill.GetProperty("name").GetString() ?? string.Empty;
            var hasSynonyms = skill.TryGetProperty(SynonymsProperty, out var synonyms)
                && synonyms.ValueKind == JsonValueKind.Object;

            foreach (var language in CoreLanguages)
            {
                var covered = hasSynonyms
                    && synonyms.TryGetProperty(language, out var list)
                    && list.ValueKind == JsonValueKind.Array
                    && list.EnumerateArray().Any(t => !string.IsNullOrWhiteSpace(t.GetString()));

                if (!covered)
                {
                    offenders.Add($"{skillName}/{language}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"{SkillSeedsFileName} has skills without synonyms in a core language. The four core languages " +
            "ship no language pack, so a missing or empty list here silently weakens retrieval and the " +
            "keyword guarantee for that language. Missing: " + string.Join(", ", offenders));
    }

    [Test]
    public void SkillNames_MustBeUnique_AcrossMainSeedFileAndEachPluginSeedFile()
    {
        var seedFiles = new List<string> { LocateDefinitionsFile(SkillSeedsFileName) };
        seedFiles.AddRange(LocatePluginSeedFiles());

        var offenders = new List<string>();

        foreach (var seedFile in seedFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
            var root = document.RootElement;
            var skills = root.ValueKind == JsonValueKind.Array
                ? root
                : root.GetProperty("skills");

            var duplicates = skills.EnumerateArray()
                .Select(s => (s.TryGetProperty("name", out var n) ? n.GetString() : null) ?? string.Empty)
                .Where(n => n.Length > 0)
                .GroupBy(n => n.ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => $"{Path.GetFileName(Path.GetDirectoryName(seedFile))}/{Path.GetFileName(seedFile)}: {g.Key} (x{g.Count()})")
                .ToList();

            offenders.AddRange(duplicates);
        }

        offenders.ShouldBeEmpty(
            "Duplicate skill names crash the application on a FRESH database: SkillSeedLoader inserts both " +
            "rows and violates the unique index ix_agent_skills_agent_id_name, aborting startup " +
            "(incident: release 1.0.20 shipped list_scheduling_rules/get_scheduling_defaults twice and could " +
            "not start on empty databases). Duplicates: " + string.Join(" | ", offenders));
    }

    private static IEnumerable<string> LocatePluginSeedFiles()
    {
        var definitionsFile = LocateDefinitionsFile(SkillSeedsFileName);
        var apiRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(definitionsFile)!, "..", "..", ".."));
        var pluginsDir = Path.Combine(apiRoot, "Plugins", "Features");
        return Directory.Exists(pluginsDir)
            ? Directory.EnumerateFiles(pluginsDir, SkillSeedsFileName, SearchOption.AllDirectories)
            : [];
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
