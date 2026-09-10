// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Extensions;

using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class NavigationTargetCacheWarmUpExtensionTests
{
    [Test]
    public async Task WarmUpNavigationTargetCacheAsync_awaits_the_cache_warm_up()
    {
        var cache = Substitute.For<INavigationTargetCacheService>();
        var app = BuildApp(cache);

        await app.WarmUpNavigationTargetCacheAsync();

        await cache.Received(1).WarmUpAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WarmUpNavigationTargetCacheAsync_does_not_fail_startup_when_warm_up_throws()
    {
        var cache = Substitute.For<INavigationTargetCacheService>();
        cache.WarmUpAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database unavailable"));
        var app = BuildApp(cache);

        await Should.NotThrowAsync(() => app.WarmUpNavigationTargetCacheAsync());
    }

    private static IApplicationBuilder BuildApp(INavigationTargetCacheService cache)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(cache)
            .BuildServiceProvider();
        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(services);
        return app;
    }
}
