// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the navigation-synonym half of the language plugin content installer. Install and
/// uninstall must reconcile only rows owned by the pack (source "plugin"); the former replace deleted
/// every row of the (target, language) pair, customer-trained "user" synonyms included.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginNavigationSynonymsInstallerTests
{
    private const string Code = "ko";
    private const string TargetWithPhrases = "overtime";
    private const string TargetWithoutPhrases = "shift-group";

    private string _pluginDirectory = null!;
    private INavigationTargetSynonymRepository _repository = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-nav-syn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, Code));
        File.WriteAllText(
            Path.Combine(_pluginDirectory, Code, "navigation-targets.json"),
            $"{{\"{TargetWithPhrases}\": {{\"synonyms\": [\"초과근무\", \"야근\"], \"status\": \"generated\"}}, " +
            $"\"{TargetWithoutPhrases}\": {{\"synonyms\": [], \"status\": \"generated\"}}}}");

        _repository = Substitute.For<INavigationTargetSynonymRepository>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(INavigationTargetSynonymRepository)).Returns(_repository);
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
    public async Task Install_reconciles_plugin_rows_only_and_never_replaces_the_whole_pair()
    {
        await _installer.InstallNavigationSynonymsAsync(_scope, Code);

        await _repository.Received(1).SyncSourceKeywordsForTargetLanguageAsync(
            TargetWithPhrases, Code,
            Arg.Is<IReadOnlyCollection<string>>(k => k.SequenceEqual(new[] { "초과근무", "야근" })),
            SynonymSources.Plugin, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().ReplaceForTargetLanguageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_reconciles_an_emptied_target_so_its_old_plugin_rows_are_removed()
    {
        await _installer.InstallNavigationSynonymsAsync(_scope, Code);

        await _repository.Received(1).SyncSourceKeywordsForTargetLanguageAsync(
            TargetWithoutPhrases, Code,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 0),
            SynonymSources.Plugin, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_warms_the_navigation_cache_so_the_first_request_sees_the_new_synonyms()
    {
        var cache = Substitute.For<Klacks.Api.Application.Interfaces.Klacksy.INavigationTargetCacheService>();
        _scope.ServiceProvider.GetService(typeof(Klacks.Api.Application.Interfaces.Klacksy.INavigationTargetCacheService)).Returns(cache);

        await _installer.InstallNavigationSynonymsAsync(_scope, Code);

        await cache.Received(1).WarmUpAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Uninstall_removes_plugin_rows_only()
    {
        await _installer.UninstallNavigationSynonymsAsync(_scope, Code);

        await _repository.Received(2).SyncSourceKeywordsForTargetLanguageAsync(
            Arg.Any<string>(), Code,
            Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 0),
            SynonymSources.Plugin, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().ReplaceForTargetLanguageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
