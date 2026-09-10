// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for NavigationTargetSynonymSeedService: verifies it delegates the actual reconciliation to
/// SyncSeedKeywordsForTargetLanguageAsync instead of the old all-or-nothing pairwise guard, so a
/// customer- or plugin-owned row in a pair no longer blocks new vendor synonyms from ever reaching
/// that pair.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class NavigationTargetSynonymSeedServiceTests
{
    private const string ManifestRelativePath = "Application/Skills/Definitions/navigation-targets.json";

    private INavigationTargetSynonymRepository _repository = null!;
    private IWebHostEnvironment _environment = null!;
    private NavigationTargetSynonymSeedService _service = null!;
    private string _contentRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-nav-synonym-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "Application", "Skills", "Definitions"));

        _repository = Substitute.For<INavigationTargetSynonymRepository>();
        _environment = Substitute.For<IWebHostEnvironment>();
        _environment.ContentRootPath.Returns(_contentRoot);

        _service = new NavigationTargetSynonymSeedService(_repository, _environment, Substitute.For<ILogger<NavigationTargetSynonymSeedService>>());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task SeedAsync_calls_sync_per_target_language_pair_instead_of_the_pairwise_guard()
    {
        WriteManifest([
            new()
            {
                TargetId = "erp-drop-points",
                Synonyms = new Dictionary<string, string[]>
                {
                    ["de"] = ["abladestelle", "lieferpunkt"]
                }
            }
        ]);

        _repository.SyncSeedKeywordsForTargetLanguageAsync("erp-drop-points", "de", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new NavigationTargetSynonymSyncResult(InsertedCount: 2, RemovedCount: 0, UntouchedForeignCount: 1));

        await _service.SeedAsync();

        await _repository.Received(1).SyncSeedKeywordsForTargetLanguageAsync(
            "erp-drop-points", "de",
            Arg.Is<IReadOnlyCollection<string>>(k => k.SequenceEqual(new[] { "abladestelle", "lieferpunkt" })),
            Arg.Any<CancellationToken>());

        await _repository.DidNotReceive().ReplaceForTargetLanguageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().GetActiveForTargetLanguageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedAsync_reconciles_pairs_with_empty_keyword_arrays_so_stale_seed_rows_are_removed()
    {
        WriteManifest([
            new()
            {
                TargetId = "shift-group",
                Synonyms = new Dictionary<string, string[]>
                {
                    ["de"] = []
                }
            }
        ]);

        _repository.SyncSeedKeywordsForTargetLanguageAsync("shift-group", "de", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new NavigationTargetSynonymSyncResult(InsertedCount: 0, RemovedCount: 8, UntouchedForeignCount: 0));

        await _service.SeedAsync();

        await _repository.Received(1).SyncSeedKeywordsForTargetLanguageAsync(
            "shift-group", "de",
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 0),
            Arg.Any<CancellationToken>());
    }

    private void WriteManifest(List<ManifestTarget> targets)
    {
        var path = Path.Combine(_contentRoot, ManifestRelativePath);
        File.WriteAllText(path, JsonSerializer.Serialize(targets));
    }

    private sealed class ManifestTarget
    {
        public string TargetId { get; set; } = string.Empty;
        public string Route { get; set; } = "/route";
        public string LabelKey { get; set; } = "nav.label";
        public Dictionary<string, string[]> Synonyms { get; set; } = new();
    }
}
