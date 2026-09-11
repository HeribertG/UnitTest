// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DateOnlyJsonConverter and DateOnlyNullableJsonConverter: a plain calendar date and an
/// ISO date/time with a zero or absent UTC offset land on the same DateOnly by taking the UTC date
/// part (the reading itself, via UtcDateTimeReader, is independent of the server's time zone; this
/// class does not spin up a second time zone to prove that), a non-zero offset is rejected (mirrors
/// UtcDateTimeJsonConverter), unparsable or culture-dependent input throws instead of silently
/// guessing, and the nullable converter treats null/blank as no value while still rejecting
/// genuinely bad input.
/// </summary>

using System.Text.Json;
using Klacks.Api.Infrastructure.Converters;

namespace Klacks.UnitTest.Infrastructure.Converters;

[TestFixture]
public class DateOnlyJsonConverterTests
{
    private JsonSerializerOptions _options = null!;
    private JsonSerializerOptions _nullableOptions = null!;

    [SetUp]
    public void Setup()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new DateOnlyJsonConverter());

        _nullableOptions = new JsonSerializerOptions();
        _nullableOptions.Converters.Add(new DateOnlyNullableJsonConverter());
    }

    [TestCase("\"2026-08-01\"", 2026, 8, 1)]
    [TestCase("\"2026-08-01T00:00:00Z\"", 2026, 8, 1)]
    [TestCase("\"2026-08-01T00:00:00.000Z\"", 2026, 8, 1)]
    [TestCase("\"2026-07-31T22:00:00.000Z\"", 2026, 7, 31)]
    [TestCase("\"2026-08-01T00:00:00\"", 2026, 8, 1)]
    [TestCase("\"2026-08-01T00:00:00+00:00\"", 2026, 8, 1)]
    [TestCase("\"2026-08-01T10:00Z\"", 2026, 8, 1)]
    [TestCase("\"2026-08-01T00:00:00.12345678Z\"", 2026, 8, 1)]
    public void ValidInput_ParsesToExpectedDate_UsesTheUtcDatePart(string json, int year, int month, int day)
    {
        var value = JsonSerializer.Deserialize<DateOnly>(json, _options);

        value.ShouldBe(new DateOnly(year, month, day));
    }

    [TestCase("\"2026-08-01T00:00:00+02:00\"")]
    [TestCase("\"2026-08-01T08:30:00-05:00\"")]
    public void NonZeroOffset_IsRejectedWithAReadableMessage(string json)
    {
        var ex = Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateOnly>(json, _options));

        ex.Message.ShouldContain("must be sent in UTC");
    }

    [TestCase("\"08/01/2026\"")]
    [TestCase("\"garbage\"")]
    [TestCase("\"\"")]
    public void UnparsableOrCultureDependentInput_Throws(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateOnly>(json, _options));
    }

    [Test]
    public void Write_IsUnchanged()
    {
        var value = new DateOnly(2026, 8, 1);

        JsonSerializer.Serialize(value, _options).ShouldBe("\"2026-08-01\"");
    }

    [TestCase("\"2026-08-01\"", 2026, 8, 1)]
    [TestCase("\"2026-07-31T22:00:00.000Z\"", 2026, 7, 31)]
    public void Nullable_ValidInput_ParsesToExpectedDate(string json, int year, int month, int day)
    {
        var value = JsonSerializer.Deserialize<DateOnly?>(json, _nullableOptions);

        value.ShouldBe(new DateOnly(year, month, day));
    }

    [Test]
    public void Nullable_Null_ReturnsNull()
    {
        JsonSerializer.Deserialize<DateOnly?>("null", _nullableOptions).ShouldBeNull();
    }

    [TestCase("\"\"")]
    [TestCase("\"   \"")]
    public void Nullable_BlankString_ReturnsNull(string json)
    {
        JsonSerializer.Deserialize<DateOnly?>(json, _nullableOptions).ShouldBeNull();
    }

    [Test]
    public void Nullable_NonZeroOffset_IsRejected()
    {
        var ex = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<DateOnly?>("\"2026-08-01T00:00:00+02:00\"", _nullableOptions));

        ex.Message.ShouldContain("must be sent in UTC");
    }

    [Test]
    public void Nullable_UnparsableNonBlankInput_Throws()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateOnly?>("\"garbage\"", _nullableOptions));
    }
}
