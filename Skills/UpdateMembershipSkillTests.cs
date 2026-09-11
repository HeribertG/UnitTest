// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateMembershipSkillTests
{
    // Pacific/Auckland edge case: 23:30 UTC on 27.06 is already 11:30 NZST (+12, no June DST) on 28.06 -
    // proves "heute" resolves to the company's local calendar day, not the UTC day.
    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
    private static readonly DateTimeOffset AucklandNowUtc = new(2026, 6, 27, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateTime CompanyToday = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static MembershipResource Membership(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Type = 0,
        ValidFrom = new DateTime(2026, 1, 1),
        ValidUntil = null
    };

    private static UpdateMembershipSkill Skill(
        IMediator mediator,
        IPendingConfirmationStore? store = null,
        ClientResource? client = null)
    {
        mediator.Send(Arg.Any<GetQuery<ClientResource>>(), Arg.Any<CancellationToken>())
            .Returns(client ?? new ClientResource());
        return new UpdateMembershipSkill(
            mediator,
            store ?? Substitute.For<IPendingConfirmationStore>(),
            new FixedCompanyClock(AucklandNowUtc, Auckland));
    }

    [Test]
    public async Task UpdateValidFromAndType_DispatchesPutCommand()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2026-03-01",
            ["type"] = 1
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c =>
                c.Resource.Id == membershipId &&
                c.Resource.ValidFrom == new DateTime(2026, 3, 1) &&
                c.Resource.Type == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_PersistsKindUtc_SoNpgsqlAcceptsTheTimestamptzWrite()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2026-03-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c => c.Resource.ValidFrom.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_TodayWord_ResolvesToCompanyToday()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "heute"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c =>
                c.Resource.ValidFrom == CompanyToday && c.Resource.ValidFrom.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_UnreadableDate_ReturnsError_NoMutation()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "not-a-date"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearValidUntil_RemovesEndDate()
    {
        var membershipId = Guid.NewGuid();
        var membership = Membership(membershipId);
        membership.ValidUntil = new DateTime(2026, 6, 30);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(membership);
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["clearValidUntil"] = true
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c => c.Resource.ValidUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownMembership_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns<MembershipResource>(_ => throw new KeyNotFoundException());
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = Guid.NewGuid().ToString(),
            ["validFrom"] = "2026-03-01"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidUntilBeforeValidFrom_ReturnsError_NoMutation()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validUntil"] = "2025-12-01"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_PersistedValuesMatch_ReportsVerified()
    {
        var membershipId = Guid.NewGuid();
        var original = Membership(membershipId);
        var persisted = Membership(membershipId);
        persisted.ValidFrom = new DateTime(2026, 3, 1);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(original, persisted);
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2026-03-01"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await mediator.Received(2).Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_PersistedValuesMismatch_ReturnsVerificationError()
    {
        var membershipId = Guid.NewGuid();
        var original = Membership(membershipId);
        var stale = Membership(membershipId);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(original, stale);
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2026-03-01"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task Update_ReReadThrowsKeyNotFound_ReturnsVerificationError()
    {
        var membershipId = Guid.NewGuid();
        var original = Membership(membershipId);
        var mediator = Substitute.For<IMediator>();
        var getCalls = 0;
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++getCalls == 1 ? original : throw new KeyNotFoundException());
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2026-03-01"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task NoFieldsSupplied_ReturnsSuccess_WithoutPut()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var skill = Skill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidFromMoreThan50YearsInPast_RequiresConfirmation_NoPut()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var store = Substitute.For<IPendingConfirmationStore>();
        store.Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns("confirm-token");
        var skill = Skill(mediator, store);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "1850-01-01"
        });

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message.ShouldContain("50 years in the past");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
        store.Received(1).Create(
            Arg.Any<Guid>(), "update_membership", Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Test]
    public async Task ValidFromBeforeClientBirthdate_RequiresConfirmation_NoPut()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var store = Substitute.For<IPendingConfirmationStore>();
        store.Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns("confirm-token");
        var client = new ClientResource { Birthdate = new DateTime(2005, 6, 15) };
        var skill = Skill(mediator, store, client);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2000-01-01"
        });

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message.ShouldContain("before the employee's birthdate");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImplausibleValidFrom_WithOverrideFlag_DispatchesPutCommand_WithoutConfirmation()
    {
        var membershipId = Guid.NewGuid();
        var original = Membership(membershipId);
        var persisted = Membership(membershipId);
        persisted.ValidFrom = new DateTime(1850, 1, 1);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(original, persisted);
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var store = Substitute.For<IPendingConfirmationStore>();
        var skill = Skill(mediator, store);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "1850-01-01",
            ["validFromPlausibilityConfirmed"] = "true"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Type.ShouldNotBe(SkillResultType.Confirmation);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c => c.Resource.ValidFrom == new DateTime(1850, 1, 1)),
            Arg.Any<CancellationToken>());
        store.DidNotReceive().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
        await mediator.DidNotReceive().Send(Arg.Any<GetQuery<ClientResource>>(), Arg.Any<CancellationToken>());
    }
}
