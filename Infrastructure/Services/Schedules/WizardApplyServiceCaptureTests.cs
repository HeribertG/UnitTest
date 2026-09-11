// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Works;
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
public class WizardApplyServiceCaptureTests
{
    private WizardResultCache _cache = null!;
    private IMediator _mediator = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkSofteningRepository _softeningRepository = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private ICompliancePartitionService _partitionService = null!;
    private WizardApplyService _sut = null!;

    private readonly List<Guid> _createdIds = new() { Guid.NewGuid(), Guid.NewGuid() };

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

        _partitionService
            .PartitionAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => new CompliancePartitionResult(
                Enumerable.Range(0, ci.Arg<IReadOnlyList<PlannedWorkRow>>().Count).ToList(),
                [],
                [],
                OverrideApplied: false));

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

    private static CoreToken MakeToken()
    {
        var date = new DateOnly(2026, 4, 20);
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
            AgentId: Guid.NewGuid().ToString());
    }

    private void StoreScenario(Guid jobId, string subScoreJson = "{\"engine\":\"tokenEvolution\"}", int stage0 = 4)
    {
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken()] };
        _cache.Store(jobId, scenario, null, null, subScoreJson, stage0);
    }

    [Test]
    public async Task ApplyAsync_WritesDirectCaptureWithCreatedWorkIds()
    {
        var jobId = Guid.NewGuid();
        StoreScenario(jobId, subScoreJson: "SCORE", stage0: 7);

        WizardRunCapture? captured = null;
        IReadOnlyList<Guid>? capturedWorkIds = null;
        await _captureRepository.AddAsync(Arg.Do<WizardRunCapture>(c => captured = c),
            Arg.Do<IReadOnlyList<Guid>>(ids => capturedWorkIds = ids),
            Arg.Any<CancellationToken>());

        var result = await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        result.CreatedWorkIds.ShouldBe(_createdIds);
        captured.ShouldNotBeNull();
        captured!.Engine.ShouldBe(WizardEngine.TokenEvolution);
        captured.ApplyKind.ShouldBe(WizardApplyKind.Direct);
        captured.ScenarioId.ShouldBeNull();
        captured.RunGroupId.ShouldBeNull();
        captured.ChurnAtApply.ShouldBeNull();
        captured.SubScoreJson.ShouldBe("SCORE");
        captured.Stage0Violations.ShouldBe(7);
        captured.JobId.ShouldBe(jobId);
        capturedWorkIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsync_CaptureFailureDoesNotBreakApply()
    {
        var jobId = Guid.NewGuid();
        StoreScenario(jobId);
        _captureRepository
            .AddAsync(Arg.Any<WizardRunCapture>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        var result = await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        result.CreatedWorkIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsScenarioAsync_WritesScenarioCaptureWithScenarioId()
    {
        var jobId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var scenarioId = Guid.NewGuid();
        StoreScenario(jobId);

        _scenarioService.CloneScenarioDataAsync(Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid>());
        _scenarioRepository.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());
        _scenarioRepository.When(r => r.Add(Arg.Any<AnalyseScenario>()))
            .Do(ci => ci.Arg<AnalyseScenario>().Id = scenarioId);

        WizardRunCapture? captured = null;
        await _captureRepository.AddAsync(Arg.Do<WizardRunCapture>(c => captured = c),
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());

        var (resource, outcome) = await _sut.ApplyAsScenarioAsync(jobId, groupId, overrideBlock: false, CancellationToken.None);

        outcome.CreatedWorkIds.ShouldBe(_createdIds);
        captured.ShouldNotBeNull();
        captured!.ApplyKind.ShouldBe(WizardApplyKind.Scenario);
        captured.ScenarioId.ShouldBe(scenarioId);
        captured.GroupId.ShouldBe(groupId);
        captured.RunGroupId.ShouldNotBeNull();
        captured.Engine.ShouldBe(WizardEngine.TokenEvolution);

        await _captureRepository.DidNotReceiveWithAnyArgs().SupersedeOpenDirectCapturesAsync(
            default, default, default, default, default);
    }

    [Test]
    public async Task ApplyAsync_SupersedesOpenDirectCapturesOfTheSamePeriod()
    {
        var jobId = Guid.NewGuid();
        StoreScenario(jobId);

        WizardRunCapture? captured = null;
        await _captureRepository.AddAsync(Arg.Do<WizardRunCapture>(c => captured = c),
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());

        await _sut.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        captured.ShouldNotBeNull();
        await _captureRepository.Received(1).SupersedeOpenDirectCapturesAsync(
            captured!.Id,
            WizardEngine.TokenEvolution,
            captured.PeriodFrom,
            captured.PeriodUntil,
            Arg.Any<CancellationToken>());
    }

    private static DataBaseContext BuildInMemoryContext()
        => new(
            new DbContextOptionsBuilder<DataBaseContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            null!);
}
