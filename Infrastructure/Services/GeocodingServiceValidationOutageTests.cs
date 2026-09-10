// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GeocodingService.ValidateExactAddressAsync when Nominatim cannot be reached: an outage is
/// reported as ServiceUnavailable (not as "address not found"), and it is never cached, so a real
/// address does not look non-existent after Nominatim recovers.
/// </summary>

using System.Net;
using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class GeocodingServiceValidationOutageTests
{
    private const string FoundJson = """
        [{"lat": "46.9480", "lon": "7.4474", "display_name": "3011 Bern, Switzerland", "address": {"state": "Bern"}}]
        """;

    private StubHandler _handler = null!;
    private MemoryCache _cache = null!;
    private GeocodingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new StubHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Nominatim").Returns(new HttpClient(_handler));
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        _service = new GeocodingService(factory, _cache, Substitute.For<ILogger<GeocodingService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task NonSuccessResponse_IsReportedAsServiceUnavailable()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var result = await _service.ValidateExactAddressAsync(null, "3011", "Bern", "Schweiz");

        result.Found.ShouldBeFalse();
        result.ServiceUnavailable.ShouldBeTrue();
    }

    [Test]
    public async Task NetworkError_IsReportedAsServiceUnavailable()
    {
        _handler.Respond = _ => throw new HttpRequestException("connection refused");

        var result = await _service.ValidateExactAddressAsync(null, "3011", "Bern", "Schweiz");

        result.Found.ShouldBeFalse();
        result.ServiceUnavailable.ShouldBeTrue();
    }

    [Test]
    public async Task OutageDuringCityFallback_IsUnavailable_AndNotCachedAsNotFound()
    {
        var calls = 0;
        _handler.Respond = _ => ++calls switch
        {
            1 => Json("[]"),
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Json(FoundJson),
        };

        var duringOutage = await _service.ValidateExactAddressAsync("Nowhere 1", "3011", "Bern", "Schweiz");
        var afterRecovery = await _service.ValidateExactAddressAsync("Nowhere 1", "3011", "Bern", "Schweiz");

        duringOutage.ServiceUnavailable.ShouldBeTrue();
        afterRecovery.ServiceUnavailable.ShouldBeFalse();
        afterRecovery.Found.ShouldBeTrue();
    }

    [Test]
    public async Task EmptyAnswer_IsARealNotFound_NotAnOutage()
    {
        _handler.Respond = _ => Json("[]");

        var result = await _service.ValidateExactAddressAsync(null, "0000", "Nirgendwo", "Schweiz");

        result.Found.ShouldBeFalse();
        result.ServiceUnavailable.ShouldBeFalse();
        result.MatchType.ShouldBe("not_found");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }
}
