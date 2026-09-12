using System.Globalization;
using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Application.Services.Assistant.Scheduling;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class TriggerErpImportRunCommandHandlerTests
{
    private static readonly TimeSpan RunnerCatchUpWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan BackgroundTickInterval = TimeSpan.FromMinutes(1);

    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private TriggerErpImportRunCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _handler = new TriggerErpImportRunCommandHandler(_settingsRepository, _unitOfWork);
    }

    [Test]
    public async Task Handle_NoExistingSetting_AddsNextRunSettingAfterWriteStabilityWindow()
    {
        _settingsRepository.GetSetting(ErpImportSettingsTypes.NextRunUtc).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        Klacks.Api.Domain.Models.Settings.Settings? added = null;
        await _settingsRepository.AddSetting(Arg.Do<Klacks.Api.Domain.Models.Settings.Settings>(s => added = s));
        _settingsRepository.ClearReceivedCalls();

        var before = DateTime.UtcNow;
        await _handler.Handle(new TriggerErpImportRunCommand(), CancellationToken.None);
        var after = DateTime.UtcNow;

        added.ShouldNotBeNull();
        added.Type.ShouldBe(ErpImportSettingsTypes.NextRunUtc);
        added.Id.ShouldNotBe(Guid.Empty);
        var parsed = ParseRoundtripUtc(added.Value);
        parsed.Kind.ShouldBe(DateTimeKind.Utc);
        parsed.ShouldBeInRange(before + ErpImportStorageTiming.WriteStabilityWindow, after + ErpImportStorageTiming.WriteStabilityWindow);
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<Klacks.Api.Domain.Models.Settings.Settings>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ExistingSetting_UpdatesValueViaPut()
    {
        var existing = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Id = Guid.NewGuid(),
            Type = ErpImportSettingsTypes.NextRunUtc,
            Value = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O")
        };
        _settingsRepository.GetSetting(ErpImportSettingsTypes.NextRunUtc).Returns(existing);

        var before = DateTime.UtcNow;
        await _handler.Handle(new TriggerErpImportRunCommand(), CancellationToken.None);
        var after = DateTime.UtcNow;

        await _settingsRepository.Received(1).PutSetting(existing);
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<Klacks.Api.Domain.Models.Settings.Settings>());
        var parsed = ParseRoundtripUtc(existing.Value);
        parsed.Kind.ShouldBe(DateTimeKind.Utc);
        parsed.ShouldBeInRange(before + ErpImportStorageTiming.WriteStabilityWindow, after + ErpImportStorageTiming.WriteStabilityWindow);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_WrittenValue_MakesNextBackgroundTickFire()
    {
        var existing = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Id = Guid.NewGuid(),
            Type = ErpImportSettingsTypes.NextRunUtc,
            Value = string.Empty
        };
        _settingsRepository.GetSetting(ErpImportSettingsTypes.NextRunUtc).Returns(existing);

        await _handler.Handle(new TriggerErpImportRunCommand(), CancellationToken.None);

        var parsed = ParseRoundtripUtc(existing.Value);
        var nextTick = parsed.Add(BackgroundTickInterval);
        var decision = new ScheduledTaskDuePolicy().Decide(parsed, nextTick, RunnerCatchUpWindow);
        decision.ShouldBe(ScheduledTaskRunDecision.Fire);
    }

    [Test]
    public async Task Handle_TickInsideWriteStabilityWindow_DoesNotFireBeforeUploadedFileIsStable()
    {
        var existing = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Id = Guid.NewGuid(),
            Type = ErpImportSettingsTypes.NextRunUtc,
            Value = string.Empty
        };
        _settingsRepository.GetSetting(ErpImportSettingsTypes.NextRunUtc).Returns(existing);

        var uploadedAtUtc = DateTime.UtcNow;
        await _handler.Handle(new TriggerErpImportRunCommand(), CancellationToken.None);

        var parsed = ParseRoundtripUtc(existing.Value);
        var tickInsideWindow = uploadedAtUtc + ErpImportStorageTiming.WriteStabilityWindow - TimeSpan.FromMilliseconds(1);
        var decision = new ScheduledTaskDuePolicy().Decide(parsed, tickInsideWindow, RunnerCatchUpWindow);
        decision.ShouldBe(ScheduledTaskRunDecision.NotDue);
    }

    private static DateTime ParseRoundtripUtc(string value)
    {
        var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }
}
