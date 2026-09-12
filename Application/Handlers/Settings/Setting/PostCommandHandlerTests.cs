// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the settings PostCommandHandler ACTIVE_INDUSTRIES value guard: an invalid value is
/// rejected before anything is persisted (neither AddSetting nor PutSetting is called, no commit),
/// a valid value is created when no setting exists yet and updated when one already exists, and an
/// irrelevant key is never validated against the ACTIVE_INDUSTRIES rule.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Settings;
using Klacks.Api.Application.Interfaces.Klacksy;
using InboxSettingKeys = Klacks.Api.Application.Constants.InboxAvailabilitySettingKeys;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using SettingHandlers = Klacks.Api.Application.Handlers.Settings.Setting;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.Settings.Setting;

[TestFixture]
public class PostCommandHandlerTests
{
    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private INavigationTargetCacheService _navigationTargetCache = null!;
    private SettingHandlers.PostCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.AddSetting(Arg.Any<SettingsEntity>())
            .Returns(ci => ci.Arg<SettingsEntity>());
        _settingsRepository.PutSetting(Arg.Any<SettingsEntity>())
            .Returns(ci => ci.Arg<SettingsEntity>());
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _encryptionService.IsServerOnlySettingType(Arg.Any<string>()).Returns(false);
        _encryptionService.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(1));
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _navigationTargetCache = Substitute.For<INavigationTargetCacheService>();

        _sut = new SettingHandlers.PostCommandHandler(
            _settingsRepository,
            _encryptionService,
            _unitOfWork,
            new SettingValueValidator(),
            _navigationTargetCache,
            NullLogger<SettingHandlers.PostCommandHandler>.Instance);
    }

    private static SettingsEntity BuildSetting(string type, string value)
        => new() { Id = Guid.NewGuid(), Type = type, Value = value };

    // Same reason as in PutCommandHandler: the inbox exists only where an incoming mail server is
    // configured, and the chat fast-path answers from a snapshot that has to be marked stale.
    [Test]
    public async Task Handle_IncomingServerKey_InvalidatesTheNavigationTargetSnapshot()
    {
        _settingsRepository.GetSetting(InboxSettingKeys.All[0]).Returns((SettingsEntity?)null);

        await _sut.Handle(
            new PostCommand(BuildSetting(InboxSettingKeys.All[0], "imap.example.com")),
            CancellationToken.None);

        _navigationTargetCache.Received(1).Invalidate();
    }

    [Test]
    public async Task Handle_UnrelatedKey_DoesNotInvalidateTheNavigationTargetSnapshot()
    {
        _settingsRepository.GetSetting(SettingKeys.DefaultLanguage).Returns((SettingsEntity?)null);

        await _sut.Handle(
            new PostCommand(BuildSetting(SettingKeys.DefaultLanguage, "fr")),
            CancellationToken.None);

        _navigationTargetCache.DidNotReceive().Invalidate();
    }
    [Test]
    public async Task Handle_ActiveIndustriesWithUnknownSlug_ThrowsAndDoesNotPersist()
    {
        _settingsRepository.GetSetting(SettingKeys.ActiveIndustries).Returns((SettingsEntity?)null);
        var command = new PostCommand(BuildSetting(SettingKeys.ActiveIndustries, "garbage"));

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(command, CancellationToken.None));

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(Arg.Any<SettingsEntity>());
        await _settingsRepository.DidNotReceiveWithAnyArgs().PutSetting(Arg.Any<SettingsEntity>());
        await _unitOfWork.DidNotReceiveWithAnyArgs().CompleteAsync();
    }

    [Test]
    public async Task Handle_ActiveIndustriesCustomInLegacyList_ThrowsAndDoesNotPersist()
    {
        _settingsRepository.GetSetting(SettingKeys.ActiveIndustries).Returns((SettingsEntity?)null);
        var command = new PostCommand(BuildSetting(SettingKeys.ActiveIndustries, "custom,healthcare"));

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(command, CancellationToken.None));

        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(Arg.Any<SettingsEntity>());
    }

    [Test]
    public async Task Handle_ActiveIndustriesNoExistingSetting_CreatesNewSetting()
    {
        _settingsRepository.GetSetting(SettingKeys.ActiveIndustries).Returns((SettingsEntity?)null);
        var command = new PostCommand(BuildSetting(SettingKeys.ActiveIndustries, "healthcare,security"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ShouldBe("healthcare,security");
        await _settingsRepository.Received(1).AddSetting(Arg.Any<SettingsEntity>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ActiveIndustriesExistingSetting_UpdatesExistingSetting()
    {
        var existing = BuildSetting(SettingKeys.ActiveIndustries, "logistics");
        _settingsRepository.GetSetting(SettingKeys.ActiveIndustries).Returns(existing);
        var command = new PostCommand(BuildSetting(SettingKeys.ActiveIndustries, "custom"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ShouldBe("custom");
        await _settingsRepository.Received(1).PutSetting(Arg.Any<SettingsEntity>());
        await _settingsRepository.DidNotReceiveWithAnyArgs().AddSetting(Arg.Any<SettingsEntity>());
    }

    [Test]
    public async Task Handle_IrrelevantKeyWithArbitraryValue_IsNeverRejected()
    {
        _settingsRepository.GetSetting(SettingKeys.DefaultLanguage).Returns((SettingsEntity?)null);
        var command = new PostCommand(BuildSetting(SettingKeys.DefaultLanguage, "not-a-slug-either"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        await _settingsRepository.Received(1).AddSetting(Arg.Any<SettingsEntity>());
    }
}
