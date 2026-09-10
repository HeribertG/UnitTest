// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the recipe-synonyms half of the language plugin content installer: installing a
/// language reads its recipe-synonyms.json and writes the terms into AgentRecipe.Synonyms[code] for the
/// matching recipes only (recipes absent from the file are left untouched), and uninstalling removes
/// exactly that language's entry. This is the install-time merge that gives recipes the same
/// per-language synonyms skills already have.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginRecipeSynonymsInstallerTests
{
    private const string Code = "es";
    private const string MatchingRecipe = "add-employee-to-group";
    private const string UnlistedRecipe = "create-group";
    private const string Term = "incorporar un empleado al grupo";

    private string _pluginDirectory = null!;
    private IAgentRecipeRepository _repository = null!;
    private ISkillPhraseRepository _phraseRepository = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;
    private AgentRecipe _matching = null!;
    private AgentRecipe _unlisted = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-recipe-syn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, Code));
        File.WriteAllText(
            Path.Combine(_pluginDirectory, Code, "recipe-synonyms.json"),
            $"{{\"{MatchingRecipe}\": [\"{Term}\"]}}");

        _matching = new AgentRecipe { Name = MatchingRecipe };
        _unlisted = new AgentRecipe { Name = UnlistedRecipe };

        _repository = Substitute.For<IAgentRecipeRepository>();
        _repository.GetAllEnabledAsync().Returns(new List<AgentRecipe> { _matching, _unlisted });

        _phraseRepository = Substitute.For<ISkillPhraseRepository>();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_repository);
        provider.GetService(typeof(ISkillPhraseRepository)).Returns(_phraseRepository);
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(provider);

        _installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Install_WritesSynonyms_ForMatchingRecipeOnly()
    {
        await _installer.InstallRecipeSynonymsAsync(_scope, Code);

        Assert.That(_matching.Synonyms, Is.Not.Null);
        Assert.That(_matching.Synonyms![Code], Does.Contain(Term));
        Assert.That(_unlisted.Synonyms, Is.Null, "a recipe absent from the file must stay untouched");
        await _repository.Received(1).UpdateAsync(_matching);
        await _repository.DidNotReceive().UpdateAsync(_unlisted);
    }

    [Test]
    public async Task Uninstall_RemovesOnlyThatLanguageEntry()
    {
        GivenPreviousPackPhrases(Term);
        _matching.Synonyms = new Dictionary<string, List<string>>
        {
            [Code] = [Term],
            ["de"] = ["mitarbeiter zur gruppe"]
        };

        await _installer.UninstallRecipeSynonymsAsync(_scope, Code);

        Assert.That(_matching.Synonyms.ContainsKey(Code), Is.False);
        Assert.That(_matching.Synonyms.ContainsKey("de"), Is.True, "other languages must be preserved");
        await _repository.Received(1).UpdateAsync(_matching);
    }

    [Test]
    public async Task Uninstall_KeepsAnAdminEditedEntryOfThatLanguage()
    {
        GivenPreviousPackPhrases(Term);
        _matching.Synonyms = new Dictionary<string, List<string>> { [Code] = [Term, "admin edit"] };

        await _installer.UninstallRecipeSynonymsAsync(_scope, Code);

        Assert.That(_matching.Synonyms[Code], Is.EqualTo(new[] { "admin edit" }));
    }

    [Test]
    public async Task Reinstall_ReplacesThePreviousPackPhrases_AndKeepsAnAdminEditedEntry()
    {
        GivenPreviousPackPhrases("old pack term");
        _matching.Synonyms = new Dictionary<string, List<string>> { [Code] = ["old pack term", "admin edit"] };

        await _installer.InstallRecipeSynonymsAsync(_scope, Code);

        Assert.That(_matching.Synonyms[Code], Is.EqualTo(new[] { Term, "admin edit" }));
    }

    private void GivenPreviousPackPhrases(params string[] phrases)
    {
        _phraseRepository
            .GetPhraseTextsBySourceAsync(
                SkillPhraseOwnerKinds.Recipe, MatchingRecipe, SkillPhraseKinds.Synonym, SkillPhraseSources.LanguagePack, Code, Arg.Any<CancellationToken>())
            .Returns(phrases);
    }
}
