// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EscalationStageAlertTriggerEvent.SummaryParams: date and dueTime must be rendered
/// in the company's configured time zone, not raw UTC, so a shift starting near midnight in a
/// positive-offset zone reports the correct local calendar day instead of the UTC day before it.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationStageAlertTriggerEventTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Test]
    public void SummaryParams_ShiftStartsJustAfterMidnightLocal_DateReflectsLocalDayNotUtcDayBefore()
    {
        // 2026-07-15T22:30Z is 2026-07-16T00:30 in Zurich (summer, UTC+2) - one calendar day later.
        var shiftStartUtc = new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Utc);
        var dueAtUtc = new DateTime(2026, 7, 15, 23, 0, 0, DateTimeKind.Utc);
        var triggerEvent = new EscalationStageAlertTriggerEvent(
            Guid.NewGuid(), Guid.NewGuid().ToString(), "Jane Doe", shiftStartUtc, dueAtUtc, Zurich);

        var summaryParams = triggerEvent.SummaryParams;

        summaryParams["date"].ShouldBe("16.07.2026");
        summaryParams["dueTime"].ShouldBe("01:00 Europe/Zurich");
    }

    [Test]
    public void SummaryParams_DueTime_CarriesTheConfiguredZoneId_NotHardcodedUtc()
    {
        var stJohns = TimeZoneInfo.FindSystemTimeZoneById("America/St_Johns");
        var shiftStartUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var dueAtUtc = new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc);
        var triggerEvent = new EscalationStageAlertTriggerEvent(
            Guid.NewGuid(), Guid.NewGuid().ToString(), "Jane Doe", shiftStartUtc, dueAtUtc, stJohns);

        triggerEvent.SummaryParams["dueTime"].ShouldEndWith("America/St_Johns");
        triggerEvent.SummaryParams["dueTime"].ShouldNotContain("UTC");
    }

    [Test]
    public void SummaryParams_CompanyTimeZoneResolvedFromAWindowsId_DueTimeCarriesTheIanaIdNotTheWindowsId()
    {
        var windowsResolvedZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var shiftStartUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var dueAtUtc = new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc);
        var triggerEvent = new EscalationStageAlertTriggerEvent(
            Guid.NewGuid(), Guid.NewGuid().ToString(), "Jane Doe", shiftStartUtc, dueAtUtc, windowsResolvedZone);

        triggerEvent.SummaryParams["dueTime"].ShouldEndWith("Europe/Berlin");
    }
}
