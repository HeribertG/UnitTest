// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_address: loads via GetQuery&lt;AddressResource&gt;, mutates only supplied
/// fields, dispatches PutCommand&lt;AddressResource&gt; and verifies the write by re-reading the
/// address; an unknown id, a missing re-read or mismatching persisted fields yield an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAddressSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static AddressResource Address(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Street = "Alte Strasse 5",
        Zip = "3000",
        City = "Bern",
        Country = "Schweiz",
        Type = AddressTypeEnum.Employee
    };

    [Test]
    public async Task UpdateCityAndZip_DispatchesPutCommand_WithMergedValues_AndReportsVerified()
    {
        var id = Guid.NewGuid();
        var address = Address(id);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(address);
        mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AddressResource>)ci[0]).Resource);
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["city"] = "Zürich",
            ["zip"] = "8001"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("verified");
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<AddressResource>>(c =>
                c.Resource.Id == id &&
                c.Resource.City == "Zürich" &&
                c.Resource.Zip == "8001" &&
                c.Resource.Street == "Alte Strasse 5"),
            Arg.Any<CancellationToken>());
        await mediator.Received(2).Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_ChangesField_AndReportsVerified()
    {
        var id = Guid.NewGuid();
        var address = Address(id);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(address);
        mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AddressResource>)ci[0]).Resource);
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["validFrom"] = "2026-09-01"
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("validFrom");
        result.Message!.ShouldContain("verified");
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<AddressResource>>(c =>
                c.Resource.ValidFrom == new DateTime(2026, 9, 1)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_PersistsKindUtc_SoNpgsqlAcceptsTheTimestamptzWrite()
    {
        var id = Guid.NewGuid();
        var address = Address(id);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(address);
        mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AddressResource>)ci[0]).Resource);
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["validFrom"] = "2026-09-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<AddressResource>>(c => c.Resource.ValidFrom!.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidValidFrom_ReturnsError_NoPut()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(Address(id));
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["validFrom"] = "not-a-date"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("validFrom");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns<AddressResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = Guid.NewGuid().ToString(),
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReReadMismatch_ReturnsError()
    {
        var id = Guid.NewGuid();
        var loaded = Address(id);
        var stale = Address(id);
        stale.ClientId = loaded.ClientId;
        var calls = 0;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0 ? loaded : stale);
        mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AddressResource>)ci[0]).Resource);
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("mismatching fields");
        result.Message!.ShouldContain("city");
    }

    [Test]
    public async Task ReReadMissingAfterPut_ReturnsError()
    {
        var id = Guid.NewGuid();
        var loaded = Address(id);
        var calls = 0;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0 ? loaded : throw new KeyNotFoundException());
        mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AddressResource>)ci[0]).Resource);
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("could not be re-read");
    }
}
