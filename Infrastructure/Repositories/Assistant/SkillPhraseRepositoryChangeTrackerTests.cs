// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins that SkillPhraseRepository leaves no phrase tracked after a replacement. The startup seed
/// loader calls ReplaceAllLanguagesAsync once per skill and kind on one scoped context; phrases that
/// stay tracked make every later SaveChanges scan all of them, which turned the 1.0.27 production
/// start into a ten-minute, 100% CPU stall and made the auto-update roll back (2026-09-10).
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillPhraseRepositoryChangeTrackerTests
{
    private const string OwnerName = "seed_skill";

    private DataBaseContext _context = null!;
    private SkillPhraseRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();
        _repository = new SkillPhraseRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task ReplaceAllLanguages_WritesPhrasesAndLeavesNothingTracked()
    {
        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill,
            OwnerName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Seed,
            new Dictionary<string, List<string>> { ["de"] = ["dienst", "schicht"], ["en"] = ["shift"] });

        var stored = await _context.SkillPhrases.AsNoTracking().Where(p => p.OwnerName == OwnerName).ToListAsync();
        stored.Count.ShouldBe(3);
        _context.ChangeTracker.Entries<SkillPhrase>().Count().ShouldBe(0);
    }

    [Test]
    public async Task ReplaceAllLanguages_ReplacesPreviousPhrasesWithoutAccumulatingTrackedRows()
    {
        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill,
            OwnerName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Seed,
            new Dictionary<string, List<string>> { ["de"] = ["alt"] });

        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill,
            OwnerName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Seed,
            new Dictionary<string, List<string>> { ["de"] = ["neu"] });

        var active = await _context.SkillPhrases.AsNoTracking().Where(p => p.OwnerName == OwnerName).ToListAsync();
        active.Select(p => p.Phrase).ShouldBe(["neu"]);
        _context.ChangeTracker.Entries<SkillPhrase>().Count().ShouldBe(0);
    }

    [Test]
    public async Task ReplaceForLanguage_LeavesNothingTracked()
    {
        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            OwnerName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.LanguagePack,
            "pl",
            ["dodaj pracownika"]);

        var stored = await _context.SkillPhrases.AsNoTracking().Where(p => p.OwnerName == OwnerName).ToListAsync();
        stored.Count.ShouldBe(1);
        _context.ChangeTracker.Entries<SkillPhrase>().Count().ShouldBe(0);
    }
}
