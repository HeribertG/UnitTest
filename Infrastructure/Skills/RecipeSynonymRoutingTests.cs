// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end routing gate for the language-pack recipe synonyms: every phrase in every
/// Plugins/Languages/&lt;code&gt;/recipe-synonyms.json must resolve to the recipe that owns it when
/// pushed through the engine's real resolution order. This mirrors RecipeEngineService.MatchByTrigger
/// exactly: the enabled recipes from recipe-seeds.json are iterated ordered by sortOrder then name (the
/// AgentRecipeRepository.GetAllEnabledAsync order), each one is tested with the production
/// RecipeTriggerMatcher plus the recipe's own synonym list for the detected language (SynonymsFor), and
/// the first hit wins. The engine passes the user message through untouched (no trim, no lowercasing;
/// the decline/question detectors only gate the semantic fallback, never the keyword trigger), so the
/// bare phrase is fed in verbatim. Synonyms are installed under the pack folder code
/// (LanguagePluginContentInstaller.InstallRecipeSynonymsAsync), so the folder name is the language key.
/// Two failure kinds are reported separately: SILENT (no recipe fires, typically because the phrase
/// trips its own recipe's noneOf veto or is blank) and HIJACKED (an earlier recipe fires first — its
/// structured allOf or its own synonyms cover the phrase). RecipeSeedQualityTests only checks substring
/// disjointness between synonym lists; it never runs a phrase against the allOf/noneOf of the other
/// recipes, which is where these misroutes hide.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeSynonymRoutingTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeSynonymsFileName = "recipe-synonyms.json";
    private const string SilentKind = "SILENT";
    private const string HijackedKind = "HIJACKED";
    private const string NoRecipeLabel = "<no recipe>";
    private const string VetoedByOwnNoneOfReason = "vetoed by own noneOf";
    private const string BlankPhraseReason = "blank phrase";
    private const string NoTriggerAndNoSynonymHitReason = "neither allOf nor own synonym matched";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record RoutingRecipe(string Name, RecipeTrigger Trigger, IReadOnlyCollection<string>? Synonyms);

    private sealed record Offender(string Kind, string Recipe, string Phrase, string Actual, string Reason);

    public static IEnumerable<string> LanguagesWithRecipeSynonyms() =>
        Directory.GetDirectories(LocatePluginsLanguagesDir())
            .Where(dir => File.Exists(Path.Combine(dir, RecipeSynonymsFileName)))
            .Select(dir => new DirectoryInfo(dir).Name)
            .OrderBy(name => name, StringComparer.Ordinal);

    [TestCaseSource(nameof(LanguagesWithRecipeSynonyms))]
    public void EveryPackSynonym_MustRouteToItsOwnRecipe_ThroughTheEngineOrder(string language)
    {
        var pack = LoadRecipeSynonymPack(language);
        var recipes = LoadEnabledRecipesInEngineOrder(pack);

        var offenders = new List<Offender>();
        foreach (var recipe in recipes)
        {
            if (recipe.Synonyms == null)
            {
                continue;
            }

            foreach (var phrase in recipe.Synonyms)
            {
                var actual = Resolve(recipes, phrase);
                if (string.Equals(actual, recipe.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add(actual == null
                    ? new Offender(SilentKind, recipe.Name, phrase, NoRecipeLabel, DiagnoseSilent(recipe, phrase))
                    : new Offender(HijackedKind, recipe.Name, phrase, actual, string.Empty));
            }
        }

        offenders.Count.ShouldBe(0, BuildFailureMessage(language, offenders));
    }

    /// <summary>
    /// Mirror of RecipeEngineService.MatchByTrigger: first recipe in resolution order whose trigger
    /// (or own-language synonym list) matches the untouched message.
    /// </summary>
    private static string? Resolve(IReadOnlyList<RoutingRecipe> recipes, string message)
        => recipes.FirstOrDefault(r => RecipeTriggerMatcher.Matches(r.Trigger, r.Synonyms, message))?.Name;

    private static string DiagnoseSilent(RoutingRecipe owner, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return BlankPhraseReason;
        }

        return RecipeTriggerMatcher.IsVetoed(owner.Trigger, phrase)
            ? VetoedByOwnNoneOfReason
            : NoTriggerAndNoSynonymHitReason;
    }

    private static string BuildFailureMessage(string language, List<Offender> offenders)
    {
        if (offenders.Count == 0)
        {
            return string.Empty;
        }

        var silent = offenders.Where(o => o.Kind == SilentKind).ToList();
        var hijacked = offenders.Where(o => o.Kind == HijackedKind).ToList();

        var lines = new List<string>
        {
            $"{language}/{RecipeSynonymsFileName}: {offenders.Count} phrase(s) do not route to their own recipe " +
            $"through the engine order ({silent.Count} {SilentKind}, {hijacked.Count} {HijackedKind}). " +
            "The engine returns the FIRST matching recipe by sortOrder and a synonym bypasses allOf but not " +
            "noneOf, so a phrase that another recipe's trigger covers is silently stolen and a phrase that trips " +
            "its own noneOf never fires."
        };

        lines.AddRange(silent.Select(o =>
            $"  {SilentKind}   {o.Recipe} / '{o.Phrase}' -> {o.Actual} ({o.Reason})"));
        lines.AddRange(hijacked.Select(o =>
            $"  {HijackedKind} {o.Recipe} / '{o.Phrase}' -> {o.Actual}"));

        return string.Join(Environment.NewLine, lines);
    }

    private static List<RoutingRecipe> LoadEnabledRecipesInEngineOrder(Dictionary<string, List<string>> pack)
    {
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(
            File.ReadAllText(LocateDefinitionsFile(RecipeSeedsFileName)), JsonReadOptions);

        return seed!.Recipes
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new RoutingRecipe(r.Name, r.Trigger, SynonymsFor(pack, r.Name)))
            .ToList();
    }

    /// <summary>
    /// Mirror of RecipeEngineService.SynonymsFor for a single language: the installer stores the pack
    /// list verbatim under the folder code, and the engine looks the language up case-insensitively, so
    /// building the recipe set per pack yields exactly the list the engine would see.
    /// </summary>
    private static IReadOnlyCollection<string>? SynonymsFor(Dictionary<string, List<string>> pack, string recipeName)
        => pack.TryGetValue(recipeName, out var phrases) ? phrases : null;

    private static Dictionary<string, List<string>> LoadRecipeSynonymPack(string language)
    {
        var file = Path.Combine(LocatePluginsLanguagesDir(), language, RecipeSynonymsFileName);
        return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file), JsonReadOptions)
               ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    private static string LocatePluginsLanguagesDir() =>
        TryLocate(PluginsLanguagesRelativePath, Directory.Exists)
        ?? throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', PluginsLanguagesRelativePath)} by walking up from the test base directory.");

    private static string LocateDefinitionsFile(string fileName) =>
        TryLocate([.. DefinitionsRelativePath, fileName], File.Exists)
        ?? throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");

    private static string? TryLocate(string[] relativeSegments, Func<string, bool> exists)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativeSegments);
            var candidate = Path.Combine(segments.ToArray());
            if (exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
