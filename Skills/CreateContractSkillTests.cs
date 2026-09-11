// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateContractSkillTests
{
    private static readonly ICompanyClock CompanyClock =
        new FixedCompanyClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static IMediator MediatorReturningCreated()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resource = call.Arg<PostCommand<ContractResource>>().Resource;
                resource.Id = Guid.NewGuid();
                return resource;
            });
        return mediator;
    }

    [Test]
    public async Task ValidFrom_TodayWord_ResolvesToCompanyLocalDay_NotUtcDay()
    {
        // Pacific/Auckland edge case: 23:30 UTC on 27.06 is already 11:30 NZST (+12, no June DST) on
        // 28.06 - "heute" must resolve to the company's local calendar day, not the UTC day.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var aucklandClock = new FixedCompanyClock(new DateTimeOffset(2026, 6, 27, 23, 30, 0, TimeSpan.Zero), auckland);
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, aucklandClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Auckland Today",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "heute"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.ValidFrom == new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc) &&
                c.Resource.ValidFrom.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidFrom_UnreadableValue_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "not-a-date"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitValues_CreatesTemplate_WithDefaultsApplied()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Standard 80%",
            ["guaranteedHours"] = 134.4m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.Name == "Standard 80%" &&
                c.Resource.GuaranteedHours == 134.4m &&
                c.Resource.MinimumHours == 134.4m &&
                c.Resource.MaximumHours == 134.4m &&
                c.Resource.FullTime == decimal.Zero &&
                c.Resource.PaymentInterval == PaymentInterval.Monthly &&
                c.Resource.ValidUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidFrom_PersistsKindUtc_SoNpgsqlAcceptsTheTimestamptzWrite()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Standard 80%",
            ["guaranteedHours"] = 134.4m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c => c.Resource.ValidFrom.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ZeroGuaranteedHours_TakesFixedHoursPath_OnCallContract()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "On-Call",
            ["guaranteedHours"] = 0m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("guaranteed 0h");
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.GuaranteedHours == 0m &&
                c.Resource.MinimumHours == 0m &&
                c.Resource.MaximumHours == 0m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoGuaranteedHours_WithPercent_TakesInheritedWorkloadPath()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Inherited 80%",
            ["validFrom"] = "2026-07-01",
            ["percent"] = 80m
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("inherited from the company-wide value");
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.GuaranteedHours == null &&
                c.Resource.MinimumHours == decimal.Zero &&
                c.Resource.MaximumHours == decimal.Zero &&
                c.Resource.FullTime == decimal.Zero &&
                c.Resource.Percent == 80m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoGuaranteedHours_NoPercent_StillSucceeds_ServerDefaultsPercentLater()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Inherited default",
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.GuaranteedHours == null &&
                c.Resource.MinimumHours == decimal.Zero &&
                c.Resource.MaximumHours == decimal.Zero &&
                c.Resource.Percent == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PercentSupplied_IsPassedThrough()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Monatsstunden 80%",
            ["guaranteedHours"] = 134.4m,
            ["validFrom"] = "2026-07-01",
            ["percent"] = 80m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c => c.Resource.Percent == 80m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegativePercent_ReturnsErrorWithoutMutation()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Broken",
            ["guaranteedHours"] = 134.4m,
            ["validFrom"] = "2026-07-01",
            ["percent"] = -10m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitRangeAndInterval_ArePassedThrough()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Flex",
            ["guaranteedHours"] = 100m,
            ["minimumHours"] = 80m,
            ["maximumHours"] = 120m,
            ["fullTime"] = 168m,
            ["paymentInterval"] = "weekly",
            ["validFrom"] = "2026-07-01",
            ["validUntil"] = "2026-12-31"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.MinimumHours == 80m &&
                c.Resource.MaximumHours == 120m &&
                c.Resource.FullTime == 168m &&
                c.Resource.PaymentInterval == PaymentInterval.Weekly &&
                c.Resource.ValidUntil != null),
            Arg.Any<CancellationToken>());
    }

    [TestCase("name")]
    [TestCase("validFrom")]
    public async Task MissingRequiredParameter_ReturnsErrorWithoutMutation(string missing)
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator, CompanyClock);
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "2026-07-01"
        };
        parameters.Remove(missing);

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MinAboveMax_ReturnsErrorWithoutMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["minimumHours"] = 120m,
            ["maximumHours"] = 80m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GuaranteedOutsideRange_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 130m,
            ["minimumHours"] = 80m,
            ["maximumHours"] = 120m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task ValidUntilBeforeValidFrom_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "2026-07-01",
            ["validUntil"] = "2026-06-01"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task InvalidPaymentInterval_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator, CompanyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "2026-07-01",
            ["paymentInterval"] = "yearly"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The direct path-2 create (guaranteedHours omitted) must NOT reproduce what the old
    /// workaround produced (create with an invented guaranteedHours, then
    /// update_contract{clearGuaranteedHours:true}). The workaround leaves MinimumHours/
    /// MaximumHours stuck at whatever dummy guaranteedHours value was invented, because
    /// clearGuaranteedHours only clears GuaranteedHours itself. The direct path leaves them at
    /// the clean "unconfigured" (0) sentinel instead — this asymmetry is the reason
    /// guaranteedHours was made optional on create_contract in the first place.
    /// </summary>
    [Test]
    public async Task DirectInheritedPath_LeavesCleanZeros_UnlikeClearGuaranteedHoursWorkaround()
    {
        ContractResource? directCreated = null;
        var directMediator = Substitute.For<IMediator>();
        directMediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resource = call.Arg<PostCommand<ContractResource>>().Resource;
                resource.Id = Guid.NewGuid();
                directCreated = resource;
                return resource;
            });
        var directSkill = new CreateContractSkill(directMediator, CompanyClock);
        var directResult = await directSkill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Direct Inherited",
            ["validFrom"] = "2026-07-01",
            ["percent"] = 80m
        });
        directResult.Success.ShouldBeTrue();

        ContractResource? workaroundCreated = null;
        var workaroundCreateMediator = Substitute.For<IMediator>();
        workaroundCreateMediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resource = call.Arg<PostCommand<ContractResource>>().Resource;
                resource.Id = Guid.NewGuid();
                workaroundCreated = resource;
                return resource;
            });
        var workaroundCreateSkill = new CreateContractSkill(workaroundCreateMediator, CompanyClock);
        var workaroundCreateResult = await workaroundCreateSkill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Workaround Inherited",
            ["validFrom"] = "2026-07-01",
            ["guaranteedHours"] = 100m,
            ["percent"] = 80m
        });
        workaroundCreateResult.Success.ShouldBeTrue();

        ContractResource? workaroundFinal = null;
        var updateMediator = Substitute.For<IMediator>();
        updateMediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(workaroundCreated!);
        updateMediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resource = call.Arg<PutCommand<ContractResource>>().Resource;
                workaroundFinal = resource;
                return resource;
            });
        var updateSkill = new UpdateContractSkill(updateMediator, CompanyClock);

        var updateResult = await updateSkill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = workaroundCreated!.Id.ToString(),
            ["clearGuaranteedHours"] = true
        });
        updateResult.Success.ShouldBeTrue();

        directCreated!.GuaranteedHours.ShouldBeNull();
        workaroundFinal!.GuaranteedHours.ShouldBeNull();

        directCreated.MinimumHours.ShouldBe(decimal.Zero);
        directCreated.MaximumHours.ShouldBe(decimal.Zero);
        workaroundFinal.MinimumHours.ShouldBe(100m);
        workaroundFinal.MaximumHours.ShouldBe(100m);
    }
}
