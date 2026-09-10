// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class NavigationEntityRouteGuardTests
{
    private const string EditAddressRoute = "/workplace/edit-address";
    private const string SettingsRoute = "/workplace/settings";

    private NavigationEntityRouteGuard _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var entries = new List<KlacksyPageKeyEntry>
        {
            new("new-employee", EditAddressRoute, null, HasEntityParam: false),
            new("edit-employee", EditAddressRoute, null, HasEntityParam: true),
            new("settings", SettingsRoute, null, HasEntityParam: false),
        };
        var catalog = Substitute.For<IKlacksyPageKeyCatalog>();
        catalog.All.Returns(entries);
        catalog.GetByPageKey(Arg.Any<string>())
            .Returns(call => entries.FirstOrDefault(e => e.PageKey == call.Arg<string>()));
        _sut = new NavigationEntityRouteGuard(catalog);
    }

    [Test]
    public void Editor_page_that_expects_an_entity_id_requires_an_entity()
    {
        _sut.RequiresEntity("edit-employee", EditAddressRoute).ShouldBeTrue();
    }

    [Test]
    public void Creation_page_on_the_same_editor_route_does_not_require_an_entity()
    {
        _sut.RequiresEntity("new-employee", EditAddressRoute).ShouldBeFalse();
    }

    [Test]
    public void In_page_target_inside_an_entity_editor_requires_an_entity()
    {
        _sut.RequiresEntity("address-contracts", EditAddressRoute).ShouldBeTrue();
    }

    [Test]
    public void In_page_target_on_a_route_without_entity_does_not_require_one()
    {
        _sut.RequiresEntity("erp-drop-points", SettingsRoute).ShouldBeFalse();
    }

    [Test]
    public void Missing_target_and_route_do_not_require_an_entity()
    {
        _sut.RequiresEntity(null, null).ShouldBeFalse();
    }
}
