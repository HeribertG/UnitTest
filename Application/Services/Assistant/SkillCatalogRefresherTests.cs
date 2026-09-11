// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SkillCatalogRefresher — cache and registry refresh first, then the knowledge index sync
/// is either only requested (RefreshAsync) or awaited (RefreshAndWaitForIndexAsync). Sync failure
/// isolation lives in the scheduler and is covered by KnowledgeIndexSyncSchedulerTests.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant;

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillCatalogRefresherTests
{
    private const string Reason = "creating skill 'x'";

    private ISkillCacheService _cache = null!;
    private SkillRegistryInitializer _initializer = null!;
    private IKnowledgeIndexSyncScheduler _scheduler = null!;
    private SkillCatalogRefresher _refresher = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Substitute.For<ISkillCacheService>();
        _initializer = Substitute.For<SkillRegistryInitializer>(
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<ISkillRegistry>(),
            Substitute.For<ILogger<SkillRegistryInitializer>>());
        _scheduler = Substitute.For<IKnowledgeIndexSyncScheduler>();

        _refresher = new SkillCatalogRefresher(_cache, _initializer, _scheduler);
    }

    [Test]
    public async Task RefreshAsync_RefreshesCacheAndRegistryThenRequestsTheIndexSync()
    {
        await _refresher.RefreshAsync(Reason, CancellationToken.None);

        Received.InOrder(() =>
        {
            _cache.InvalidateCache();
            _initializer.InitializeAsync(Arg.Any<CancellationToken>());
            _scheduler.Request(Reason);
        });
        await _scheduler.DidNotReceive().RunNowAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefreshAndWaitForIndexAsync_RefreshesCacheAndRegistryThenAwaitsTheIndexSync()
    {
        using var cancellation = new CancellationTokenSource();

        await _refresher.RefreshAndWaitForIndexAsync(Reason, cancellation.Token);

        Received.InOrder(() =>
        {
            _cache.InvalidateCache();
            _initializer.InitializeAsync(Arg.Any<CancellationToken>());
            _scheduler.RunNowAsync(Reason, cancellation.Token);
        });
        _scheduler.DidNotReceive().Request(Arg.Any<string>());
    }

    [Test]
    public async Task RefreshAndWaitForIndexAsync_ReturnsOnlyOnceTheSchedulerReportsTheSyncDone()
    {
        var sync = new TaskCompletionSource();
        _scheduler.RunNowAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(sync.Task);

        var refresh = _refresher.RefreshAndWaitForIndexAsync(Reason, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        refresh.IsCompleted.ShouldBeFalse();

        sync.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
