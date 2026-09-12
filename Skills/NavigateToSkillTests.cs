// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NavigateToSkill — verifies that navigation to client editor pages
/// is refused without an entityId, permitted with one, and unrestricted for other pages.
/// Also covers the two permissions a page key can carry (spec 2.4 of the Option B design,
/// 2026-09-12): requiredPermission is the permission of the route, actionPermission the right to save
/// what the page's form produces. Both are checked, an Admin bypasses both, and neither is named in a
/// refusal. The entries are built here rather than read from the generated manifest, so the tests do
/// not depend on when the Klacks.Ui scanner emits the field.
/// </summary>

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using SettingKeys = Klacks.Api.Application.Constants.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class NavigateToSkillTests
{
    private const string MessagingPageKey = "messaging";
    private const string MessagingFeature = "messaging";
    private const string NewEmployeePageKey = "new-employee";

    private IKlacksyPageKeyCatalog _catalog = null!;
    private INavigationTargetCatalog _navigationTargetCatalog = null!;
    private IPluginNavigationRouteCatalog _pluginRouteCatalog = null!;
    private IFeatureAvailabilityService _featureAvailability = null!;
    private INavigationGuidanceProvider _guidanceProvider = null!;
    private NavigateToSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _catalog = Substitute.For<IKlacksyPageKeyCatalog>();
        _navigationTargetCatalog = Substitute.For<INavigationTargetCatalog>();
        _pluginRouteCatalog = Substitute.For<IPluginNavigationRouteCatalog>();
        _featureAvailability = Substitute.For<IFeatureAvailabilityService>();
        _featureAvailability.IsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _guidanceProvider = Substitute.For<INavigationGuidanceProvider>();
        _skill = new NavigateToSkill(
            _catalog,
            _navigationTargetCatalog,
            _pluginRouteCatalog,
            _featureAvailability,
            new[] { _guidanceProvider },
            NullLogger<NavigateToSkill>.Instance);
    }

    private static NavigationTargetEntry MakeTarget(
        string targetId,
        Dictionary<string, IReadOnlyList<string>>? synonyms = null,
        string? requiredPermission = null,
        string? requiredFeature = null) =>
        new(targetId, synonyms ?? new Dictionary<string, IReadOnlyList<string>>(), requiredPermission, requiredFeature);

    private static SkillExecutionContext Ctx(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = permissions.ToList()
    };

    private static KlacksyPageKeyEntry MakeEntry(
        string pageKey,
        string route = "/workplace/test",
        bool hasEntityParam = true,
        string? requiredPermission = null,
        string? requiredFeature = null,
        string? actionPermission = null) =>
        new(pageKey, route, requiredPermission, hasEntityParam, requiredFeature, actionPermission);

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
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[] { MakeTarget("macros") });
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
        _navigationTargetCatalog.GetByRoute("/workplace/edit-address")
            .Returns(new[] { MakeTarget("address-contracts") });
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
    public async Task NavigatesWithoutTarget_WhenTargetRequiresAPermissionTheUserLacks()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[]
        {
            MakeTarget("macros", requiredPermission: Roles.Admin)
        });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "macros" };

        var result = await _skill.ExecuteAsync(Ctx(Permissions.CanViewSettings), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":null"));
        Assert.That(result.Message, Does.Contain("not allowed to open"));
        Assert.That(result.Message, Does.Contain("never say it could not be found"));
        Assert.That(result.Message, Does.Not.Contain("macros"));
        Assert.That(result.Message, Does.Not.Contain("'settings'"));
        Assert.That(result.Message, Does.Not.Contain(Roles.Admin));
    }

    [Test]
    public async Task NavigatesWithTarget_WhenAdminBypassesTheTargetPermission()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[]
        {
            MakeTarget("macros", requiredPermission: Permissions.CanEditSettings)
        });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "macros" };

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"macros\""));
        Assert.That(result.Message, Is.EqualTo("Navigate to settings"));
    }

    [Test]
    public async Task NavigatesWithTarget_WhenTargetCarriesNoPermission()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[] { MakeTarget("macros") });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "macros" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"macros\""));
        Assert.That(result.Message, Is.EqualTo("Navigate to settings"));
    }

    [Test]
    public async Task NavigatesWithoutTarget_WhenSynonymResolvesToAForbiddenTarget()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings").Returns(new[]
        {
            MakeTarget(
                "manual-upload",
                new Dictionary<string, IReadOnlyList<string>> { ["de"] = new List<string> { "Manueller Upload" } },
                Roles.Admin)
        });
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "manueller-upload" };

        var result = await _skill.ExecuteAsync(Ctx(Permissions.CanViewSettings), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":null"));
        Assert.That(result.Message, Does.Contain("not allowed to open"));
        Assert.That(result.Message, Does.Not.Contain("manual-upload"));
        Assert.That(result.Message, Does.Not.Contain(Roles.Admin));
    }

    [Test]
    public async Task Navigates_WhenPageKeyIsOnlyKnownToAnInstalledPlugin()
    {
        _catalog.GetByPageKey("floor-plan").Returns((KlacksyPageKeyEntry?)null);
        _pluginRouteCatalog.GetRoute("floor-plan").Returns("/workplace/floor-plan");
        var parameters = new Dictionary<string, object> { ["page"] = "floor-plan" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Route\":\"/workplace/floor-plan\""));
    }

    [Test]
    public async Task ReturnsError_WhenPageKeyIsUnknownToBothCatalogs()
    {
        _catalog.GetByPageKey("floor-plan").Returns((KlacksyPageKeyEntry?)null);
        _pluginRouteCatalog.GetRoute("floor-plan").Returns((string?)null);
        var parameters = new Dictionary<string, object> { ["page"] = "floor-plan" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not a recognized page key"));
    }

    // The seam between the stored routes and the skill, over a real PluginNavigationRouteCatalog: once
    // uninstalling or disabling a plugin drops its route, the page has to fail honestly instead of
    // navigating to a route nobody serves any more.
    [Test]
    public async Task ReturnsError_WhenTheRouteWasRemovedFromTheStoredHandlerConfig()
    {
        var skill = SkillOverStoredRoutes("{\"routes\":{\"messaging\":\"/workplace/messaging\"}}");
        var parameters = new Dictionary<string, object> { ["page"] = "floor-plan" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Type, Is.Not.EqualTo(SkillResultType.Navigation));
        Assert.That(result.Message, Does.Contain("not a recognized page key"));
    }

    [Test]
    public async Task Navigates_WhenTheRouteIsStillInTheStoredHandlerConfig()
    {
        var skill = SkillOverStoredRoutes("{\"routes\":{\"floor-plan\":\"/workplace/floor-plan\"}}");
        var parameters = new Dictionary<string, object> { ["page"] = "floor-plan" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Route\":\"/workplace/floor-plan\""));
    }

    // A page the manifest marks with a requiredFeature only exists where the installation has that
    // feature - its Angular guard sends everyone else to /no-access. Offering it anyway produced a
    // rights error for something that is not a rights problem, so the skill asks the same question the
    // guard asks. The inbox is the feature whose gate is configuration rather than a plugin.
    [Test]
    public async Task ReturnsError_WhenInboxIsRequestedAndNoIncomingMailServerIsConfigured()
    {
        _catalog.GetByPageKey(UiPageKeys.Inbox).Returns(MakeEntry(
            UiPageKeys.Inbox, "/workplace/inbox", hasEntityParam: false, requiredFeature: KlacksyFeatures.Inbox));
        _featureAvailability.IsAvailableAsync(KlacksyFeatures.Inbox, Arg.Any<CancellationToken>()).Returns(false);
        var parameters = new Dictionary<string, object> { ["page"] = UiPageKeys.Inbox };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Type, Is.Not.EqualTo(SkillResultType.Navigation));
        Assert.That(result.Message, Does.Contain("not enabled on this installation"));
        Assert.That(result.Message, Does.Not.Contain("not allowed to open"));
        Assert.That(result.Message, Does.Not.Contain(SettingKeys.APP_INCOMING_SERVER));
    }

    [Test]
    public async Task Navigates_WhenInboxIsRequestedAndAnIncomingMailServerIsConfigured()
    {
        _catalog.GetByPageKey(UiPageKeys.Inbox).Returns(MakeEntry(
            UiPageKeys.Inbox, "/workplace/inbox", hasEntityParam: false, requiredFeature: KlacksyFeatures.Inbox));
        _featureAvailability.IsAvailableAsync(KlacksyFeatures.Inbox, Arg.Any<CancellationToken>()).Returns(true);
        var parameters = new Dictionary<string, object> { ["page"] = UiPageKeys.Inbox };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Route\":\"/workplace/inbox\""));
    }

    // The same gate for a plugin page: messaging has a static page key now, so it no longer depends on
    // the plugin route fallback - but it must still be refused where the plugin is off, naming neither
    // the plugin nor a right.
    [Test]
    public async Task ReturnsError_WhenMessagingIsRequestedAndThePluginIsNotEnabled()
    {
        _catalog.GetByPageKey(MessagingPageKey).Returns(MakeEntry(
            MessagingPageKey, "/workplace/messaging", hasEntityParam: false, requiredFeature: MessagingFeature));
        _featureAvailability.IsAvailableAsync(MessagingFeature, Arg.Any<CancellationToken>()).Returns(false);
        var parameters = new Dictionary<string, object> { ["page"] = MessagingPageKey };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Type, Is.Not.EqualTo(SkillResultType.Navigation));
        Assert.That(result.Message, Does.Contain("not enabled on this installation"));
        Assert.That(result.Message, Does.Not.Contain("not allowed to open"));
        Assert.That(result.Message, Does.Not.Contain(MessagingFeature));
    }

    [Test]
    public async Task Navigates_WhenMessagingIsRequestedAndThePluginIsEnabled()
    {
        _catalog.GetByPageKey(MessagingPageKey).Returns(MakeEntry(
            MessagingPageKey, "/workplace/messaging", hasEntityParam: false, requiredFeature: MessagingFeature));
        _featureAvailability.IsAvailableAsync(MessagingFeature, Arg.Any<CancellationToken>()).Returns(true);
        var parameters = new Dictionary<string, object> { ["page"] = MessagingPageKey };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Route\":\"/workplace/messaging\""));
    }

    [Test]
    public async Task DoesNotAskForFeatureAvailability_WhenThePageNeedsNoFeature()
    {
        _catalog.GetByPageKey("dashboard")
            .Returns(MakeEntry("dashboard", "/workplace/dashboard", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "dashboard" };

        await _skill.ExecuteAsync(Ctx(), parameters);

        await _featureAvailability.DidNotReceive().IsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private NavigateToSkill SkillOverStoredRoutes(string handlerConfig)
    {
        _catalog.GetByPageKey("floor-plan").Returns((KlacksyPageKeyEntry?)null);

        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(SkillNames.NavigateTo).Returns(new SkillDescriptor(
            SkillNames.NavigateTo,
            "Navigate",
            SkillCategory.UI,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null)
        {
            HandlerConfig = handlerConfig
        });

        return new NavigateToSkill(
            _catalog,
            _navigationTargetCatalog,
            new PluginNavigationRouteCatalog(registry),
            _featureAvailability,
            new[] { _guidanceProvider },
            NullLogger<NavigateToSkill>.Instance);
    }

    // A page permission may be a comma separated list, exactly like a target permission. The page check
    // used to compare it as one literal string, so it denied even a user holding every listed right.
    [Test]
    public async Task Navigates_WhenPagePermissionIsACommaListTheUserFullyHolds()
    {
        _catalog.GetByPageKey("client-list").Returns(MakeEntry(
            "client-list",
            "/workplace/client-list",
            hasEntityParam: false,
            requiredPermission: $"{Permissions.CanViewClients},{Permissions.CanViewGroups}"));
        var parameters = new Dictionary<string, object> { ["page"] = "client-list" };

        var result = await _skill.ExecuteAsync(
            Ctx(Permissions.CanViewClients, Permissions.CanViewGroups), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
    }

    [Test]
    public async Task ReturnsError_WhenPagePermissionCommaListIsOnlyPartiallyHeld()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry(
            "settings",
            "/workplace/settings",
            hasEntityParam: false,
            requiredPermission: $"{Permissions.CanViewSettings},{Permissions.CanEditSettings}"));
        var parameters = new Dictionary<string, object> { ["page"] = "settings" };

        var result = await _skill.ExecuteAsync(Ctx(Permissions.CanViewSettings), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not allowed to open"));
        Assert.That(result.Message, Does.Contain("never say it could not be found"));
        Assert.That(result.Message, Does.Not.Contain("'settings'"));
        Assert.That(result.Message, Does.Not.Contain(Permissions.CanViewSettings));
        Assert.That(result.Message, Does.Not.Contain(Permissions.CanEditSettings));
    }

    // Fail-closed: a route the target catalog knows nothing about used to hand the caller's raw target
    // string straight to the frontend behind nothing but a warning log.
    [Test]
    public async Task NavigatesWithoutTarget_WhenRouteHasNoKnownTargets()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        _navigationTargetCatalog.GetByRoute("/workplace/settings")
            .Returns(Array.Empty<NavigationTargetEntry>());
        var parameters = new Dictionary<string, object> { ["page"] = "settings", ["target"] = "whatever" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":null"));
        Assert.That(dataJson, Does.Not.Contain("whatever"));
        Assert.That(result.Message, Does.Not.Contain("whatever"));
    }

    // The plugin page fallback is the likely case: only a plugin's sidebar nav button is scanned, never
    // its page content, so a plugin route often holds no target at all — floor-plan's only entry is
    // obsolete and the cache filters those out, leaving the route empty in production too.
    // Spec 2.4: an action page key carries the right to save what its form produces on top of the route
    // permission. A planner may read the client list, so the route permission alone would have walked
    // them into the creation form and let the save fail with a 403 the assistant cannot explain.
    [Test]
    public async Task ReturnsError_WhenPlannerOpensACreationFormWhoseActionPermissionTheyLack()
    {
        _catalog.GetByPageKey(NewEmployeePageKey).Returns(MakeEntry(
            NewEmployeePageKey,
            "/workplace/edit-address",
            hasEntityParam: false,
            requiredPermission: Permissions.CanViewClients,
            actionPermission: Permissions.CanCreateClients));
        var parameters = new Dictionary<string, object> { ["page"] = NewEmployeePageKey };

        var result = await _skill.ExecuteAsync(Ctx(Permissions.PlannerFloor.ToArray()), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Type, Is.Not.EqualTo(SkillResultType.Navigation));
        Assert.That(result.Message, Does.Contain("not allowed to open"));
        Assert.That(result.Message, Does.Contain("never say it could not be found"));
        Assert.That(result.Message, Does.Not.Contain(Permissions.CanCreateClients));
        Assert.That(result.Message, Does.Not.Contain(Permissions.CanViewClients));
        Assert.That(result.Message, Does.Not.Contain(NewEmployeePageKey));
    }

    [Test]
    public async Task Navigates_WhenTheCallerHoldsBothTheRouteAndTheActionPermission()
    {
        _catalog.GetByPageKey(NewEmployeePageKey).Returns(MakeEntry(
            NewEmployeePageKey,
            "/workplace/edit-address",
            hasEntityParam: false,
            requiredPermission: Permissions.CanViewClients,
            actionPermission: Permissions.CanCreateClients));
        var parameters = new Dictionary<string, object> { ["page"] = NewEmployeePageKey };

        var result = await _skill.ExecuteAsync(
            Ctx(Permissions.CanViewClients, Permissions.CanCreateClients), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
    }

    [Test]
    public async Task Navigates_WhenAdminBypassesTheActionPermission()
    {
        _catalog.GetByPageKey(NewEmployeePageKey).Returns(MakeEntry(
            NewEmployeePageKey,
            "/workplace/edit-address",
            hasEntityParam: false,
            requiredPermission: Permissions.CanViewClients,
            actionPermission: Permissions.CanCreateClients));
        var parameters = new Dictionary<string, object> { ["page"] = NewEmployeePageKey };

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
    }

    [Test]
    public async Task Navigates_WhenAPlainDestinationCarriesNoActionPermission()
    {
        _catalog.GetByPageKey("client").Returns(MakeEntry(
            "client",
            "/workplace/client",
            hasEntityParam: false,
            requiredPermission: Permissions.CanViewClients));
        var parameters = new Dictionary<string, object> { ["page"] = "client" };

        var result = await _skill.ExecuteAsync(Ctx(Permissions.PlannerFloor.ToArray()), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
    }

    [Test]
    public async Task NavigatesWithoutTarget_WhenPluginPageCarriesAnUnvalidatableTarget()
    {
        const string injectedTarget = "../../evil-section";
        _catalog.GetByPageKey("floor-plan").Returns((KlacksyPageKeyEntry?)null);
        _pluginRouteCatalog.GetRoute("floor-plan").Returns("/workplace/floor-plan");
        _navigationTargetCatalog.GetByRoute("/workplace/floor-plan")
            .Returns(Array.Empty<NavigationTargetEntry>());
        var parameters = new Dictionary<string, object>
        {
            ["page"] = "floor-plan",
            ["target"] = injectedTarget
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Route\":\"/workplace/floor-plan\""));
        Assert.That(dataJson, Does.Contain("\"Target\":null"));
        Assert.That(dataJson, Does.Not.Contain(injectedTarget));
        Assert.That(result.Message, Does.Not.Contain(injectedTarget));
    }
}
