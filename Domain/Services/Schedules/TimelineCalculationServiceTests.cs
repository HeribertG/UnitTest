using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class TimelineCalculationServiceTests
{
    private static readonly DateOnly BaseDate = new(2026, 3, 15);
    private TimelineCalculationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _service = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);
    }

    private Work CreateWork(TimeOnly start, TimeOnly end, Guid? clientId = null, Guid? id = null, DateOnly? date = null)
    {
        return new Work
        {
            Id = id ?? Guid.NewGuid(),
            ClientId = clientId ?? Guid.NewGuid(),
            CurrentDate = date ?? BaseDate,
            StartTime = start,
            EndTime = end,
            ShiftId = Guid.NewGuid()
        };
    }

    [Test]
    public void CalculateScheduleBlocks_NormalDayShift_ReturnsOneBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].BlockType.ShouldBe(ScheduleBlockType.Work);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        blocks[0].End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(16, 0)));
        blocks[0].Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void CalculateScheduleBlocks_NightShift_ReturnsOneUnsplitBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(22, 0), new TimeOnly(6, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(22, 0)));
        blocks[0].End.ShouldBe(BaseDate.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        blocks[0].Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void CalculateScheduleBlocks_WithCorrectionStart_WorkBlockKeepsOriginalStart()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionStart,
            ChangeTime = 1m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);

        // Assert: Work block stays at original times - no overlap with correction block
        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        workBlock.End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(16, 0)));

        // Correction block precedes the work block
        var correctionBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Correction);
        correctionBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(7, 0)));
        correctionBlock.End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_WithCorrectionEnd_WorkBlockKeepsOriginalEnd()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 1m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);

        // Assert: Work block stays at original times - no overlap with correction block
        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        workBlock.End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(16, 0)));

        // Correction block follows the work block
        var correctionBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Correction);
        correctionBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(16, 0)));
        correctionBlock.End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(17, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_WithReplacement_CreatesReplacementBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var replaceClientId = Guid.NewGuid();
        var replacement = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementStart,
            ChangeTime = 4m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            ReplaceClientId = replaceClientId
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        // Assert
        var replacementBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Replacement);
        replacementBlock.ClientId.ShouldBe(replaceClientId);
        replacementBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        replacementBlock.End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(12, 0)));

        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(12, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_BreakEntry_CreatesBreakBlock()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = BaseDate,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            AbsenceId = Guid.NewGuid()
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([], [], [breakEntry]);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].BlockType.ShouldBe(ScheduleBlockType.Break);
        blocks[0].ClientId.ShouldBe(clientId);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(12, 0)));
        blocks[0].End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(13, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_ShiftEndingAtMidnight_EndsAtNextDayMidnight()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(14, 0), new TimeOnly(0, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(14, 0)));
        blocks[0].End.ShouldBe(BaseDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
        blocks[0].Duration.ShouldBe(TimeSpan.FromHours(10));
    }

    [Test]
    public void CalculateScheduleBlocks_ShiftStartingAtMidnight_StartsAtMidnight()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(0, 0), new TimeOnly(8, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(TimeOnly.MinValue));
        blocks[0].End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
        blocks[0].Duration.ShouldBe(TimeSpan.FromHours(8));
    }

    [Test]
    public void CalculateScheduleBlocks_MultipleWorks_ReturnsAllBlocks()
    {
        // Arrange
        var work1 = CreateWork(new TimeOnly(6, 0), new TimeOnly(14, 0));
        var work2 = CreateWork(new TimeOnly(14, 0), new TimeOnly(22, 0));

        // Act
        var blocks = _service.CalculateScheduleBlocks([work1, work2], [], []);

        // Assert
        blocks.Count().ShouldBe(2);
        foreach (var _item in blocks) { _item.BlockType.ShouldBe(ScheduleBlockType.Work); };
    }

    [Test]
    public void CalculateScheduleBlocks_WorkWithNoChanges_UsesOriginalTimes()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var work = CreateWork(new TimeOnly(7, 30), new TimeOnly(15, 45), clientId);

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].SourceId.ShouldBe(work.Id);
        blocks[0].ClientId.ShouldBe(clientId);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(7, 30)));
        blocks[0].End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(15, 45)));
    }

    [Test]
    public void CalculateScheduleBlocks_ReplacementWithoutClientId_NoReplacementBlock()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var replacement = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementEnd,
            ChangeTime = 2m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            ReplaceClientId = null
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [replacement], []);

        // Assert
        blocks.ShouldNotContain(b => b.BlockType == ScheduleBlockType.Replacement);
    }

    [Test]
    public void CalculateScheduleBlocks_EmptyInputs_ReturnsEmpty()
    {
        // Arrange & Act
        var blocks = _service.CalculateScheduleBlocks([], [], []);

        // Assert
        blocks.ShouldBeEmpty();
    }

    [Test]
    public void CalculateScheduleBlocks_CorrectionStart_NoFalseCollisionWithOwnWork()
    {
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionStart,
            ChangeTime = 1m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };

        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);
        var timeline = new ClientTimeline(work.ClientId);
        timeline.AddBlocks(blocks.Where(b => b.ClientId == work.ClientId));
        timeline.SortBlocks();

        timeline.GetCollisions().ShouldBeEmpty("work block and its own correction must not collide");
    }

    [Test]
    public void CalculateScheduleBlocks_CorrectionEnd_NoFalseCollisionWithOwnWork()
    {
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 1m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };

        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);
        var timeline = new ClientTimeline(work.ClientId);
        timeline.AddBlocks(blocks.Where(b => b.ClientId == work.ClientId));
        timeline.SortBlocks();

        timeline.GetCollisions().ShouldBeEmpty("work block and its own correction must not collide");
    }

    [Test]
    public void CalculateScheduleBlocks_StackedBeforeShift_CorrectOrder()
    {
        // Arrange: Work 08:00-16:00, TravelStart=2h + Briefing=0.5h
        // Expected: TravelStart 05:30-07:30, Briefing 07:30-08:00
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var travelStart = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.TravelStart,
            ChangeTime = 2m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };
        var briefing = new WorkChange
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            WorkId = work.Id,
            Type = WorkChangeType.Briefing,
            ChangeTime = 0.5m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [briefing, travelStart], []);

        // Assert
        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));

        var correctionBlocks = blocks.Where(b => b.BlockType == ScheduleBlockType.Correction)
            .OrderBy(b => b.Start).ToList();
        correctionBlocks.Count().ShouldBe(2);

        // TravelStart outermost: 05:30-07:30
        correctionBlocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(5, 30)));
        correctionBlocks[0].End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(7, 30)));

        // Briefing innermost: 07:30-08:00
        correctionBlocks[1].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(7, 30)));
        correctionBlocks[1].End.ShouldBe(BaseDate.ToDateTime(new TimeOnly(8, 0)));
    }

    [Test]
    public void CalculateScheduleBlocks_NightBreak_CrossesMidnight()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = BaseDate,
            StartTime = new TimeOnly(23, 0),
            EndTime = new TimeOnly(1, 0),
            AbsenceId = Guid.NewGuid()
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([], [], [breakEntry]);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(23, 0)));
        blocks[0].End.ShouldBe(BaseDate.AddDays(1).ToDateTime(new TimeOnly(1, 0)));
        blocks[0].Duration.ShouldBe(TimeSpan.FromHours(2));
    }

    [Test]
    public void CalculateScheduleBlocks_WorkBlock_ContainsShiftId()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            CurrentDate = BaseDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ShiftId = shiftId
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [], []);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].ShiftId.ShouldBe(shiftId);
    }

    [Test]
    public void CalculateScheduleBlocks_BreakBlock_HasNoShiftId()
    {
        // Arrange
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            CurrentDate = BaseDate,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            AbsenceId = Guid.NewGuid()
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([], [], [breakEntry]);

        // Assert
        blocks.Count().ShouldBe(1);
        blocks[0].ShiftId.ShouldBeNull();
    }

    [Test]
    public void CalculateScheduleBlocks_CorrectionBlock_HasNoShiftId()
    {
        // Arrange
        var work = CreateWork(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var correction = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionStart,
            ChangeTime = 1m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue
        };

        // Act
        var blocks = _service.CalculateScheduleBlocks([work], [correction], []);

        // Assert
        var correctionBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Correction);
        correctionBlock.ShiftId.ShouldBeNull();

        var workBlock = blocks.First(b => b.BlockType == ScheduleBlockType.Work);
        workBlock.ShiftId.ShouldNotBeNull();
    }

    [Test]
    public void Constructor_DstAwareWithEmptyTimeZoneId_LogsErrorAndTreatsDstAwareAsOff()
    {
        var logger = new RecordingLogger<TimelineCalculationService>();
        var service = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions { DstAware = true, TimeZoneId = "" }),
            logger);

        var work = CreateWork(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var blocks = service.CalculateScheduleBlocks([work], [], []);

        blocks[0].Start.Kind.ShouldBe(DateTimeKind.Unspecified);
        blocks[0].Start.ShouldBe(BaseDate.ToDateTime(new TimeOnly(22, 0)));
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error);
    }
}
