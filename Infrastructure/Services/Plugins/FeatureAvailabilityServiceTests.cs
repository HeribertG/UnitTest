// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the service mirrors the two Angular feature guards: a plugin page needs the plugin
/// installed AND enabled (never the operational check, which the guard does not consult either), the
/// inbox delegates to the incoming-server configuration, and a name no gate knows is refused rather
/// than assumed present.
/// </summary>

using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Infrastructure.Services.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Plugins;

[TestFixture]
public class FeatureAvailabilityServiceTests
{
    private const string PluginName = "messaging";

    private IFeaturePluginService _plugins = null!;
    private IInboxAvailabilityService _inbox = null!;
    private FeatureAvailabilityService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _plugins = Substitute.For<IFeaturePluginService>();
        _inbox = Substitute.For<IInboxAvailabilityService>();
        _sut = new FeatureAvailabilityService(
            _plugins, _inbox, NullLogger<FeatureAvailabilityService>.Instance);
    }

    [Test]
    public async Task ReturnsTrue_WhenThePluginIsInstalledAndEnabled()
    {
        _plugins.IsDiscovered(PluginName).Returns(true);
        _plugins.IsInstalled(PluginName).Returns(true);
        _plugins.IsEnabled(PluginName).Returns(true);

        (await _sut.IsAvailableAsync(PluginName)).ShouldBeTrue();
    }

    [Test]
    public async Task ReturnsFalse_WhenThePluginIsInstalledButDisabled()
    {
        _plugins.IsDiscovered(PluginName).Returns(true);
        _plugins.IsInstalled(PluginName).Returns(true);
        _plugins.IsEnabled(PluginName).Returns(false);

        (await _sut.IsAvailableAsync(PluginName)).ShouldBeFalse();
    }

    [Test]
    public async Task ReturnsFalse_WhenThePluginIsEnabledButNotInstalled()
    {
        _plugins.IsDiscovered(PluginName).Returns(true);
        _plugins.IsInstalled(PluginName).Returns(false);
        _plugins.IsEnabled(PluginName).Returns(true);

        (await _sut.IsAvailableAsync(PluginName)).ShouldBeFalse();
    }

    [Test]
    public async Task ReturnsFalse_AndNeverAsksThePlugins_WhenTheNameIsUnknown()
    {
        _plugins.IsDiscovered("typo-feature").Returns(false);

        (await _sut.IsAvailableAsync("typo-feature")).ShouldBeFalse();

        _plugins.DidNotReceive().IsInstalled(Arg.Any<string>());
        _plugins.DidNotReceive().IsEnabled(Arg.Any<string>());
    }

    [Test]
    public async Task DelegatesTheInboxToTheIncomingServerConfiguration()
    {
        _inbox.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await _sut.IsAvailableAsync(KlacksyFeatures.Inbox)).ShouldBeFalse();

        _inbox.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        (await _sut.IsAvailableAsync(KlacksyFeatures.Inbox)).ShouldBeTrue();

        _plugins.DidNotReceive().IsDiscovered(Arg.Any<string>());
    }

    [Test]
    public async Task NeverConsultsTheOperationalCheckThatTheAngularGuardIgnores()
    {
        _plugins.IsDiscovered(PluginName).Returns(true);
        _plugins.IsInstalled(PluginName).Returns(true);
        _plugins.IsEnabled(PluginName).Returns(true);

        (await _sut.IsAvailableAsync(PluginName)).ShouldBeTrue();

        await _plugins.DidNotReceive().GetPluginAsync(Arg.Any<string>());
        await _plugins.DidNotReceive().GetAllPluginsAsync();
    }
}
