// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class WelcomeFocusPriorityTests
{
    [Test]
    public void Rank_EveryDeclaredTriggerKind_ResolvesToAKnownRank()
    {
        foreach (var kind in AgentTriggerKinds.All)
        {
            var rank = WelcomeFocusPriority.Rank(kind);
            rank.ShouldBeGreaterThanOrEqualTo(0);
            rank.ShouldBeLessThanOrEqualTo(WelcomeFocusPriority.GenericRank);
        }
    }

    [Test]
    public void Rank_UnknownKind_FallsBackToGenericRank()
    {
        WelcomeFocusPriority.Rank("a_kind_nobody_declared").ShouldBe(WelcomeFocusPriority.GenericRank);
    }

    [Test]
    public void Rank_OrdersSetupBeforeOverdueBeforeCloseDueBeforeNextPeriod()
    {
        var setup = WelcomeFocusPriority.Rank(AgentTriggerKinds.NoScheduleYet);
        var overdue = WelcomeFocusPriority.Rank(AgentTriggerKinds.PeriodOverdue);
        var closeDue = WelcomeFocusPriority.Rank(AgentTriggerKinds.PeriodCloseDue);
        var nextPeriod = WelcomeFocusPriority.Rank(AgentTriggerKinds.NextPeriodSchedulingDue);

        setup.ShouldBeLessThan(overdue);
        overdue.ShouldBeLessThan(closeDue);
        closeDue.ShouldBeLessThan(nextPeriod);
        nextPeriod.ShouldBeLessThan(WelcomeFocusPriority.GenericRank);
    }

    [Test]
    public void Rank_EveryOtherKind_SharesTheGenericRank()
    {
        WelcomeFocusPriority.Rank(AgentTriggerKinds.UnstaffedShift).ShouldBe(WelcomeFocusPriority.GenericRank);
        WelcomeFocusPriority.Rank(AgentTriggerKinds.OpenOrder).ShouldBe(WelcomeFocusPriority.GenericRank);
    }

    [Test]
    public void SeverityRank_OrdersHighBeforeMediumBeforeLow()
    {
        var high = WelcomeFocusPriority.SeverityRank(AgentTriggerSeverity.High);
        var medium = WelcomeFocusPriority.SeverityRank(AgentTriggerSeverity.Medium);
        var low = WelcomeFocusPriority.SeverityRank(AgentTriggerSeverity.Low);

        high.ShouldBeLessThan(medium);
        medium.ShouldBeLessThan(low);
    }

    [Test]
    public void SeverityRank_UnknownSeverity_IsTreatedAsLow()
    {
        WelcomeFocusPriority.SeverityRank("whatever")
            .ShouldBe(WelcomeFocusPriority.SeverityRank(AgentTriggerSeverity.Low));
    }
}
