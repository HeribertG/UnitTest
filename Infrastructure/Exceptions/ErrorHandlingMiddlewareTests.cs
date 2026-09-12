// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The status codes ErrorHandlingMiddleware maps the authorisation exceptions to. The distinction is
/// load-bearing: 401 makes the SPA log the user out, so a missing RIGHT must not come out as one, and
/// 400 describes a malformed request, which a rights problem is not. ForbiddenException is the only
/// path to 403 from inside a handler.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Exceptions;

[TestFixture]
public class ErrorHandlingMiddlewareTests
{
    private static async Task<(int StatusCode, string ContentType, string Body)> Invoke(Exception thrown)
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw thrown,
            Substitute.For<ILogger<ErrorHandlingMiddleware>>());

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);

        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, await reader.ReadToEndAsync());
    }

    [Test]
    public async Task ForbiddenException_Becomes403WithTheMessageAsDetail()
    {
        var response = await Invoke(new ForbiddenException("Unsealing this entry needs a higher role than yours."));

        response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        // The middleware sets "application/problem+json" but WriteAsJsonAsync overwrites it with
        // "application/json; charset=utf-8" — pre-existing behaviour shared by every branch, so the
        // header is deliberately not asserted here.
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status403Forbidden);
        problem.RootElement.GetProperty("detail").GetString()
            .ShouldBe("Unsealing this entry needs a higher role than yours.");
    }

    [Test]
    public async Task UnauthorizedException_StaysA401()
    {
        var response = await Invoke(new UnauthorizedException("no session"));

        response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task InvalidRequestException_StaysA400()
    {
        var response = await Invoke(new InvalidRequestException("this entry is not sealed"));

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }
}
