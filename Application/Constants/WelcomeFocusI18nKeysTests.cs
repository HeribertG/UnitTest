// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Constants;

[TestFixture]
public class WelcomeFocusI18nKeysTests
{
    private const int ExpectedKeyCount = 12;

    [Test]
    public void All_ContainsExactlyTheTwelveNewFocusKeys()
    {
        WelcomeFocusI18nKeys.All.Count.ShouldBe(ExpectedKeyCount);
        WelcomeFocusI18nKeys.All.Distinct().Count().ShouldBe(ExpectedKeyCount);
    }

    [Test]
    public void All_EveryKeyCarriesTheFocusPrefix()
    {
        foreach (var key in WelcomeFocusI18nKeys.All)
        {
            key.ShouldStartWith(WelcomeFocusI18nKeys.KeyPrefix + ".");
        }
    }

    [Test]
    public void All_ContainsEveryDeclaredFocusConstant()
    {
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.NoOrdersPrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.NoShiftsPrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.NoWorkPrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.PeriodOverduePrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.PeriodOverdueAction);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.PeriodCloseDuePrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.PeriodCloseDueAction);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.NextPeriodPrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.NextPeriodAction);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.GenericPrompt);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.GenericAction);
        WelcomeFocusI18nKeys.All.ShouldContain(WelcomeFocusI18nKeys.Later);
    }

    [Test]
    public void SetupConsultationAction_ReusesTheExistingInboxButtonKey()
    {
        WelcomeFocusI18nKeys.SetupConsultationAction.ShouldBe("setupConsultation.startButton");
        WelcomeFocusI18nKeys.All.ShouldNotContain(WelcomeFocusI18nKeys.SetupConsultationAction);
    }

    [Test]
    public void ActionKinds_AreTheTwoValuesTheFrontendSwitchesOn()
    {
        WelcomeFocusActionKinds.Navigate.ShouldBe("navigate");
        WelcomeFocusActionKinds.Consultation.ShouldBe("consultation");
    }
}
