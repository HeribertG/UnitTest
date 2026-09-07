// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleActivityProbe.GetSetupStateAsync — verifies the two prerequisite probes
/// report existence flatly, in particular that a group with an unset nested set (Root = null,
/// Lft = Rgt = 0) still counts, because most groups in the reference installation look like that.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class ScheduleActivityProbeSetupStateTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private ScheduleActivityProbe CreateProbe() => new(CreateContext());

    [Test]
    public async Task GetSetupStateAsync_GroupWithUnsetNestedSet_IsStillReportedAsExisting()
    {
        using (var context = CreateContext())
        {
            context.Group.Add(new Group
            {
                Id = Guid.NewGuid(),
                Name = "Unset Nested Set Group",
                Root = null,
                Lft = 0,
                Rgt = 0
            });
            await context.SaveChangesAsync();
        }

        var state = await CreateProbe().GetSetupStateAsync();

        state.HasGroups.ShouldBeTrue();
    }

    [Test]
    public async Task GetSetupStateAsync_NoGroups_ReportsNoGroups()
    {
        var state = await CreateProbe().GetSetupStateAsync();

        state.HasGroups.ShouldBeFalse();
    }

    [Test]
    public async Task GetSetupStateAsync_OnlyEmployees_ReportsNoCustomers()
    {
        using (var context = CreateContext())
        {
            context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = "Only Employee",
                Type = EntityTypeEnum.Employee
            });
            await context.SaveChangesAsync();
        }

        var state = await CreateProbe().GetSetupStateAsync();

        state.HasCustomers.ShouldBeFalse();
    }

    [Test]
    public async Task GetSetupStateAsync_CustomerPresent_ReportsCustomers()
    {
        using (var context = CreateContext())
        {
            context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = "A Customer",
                Type = EntityTypeEnum.Customer
            });
            await context.SaveChangesAsync();
        }

        var state = await CreateProbe().GetSetupStateAsync();

        state.HasCustomers.ShouldBeTrue();
    }

    [Test]
    public async Task GetSetupStateAsync_DeletedCustomer_DoesNotCount()
    {
        using (var context = CreateContext())
        {
            context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = "Deleted Customer",
                Type = EntityTypeEnum.Customer,
                IsDeleted = true
            });
            await context.SaveChangesAsync();
        }

        var state = await CreateProbe().GetSetupStateAsync();

        state.HasCustomers.ShouldBeFalse();
    }
}
