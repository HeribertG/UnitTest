// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleSetupStages — the single derivation both NoScheduleYetDetector and
/// GetSetupGuidanceSkill read their stage from. Pins all three stages and the refusal to name one for
/// an installation that already schedules, which is what keeps a fourth, unrendered stage from being
/// invented silently.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ScheduleSetupStagesTests
{
    [Test]
    public void For_EmptyInstallation_IsNothingYet() =>
        ScheduleSetupStages.For(new ScheduleSetupState(false, false, false))
            .ShouldBe(ScheduleSetupStage.NothingYet);

    [Test]
    public void For_OrdersWithoutShifts_IsOrdersButNoShifts() =>
        ScheduleSetupStages.For(new ScheduleSetupState(true, false, false))
            .ShouldBe(ScheduleSetupStage.OrdersButNoShifts);

    [Test]
    public void For_ShiftsWithoutWork_IsShiftsButNoWork() =>
        ScheduleSetupStages.For(new ScheduleSetupState(true, true, false))
            .ShouldBe(ScheduleSetupStage.ShiftsButNoWork);

    [Test]
    public void For_ShiftsWithoutOrders_StillReportsShiftsButNoWork() =>
        ScheduleSetupStages.For(new ScheduleSetupState(false, true, false))
            .ShouldBe(ScheduleSetupStage.ShiftsButNoWork);

    [Test]
    public void For_InstallationWithWork_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => ScheduleSetupStages.For(new ScheduleSetupState(true, true, true)));
}
