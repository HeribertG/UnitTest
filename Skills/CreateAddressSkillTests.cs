// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_address: dispatches PostCommand&lt;AddressResource&gt; with the supplied client
/// id and address fields, verifies the write by re-reading via GetQuery&lt;AddressResource&gt;; a null
/// result (creation failure), a missing re-read or mismatching persisted fields yield an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateAddressSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static IMediator MediatorWithEchoingPostAndReRead(Func<AddressResource, AddressResource>? rereadTransform = null)
    {
        AddressResource? posted = null;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                posted = ((PostCommand<AddressResource>)ci[0]).Resource;
                return posted;
            });
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => rereadTransform == null ? posted! : rereadTransform(posted!));
        return mediator;
    }

    [Test]
    public async Task CreateAddress_DispatchesPostCommand_WithClientAndFields_AndReportsVerified()
    {
        var clientId = Guid.NewGuid();
        var mediator = MediatorWithEchoingPostAndReRead();
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientId.ToString(),
            ["street"] = "Bahnhofstrasse 1",
            ["zip"] = "8001",
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("verified");
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<AddressResource>>(c =>
                c.Resource.ClientId == clientId &&
                c.Resource.Street == "Bahnhofstrasse 1" &&
                c.Resource.Zip == "8001" &&
                c.Resource.City == "Zürich"),
            Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAddress_PassesValidFromThrough()
    {
        var mediator = MediatorWithEchoingPostAndReRead();
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern",
            ["validFrom"] = "2026-08-01"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("verified");
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<AddressResource>>(c =>
                c.Resource.ValidFrom == new DateTime(2026, 8, 1)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAddress_ValidFrom_PersistsKindUtc_SoNpgsqlAcceptsTheTimestamptzWrite()
    {
        var mediator = MediatorWithEchoingPostAndReRead();
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern",
            ["validFrom"] = "2026-08-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<AddressResource>>(c => c.Resource.ValidFrom!.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAddress_ValidFromWithOffset_KeepsTheWrittenCalendarDay()
    {
        var mediator = MediatorWithEchoingPostAndReRead();
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern",
            ["validFrom"] = "2026-08-01T00:00:00+02:00"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<AddressResource>>(c =>
                c.Resource.ValidFrom == new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) &&
                c.Resource.ValidFrom!.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAddress_InvalidValidFrom_ReturnsError_NoPost()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern",
            ["validFrom"] = "not-a-date"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("validFrom");
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAddress_NullResult_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns((AddressResource?)null);
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task CreateAddress_ReReadMissing_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PostCommand<AddressResource>)ci[0]).Resource);
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns<AddressResource>(_ => throw new KeyNotFoundException());
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("could not be re-read");
    }

    [Test]
    public async Task CreateAddress_ReReadMismatch_ReturnsError()
    {
        var mediator = MediatorWithEchoingPostAndReRead(posted => new AddressResource
        {
            Id = posted.Id,
            ClientId = posted.ClientId,
            Street = posted.Street,
            Zip = posted.Zip,
            City = "Somewhere Else",
            Country = posted.Country,
            State = posted.State,
            Type = posted.Type
        });
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("mismatching fields");
        result.Message!.ShouldContain("city");
    }
}
