// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PlanningScopeEnricher (WP-P0.2 per-client scope): resolves the in-scope client's
/// effective scheduling policy onto the LLMContext only when the turn is a scheduling context AND a
/// valid SelectedClientId is present; otherwise it is a no-op and never throws on malformed input.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Assistant;

[TestFixture]
public class PlanningScopeEnricherTests
{
    private static readonly DateTimeOffset CompanyNow = new(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);

    private IRuleContextProvider _ruleContext = null!;
    private ISchedulingPolicyResolver _resolver = null!;
    private PlanningScopeEnricher _sut = null!;

    private static readonly SchedulingPolicy Policy = new(
        TimeSpan.FromHours(11), TimeSpan.FromHours(10), 6, TimeSpan.FromHours(50), 2);

    [SetUp]
    public void SetUp()
    {
        _ruleContext = Substitute.For<IRuleContextProvider>();
        _resolver = Substitute.For<ISchedulingPolicyResolver>();
        _resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(Policy);
        _sut = new PlanningScopeEnricher(_ruleContext, _resolver, new FixedCompanyClock(CompanyNow));
    }

    private static LLMContext Ctx(bool schedulingSkill, string? clientId, string? periodFrom = null)
        => new()
        {
            AvailableFunctions = schedulingSkill
                ? new() { new LLMFunction { Name = "find_replacement" } }
                : new() { new LLMFunction { Name = "get_user_context" } },
            PageContext = new AssistantPageContext { SelectedClientId = clientId, SelectedPeriodFrom = periodFrom }
        };

    [Test]
    public async Task SchedulingContext_WithClient_ResolvesAndStampsPolicy()
    {
        _ruleContext.IsSchedulingContext(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var clientId = Guid.NewGuid();
        var ctx = Ctx(schedulingSkill: true, clientId.ToString(), periodFrom: "2026-06-01");

        await _sut.EnrichAsync(ctx);

        ctx.ScopedClientPolicy.ShouldBe(Policy);
        await _resolver.Received(1).GetForClientAsync(clientId, new DateOnly(2026, 6, 1));
    }

    [Test]
    public async Task NonSchedulingContext_NoResolve()
    {
        _ruleContext.IsSchedulingContext(Arg.Any<IReadOnlyList<string>>()).Returns(false);
        var ctx = Ctx(schedulingSkill: false, Guid.NewGuid().ToString());

        await _sut.EnrichAsync(ctx);

        ctx.ScopedClientPolicy.ShouldBeNull();
        await _resolver.DidNotReceive().GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    [Test]
    public async Task SchedulingContext_NoClient_NoResolve()
    {
        _ruleContext.IsSchedulingContext(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var ctx = Ctx(schedulingSkill: true, clientId: null);

        await _sut.EnrichAsync(ctx);

        ctx.ScopedClientPolicy.ShouldBeNull();
        await _resolver.DidNotReceive().GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    [Test]
    public async Task SchedulingContext_InvalidClientId_NoThrow_NoResolve()
    {
        _ruleContext.IsSchedulingContext(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var ctx = Ctx(schedulingSkill: true, clientId: "not-a-guid");

        await _sut.EnrichAsync(ctx);

        ctx.ScopedClientPolicy.ShouldBeNull();
        await _resolver.DidNotReceive().GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    [Test]
    public async Task InvalidPeriodFrom_FallsBackToToday()
    {
        _ruleContext.IsSchedulingContext(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var clientId = Guid.NewGuid();
        var ctx = Ctx(schedulingSkill: true, clientId.ToString(), periodFrom: "garbage");
        var today = DateOnly.FromDateTime(CompanyNow.UtcDateTime);

        await _sut.EnrichAsync(ctx);

        ctx.ScopedClientPolicy.ShouldBe(Policy);
        await _resolver.Received(1).GetForClientAsync(clientId, today);
    }
}
