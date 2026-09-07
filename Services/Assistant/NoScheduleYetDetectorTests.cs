// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NoScheduleYetDetector — covers the silence once any work exists and the three
/// setup stages, including that each stage renders its own i18n key so the message never claims
/// that orders are missing in an installation that holds them.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NoScheduleYetDetectorTests
{
    private IScheduleActivityProbe _activityProbe = null!;
    private NoScheduleYetDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _sut = new NoScheduleYetDetector(_activityProbe, NullLogger<NoScheduleYetDetector>.Instance);
    }

    private void StubState(bool hasOrders, bool hasShifts, bool hasWork) =>
        _activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(hasOrders, hasShifts, hasWork, false, false));

    [Test]
    public async Task DetectAsync_AnyWorkExists_EmitsNothing()
    {
        StubState(hasOrders: true, hasShifts: true, hasWork: true);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_EmptyInstallation_ReportsNothingYet()
    {
        StubState(hasOrders: false, hasShifts: false, hasWork: false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].Summary, Does.Contain(ProactiveMessageI18nKeys.SetupNothingYet));
    }

    [Test]
    public async Task DetectAsync_OrdersWithoutShifts_ReportsOrdersButNoShifts()
    {
        StubState(hasOrders: true, hasShifts: false, hasWork: false);

        var events = await _sut.DetectAsync();

        Assert.That(events[0].Summary, Does.Contain(ProactiveMessageI18nKeys.SetupOrdersButNoShifts));
    }

    [Test]
    public async Task DetectAsync_ShiftsWithoutWork_ReportsShiftsButNoWork()
    {
        StubState(hasOrders: true, hasShifts: true, hasWork: false);

        var events = await _sut.DetectAsync();

        Assert.That(events[0].Summary, Does.Contain(ProactiveMessageI18nKeys.SetupShiftsButNoWork));
    }

    [Test]
    public async Task DetectAsync_EachStage_CarriesItsOwnDedupKey()
    {
        StubState(hasOrders: false, hasShifts: false, hasWork: false);
        var nothingYet = (await _sut.DetectAsync())[0];

        StubState(hasOrders: true, hasShifts: true, hasWork: false);
        var shiftsButNoWork = (await _sut.DetectAsync())[0];

        Assert.That(nothingYet.DedupKey, Is.Not.EqualTo(shiftsButNoWork.DedupKey));
    }

    [Test]
    public async Task DetectAsync_EmittedEvent_IsPlannersOnlyAndUngrouped()
    {
        StubState(hasOrders: false, hasShifts: false, hasWork: false);

        var triggerEvent = (await _sut.DetectAsync())[0];

        Assert.Multiple(() =>
        {
            Assert.That(triggerEvent.PlannersOnly, Is.True);
            Assert.That(triggerEvent.GroupId, Is.Null);
            Assert.That(triggerEvent.Kind, Is.EqualTo(AgentTriggerKinds.NoScheduleYet));
        });
    }
}
