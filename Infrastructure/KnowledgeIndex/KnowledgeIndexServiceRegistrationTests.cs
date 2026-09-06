// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

/// <summary>
/// Guards the one registration mistake that would silently double the ONNX memory footprint: giving
/// each interface its own factory lambda builds a second session per interface, and the idle sweep
/// would then hold a live reference to a session nothing ever calls.
/// No model is loaded here - both providers build their session on first use, never in the constructor.
/// </summary>
[TestFixture]
public class KnowledgeIndexServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(bool onnxEnabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnowledgeIndexConstants.OnnxEnabledConfigKey] = onnxEnabled ? "true" : "false",
                [KnowledgeIndexConstants.ModelsRootConfigKey] =
                    Path.Combine(Path.GetTempPath(), "klacks-test-models"),

                // Off so resolving IHostedService does not construct the startup sync service, which
                // needs the database. The two services under test here are unaffected by this flag.
                ["BackgroundServices:KnowledgeIndexStartup"] = "false",
            })
            .Build();

        // Deliberately no TimeProvider here. AddKnowledgeIndexServices registers its own, and this test
        // is what proves the method can be composed on its own instead of only after the LLM block.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        services.AddKnowledgeIndexServices(configuration);

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task AddKnowledgeIndexServices_EmbeddingProvider_IsTheSameInstanceAsItsUnloadableSession()
    {
        await using var provider = BuildProvider();

        var embedding = provider.GetRequiredService<IEmbeddingProvider>();
        var unloadable = provider.GetServices<IUnloadableInferenceSession>().OfType<OnnxEmbeddingProvider>().Single();

        ReferenceEquals(embedding, unloadable).ShouldBeTrue();
    }

    [Test]
    public async Task AddKnowledgeIndexServices_RerankerProvider_IsTheSameInstanceAsItsUnloadableSession()
    {
        await using var provider = BuildProvider();

        var reranker = provider.GetRequiredService<IRerankerProvider>();
        var unloadable = provider.GetServices<IUnloadableInferenceSession>().OfType<OnnxRerankerProvider>().Single();

        ReferenceEquals(reranker, unloadable).ShouldBeTrue();
    }

    [Test]
    public async Task AddKnowledgeIndexServices_RegistersBothProvidersAsUnloadableSessions()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IUnloadableInferenceSession>().Count().ShouldBe(2);
    }

    [Test]
    public async Task AddKnowledgeIndexServices_RegistersTheIdleUnloadSweepAsHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>()
            .OfType<OnnxSessionIdleUnloadService>()
            .ShouldHaveSingleItem();
    }

    // The sweep asks the allocator for the pages the session dispose only handed back to it, so a
    // missing trimmer registration would fail the whole hosted service at resolve time.
    [Test]
    public async Task AddKnowledgeIndexServices_RegistersTheProcessHeapTrimmer()
    {
        await using var provider = BuildProvider();

        provider.GetRequiredService<IProcessHeapTrimmer>().ShouldBeOfType<GlibcHeapTrimmer>();
    }

    // The sweep is registered unconditionally, also on hosts that cannot run ONNX at all. It has to
    // resolve there too and find nothing to watch, otherwise the Windows-ARM64 / remote-provider branch
    // would fail at startup over a service that has no work to do on it.
    [Test]
    public async Task AddKnowledgeIndexServices_WithoutOnnx_RegistersTheSweepWithNoSessionToWatch()
    {
        await using var provider = BuildProvider(onnxEnabled: false);

        provider.GetServices<IUnloadableInferenceSession>().ShouldBeEmpty();
        provider.GetServices<IHostedService>()
            .OfType<OnnxSessionIdleUnloadService>()
            .ShouldHaveSingleItem();
    }
}
