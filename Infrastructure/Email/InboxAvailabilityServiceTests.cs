// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the backend copy of the Angular InboxVisibilityService condition: server, user name and
/// password all present. A drift here makes Klacksy offer a page the router guard then refuses, or
/// refuse one a click would open.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Email;

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Email;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingKeys = Klacks.Api.Application.Constants.Settings;

[TestFixture]
public class InboxAvailabilityServiceTests
{
    private ISettingsReader _settingsReader = null!;
    private InboxAvailabilityService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _sut = new InboxAvailabilityService(_settingsReader);
    }

    private void StoredSettings(params (string Key, string Value)[] settings) =>
        _settingsReader
            .GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, string>>(
                _ => settings.ToDictionary(s => s.Key, s => s.Value));

    [Test]
    public async Task IsAvailableAsync_WithServerUsernameAndPassword_IsTrue()
    {
        StoredSettings(
            (SettingKeys.APP_INCOMING_SERVER, "imap.example.com"),
            (SettingKeys.APP_INCOMING_SERVER_USERNAME, "mailbox"),
            (SettingKeys.APP_INCOMING_SERVER_PASSWORD, "encrypted-blob"));

        (await _sut.IsAvailableAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task IsAvailableAsync_WithNoSettingsAtAll_IsFalse()
    {
        StoredSettings();

        (await _sut.IsAvailableAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task IsAvailableAsync_WithoutAPassword_IsFalse()
    {
        StoredSettings(
            (SettingKeys.APP_INCOMING_SERVER, "imap.example.com"),
            (SettingKeys.APP_INCOMING_SERVER_USERNAME, "mailbox"));

        (await _sut.IsAvailableAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task IsAvailableAsync_WithABlankServer_IsFalse()
    {
        StoredSettings(
            (SettingKeys.APP_INCOMING_SERVER, "   "),
            (SettingKeys.APP_INCOMING_SERVER_USERNAME, "mailbox"),
            (SettingKeys.APP_INCOMING_SERVER_PASSWORD, "encrypted-blob"));

        (await _sut.IsAvailableAsync()).ShouldBeFalse();
    }
}
