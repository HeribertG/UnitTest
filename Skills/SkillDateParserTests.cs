// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillDateParser: blank input defaults (no value, not invalid), ISO and Swiss
/// dotted dates parse to UTC midnight, "today" words resolve to the supplied company-today (not the
/// server's UTC day), and a non-blank value that cannot be understood is flagged Invalid so the skill
/// rejects it instead of silently using now.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillDateParserTests
{
    private static readonly DateTime Today = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Blank_ReturnsNoValue_AndNotInvalid(string? raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.False);
    }

    [Test]
    public void IsoDate_ParsesToUtcMidnight()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-05-01", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void SwissDottedDate_IsReadAsDayMonthYear()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("01.05.2026", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void UtcDateTimeWithOffset_TakesOnlyTheWrittenCalendarDay_NotShiftedByServerTimeZone()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-08-01T00:00:00+02:00", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void UtcDateTimeWithZ_IsReadAsThatCalendarDay()
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate("2026-08-01T00:00:00Z", Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("today")]
    [TestCase("heute")]
    [TestCase("Heute")]
    [TestCase("ab sofort")]
    public void TodayWords_ResolveToSuppliedCompanyToday(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(invalid, Is.False);
        Assert.That(value, Is.EqualTo(Today));
        Assert.That(value!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("irgendwann bald")]
    [TestCase("nächsten Monat")]
    [TestCase("whenever")]
    public void UnparseableNonBlank_IsFlaggedInvalid(string raw)
    {
        var (value, invalid) = SkillDateParser.ParseOptionalUtcDate(raw, Today);

        Assert.That(value, Is.Null);
        Assert.That(invalid, Is.True);
    }
}
