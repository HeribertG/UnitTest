// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class HarmonizerApplyServiceCaptureTests
{
    private static readonly DateOnly D = new(2026, 4, 20);

    private DataBaseContext _context = null!;
    private HarmonizerResultCache _cache = null!;
    private IMediator _mediator = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private IScenarioComplianceService _scenarioComplianceService = null!;
    private IScheduleTimelineService _timelineService = null!;
    private IScheduleSnapshotMarkerService _snapshotMarkerService = null!;

    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly List<Guid> _createdIds = new() { Guid.NewGuid(), Guid.NewGuid() };

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = Guid.NewGuid(),
            ShiftId = _shiftId,
            CurrentDate = D,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
        });
        _context.SaveChanges();

        _cache = new HarmonizerResultCache();
        _mediator = Substitute.For<IMediator>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        // The real unit of work runs the delegate inside a transaction; the substitute must do the same
        // or the apply would silently produce nothing.
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<(IReadOnlyList<Guid> Ids, Guid Token, AnalyseScenario Scenario)>>>())
            .Returns(ci => ci.Arg<Func<Task<(IReadOnlyList<Guid> Ids, Guid Token, AnalyseScenario Scenario)>>>()());
        _captureRepository = Substitute.For<IWizardRunCaptureRepository>();
        _scenarioComplianceService = Substitute.For<IScenarioComplianceService>();
        _scenarioComplianceService
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));
        _timelineService = Substitute.For<IScheduleTimelineService>();
        _snapshotMarkerService = Substitute.For<IScheduleSnapshotMarkerService>();

        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = _createdIds });
        _scenarioService.CloneScenarioDataWithMapsAsync(Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<Guid, Guid> { [_shiftId] = Guid.NewGuid() }, new Dictionary<Guid, Guid>()));
        _scenarioRepository.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());
        _scenarioRepository.GetByTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { RunGroupId = Guid.NewGuid() });
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private HarmonizerApplyService BuildHarmonizerSut() => new(
        _cache, _mediator, _scenarioRepository, _scenarioService, _unitOfWork, _context, _captureRepository,
        _scenarioComplianceService, _timelineService, _snapshotMarkerService,
        new FixedCompanyClock(new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero)),
        NullLogger<HarmonizerApplyService>.Instance);

    private HolisticHarmonizerApplyService BuildHolisticSut() => new(
        _cache, _mediator, _scenarioRepository, _scenarioService, _unitOfWork, _context, _captureRepository,
        _scenarioComplianceService, _timelineService, _snapshotMarkerService,
        new FixedCompanyClock(new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero)),
        NullLogger<HarmonizerApplyService>.Instance);

    private HarmonyBitmap BestBitmap()
    {
        var cells = new Cell[1, 1];
        cells[0, 0] = new Cell(CellSymbol.Early, _shiftId, [_workId], false,
            D.ToDateTime(new TimeOnly(8, 0)), D.ToDateTime(new TimeOnly(16, 0)), 8m);
        return new HarmonyBitmap([new BitmapAgent(Guid.NewGuid().ToString(), "A", 8m, new HashSet<CellSymbol>())], [D], cells);
    }

    private Guid StoreScenarioSourceResult(Guid scenarioId)
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, BestBitmap(), BestBitmap(), sourceAnalyseToken: Guid.NewGuid(), subScoreJson: "SCORE", stage0Violations: 3);
        _scenarioRepository.When(r => r.Add(Arg.Any<AnalyseScenario>()))
            .Do(ci => ci.Arg<AnalyseScenario>().Id = scenarioId);
        return jobId;
    }

    [Test]
    public async Task ApplyAsScenarioAsync_WritesHarmonizerCapture()
    {
        var scenarioId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var jobId = StoreScenarioSourceResult(scenarioId);

        WizardRunCapture? captured = null;
        IReadOnlyList<Guid>? capturedWorkIds = null;
        await _captureRepository.AddAsync(Arg.Do<WizardRunCapture>(c => captured = c),
            Arg.Do<IReadOnlyList<Guid>>(ids => capturedWorkIds = ids), Arg.Any<CancellationToken>());

        var (_, createdIds, _) = await BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, groupId, CancellationToken.None);

        createdIds.ShouldBe(_createdIds);
        captured.ShouldNotBeNull();
        captured!.Engine.ShouldBe(WizardEngine.Harmonizer);
        captured.ApplyKind.ShouldBe(WizardApplyKind.Scenario);
        captured.ScenarioId.ShouldBe(scenarioId);
        captured.GroupId.ShouldBe(groupId);
        captured.RunGroupId.ShouldNotBeNull();
        captured.SubScoreJson.ShouldBe("SCORE");
        captured.Stage0Violations.ShouldBe(3);
        captured.ChurnAtApply.ShouldNotBeNull();
        capturedWorkIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsScenarioAsync_HolisticSubclass_WritesHolisticCapture()
    {
        var jobId = StoreScenarioSourceResult(Guid.NewGuid());

        WizardRunCapture? captured = null;
        await _captureRepository.AddAsync(Arg.Do<WizardRunCapture>(c => captured = c),
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());

        await BuildHolisticSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Engine.ShouldBe(WizardEngine.Holistic);
    }

    [Test]
    public async Task ApplyAsScenarioAsync_CaptureRunFalse_SkipsCapture()
    {
        var jobId = StoreScenarioSourceResult(Guid.NewGuid());

        await BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None, namePrefixOverride: null, captureRun: false);

        await _captureRepository.DidNotReceive().AddAsync(
            Arg.Any<WizardRunCapture>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAsScenarioAsync_CaptureFailureDoesNotBreakApply()
    {
        var jobId = StoreScenarioSourceResult(Guid.NewGuid());
        _captureRepository
            .AddAsync(Arg.Any<WizardRunCapture>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        var (_, createdIds, _) = await BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None);

        createdIds.ShouldBe(_createdIds);
    }

    [Test]
    public async Task ApplyAsScenarioAsync_EvaluatesComplianceByDefault_AndReturnsReport()
    {
        var jobId = StoreScenarioSourceResult(Guid.NewGuid());
        var report = new ScenarioComplianceReport([], []);
        _scenarioComplianceService
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(report);

        var (_, _, complianceReport) = await BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None);

        complianceReport.ShouldBe(report);
        await _scenarioComplianceService.Received(1).EvaluateAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAsScenarioAsync_EvaluateComplianceFalse_SkipsEvaluationAndReturnsNull()
    {
        var jobId = StoreScenarioSourceResult(Guid.NewGuid());

        var (_, _, complianceReport) = await BuildHarmonizerSut().ApplyAsScenarioAsync(
            jobId, Guid.NewGuid(), CancellationToken.None, namePrefixOverride: null, captureRun: true, evaluateCompliance: false);

        complianceReport.ShouldBeNull();
        await _scenarioComplianceService.DidNotReceive().EvaluateAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static ScheduleSnapshotMarker Marker(int works, int breaks, string hash)
        => new(D, D, [Guid.NewGuid()], null, works, breaks, hash);

    [Test]
    public async Task ApplyAsScenarioAsync_ScheduleChangedSinceTheRun_ThrowsStaleAndKeepsTheResult()
    {
        var jobId = Guid.NewGuid();
        var marker = Marker(works: 4, breaks: 1, hash: "AAAA");
        _cache.Store(jobId, BestBitmap(), BestBitmap(), sourceAnalyseToken: null, snapshotMarker: marker);
        _snapshotMarkerService
            .ComputeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Marker(works: 5, breaks: 1, hash: "BBBB"));

        var ex = await Should.ThrowAsync<StaleWizardResultException>(
            () => BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None));

        ex.ExpectedWorkCount.ShouldBe(4);
        ex.ActualWorkCount.ShouldBe(5);
        // The cached result survives so the operator can re-run or repeat the apply deliberately.
        _cache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeTrue();
    }

    [Test]
    public async Task ApplyAsScenarioAsync_OnlyPlacementMoved_IsStillReportedAsStale()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, BestBitmap(), BestBitmap(), sourceAnalyseToken: null,
            snapshotMarker: Marker(works: 4, breaks: 1, hash: "AAAA"));
        _snapshotMarkerService
            .ComputeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Marker(works: 4, breaks: 1, hash: "MOVED"));

        var ex = await Should.ThrowAsync<StaleWizardResultException>(
            () => BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None));

        ex.PlacementChanged.ShouldBeTrue();
    }

    [Test]
    public async Task ApplyAsScenarioAsync_UnchangedSchedule_AppliesNormally()
    {
        var jobId = Guid.NewGuid();
        var marker = Marker(works: 4, breaks: 1, hash: "AAAA");
        _cache.Store(jobId, BestBitmap(), BestBitmap(), sourceAnalyseToken: null, snapshotMarker: marker);
        _snapshotMarkerService
            .ComputeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(marker);

        await Should.NotThrowAsync(
            () => BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None));

        // The entry was consumed, so the apply really went through instead of being put back.
        _cache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public async Task ApplyAsScenarioAsync_WithoutMarker_SkipsTheStalenessCheck()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, BestBitmap(), BestBitmap(), sourceAnalyseToken: null);

        await BuildHarmonizerSut().ApplyAsScenarioAsync(jobId, Guid.NewGuid(), CancellationToken.None);

        await _snapshotMarkerService.DidNotReceiveWithAnyArgs()
            .ComputeAsync(default, default, default!, default, default);
    }
}

