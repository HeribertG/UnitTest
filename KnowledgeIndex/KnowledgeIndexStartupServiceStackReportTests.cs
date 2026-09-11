// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the startup report of the active retrieval stack. A silent fallback to a remote embedding
/// provider changes retrieval quality without any visible signal, which once invalidated a whole
/// measurement round, so the warning must not disappear in a refactoring. Also pins that the startup
/// sync goes through the single-flight scheduler and is awaited.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexStartupServiceStackReportTests
{
    [Test]
    public async Task StartAsync_WarnsOnce_WhenTheEmbeddingProviderIsNotTheLocalOnnxStack()
    {
        var recorder = await RunWithEmbeddingSpaceAsync("openai:text-embedding-3-small@384");

        recorder.Warnings.ShouldContain(m => m.Contains("FALLBACK"));
        recorder.Warnings.ShouldContain(m => m.Contains("openai:text-embedding-3-small@384"));
    }

    [Test]
    public async Task StartAsync_DoesNotWarn_WhenTheLocalOnnxStackIsActive()
    {
        var recorder = await RunWithEmbeddingSpaceAsync(
            KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384");

        recorder.Warnings.ShouldBeEmpty();
        recorder.Infos.ShouldContain(m => m.Contains("Retrieval stack"));
    }

    [Test]
    public async Task StartAsync_RunsTheSyncThroughTheSchedulerAndWaitsForIt()
    {
        var scheduler = new StubScheduler(succeeds: true);

        await RunWithEmbeddingSpaceAsync(
            KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384", scheduler);

        scheduler.RunNowReasons.ShouldBe([KnowledgeIndexSyncConstants.StartupReason]);
        scheduler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task StartAsync_WarnsWithTheError_WhenTheStartupSyncFailed()
    {
        var recorder = await RunWithEmbeddingSpaceAsync(
            KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384",
            new StubScheduler(succeeds: false));

        recorder.Warnings.ShouldContain(m => m.Contains("did not complete") && m.Contains(StubScheduler.FailureMessage));
    }

    private static async Task<LogRecorder> RunWithEmbeddingSpaceAsync(
        string embeddingSpaceId, StubScheduler? scheduler = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IEmbeddingProvider>(_ => new StubEmbeddingProvider(embeddingSpaceId));

        var recorder = new LogRecorder();
        var sut = new KnowledgeIndexStartupService(
            services.BuildServiceProvider(), scheduler ?? new StubScheduler(succeeds: true), recorder);

        await sut.StartAsync(CancellationToken.None);

        return recorder;
    }

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public StubEmbeddingProvider(string embeddingSpaceId) => EmbeddingSpaceId = embeddingSpaceId;

        public string EmbeddingSpaceId { get; }

        public int Dimension => KnowledgeIndexConstants.EmbeddingDimension;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float>());

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float[]>());

        public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float>());
    }

    private sealed class StubScheduler : IKnowledgeIndexSyncScheduler
    {
        public const string FailureMessage = "embedding provider down";

        private static readonly DateTimeOffset RunEnd = new(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

        private readonly bool _succeeds;

        public StubScheduler(bool succeeds) => _succeeds = succeeds;

        public List<string> RunNowReasons { get; } = [];

        public List<string> Requests { get; } = [];

        public KnowledgeIndexSyncStatus Status { get; private set; } =
            new(false, false, null, null, null, null);

        public void Request(string reason) => Requests.Add(reason);

        public Task RunNowAsync(string reason, CancellationToken cancellationToken)
        {
            RunNowReasons.Add(reason);
            Status = _succeeds
                ? new KnowledgeIndexSyncStatus(false, false, RunEnd, null, reason, null)
                : new KnowledgeIndexSyncStatus(false, false, null, RunEnd, reason, FailureMessage);
            return Task.CompletedTask;
        }
    }

    private sealed class LogRecorder : ILogger<KnowledgeIndexStartupService>
    {
        public List<string> Warnings { get; } = [];

        public List<string> Infos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(message);
            }
            else if (logLevel == LogLevel.Information)
            {
                Infos.Add(message);
            }
        }
    }
}
