// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CompanyClockController: forwards GetCompanyClockQuery through the mediator and returns
/// its result as 200 OK - no role check, unlike the Admin-only GeneralSettingsController.
/// </summary>
namespace Klacks.UnitTest.Controllers.Settings;

using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.CompanyClock;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class CompanyClockControllerTests
{
    private IMediator _mediator = null!;
    private CompanyClockController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new CompanyClockController(_mediator);
    }

    [Test]
    public async Task Get_ReturnsMediatorResultAsOk()
    {
        var expected = new CompanyClockResource
        {
            TimeZone = "Asia/Kolkata",
            Today = new DateOnly(2026, 6, 28),
            Source = "Setting",
        };
        _mediator.Send(Arg.Any<GetCompanyClockQuery>(), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _controller.Get(CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.Value.ShouldBe(expected);
    }

    [Test]
    public async Task Get_SendsGetCompanyClockQuery()
    {
        _mediator.Send(Arg.Any<GetCompanyClockQuery>(), Arg.Any<CancellationToken>())
            .Returns(new CompanyClockResource());

        await _controller.Get(CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Any<GetCompanyClockQuery>(), Arg.Any<CancellationToken>());
    }
}
