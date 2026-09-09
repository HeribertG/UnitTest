// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NavigateToSkill — verifies that navigation to client editor pages
/// is refused without an entityId, permitted with one, and unrestricted for other pages.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class NavigateToSkillTests
{
    private IKlacksyPageKeyCatalog _catalog = null!;
    private INavigationTargetCatalog _navigationTargetCatalog = null!;
    private INavigationGuidanceProvider _guidanceProvider = null!;
    private NavigateToSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _catalog = Substitute.For<IKlacksyPageKeyCatalog>();
        _navigationTargetCatalog = Substitute.For<INavigationTargetCatalog>();
        _guidanceProvider = Substitute.For<INavigationGuidanceProvider>();
        _skill = new NavigateToSkill(
            _catalog,
            _navigationTargetCatalog,
            new[] { _guidanceProvider },
            NullLogger<NavigateToSkill>.Instance);
    }

    private static NavigationTargetEntry MakeTarget(string targetId, Dictionary<string, IReadOnlyList<string>>? synonyms = null) =>
        new(targetId, synonyms ?? new Dictionary<string, IReadOnlyList<string>>());

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static KlacksyPageKeyEntry MakeEntry(string pageKey, string route = "/workplace/test", bool hasEntityParam = true) =>
        new(pageKey, route, null, hasEntityParam);

    [Test]
    public async Task ReturnsError_WhenEditEmployee_WithoutEntityId()
    {
        var parameters = new Dictionary<string, object> { ["page"] = UiPageKeys.EditEmployee };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("create_employee"));
        Assert.That(result.Message, Does.Contain("entityId"));
    }

    [Test]
    public async Task ReturnsError_WhenEditAddress_WithoutEntityId()
    {
        var parameters = new Dictionary<string, object> { ["page"] = UiPageKeys.EditAddress };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("create_employee"));
        Assert.That(result.Message, Does.Contain("entityId"));
    }

    [Test]
    public async Task Navigates_WhenEditEmployee_WithEntityId()
    {
        var entityId = Guid.NewGuid().ToString();
        _catalog.GetByPageKey(UiPageKeys.EditEmployee)
            .Returns(MakeEntry(UiPageKeys.EditEmployee, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditEmployee,
            ["entityId"] = entityId
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task Navigates_WhenEditAddress_WithEntityId()
    {
        var entityId = Guid.NewGuid().ToString();
        _catalog.GetByPageKey(UiPageKeys.EditAddress)
            .Returns(MakeEntry(UiPageKeys.EditAddress, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditAddress,
            ["entityId"] = entityId
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task ReturnsError_WhenEditAddress_WithNonGuidEntityId()
    {
        _catalog.GetByPageKey(UiPageKeys.EditAddress)
            .Returns(MakeEntry(UiPageKeys.EditAddress, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditAddress,
            ["entityId"] = "6556"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("6556"));
        Assert.That(result.Message, Does.Contain("search_and_navigate"));
    }

    [Test]
    public async Task Navigates_WhenOtherPage_WithoutEntityId()
    {
        _catalog.GetByPageKey("client")
            .Returns(MakeEntry("client", "/workplace/client", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "client" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task ReturnsError_WhenEditEmployee_PageKeyIsCaseInsensitive()
    {
        var parameters = new Dictionary<string, object> { ["page"] = "Edit-Employee" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("create_employee"));
    }

    [Test]
    public async Task NavigationToExplainablePage_KeepsPlainMessage_KnowledgeInjectionLivesInExecutor()
    {
        _catalog.GetByPageKey("new-shift").Returns(MakeEntry("new-shift", "/workplace/new-shift", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "new-shift" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Not.Contain("explain_page"));
    }

    [Test]
    public async Task WithTargetParam_IncludesTargetInNavigationData()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = "settings",
            ["target"] = "macros"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var data = result.Data as dynamic;
        Assert.That(data, Is.Not.Null);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"macros\""));
    }

    [Test]
    public async Task WithoutTargetParam_DataHasNullTarget()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "settings" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":null"));
    }

    [Test]
    public async Task WithTargetAndEntityId_BothIncludedInNavigationData()
    {
        var entityId = Guid.NewGuid().ToString();
        _catalog.GetByPageKey(UiPageKeys.EditEmployee)
            .Returns(MakeEntry(UiPageKeys.EditEmployee, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditEmployee,
            ["entityId"] = entityId,
            ["target"] = "address-contracts"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"address-contracts\""));
        Assert.That(dataJson, Does.Contain(entityId));
    }

    [Test]
    public async Task EditShiftNavigation_AppendsGuidanceFromMatchingProvider()
    {
        var entityId = Guid.NewGuid();
        _catalog.GetByPageKey(UiPageKeys.EditShift)
            .Returns(MakeEntry(UiPageKeys.EditShift, "/workplace/edit-shift"));
        _guidanceProvider.CanHandle(UiPageKeys.EditShift).Returns(true);
        _guidanceProvider.GetGuidanceAsync(UiPageKeys.EditShift, entityId, Arg.Any<CancellationToken>())
            .Returns("This shift is locked.");
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditShift,
            ["entityId"] = entityId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Is.EqualTo($"Navigate to {UiPageKeys.EditShift} This shift is locked."));
    }

    [Test]
    public async Task EditShiftNavigation_GuidanceFailure_DoesNotBreakNavigation()
    {
        var entityId = Guid.NewGuid();
        _catalog.GetByPageKey(UiPageKeys.EditShift)
            .Returns(MakeEntry(UiPageKeys.EditShift, "/workplace/edit-shift"));
        _guidanceProvider.CanHandle(UiPageKeys.EditShift).Returns(true);
        _guidanceProvider.GetGuidanceAsync(UiPageKeys.EditShift, entityId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new InvalidOperationException("lookup failed")));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditShift,
            ["entityId"] = entityId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
        Assert.That(result.Message, Is.EqualTo($"Navigate to {UiPageKeys.EditShift}"));
    }

    [Test]
    public async Task EditShiftNavigation_ProviderNotResponsible_GuidanceNeverQueried()
    {
        var entityId = Guid.NewGuid();
        _catalog.GetByPageKey(UiPageKeys.EditShift)
            .Returns(MakeEntry(UiPageKeys.EditShift, "/workplace/edit-shift"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditShift,
            ["entityId"] = entityId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Is.EqualTo($"Navigate to {UiPageKeys.EditShift}"));
        await _guidanceProvider.DidNotReceive()
            .GetGuidanceAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PageWithoutEntityParam_ProviderNeverConsulted()
    {
        _catalog.GetByPageKey("dashboard")
            .Returns(MakeEntry("dashboard", "/workplace/dashboard", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "dashboard" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _guidanceProvider.DidNotReceive().CanHandle(Arg.Any<string>());
        await _guidanceProvider.DidNotReceive()
            .GetGuidanceAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Navigates_WhenTargetIsAValidTargetIdOfTheResolvedRoute()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings")
            .Returns(new[] { MakeTarget("macros"), MakeTarget("company-rules") });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "macros" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"macros\""));
    }

    [Test]
    public async Task ReturnsError_WhenTargetIsNotAKnownIdOrSynonymOfTheRoute()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings")
            .Returns(new[] { MakeTarget("macros"), MakeTarget("company-rules") });
        var parameters = new Dictionary<string, object>
        {
            ["page"] = "settings",
            ["target"] = "erp-drop-points-upload-zone"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("erp-drop-points-upload-zone"));
        Assert.That(result.Message, Does.Contain("macros"));
        Assert.That(result.Message, Does.Contain("company-rules"));
    }

    [Test]
    public async Task Navigates_WhenTargetIsFreeTextMatchingExactlyOneSynonym()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[]
        {
            MakeTarget("manual-upload", new Dictionary<string, IReadOnlyList<string>>
            {
                ["de"] = new List<string> { "Manueller Upload" }
            }),
            MakeTarget("company-rules")
        });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "manueller-upload" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"manual-upload\""));
    }

    [Test]
    public async Task ReturnsError_WhenTargetFreeTextMatchesMultipleSynonyms()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[]
        {
            MakeTarget("upload-a", new Dictionary<string, IReadOnlyList<string>>
            {
                ["de"] = new List<string> { "Upload" }
            }),
            MakeTarget("upload-b", new Dictionary<string, IReadOnlyList<string>>
            {
                ["en"] = new List<string> { "Upload" }
            })
        });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "Upload" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("upload-a"));
        Assert.That(result.Message, Does.Contain("upload-b"));
    }

    [Test]
    public async Task Navigates_WhenTargetIsNullOrEmpty_ValidationIsSkipped()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "settings" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _navigationTargetCatalog.DidNotReceive().GetByRoute(Arg.Any<string>());
    }

    [Test]
    public async Task Navigates_WhenTargetIsEmptyString_ValidationIsSkipped()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _navigationTargetCatalog.DidNotReceive().GetByRoute(Arg.Any<string>());
    }

    [Test]
    public async Task ReturnsError_WhenTargetIsAValidIdButBelongsToAnotherRoute()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings")
            .Returns(new[] { MakeTarget("macros"), MakeTarget("company-rules") });
        _navigationTargetCatalog.GetByRoute("/workplace/client")
            .Returns(new[] { MakeTarget("client-search-bar") });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "client-search-bar" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("client-search-bar"));
    }

    [Test]
    public async Task Navigates_WhenRouteHasNoKnownTargets_ValidationFailsOpen()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings")
            .Returns(Array.Empty<NavigationTargetEntry>());
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "whatever" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"whatever\""));
    }
}
