// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The client endpoints and the two paths PUT api/backend/Clients takes. The note card of edit-address
/// saves its notes through the client aggregate, so Put has to be reachable by a caller without any
/// role (Planer) — which InputBaseController made impossible, because an attribute on an override is
/// AND-combined with the base method's. The controller therefore derives from BaseController and
/// declares its own verbs: Get and Put open to every authenticated caller, Post and Delete still
/// Admin/Authorised. A caller with CanEditClients writes the whole client; a caller with only
/// CanEditClientNotes reaches a command that can write nothing but the notes, so the rest of the sent
/// resource is ignored instead of refused.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class ClientsControllerAuthorizationTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private ClientsController ControllerFor(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToArray();

        return new ClientsController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            }
        };
    }

    private static AuthorizeAttribute? AuthorizeOf(string methodName)
        => typeof(ClientsController)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .FirstOrDefault();

    [Test]
    public void ClientsController_DoesNotInheritTheInputBaseRestriction()
    {
        typeof(ClientsController).BaseType.ShouldBe(
            typeof(BaseController),
            "Notes are saved through the client aggregate, so Put must be open to a Planer. An " +
            "override cannot lift the InputBaseController role restriction.");
    }

    [Test]
    public void Put_CarriesNoRoleRestriction()
    {
        typeof(ClientsController)
            .GetMethod(nameof(ClientsController.Put), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    [Test]
    public void Get_CarriesNoRoleRestriction()
    {
        typeof(ClientsController)
            .GetMethod(nameof(ClientsController.Get), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    [Test]
    public void PostAndDelete_StayRestrictedToAdminAndAuthorised()
    {
        AuthorizeOf(nameof(ClientsController.Post))!.Roles.ShouldBe($"{Roles.Admin},{Roles.Authorised}");
        AuthorizeOf(nameof(ClientsController.Delete))!.Roles.ShouldBe($"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public async Task Put_Planner_ReachesOnlyTheNoteCommand_NeverTheFullUpdate()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        resource.Annotations.Add(new AnnotationResource { ClientId = resource.Id, Note = "new note" });
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        var result = await ControllerFor().Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<ClientResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_Planner_PassesTheClientIdAndTheNotesOfTheSentResource()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        resource.Annotations.Add(new AnnotationResource { ClientId = resource.Id, Note = "new note" });
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        await ControllerFor().Put(resource, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<UpdateClientAnnotationsCommand>(command =>
                command.ClientId == resource.Id
                && command.Annotations.Count == 1
                && command.Annotations[0].Note == "new note"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_PlannerWithANullAnnotationList_SavesNoNotesInsteadOfFailing()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Annotations = null! };
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        var result = await ControllerFor().Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<UpdateClientAnnotationsCommand>(command => command.Annotations.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_PlannerOnAnUnknownClient_IsNotFound()
    {
        var resource = new ClientResource { Id = Guid.NewGuid() };
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns((ClientResource?)null);

        var result = await ControllerFor().Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task Put_SupervisorChangingTheName_TakesTheFullUpdate()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        _mediator.Send(Arg.Any<PutCommand<ClientResource>>(), Arg.Any<CancellationToken>()).Returns(resource);

        var result = await ControllerFor(Roles.Authorised).Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.DidNotReceive().Send(
            Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_AdminChangingTheName_TakesTheFullUpdate()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        _mediator.Send(Arg.Any<PutCommand<ClientResource>>(), Arg.Any<CancellationToken>()).Returns(resource);

        var result = await ControllerFor(Roles.Admin).Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.DidNotReceive().Send(
            Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>());
    }
}
