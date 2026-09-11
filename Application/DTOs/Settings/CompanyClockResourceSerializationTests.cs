// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the exact wire contract of CompanyClockResource - camelCase property names (Program.cs
/// PropertyNamingPolicy) and the yyyy-MM-dd DateOnly shape (DateOnlyJsonConverter) - so a UI built
/// against this JSON does not silently break if the DTO or the global serializer options drift apart.
/// </summary>
namespace Klacks.UnitTest.Application.DTOs.Settings;

using System.Text.Json;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Infrastructure.Converters;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class CompanyClockResourceSerializationTests
{
    [Test]
    public void Serialize_MirrorsProgramJsonOptions_ProducesCamelCaseWithDateOnlyFormat()
    {
        var resource = new CompanyClockResource
        {
            TimeZone = "Asia/Kolkata",
            Today = new DateOnly(2026, 6, 28),
            Source = "Utc",
        };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new DateOnlyJsonConverter());

        var json = JsonSerializer.Serialize(resource, options);

        json.ShouldContain("\"timeZone\":\"Asia/Kolkata\"");
        json.ShouldContain("\"today\":\"2026-06-28\"");
        json.ShouldContain("\"source\":\"Utc\"");
    }
}
