// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Behaviour of the client-note endpoints after Option B: a caller without any role (Planer) may
/// create, change and delete notes, because CanEditClientNotes is part of the Planer floor. The
/// in-body Permissions.HasPermission check is therefore unreachable today for an authenticated
/// caller — it is kept as the enforcement point should the floor ever be narrowed, and these tests
/// pin the reachable side: an empty claims principal is served, not forbidden.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AnnotationsControllerTests
{
    private IMediator _mediator = null!;
    private AnnotationsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new AnnotationsController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "TestAuth"))
                }
            }
        };
    }

    [Test]
    public async Task Post_CallerWithoutAnyRole_IsServedNotForbidden()
    {
        var resource = new AnnotationResource();
        _mediator.Send(Arg.Any<PostCommand<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        var result = await _controller.Post(resource);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Test]
    public async Task Put_CallerWithoutAnyRole_IsServedNotForbidden()
    {
        var resource = new AnnotationResource();
        _mediator.Send(Arg.Any<PutCommand<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        var result = await _controller.Put(resource);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Test]
    public async Task Delete_CallerWithoutAnyRole_IsServedNotForbidden()
    {
        _mediator.Send(Arg.Any<DeleteCommand<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AnnotationResource());

        var result = await _controller.Delete(Guid.NewGuid());

        result.Result.ShouldBeOfType<OkObjectResult>();
    }
}
