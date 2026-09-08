// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that every welcome-focus i18n key the backend can hand out exists in all four core
/// translation catalogues, with the placeholders the backend actually fills. Follows
/// MessengerProactiveTextsTests: the JSON files live in a different repository and the backend CI
/// job does not check out Klacks.Ui, so the test reports itself inconclusive when they are
/// unreachable instead of failing a job that has nothing to do with the frontend.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class WelcomeFocusI18nGateTests
{
    private static readonly string[] Languages = ["de", "en", "fr", "it"];
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";

    [Test]
    public void EveryFocusKeyExistsInAllFourCoreLanguages()
    {
        var catalogueDirectory = FindUiCatalogueDirectory();
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        foreach (var language in Languages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            Assert.That(File.Exists(path), Is.True, $"Missing frontend catalogue '{path}'.");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in WelcomeFocusI18nKeys.All)
            {
                Assert.That(document.RootElement.TryGetProperty(key, out var value), Is.True,
                    $"'{key}' is missing from '{path}'.");
                Assert.That(value.GetString(), Is.Not.Null.And.Not.Empty, $"'{key}' is empty in '{path}'.");
            }
        }
    }

    [Test]
    public void TheReusedSetupConsultationButtonKeyExistsInAllFourCoreLanguages()
    {
        var catalogueDirectory = FindUiCatalogueDirectory();
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        foreach (var language in Languages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(
                document.RootElement.TryGetProperty(WelcomeFocusI18nKeys.SetupConsultationAction, out var value),
                Is.True,
                $"'{WelcomeFocusI18nKeys.SetupConsultationAction}' is missing from '{path}'.");
            Assert.That(value.GetString(), Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public void PromptsCarryThePlaceholdersTheBackendFills()
    {
        var catalogueDirectory = FindUiCatalogueDirectory();
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        var expectations = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WelcomeFocusI18nKeys.PeriodOverduePrompt] = ["{{group}}", "{{periodEnd}}", "{{days}}"],
            [WelcomeFocusI18nKeys.PeriodCloseDuePrompt] = ["{{group}}", "{{periodEnd}}", "{{days}}"],
            [WelcomeFocusI18nKeys.NextPeriodPrompt] = ["{{group}}", "{{periodStart}}", "{{days}}"],
            [WelcomeFocusI18nKeys.GenericPrompt] = ["{{count}}"]
        };

        foreach (var language in Languages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var (key, placeholders) in expectations)
            {
                Assert.That(document.RootElement.TryGetProperty(key, out var value), Is.True,
                    $"'{key}' is missing from '{path}'.");
                var text = value.GetString() ?? string.Empty;
                foreach (var placeholder in placeholders)
                {
                    Assert.That(text, Does.Contain(placeholder),
                        $"'{key}' in '{path}' is missing the placeholder '{placeholder}'.");
                }
            }
        }
    }

    private static string? FindUiCatalogueDirectory()
    {
        var relative = UiCatalogueRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
