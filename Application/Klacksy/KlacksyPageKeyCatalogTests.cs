// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves empirically that KlacksyPageKeyCatalog reads the scanner manifest into KlacksyPageKeyEntry
/// without a hand-written binding step: the record is positional, so System.Text.Json binds by
/// constructor parameter name, case-insensitively, and a property the manifest does not carry falls to
/// the parameter default. actionPermission (spec 2.4 of the Option B design, 2026-09-12) was added as
/// such an optional parameter, which is why the catalog itself needed no change — and why a manifest
/// written before the scanner emitted the field still loads, with the field null. Both directions are
/// asserted here so a future switch to a non-positional model or to source-generated serialization
/// cannot silently drop the field.
/// </summary>

namespace Klacks.UnitTest.Application.Klacksy;

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class KlacksyPageKeyCatalogTests
{
    private const string NewEmployeePageKey = "new-employee";
    private const string DashboardPageKey = "dashboard";

    private string _manifestPath = null!;

    [SetUp]
    public void SetUp()
    {
        _manifestPath = Path.Combine(Path.GetTempPath(), $"klacksy-page-keys-{Guid.NewGuid():N}.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    [Test]
    public void ActionPermission_IsReadFromTheManifest()
    {
        File.WriteAllText(_manifestPath, $$"""
        {
          "generatedAt": "2026-09-12T00:00:00.000Z",
          "source": "test",
          "entries": [
            {
              "pageKey": "{{NewEmployeePageKey}}",
              "route": "/workplace/edit-address",
              "requiredPermission": "{{Permissions.CanViewClients}}",
              "hasEntityParam": false,
              "actionPermission": "{{Permissions.CanCreateClients}}"
            }
          ]
        }
        """);

        var entry = new KlacksyPageKeyCatalog(_manifestPath).GetByPageKey(NewEmployeePageKey);

        entry.ShouldNotBeNull();
        entry!.RequiredPermission.ShouldBe(Permissions.CanViewClients);
        entry.ActionPermission.ShouldBe(Permissions.CanCreateClients);
    }

    [Test]
    public void ActionPermission_IsNullWhenTheManifestDoesNotCarryIt()
    {
        File.WriteAllText(_manifestPath, $$"""
        {
          "generatedAt": "2026-09-12T00:00:00.000Z",
          "source": "test",
          "entries": [
            {
              "pageKey": "{{DashboardPageKey}}",
              "route": "/workplace/dashboard",
              "requiredPermission": null,
              "hasEntityParam": false
            }
          ]
        }
        """);

        var entry = new KlacksyPageKeyCatalog(_manifestPath).GetByPageKey(DashboardPageKey);

        entry.ShouldNotBeNull();
        entry!.ActionPermission.ShouldBeNull();
    }
}
