// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for IanaTimeZoneId.From: an IANA-resolved TimeZoneInfo is returned unchanged, a
/// Windows-id-resolved one is converted to its IANA form, and UTC (which has an IANA id on every
/// platform) passes through unchanged too.
/// </summary>

using Klacks.Api.Domain.Services.Settings;

namespace Klacks.UnitTest.Domain.Services.Settings;

[TestFixture]
public class IanaTimeZoneIdTests
{
    [Test]
    public void From_IanaZone_ReturnsItsIdUnchanged()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

        IanaTimeZoneId.From(zone).ShouldBe("Europe/Zurich");
    }

    [Test]
    public void From_WindowsZone_ReturnsTheIanaId()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        IanaTimeZoneId.From(zone).ShouldBe("Europe/Berlin");
    }

    [Test]
    public void From_Utc_ReturnsUtc()
    {
        IanaTimeZoneId.From(TimeZoneInfo.Utc).ShouldBe("UTC");
    }

    [Test]
    public void TryFrom_IanaId_ReturnsItUnchanged()
    {
        IanaTimeZoneId.TryFrom("Europe/Zurich", out var ianaId).ShouldBeTrue();
        ianaId.ShouldBe("Europe/Zurich");
    }

    [Test]
    public void TryFrom_WindowsId_ReturnsTheIanaId()
    {
        IanaTimeZoneId.TryFrom("W. Europe Standard Time", out var ianaId).ShouldBeTrue();
        ianaId.ShouldBe("Europe/Berlin");
    }

    [Test]
    public void TryFrom_Unknown_ReturnsFalse()
    {
        IanaTimeZoneId.TryFrom("Mars/Olympus", out var ianaId).ShouldBeFalse();
        ianaId.ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void TryFrom_BlankOrNull_ReturnsFalse(string? input)
    {
        IanaTimeZoneId.TryFrom(input, out var ianaId).ShouldBeFalse();
        ianaId.ShouldBeNull();
    }
}
