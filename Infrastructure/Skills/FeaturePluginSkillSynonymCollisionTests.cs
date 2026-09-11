// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards feature-plugin skill seeds against taking over core vocabulary: a synonym of a plugin skill
/// (Plugins/Features/&lt;plugin&gt;/skill-seeds.json) must not be identical to a synonym of a core skill
/// (Application/Skills/Definitions/skill-seeds.json) or of another plugin's skill in the same language.
/// The deterministic keyword match boosts every skill that owns a matching phrase, so a shared phrase lets
/// an optional plugin compete with the core skill the phrase belongs to. Incident 2026-09-11: the messaging
/// skill read_messages carried "posteingang anzeigen" / "show inbox", which are the core list_emails
/// phrases for the e-mail inbox. Core skills among themselves are not checked here.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class FeaturePluginSkillSynonymCollisionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SynonymsProperty = "synonyms";
    private const string NameProperty = "name";
    private const string SkillsProperty = "skills";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    [Test]
    public void PluginSkillSynonyms_MustNotRepeatAnotherSkillsSynonym_PerLanguage()
    {
        var owners = new Dictionary<(string Language, string Phrase), HashSet<string>>();

        using (var core = JsonDocument.Parse(File.ReadAllText(LocateFile(DefinitionsRelativePath, SkillSeedsFileName))))
        {
            Register(owners, core.RootElement.GetProperty(SkillsProperty), source: "core");
        }

        var pluginPhrases = new List<(string Language, string Phrase, string Owner)>();
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

                var plugin = new DirectoryInfo(pluginDir).Name;
                using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
                pluginPhrases.AddRange(Register(owners, document.RootElement, source: plugin));
            }
        }

        var violations = pluginPhrases
            .Where(entry => owners[(entry.Language, entry.Phrase)].Count > 1)
            .Select(entry =>
                $"{entry.Language} '{entry.Phrase}' -> " +
                string.Join(", ", owners[(entry.Language, entry.Phrase)].OrderBy(o => o, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "A feature-plugin skill synonym repeats the synonym of another skill in the same language; the " +
            "keyword match then boosts both and the plugin competes with the phrase's owner. Rephrase the " +
            "plugin synonym. Violations: " + string.Join("; ", violations));
    }

    private static List<(string Language, string Phrase, string Owner)> Register(
        Dictionary<(string Language, string Phrase), HashSet<string>> owners, JsonElement skills, string source)
    {
        var registered = new List<(string Language, string Phrase, string Owner)>();

        foreach (var skill in skills.EnumerateArray())
        {
            var name = skill.GetProperty(NameProperty).GetString();
            if (string.IsNullOrWhiteSpace(name) ||
                !skill.TryGetProperty(SynonymsProperty, out var synonyms) ||
                synonyms.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var owner = $"{source}:{name}";
            foreach (var language in synonyms.EnumerateObject())
            {
                foreach (var phrase in language.Value.EnumerateArray().Select(p => p.GetString()))
                {
                    if (string.IsNullOrWhiteSpace(phrase))
                    {
                        continue;
                    }

                    var key = (language.Name, phrase.Trim().ToLowerInvariant());
                    if (!owners.TryGetValue(key, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        owners[key] = set;
                    }

                    set.Add(owner);
                    registered.Add((key.Item1, key.Item2, owner));
                }
            }
        }

        return registered;
    }

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

    private static string LocateFile(string[] relativePath, string fileName)
    {
        var dir = TryLocateDir(relativePath)
            ?? throw new DirectoryNotFoundException(
                $"Could not locate {string.Join('/', relativePath)} by walking up from the test base directory.");

        return Path.Combine(dir, fileName);
    }
}
