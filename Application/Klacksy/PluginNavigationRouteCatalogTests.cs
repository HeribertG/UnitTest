// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that PluginNavigationRouteCatalog reads the exact HandlerConfig shape
/// FeaturePluginService.SerializeRoutes writes ({"routes":{"&lt;plugin&gt;":"&lt;route&gt;"}}), and that a
/// missing, empty or malformed config yields no route instead of throwing — navigation must never
/// break because a plugin left garbage behind.
/// </summary>

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Klacksy;

[TestFixture]
public class PluginNavigationRouteCatalogTests
{
    private const string SerializedByFeaturePluginService =
        "{\"routes\":{\"floor-plan\":\"/workplace/floor-plan\",\"messaging\":\"/workplace/messaging\"}}";

    private ISkillRegistry _skillRegistry = null!;
    private PluginNavigationRouteCatalog _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _sut = new PluginNavigationRouteCatalog(_skillRegistry);
    }

    private void WithHandlerConfig(string? handlerConfig) =>
        _skillRegistry.GetSkillByName("navigate_to").Returns(new SkillDescriptor(
            "navigate_to",
            "Navigate",
            SkillCategory.UI,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null)
        {
            HandlerConfig = handlerConfig
        });

    [Test]
    public void GetRoute_ReturnsTheRoute_FeaturePluginServiceRegistered()
    {
        WithHandlerConfig(SerializedByFeaturePluginService);

        _sut.GetRoute("floor-plan").ShouldBe("/workplace/floor-plan");
        _sut.GetRoute("messaging").ShouldBe("/workplace/messaging");
    }

    [Test]
    public void GetRoute_MatchesThePageKeyCaseInsensitively()
    {
        WithHandlerConfig(SerializedByFeaturePluginService);

        _sut.GetRoute("Floor-Plan").ShouldBe("/workplace/floor-plan");
    }

    [Test]
    public void GetRoute_ReturnsNull_ForAPageKeyNoPluginRegistered()
    {
        WithHandlerConfig(SerializedByFeaturePluginService);

        _sut.GetRoute("settings").ShouldBeNull();
    }

    [Test]
    public void GetRoute_ReturnsNull_ForTheSeededEmptyHandlerConfig()
    {
        WithHandlerConfig("{}");

        _sut.GetRoute("floor-plan").ShouldBeNull();
    }

    [Test]
    public void GetRoute_ReturnsNull_ForMalformedJson()
    {
        WithHandlerConfig("{\"routes\": ");

        _sut.GetRoute("floor-plan").ShouldBeNull();
    }

    [Test]
    public void GetRoute_ReturnsNull_WhenTheSkillIsNotInTheRegistry()
    {
        _skillRegistry.GetSkillByName("navigate_to").Returns((SkillDescriptor?)null);

        _sut.GetRoute("floor-plan").ShouldBeNull();
    }

    [Test]
    public void GetRoute_ReturnsNull_ForAnEmptyPageKey()
    {
        WithHandlerConfig(SerializedByFeaturePluginService);

        _sut.GetRoute(string.Empty).ShouldBeNull();
        _skillRegistry.DidNotReceive().GetSkillByName(Arg.Any<string>());
    }
}
