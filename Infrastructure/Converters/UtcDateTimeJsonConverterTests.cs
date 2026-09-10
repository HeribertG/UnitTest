// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for UtcDateTimeJsonConverter: UTC input ("Z", "+00:00") is accepted as Kind Utc, a non-zero
/// offset is rejected with a readable message instead of failing later in the database, values
/// without an offset are read as UTC with the same wall clock, and writing is unchanged.
/// </summary>

using System.Text.Json;
using Klacks.Api.Infrastructure.Converters;

namespace Klacks.UnitTest.Infrastructure.Converters;

[TestFixture]
public class UtcDateTimeJsonConverterTests
{
    private JsonSerializerOptions _options = null!;

    [SetUp]
    public void Setup()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new UtcDateTimeJsonConverter());
    }

    [TestCase("\"2026-09-10T00:00:00Z\"")]
    [TestCase("\"2026-09-10T00:00:00.000Z\"")]
    [TestCase("\"2026-09-10T00:00:00+00:00\"")]
    public void UtcInput_IsAcceptedAsUtc_WithTheSameCalendarDay(string json)
    {
        var value = JsonSerializer.Deserialize<DateTime>(json, _options);

        value.Kind.ShouldBe(DateTimeKind.Utc);
        value.ShouldBe(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [TestCase("\"2026-09-10T00:00:00+02:00\"")]
    [TestCase("\"2026-09-10T08:30:00-05:00\"")]
    public void NonZeroOffset_IsRejectedWithAReadableMessage(string json)
    {
        var ex = Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateTime>(json, _options));

        ex.Message.ShouldContain("must be sent in UTC");
    }

    [Test]
    public void NullableProperty_UsesTheSameRule()
    {
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Holder>("{\"At\":\"2026-09-10T00:00:00+02:00\"}", _options));

        JsonSerializer.Deserialize<Holder>("{\"At\":null}", _options)!.At.ShouldBeNull();
        JsonSerializer.Deserialize<Holder>("{\"At\":\"2026-09-10T00:00:00Z\"}", _options)!.At!.Value.Kind
            .ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void ValueWithoutOffset_IsReadAsUtc_WithTheSameWallClock()
    {
        var value = JsonSerializer.Deserialize<DateTime>("\"2026-09-10T08:30:00\"", _options);

        value.Kind.ShouldBe(DateTimeKind.Utc);
        value.ShouldBe(new DateTime(2026, 9, 10, 8, 30, 0, DateTimeKind.Utc));
    }

    [Test]
    public void CalendarDateWithoutOffset_LandsOnUtcMidnightOfThatDay()
    {
        var value = JsonSerializer.Deserialize<DateTime>("\"2026-09-10T00:00:00\"", _options);

        value.ShouldBe(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void NonDateInput_IsRejected()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateTime>("\"not a date\"", _options));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateTime>("42", _options));
    }

    [Test]
    public void Write_IsUnchanged()
    {
        var utc = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

        JsonSerializer.Serialize(utc, _options).ShouldBe(JsonSerializer.Serialize(utc));
    }

    private sealed class Holder
    {
        public DateTime? At { get; set; }
    }
}
