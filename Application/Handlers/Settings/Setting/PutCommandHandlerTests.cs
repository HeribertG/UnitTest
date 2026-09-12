// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the settings PutCommandHandler dispatch of SurchargeSettingsChangedEvent: a
/// surcharge-relevant key whose stored value actually changed dispatches exactly one event after the
/// commit, an irrelevant key or an unchanged value dispatches nothing, a previously missing relevant
/// setting counts as changed, and a dispatcher failure never breaks the committed update. Also
/// covers the ACTIVE_INDUSTRIES value guard: an invalid value is rejected before anything is
/// persisted, a valid value passes through unchanged.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Settings;
using Klacks.Api.Application.Interfaces.Klacksy;
using InboxSettingKeys = Klacks.Api.Application.Constants.InboxAvailabilitySettingKeys;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using SettingHandlers = Klacks.Api.Application.Handlers.Settings.Setting;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.Settings.Setting;

[TestFixture]
public class PutCommandHandlerTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private INavigationTargetCacheService _navigationTargetCache = null!;
    private SettingHandlers.PutCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.PutSetting(Arg.Any<SettingsEntity>())
            .Returns(ci => ci.Arg<SettingsEntity>());
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _encryptionService.IsServerOnlySettingType(Arg.Any<string>()).Returns(false);
        _encryptionService.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(1));
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
        _navigationTargetCache = Substitute.For<INavigationTargetCacheService>();

        _sut = new SettingHandlers.PutCommandHandler(
            _settingsRepository,
            _encryptionService,
            _unitOfWork,
            _eventDispatcher,
            new SettingValueValidator(),
            _navigationTargetCache,
            NullLogger<SettingHandlers.PutCommandHandler>.Instance);
    }

    private static SettingsEntity BuildSetting(string type, string value)
        => new() { Id = Guid.NewGuid(), Type = type, Value = value };

    // The inbox page exists only where an incoming mail server is configured, and the chat fast-path
    // answers from a snapshot - so a write to one of those keys has to mark that snapshot stale or the
    // page stays offered (or stays hidden) for up to the cache TTL.
    [Test]
    public async Task Handle_IncomingServerKey_InvalidatesTheNavigationTargetSnapshot()
    {
        await _sut.Handle(
            new PutCommand(BuildSetting(InboxSettingKeys.All[0], "imap.example.com")),
            CancellationToken.None);

        _navigationTargetCache.Received(1).Invalidate();
    }

    [Test]
    public async Task Handle_UnrelatedKey_DoesNotInvalidateTheNavigationTargetSnapshot()
    {
        await _sut.Handle(
            new PutCommand(BuildSetting(SettingKeys.DefaultLanguage, "fr")),
            CancellationToken.None);

        _navigationTargetCache.DidNotReceive().Invalidate();
    }
    [Test]
    public async Task Handle_RelevantKeyWithChangedValue_DispatchesSurchargeSettingsChangedEvent()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.NightRate)
            .Returns(BuildSetting(SettingKeys.NightRate, "1.25"));

        await _sut.Handle(new PutCommand(BuildSetting(SettingKeys.NightRate, "1.5")), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<SurchargeSettingsChangedEvent>(e => e.ChangedKeys.Count == 1 && e.ChangedKeys.Contains(SettingKeys.NightRate)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RelevantKeyPreviouslyMissing_CountsAsChangedAndDispatches()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.OvertimeTier1Rate)
            .Returns((SettingsEntity?)null);

        await _sut.Handle(new PutCommand(BuildSetting(SettingKeys.OvertimeTier1Rate, "1.25")), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<SurchargeSettingsChangedEvent>(e => e.ChangedKeys.Contains(SettingKeys.OvertimeTier1Rate)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RelevantKeyWithUnchangedValue_DoesNotDispatch()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.NightRate)
            .Returns(BuildSetting(SettingKeys.NightRate, "1.5"));

        await _sut.Handle(new PutCommand(BuildSetting(SettingKeys.NightRate, "1.5")), CancellationToken.None);

        await _eventDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync((IDomainEvent)null!, default);
    }

    [Test]
    public async Task Handle_IrrelevantKey_DoesNotDispatchEvenWhenValueChanged()
    {
        await _sut.Handle(new PutCommand(BuildSetting(SettingKeys.DefaultLanguage, "fr")), CancellationToken.None);

        await _settingsRepository.Received(1).PutSetting(Arg.Any<SettingsEntity>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync((IDomainEvent)null!, default);
    }

    [Test]
    public async Task Handle_DispatcherFailure_IsSwallowedAndUpdateResultIsReturned()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.NightRate)
            .Returns(BuildSetting(SettingKeys.NightRate, "1.25"));
        _eventDispatcher.DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var result = await _sut.Handle(new PutCommand(BuildSetting(SettingKeys.NightRate, "1.5")), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ShouldBe("1.5");
    }

    [Test]
    public async Task Handle_ActiveIndustriesWithUnknownSlug_ThrowsAndDoesNotPersist()
    {
        var command = new PutCommand(BuildSetting(SettingKeys.ActiveIndustries, "not-a-slug"));

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(command, CancellationToken.None));

        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(Arg.Any<SettingsEntity>());
        await _unitOfWork.DidNotReceiveWithAnyArgs().CompleteAsync();
    }

    [Test]
    public async Task Handle_ActiveIndustriesWithKnownSlug_Persists()
    {
        var command = new PutCommand(BuildSetting(SettingKeys.ActiveIndustries, "healthcare"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ShouldBe("healthcare");
        await _settingsRepository.Received(1).PutSetting(Arg.Any<SettingsEntity>());
    }

    [Test]
    public async Task Handle_ActiveIndustriesChanged_DispatchesActiveIndustriesChangedEventWithBothValues()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.ActiveIndustries)
            .Returns(BuildSetting(SettingKeys.ActiveIndustries, "homecare"));
        var command = new PutCommand(BuildSetting(SettingKeys.ActiveIndustries, "healthcare"));

        await _sut.Handle(command, CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<ActiveIndustriesChangedEvent>(e => e.PreviousValue == "homecare" && e.CurrentValue == "healthcare"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ActiveIndustriesUnchanged_DispatchesNothing()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.ActiveIndustries)
            .Returns(BuildSetting(SettingKeys.ActiveIndustries, "healthcare"));
        var command = new PutCommand(BuildSetting(SettingKeys.ActiveIndustries, "healthcare"));

        await _sut.Handle(command, CancellationToken.None);

        await _eventDispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<ActiveIndustriesChangedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ActiveIndustriesDispatchFails_StillReturnsThePersistedSetting()
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.ActiveIndustries)
            .Returns(BuildSetting(SettingKeys.ActiveIndustries, "homecare"));
        _eventDispatcher
            .DispatchAsync(Arg.Any<ActiveIndustriesChangedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("dispatch down")));
        var command = new PutCommand(BuildSetting(SettingKeys.ActiveIndustries, "healthcare"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
