// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that installing and uninstalling a language pack maintains skill_phrase alongside the legacy
/// jsonb dictionary, and that the uninstall removes exactly the rows of that language and that origin:
/// the seeded core-language rows and the rows of another installed pack have to survive it.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginSkillPhraseInstallerTests
{
    private const string Code = "pl";
    private const string MatchingSkill = "add_employee_to_group";
    private const string UnlistedSkill = "create_group";
    private const string Term = "dodaj pracownika do grupy";

    private string _pluginDirectory = null!;
    private DataBaseContext _context = null!;
    private SkillPhraseRepository _phraseRepository = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;
    private AgentSkill _matching = null!;
    private AgentSkill _unlisted = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-skill-phrase-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, Code));
        File.WriteAllText(
            Path.Combine(_pluginDirectory, Code, "skill-synonyms.json"),
            $"{{\"{MatchingSkill}\": [\"{Term}\"]}}");

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();
        _phraseRepository = new SkillPhraseRepository(_context);

        _matching = new AgentSkill { Id = Guid.NewGuid(), Name = MatchingSkill };
        _unlisted = new AgentSkill { Id = Guid.NewGuid(), Name = UnlistedSkill };

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetAllEnabledAsync().Returns(new List<AgentSkill> { _matching, _unlisted });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(ISkillPhraseRepository)).Returns(_phraseRepository);
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(provider);

        _installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _context.Database.EnsureDeleted();
        _context.Dispose();

        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Install_WritesPhrasesForMatchingSkillOnly_WithLanguagePackSource()
    {
        await _installer.InstallSkillSynonymsAsync(_scope, Code);

        _matching.Synonyms!.ShouldContainKey(Code);
        _unlisted.Synonyms.ShouldBeNull();

        var rows = await _context.SkillPhrases.AsNoTracking().ToListAsync();

        rows.Count.ShouldBe(1);
        rows[0].OwnerName.ShouldBe(MatchingSkill);
        rows[0].OwnerKind.ShouldBe(SkillPhraseOwnerKinds.Skill);
        rows[0].Kind.ShouldBe(SkillPhraseKinds.Synonym);
        rows[0].Language.ShouldBe(Code);
        rows[0].Source.ShouldBe(SkillPhraseSources.LanguagePack);
        rows[0].Phrase.ShouldBe(Term);
        rows[0].SortOrder.ShouldBe(0);
    }

    [Test]
    public async Task Uninstall_RemovesOnlyThatLanguageAndSource()
    {
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "mitarbeiter zur gruppe");
        await GivenPhraseAsync("es", SkillPhraseSources.LanguagePack, "anadir empleado");
        await GivenPhraseAsync(Code, SkillPhraseSources.LanguagePack, Term);
        await GivenPhraseAsync(Code, SkillPhraseSources.Admin, "recznie dodane");

        _matching.Synonyms = new Dictionary<string, List<string>>
        {
            [Code] = [Term],
            ["de"] = ["mitarbeiter zur gruppe"]
        };

        await _installer.UninstallSkillSynonymsAsync(_scope, Code);

        _matching.Synonyms.ContainsKey(Code).ShouldBeFalse();
        _matching.Synonyms.ContainsKey("de").ShouldBeTrue();

        var rows = await _context.SkillPhrases.AsNoTracking().ToListAsync();

        rows.Select(p => (p.Language, p.Source)).ShouldBe(
            [
                ("de", SkillPhraseSources.Seed),
                ("es", SkillPhraseSources.LanguagePack),
                (Code, SkillPhraseSources.Admin)
            ],
            ignoreOrder: true);
    }

    [Test]
    public async Task Install_LeavesSeededPhrasesOfTheSameSkillUntouched()
    {
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "mitarbeiter zur gruppe");

        await _installer.InstallSkillSynonymsAsync(_scope, Code);

        var rows = await _context.SkillPhrases.AsNoTracking().ToListAsync();

        rows.Select(p => (p.Language, p.Source)).ShouldBe(
            [("de", SkillPhraseSources.Seed), (Code, SkillPhraseSources.LanguagePack)],
            ignoreOrder: true);
    }

    [Test]
    public async Task Install_KeepsMirrorEntriesThatDoNotComeFromThePack()
    {
        _matching.Synonyms = new Dictionary<string, List<string>> { [Code] = ["recznie dodane"] };

        await _installer.InstallSkillSynonymsAsync(_scope, Code);

        _matching.Synonyms[Code].ShouldBe([Term, "recznie dodane"]);
    }

    [Test]
    public async Task Reinstall_ReplacesOnlyThePreviousPackPhrasesInTheMirror()
    {
        await GivenPhraseAsync(Code, SkillPhraseSources.LanguagePack, "stary termin");
        _matching.Synonyms = new Dictionary<string, List<string>> { [Code] = ["stary termin", "recznie dodane"] };

        await _installer.InstallSkillSynonymsAsync(_scope, Code);

        _matching.Synonyms[Code].ShouldBe([Term, "recznie dodane"]);
    }

    [Test]
    public async Task Uninstall_KeepsMirrorEntriesThatDoNotComeFromThePack()
    {
        await GivenPhraseAsync(Code, SkillPhraseSources.LanguagePack, Term);
        _matching.Synonyms = new Dictionary<string, List<string>> { [Code] = [Term, "recznie dodane"] };

        await _installer.UninstallSkillSynonymsAsync(_scope, Code);

        _matching.Synonyms[Code].ShouldBe(["recznie dodane"]);
    }

    private async Task GivenPhraseAsync(string language, string source, string phrase)
    {
        _context.SkillPhrases.Add(new SkillPhrase
        {
            Id = Guid.NewGuid(),
            OwnerKind = SkillPhraseOwnerKinds.Skill,
            OwnerName = MatchingSkill,
            Language = language,
            Kind = SkillPhraseKinds.Synonym,
            Phrase = phrase,
            Source = source,
            Status = SkillPhraseStatuses.Active
        });

        await _context.SaveChangesAsync();
    }
}
