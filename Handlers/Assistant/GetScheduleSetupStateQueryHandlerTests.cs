// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetScheduleSetupStateQueryHandler — verifies the installation-wide setup
/// snapshot from IScheduleActivityProbe is passed through unchanged.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetScheduleSetupStateQueryHandlerTests
{
    private IScheduleActivityProbe _activityProbe = null!;
    private GetScheduleSetupStateQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _sut = new GetScheduleSetupStateQueryHandler(_activityProbe);
    }

    [Test]
    public async Task Handle_EmptyInstallation_ReturnsAllFlagsFalse()
    {
        _activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(
                HasOrders: false,
                HasShifts: false,
                HasWork: false,
                HasCustomers: false,
                HasGroups: false));

        var result = await _sut.Handle(new GetScheduleSetupStateQuery(), CancellationToken.None);

        result.HasOrders.ShouldBeFalse();
        result.HasShifts.ShouldBeFalse();
        result.HasWork.ShouldBeFalse();
        result.HasCustomers.ShouldBeFalse();
        result.HasGroups.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_FullyConfiguredInstallation_ReturnsAllFlagsTrue()
    {
        _activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(
                HasOrders: true,
                HasShifts: true,
                HasWork: true,
                HasCustomers: true,
                HasGroups: true));

        var result = await _sut.Handle(new GetScheduleSetupStateQuery(), CancellationToken.None);

        result.HasOrders.ShouldBeTrue();
        result.HasShifts.ShouldBeTrue();
        result.HasWork.ShouldBeTrue();
        result.HasCustomers.ShouldBeTrue();
        result.HasGroups.ShouldBeTrue();
    }
}
