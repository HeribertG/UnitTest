// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for NavigationTargetSynonymRepository: verifies ReplaceForTargetLanguage soft-deletes existing
/// entries and inserts fresh ones, that HasActiveEntries returns the correct result, and that
/// SyncSeedKeywordsForTargetLanguage reconciles seed rows against the manifest row by row without ever
/// touching customer- or plugin-owned rows in the same (TargetId, Language) pair.
/// </summary>
namespace Klacks.UnitTest.Application.Klacksy;

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
public class NavigationTargetSynonymRepositoryTests
{
    private DataBaseContext _context = null!;
    private NavigationTargetSynonymRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpAccessor);
        _context.Database.EnsureCreated();
        _repository = new NavigationTargetSynonymRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task GetAllAsync_returns_only_active_entries()
    {
        await _context.NavigationTargetSynonyms.AddRangeAsync(new[]
        {
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "test", IsDeleted = false },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "deleted", IsDeleted = true }
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetAllAsync();

        result.Count.ShouldBe(1);
        result[0].Keyword.ShouldBe("test");
    }

    [Test]
    public async Task ReplaceForTargetLanguage_soft_deletes_existing_and_adds_new()
    {
        var existingId = Guid.NewGuid();
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = existingId,
            TargetId = "absence",
            Language = "de",
            Keyword = "abwesenheit",
            IsDeleted = false
        });
        await _context.SaveChangesAsync();

        await _repository.ReplaceForTargetLanguageAsync("absence", "de", new[] { "fehlzeit", "urlaub" }, SynonymSources.Seed);

        var allWithDeleted = await _context.NavigationTargetSynonyms
            .IgnoreQueryFilters()
            .Where(s => s.TargetId == "absence" && s.Language == "de")
            .ToListAsync();

        var active = allWithDeleted.Where(s => !s.IsDeleted).ToList();
        var deleted = allWithDeleted.Where(s => s.IsDeleted).ToList();

        active.Count.ShouldBe(2);
        active.Select(s => s.Keyword).ShouldContain("fehlzeit");
        active.Select(s => s.Keyword).ShouldContain("urlaub");
        deleted.Count.ShouldBe(1);
        deleted[0].Id.ShouldBe(existingId);
    }

    [Test]
    public async Task ReplaceForTargetLanguage_with_empty_list_removes_all()
    {
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = Guid.NewGuid(), TargetId = "t1", Language = "fr", Keyword = "test", IsDeleted = false
        });
        await _context.SaveChangesAsync();

        await _repository.ReplaceForTargetLanguageAsync("t1", "fr", Array.Empty<string>(), SynonymSources.Seed);

        var active = await _repository.GetAllAsync();
        active.ShouldBeEmpty();
    }

    [Test]
    public async Task HasActiveEntriesForTargetLanguage_returns_true_when_entries_exist()
    {
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = Guid.NewGuid(), TargetId = "schedule", Language = "en", Keyword = "schedule", IsDeleted = false
        });
        await _context.SaveChangesAsync();

        var result = await _repository.HasActiveEntriesForTargetLanguageAsync("schedule", "en");
        result.ShouldBeTrue();
    }

    [Test]
    public async Task HasActiveEntriesForTargetLanguage_returns_false_when_all_soft_deleted()
    {
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = Guid.NewGuid(), TargetId = "schedule", Language = "en", Keyword = "schedule", IsDeleted = true
        });
        await _context.SaveChangesAsync();

        var result = await _repository.HasActiveEntriesForTargetLanguageAsync("schedule", "en");
        result.ShouldBeFalse();
    }

    [Test]
    public async Task SyncSeedKeywords_inserts_all_manifest_keywords_when_pair_has_no_rows()
    {
        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("erp-drop-points", "de", new[] { "abladestelle", "lieferpunkt" });

        result.InsertedCount.ShouldBe(2);
        result.RemovedCount.ShouldBe(0);
        result.UntouchedForeignCount.ShouldBe(0);

        var active = await _repository.GetActiveForTargetLanguageAsync("erp-drop-points", "de");
        active.Count.ShouldBe(2);
        active.ShouldAllBe(s => s.Source == SynonymSources.Seed);
        active.Select(s => s.Keyword).ShouldContain("abladestelle");
        active.Select(s => s.Keyword).ShouldContain("lieferpunkt");
    }

    [Test]
    public async Task SyncSeedKeywords_with_unchanged_seed_only_set_writes_nothing()
    {
        _context.NavigationTargetSynonyms.AddRange(
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "a", Source = SynonymSources.Seed },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "b", Source = SynonymSources.Seed });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("t1", "de", new[] { "a", "b" });

        result.InsertedCount.ShouldBe(0);
        result.RemovedCount.ShouldBe(0);
        result.UntouchedForeignCount.ShouldBe(0);

        var active = await _repository.GetActiveForTargetLanguageAsync("t1", "de");
        active.Count.ShouldBe(2);
    }

    [Test]
    public async Task SyncSeedKeywords_with_seed_only_set_adds_new_manifest_keyword()
    {
        _context.NavigationTargetSynonyms.Add(
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "a", Source = SynonymSources.Seed });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("t1", "de", new[] { "a", "b" });

        result.InsertedCount.ShouldBe(1);
        result.RemovedCount.ShouldBe(0);

        var active = await _repository.GetActiveForTargetLanguageAsync("t1", "de");
        active.Select(s => s.Keyword).ShouldBe(new[] { "a", "b" }, ignoreOrder: true);
    }

    [Test]
    public async Task SyncSeedKeywords_with_seed_only_set_removes_dropped_manifest_keyword()
    {
        _context.NavigationTargetSynonyms.AddRange(
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "a", Source = SynonymSources.Seed },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "b", Source = SynonymSources.Seed });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("t1", "de", new[] { "a" });

        result.InsertedCount.ShouldBe(0);
        result.RemovedCount.ShouldBe(1);

        var active = await _repository.GetActiveForTargetLanguageAsync("t1", "de");
        active.Count.ShouldBe(1);
        active[0].Keyword.ShouldBe("a");
    }

    [Test]
    public async Task SyncSeedKeywords_preserves_user_row_while_adding_new_manifest_keywords()
    {
        var userRowId = Guid.NewGuid();
        _context.NavigationTargetSynonyms.AddRange(
            new NavigationTargetSynonym { Id = userRowId, TargetId = "erp-drop-points", Language = "de", Keyword = "kundenbegriff", Source = SynonymSources.User },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "erp-drop-points", Language = "de", Keyword = "abladestelle", Source = SynonymSources.Seed });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync(
            "erp-drop-points", "de", new[] { "abladestelle", "lieferpunkt" });

        result.InsertedCount.ShouldBe(1);
        result.RemovedCount.ShouldBe(0);
        result.UntouchedForeignCount.ShouldBe(1);

        var active = await _repository.GetActiveForTargetLanguageAsync("erp-drop-points", "de");
        active.Count.ShouldBe(3);
        active.Select(s => s.Keyword).ShouldContain("lieferpunkt");

        var userRow = active.Single(s => s.Id == userRowId);
        userRow.Keyword.ShouldBe("kundenbegriff");
        userRow.Source.ShouldBe(SynonymSources.User);
    }

    [Test]
    public async Task SyncSeedKeywords_does_not_duplicate_keyword_already_owned_by_user()
    {
        var userRowId = Guid.NewGuid();
        _context.NavigationTargetSynonyms.Add(
            new NavigationTargetSynonym { Id = userRowId, TargetId = "t1", Language = "de", Keyword = "Abladestelle", Source = SynonymSources.User });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("t1", "de", new[] { "abladestelle" });

        result.InsertedCount.ShouldBe(0);
        result.RemovedCount.ShouldBe(0);
        result.UntouchedForeignCount.ShouldBe(1);

        var active = await _repository.GetActiveForTargetLanguageAsync("t1", "de");
        active.Count.ShouldBe(1);
        active[0].Id.ShouldBe(userRowId);
        active[0].Source.ShouldBe(SynonymSources.User);
    }

    [Test]
    public async Task SyncSeedKeywords_preserves_plugin_rows_and_adds_new_seed_keywords()
    {
        _context.NavigationTargetSynonyms.AddRange(
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "plugin-wort-1", Source = SynonymSources.Plugin },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "plugin-wort-2", Source = SynonymSources.Plugin });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("t1", "de", new[] { "seed-wort" });

        result.InsertedCount.ShouldBe(1);
        result.RemovedCount.ShouldBe(0);
        result.UntouchedForeignCount.ShouldBe(2);

        var active = await _repository.GetActiveForTargetLanguageAsync("t1", "de");
        active.Count.ShouldBe(3);
        active.ShouldContain(s => s.Keyword == "seed-wort" && s.Source == SynonymSources.Seed);
        active.ShouldContain(s => s.Keyword == "plugin-wort-1" && s.Source == SynonymSources.Plugin);
        active.ShouldContain(s => s.Keyword == "plugin-wort-2" && s.Source == SynonymSources.Plugin);
    }

    [Test]
    public async Task SyncSeedKeywords_with_empty_manifest_list_removes_only_seed_rows()
    {
        _context.NavigationTargetSynonyms.AddRange(
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "alter satz", Source = SynonymSources.Seed },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "plugin-wort", Source = SynonymSources.Plugin },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "kundenbegriff", Source = SynonymSources.User });
        await _context.SaveChangesAsync();

        var result = await _repository.SyncSeedKeywordsForTargetLanguageAsync("t1", "de", Array.Empty<string>());

        result.InsertedCount.ShouldBe(0);
        result.RemovedCount.ShouldBe(1);
        result.UntouchedForeignCount.ShouldBe(2);

        var active = await _repository.GetActiveForTargetLanguageAsync("t1", "de");
        active.Select(s => s.Keyword).ShouldBe(new[] { "plugin-wort", "kundenbegriff" }, ignoreOrder: true);
    }

    [Test]
    public async Task GetByLanguagesAsync_returns_only_requested_languages()
    {
        await _context.NavigationTargetSynonyms.AddRangeAsync(new[]
        {
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "test-de" },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "en", Keyword = "test-en" },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "fr", Keyword = "test-fr" }
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetByLanguagesAsync(new[] { "de", "fr" });

        result.Count.ShouldBe(2);
        result.Select(s => s.Language).ShouldContain("de");
        result.Select(s => s.Language).ShouldContain("fr");
        result.Select(s => s.Language).ShouldNotContain("en");
    }
}
