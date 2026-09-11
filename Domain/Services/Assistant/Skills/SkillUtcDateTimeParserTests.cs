// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillUtcDateTimeParser: a bare calendar date, a UTC date/time and an offset
/// date/time all come back with Kind=Utc (the shape Npgsql accepts for timestamptz columns) while
/// keeping the calendar day/wall clock exactly as written — no offset arithmetic is applied, because
/// every caller (Contract/Membership validFrom/validUntil, Address.ValidFrom) treats the value as a
/// calendar boundary, not a precise instant. A Swiss dotted date still parses, and blank/unparsable
/// input fails instead of defaulting to now. Note: unlike this parser, SkillParameterTypeValidator's
/// dispatch-time gate also accepts "today" words (e.g. "heute") — the two are not fully aligned, see
/// SkillUtcDateTimeParser's own doc comment.
/// </summary>

using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillUtcDateTimeParserTests
{
    [Test]
    public void CalendarDate_ParsesToUtcMidnight()
    {
        var success = SkillUtcDateTimeParser.TryParse("2026-08-01", out var value);

        success.ShouldBeTrue();
        value.ShouldBe(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void UtcDateTime_ParsesToUtc()
    {
        var success = SkillUtcDateTimeParser.TryParse("2026-08-01T10:00:00Z", out var value);

        success.ShouldBeTrue();
        value.ShouldBe(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void OffsetDateTime_KeepsTheWrittenCalendarDay()
    {
        var success = SkillUtcDateTimeParser.TryParse("2026-08-01T12:00:00+02:00", out var value);

        success.ShouldBeTrue();
        value.ShouldBe(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void OffsetAtMidnight_DoesNotShiftToThePreviousDay()
    {
        var success = SkillUtcDateTimeParser.TryParse("2026-08-01T00:00:00+02:00", out var value);

        success.ShouldBeTrue();
        value.ShouldBe(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void NoOffsetDateTime_IsReadAsUtc_WithTheSameWallClock()
    {
        var success = SkillUtcDateTimeParser.TryParse("2026-08-01T08:30:00", out var value);

        success.ShouldBeTrue();
        value.ShouldBe(new DateTime(2026, 8, 1, 8, 30, 0, DateTimeKind.Utc));
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void SwissDottedDate_StillParses_KindIsUtc()
    {
        var success = SkillUtcDateTimeParser.TryParse("01.05.2026", out var value);

        success.ShouldBeTrue();
        value.ShouldBe(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("garbage")]
    [TestCase("whenever")]
    public void BlankOrUnparsable_ReturnsFalse(string? raw)
    {
        var success = SkillUtcDateTimeParser.TryParse(raw, out _);

        success.ShouldBeFalse();
    }
}
