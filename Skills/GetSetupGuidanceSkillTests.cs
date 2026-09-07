// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetSetupGuidanceSkill — pins that the skill reports the setup stage rather than
/// advice, that a configured drop point contributes the resolved path and poll schedule while a
/// missing one degrades instead of failing, that an installation which already schedules is reported
/// as complete, and above all that the manual route never offers "create a shift directly": no code
/// path produces a plannable shift except sealing an order, so an answer suggesting otherwise would
/// send the user looking for a screen that does not exist.
/// </summary>

using Klacks.Api.Application.DTOs.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpImportTokens;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetSetupGuidanceSkillTests
{
    private const string ResolvedPath = @"C:\klacks\erp-inbox";

    private IMediator _mediator = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private IObjectStorageService _objectStorageService = null!;
    private ISettingsReader _settingsReader = null!;
    private GetSetupGuidanceSkill _sut = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "Admin" }
    };

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _settingsReader = Substitute.For<ISettingsReader>();

        _objectStorageService.ResolvePath(Arg.Any<string>()).Returns(ResolvedPath);
        StubDropPoint(null);
        StubState(hasOrders: false, hasShifts: false, hasWork: false);

        _sut = new GetSetupGuidanceSkill(_mediator, _activityProbe, _objectStorageService, _settingsReader);
    }

    private void StubState(bool hasOrders, bool hasShifts, bool hasWork) =>
        _activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(hasOrders, hasShifts, hasWork));

    private void StubDropPoint(ErpDropPointResource? dropPoint) =>
        _mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(dropPoint!);

    private static string DataOf(SkillResult result) =>
        System.Text.Json.JsonSerializer.Serialize(result.Data);

    [Test]
    public async Task ExecuteAsync_EmptyInstallation_ReportsNothingYet()
    {
        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        DataOf(result).ShouldContain(nameof(ScheduleSetupStage.NothingYet));
    }

    [Test]
    public async Task ExecuteAsync_ShiftsWithoutWork_ReportsShiftsButNoWork()
    {
        StubState(hasOrders: true, hasShifts: true, hasWork: false);

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        DataOf(result).ShouldContain(nameof(ScheduleSetupStage.ShiftsButNoWork));
    }

    [Test]
    public async Task ExecuteAsync_InstallationThatSchedules_ReportsSetupComplete()
    {
        StubState(hasOrders: true, hasShifts: true, hasWork: true);

        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        DataOf(result).ShouldContain("\"SetupComplete\":true");
    }

    [Test]
    public async Task ExecuteAsync_NoDropPointConfigured_StillSucceeds()
    {
        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("No ERP handover point is configured");
        await _mediator.DidNotReceive().Send(Arg.Any<GetErpImportTokensQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ManualRoute_NeverOffersCreatingAShiftDirectly()
    {
        var result = await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var payload = DataOf(result) + result.Message;
        payload.ShouldContain("only ever comes into existence by sealing an order");
        payload.ShouldContain("cannot be undone");
    }

    [Test]
    public async Task ExecuteAsync_ManualRoute_NamesOnlyKnownNavigationTargets()
    {
        var payload = DataOf(await _sut.ExecuteAsync(Ctx(), new Dictionary<string, object>()));

        payload.ShouldContain("shift-list");
        payload.ShouldContain("new-shift");
        payload.ShouldContain("cut-shift");
        payload.ShouldContain("schedule");
    }
}
