// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.Wizard;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardApplyServiceComplianceTests
{
    private const string BlockReasonKey = "schedule.error-list.period-cap";

    private static readonly DateOnly Day1 = new(2026, 4, 20);
    private static readonly DateOnly Day2 = new(2026, 4, 21);

    private WizardResultCache _cache = null!;
    private IMediator _mediator = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkSofteningRepository _softeningRepository = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private ICompliancePartitionService _partitionService = null!;
    private WizardApplyService _sut = null!;

    private readonly List<Guid> _createdIds = new() { Guid.NewGuid() };

    [SetUp]
    public void SetUp()
    {
        _cache = new WizardResultCache();
        _mediator = Substitute.For<IMediator>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _softeningRepository = Substitute.For<IWorkSofteningRepository>();
        _captureRepository = Substitute.For<IWizardRunCaptureRepository>();
        _partitionService = Substitute.For<ICompliancePartitionService>();

        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = _createdIds });
        _scenarioService.CloneScenarioDataAsync(Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid>());
        _scenarioRepository.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());

        _sut = new WizardApplyService(
            _cache,
            _mediator,
            _scenarioRepository,
            _scenarioService,
            _unitOfWork,
            _softeningRepository,
            _captureRepository,
            _partitionService,
            BuildInMemoryContext(),
            Substitute.For<IScheduleTimelineService>(),
            new FixedCompanyClock(new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<WizardApplyService>.Instance);

        // The real unit of work runs the delegate inside a transaction; the substitute must do the same
        // or the apply would silently produce nothing.
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<IReadOnlyList<Guid>>>>())
            .Returns(ci => ci.Arg<Func<Task<IReadOnlyList<Guid>>>>()());
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<(IReadOnlyList<Guid> Ids, CompliancePartitionResult Partition, IReadOnlyList<SkippedPlacementDto> Skipped, AnalyseScenario Scenario)>>>())
            .Returns(ci => ci.Arg<Func<Task<(IReadOnlyList<Guid> Ids, CompliancePartitionResult Partition, IReadOnlyList<SkippedPlacementDto> Skipped, AnalyseScenario Scenario)>>>()());
    }

    private static CoreToken MakeToken(Guid agentId, DateOnly date)
    {
        var start = date.ToDateTime(new TimeOnly(8, 0));
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: date,
            TotalHours: 8,
            StartAt: start,
            EndAt: start.AddHours(8),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.NewGuid(),
            AgentId: agentId.ToString());
    }

    private void SetPartitionResult(IReadOnlyList<int> accepted, IReadOnlyList<BlockedRow> blocked,
        IReadOnlyList<ScheduleValidationNotificationDto>? reportable = null, bool overrideApplied = false)
    {
        _partitionService
            .PartitionAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CompliancePartitionResult(accepted, blocked, reportable ?? [], overrideApplied));
    }

    private void SetPartitionPassThrough()
    {
        _partitionService
            .PartitionAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => new CompliancePartitionResult(
                Enumerable.Range(0, ci.Arg<IReadOnlyList<PlannedWorkRow>>().Count).ToList(), [], [], false));
    }

    [Test]
    public async Task ApplyAsync_BlockedRow_IsExcludedFromBulkPayload_AndReportedAsSkipped()
    {
        var acceptedAgent = Guid.NewGuid();
        var blockedAgent = Guid.NewGuid();
        var tokens = new List<CoreToken> { MakeToken(acceptedAgent, Day1), MakeToken(blockedAgent, Day2) };
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = tokens }, null);
        SetPartitionResult(accepted: [0], blocked: [new BlockedRow(1, BlockReasonKey)]);

        BulkAddWorksCommand? sent = null;
        _mediator.Send(Arg.Do<BulkAddWorksCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = _createdIds });

        var outcome = await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        sent.ShouldNotBeNull();
        sent!.Request.Works.Count.ShouldBe(1);
        sent.Request.Works[0].ClientId.ShouldBe(acceptedAgent);

        var skipped = outcome.SkippedPlacements.ShouldHaveSingleItem();
        skipped.ClientId.ShouldBe(blockedAgent);
        skipped.Date.ShouldBe(Day2);
        skipped.ShiftId.ShouldBe(tokens[1].ShiftRefId);
        skipped.ReasonKey.ShouldBe(BlockReasonKey);
        outcome.CreatedWorkIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsync_EscalationsAndCapture_CoverOnlyAcceptedRows()
    {
        var acceptedAgent = Guid.NewGuid();
        var blockedAgent = Guid.NewGuid();
        var tokens = new List<CoreToken> { MakeToken(acceptedAgent, Day1), MakeToken(blockedAgent, Day2) };
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = tokens }, null);
        SetPartitionResult(accepted: [0], blocked: [new BlockedRow(1, BlockReasonKey)]);

        IReadOnlyList<Guid>? escalationAgents = null;
        await _softeningRepository.ReplaceForRangeAsync(
            Arg.Do<IReadOnlyList<Guid>>(a => escalationAgents = a),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<WorkSoftening>>(), Arg.Any<CancellationToken>());

        IReadOnlyList<Guid>? capturedWorkIds = null;
        await _captureRepository.AddAsync(Arg.Any<WizardRunCapture>(),
            Arg.Do<IReadOnlyList<Guid>>(ids => capturedWorkIds = ids), Arg.Any<CancellationToken>());

        await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        escalationAgents.ShouldNotBeNull();
        escalationAgents!.ShouldBe([acceptedAgent]);
        capturedWorkIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsync_AllBlocked_MaterialisesNothing_AndKeepsCacheForOverrideRetry()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(Guid.NewGuid(), Day1)] }, null);
        SetPartitionResult(accepted: [], blocked: [new BlockedRow(0, BlockReasonKey)]);

        var outcome = await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        outcome.CreatedWorkIds.ShouldBeEmpty();
        outcome.SkippedPlacements.ShouldHaveSingleItem().ReasonKey.ShouldBe(BlockReasonKey);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
        await _softeningRepository.DidNotReceive().ReplaceForRangeAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<WorkSoftening>>(), Arg.Any<CancellationToken>());
        await _captureRepository.DidNotReceive().AddAsync(
            Arg.Any<WizardRunCapture>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
        _cache.TryGet(jobId, out _, out _, out _, out _, out _).ShouldBeTrue(
            "a fully blocked apply must keep the cached result so a supervisor can retry with an override");
    }

    [Test]
    public async Task ApplyAsync_WarnMode_MaterialisesEverything_AndSurfacesViolations()
    {
        var agent = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(agent, Day1), MakeToken(agent, Day2)] }, null);
        var warning = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Warning,
            ClientId = agent,
            Date = Day1,
            Comment = BlockReasonKey,
        };
        SetPartitionResult(accepted: [0, 1], blocked: [], reportable: [warning]);

        BulkAddWorksCommand? sent = null;
        _mediator.Send(Arg.Do<BulkAddWorksCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = _createdIds });

        var outcome = await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        sent.ShouldNotBeNull();
        sent!.Request.Works.Count.ShouldBe(2);
        outcome.ComplianceViolations.ShouldHaveSingleItem().Comment.ShouldBe(BlockReasonKey);
        outcome.SkippedPlacements.ShouldBeEmpty();
        _cache.TryGet(jobId, out _, out _, out _, out _, out _).ShouldBeFalse(
            "a materialised apply must invalidate the cached result");
    }

    [Test]
    public async Task ApplyAsync_SourceScenarioRun_PassesSourceTokenToPartition()
    {
        var sourceToken = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(Guid.NewGuid(), Day1)] }, sourceToken);
        SetPartitionPassThrough();

        await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        await _partitionService.Received(1).PartitionAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), sourceToken, false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAsync_ForwardsOverrideBlock_AndSurfacesOverrideApplied()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(Guid.NewGuid(), Day1)] }, null);
        SetPartitionResult(accepted: [0], blocked: [], overrideApplied: true);

        var outcome = await _sut.ApplyAsync(jobId, overrideBlock: true, CancellationToken.None);

        outcome.OverrideApplied.ShouldBeTrue();
        await _partitionService.Received(1).PartitionAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAsScenarioAsync_PartitionRunsAfterSlotSoftDelete_WithNewScenarioToken()
    {
        var sourceToken = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(Guid.NewGuid(), Day1)] }, sourceToken);
        _scenarioRepository.GetByTokenAsync(sourceToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { RunGroupId = Guid.NewGuid() });
        SetPartitionPassThrough();

        Guid? partitionToken = null;
        _partitionService
            .PartitionAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Do<Guid?>(t => partitionToken = t),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => new CompliancePartitionResult(
                Enumerable.Range(0, ci.Arg<IReadOnlyList<PlannedWorkRow>>().Count).ToList(), [], [], false));

        var (resource, _) = await _sut.ApplyAsScenarioAsync(jobId, null, overrideBlock: false, CancellationToken.None);

        partitionToken.ShouldBe(resource.Token, "the partition must check the NEW scenario token's clone world");
        partitionToken.ShouldNotBe(sourceToken);

        Received.InOrder(() =>
        {
            _scenarioService.SoftDeleteClonedWorksOnSlotsAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<IReadOnlySet<(Guid ShiftId, DateOnly Date)>>(), Arg.Any<CancellationToken>());
            _partitionService.PartitionAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task ApplyAsScenarioAsync_BlockedRow_IsExcludedFromBulkPayload_AndReportedAsSkipped()
    {
        var acceptedAgent = Guid.NewGuid();
        var blockedAgent = Guid.NewGuid();
        var tokens = new List<CoreToken> { MakeToken(acceptedAgent, Day1), MakeToken(blockedAgent, Day2) };
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = tokens }, null);
        SetPartitionResult(accepted: [0], blocked: [new BlockedRow(1, BlockReasonKey)]);

        BulkAddWorksCommand? sent = null;
        _mediator.Send(Arg.Do<BulkAddWorksCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = _createdIds });

        var (_, outcome) = await _sut.ApplyAsScenarioAsync(jobId, null, overrideBlock: false, CancellationToken.None);

        sent.ShouldNotBeNull();
        sent!.Request.Works.Count.ShouldBe(1);
        sent.Request.Works[0].ClientId.ShouldBe(acceptedAgent);

        var skipped = outcome.SkippedPlacements.ShouldHaveSingleItem();
        skipped.ClientId.ShouldBe(blockedAgent);
        skipped.ReasonKey.ShouldBe(BlockReasonKey);
        outcome.CreatedWorkIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsync_EverythingBlocked_KeepsTheCachedResultForAnOverrideRetry()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(Guid.NewGuid(), Day1)] }, null);
        SetPartitionResult(accepted: [], blocked: [new BlockedRow(0, BlockReasonKey)]);

        var result = await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        result.CreatedWorkIds.ShouldBeEmpty();
        // Without the re-store a supervisor would have to re-run the whole GA just to apply with override.
        _cache.TryGet(jobId, out var scenario, out _, out _, out _, out _).ShouldBeTrue();
        scenario.ShouldNotBeNull();
    }

    [Test]
    public async Task ApplyAsync_BulkAddThrows_PutsTheResultBackAndPropagates()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [MakeToken(Guid.NewGuid(), Day1)] }, null);
        SetPartitionPassThrough();
        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None));

        // A transient failure must not consume the run - retrying should still be possible.
        _cache.TryGet(jobId, out _, out _, out _, out _, out _).ShouldBeTrue();
    }

    private static DataBaseContext BuildInMemoryContext()
        => new(
            new DbContextOptionsBuilder<DataBaseContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            null!);
}
