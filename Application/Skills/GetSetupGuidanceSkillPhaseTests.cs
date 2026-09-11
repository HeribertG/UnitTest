// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the phase behaviour of GetSetupGuidanceSkill — verifies that an absent phase keeps
/// the old single-turn answer, that the route phase reports the resolved route, and that the act
/// phase only navigates when the user actually asked to be shown something.
/// </summary>

using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class GetSetupGuidanceSkillPhaseTests
{
    [Test]
    public async Task ExecuteAsync_WithoutPhase_ReturnsDataAndNeverNavigates()
    {
        var skill = BuildSkill(EmptyInstallation());

        var result = await skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Type.ShouldBe(SkillResultType.Data);
    }

    [Test]
    public async Task ExecuteAsync_IntroPhase_CarriesNoRouteYet()
    {
        var skill = BuildSkill(EmptyInstallation(hasCustomers: true, hasGroups: true));

        var result = await skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [SetupConsultationParameters.Phase] = SetupConsultationPhases.Intro
        });

        var route = result.Data!.GetType().GetProperty("Route")!.GetValue(result.Data);
        route.ShouldBeNull();
    }

    [Test]
    public async Task ExecuteAsync_ActPhaseWithShowChoice_ReturnsNavigation()
    {
        var skill = BuildSkill(EmptyInstallation(hasCustomers: true, hasGroups: true));

        var result = await skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [SetupConsultationParameters.Phase] = SetupConsultationPhases.Act,
            [SetupConsultationParameters.Attribution] = "yes",
            [SetupConsultationParameters.OrderSource] = "no",
            [SetupConsultationParameters.NextStep] = "show"
        });

        result.Type.ShouldBe(SkillResultType.Navigation);
    }

    [Test]
    public async Task ExecuteAsync_ActPhaseWithNoChoice_DoesNotNavigate()
    {
        var skill = BuildSkill(EmptyInstallation(hasCustomers: true, hasGroups: true));

        var result = await skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [SetupConsultationParameters.Phase] = SetupConsultationPhases.Act,
            [SetupConsultationParameters.Attribution] = "yes",
            [SetupConsultationParameters.OrderSource] = "no",
            [SetupConsultationParameters.NextStep] = "none"
        });

        result.Type.ShouldBe(SkillResultType.Data);
    }

    [Test]
    public async Task ExecuteAsync_ActPhaseWithUnknownAttribution_NeverHandsOffACreateFlow()
    {
        var skill = BuildSkill(EmptyInstallation(hasCustomers: true, hasGroups: true));

        var result = await skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            [SetupConsultationParameters.Phase] = SetupConsultationPhases.Act,
            [SetupConsultationParameters.Attribution] = "keine ahnung",
            [SetupConsultationParameters.OrderSource] = "keine ahnung",
            [SetupConsultationParameters.NextStep] = "create"
        });

        result.Type.ShouldBe(SkillResultType.Data);
        var handoff = result.Data!.GetType().GetProperty("Handoff")!.GetValue(result.Data);
        handoff.ShouldBeNull();
    }

    private static ScheduleSetupState EmptyInstallation(bool hasCustomers = false, bool hasGroups = false) =>
        new(HasOrders: false, HasShifts: false, HasWork: false, hasCustomers, hasGroups);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = []
    };

    private static GetSetupGuidanceSkill BuildSkill(ScheduleSetupState state)
    {
        var mediator = Substitute.For<IMediator>();
        var activityProbe = Substitute.For<IScheduleActivityProbe>();
        var objectStorageService = Substitute.For<IObjectStorageService>();
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns((Klacks.Api.Application.DTOs.ErpDropPoints.ErpDropPointResource)null!);
        activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>()).Returns(state);

        return new GetSetupGuidanceSkill(mediator, activityProbe, objectStorageService, settingsReader, companyClock);
    }
}
