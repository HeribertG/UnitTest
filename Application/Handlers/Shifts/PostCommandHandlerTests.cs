// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Shift PostCommandHandler: applies the resolved default macro only when the
/// caller left MacroId empty, never overrides an explicitly supplied macro, and enforces
/// ClientlessOrderRules - a draft without a customer is refused, and an order that deliberately has
/// none must already meet the sealing requirements, because it is sealed on creation and sealing
/// cannot be undone.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class PostCommandHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private IOrderSealingService _orderSealingService = null!;
    private ILogger<PostCommandHandler> _logger = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();
        _orderSealingService = Substitute.For<IOrderSealingService>();
        _orderSealingService.CollectMissingRequirements(Arg.Any<Shift>()).Returns(Array.Empty<string>());
        _logger = Substitute.For<ILogger<PostCommandHandler>>();

        _handler = new PostCommandHandler(
            _shiftRepository,
            _mapper,
            _unitOfWork,
            _defaultShiftMacroResolver,
            _orderSealingService,
            _logger);

        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci => ci.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns((Shift?)null);
    }

    [Test]
    public async Task Handle_WithoutMacroId_AppliesResolvedDefaultMacro()
    {
        var defaultMacroId = Guid.NewGuid();
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns(defaultMacroId);
        var resource = new ShiftResource { Name = "Early shift", MacroId = null, ClientId = Guid.NewGuid() };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBe(defaultMacroId);
        await _defaultShiftMacroResolver.Received(1).ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithoutMacroId_AndNoDefaultConfigured_LeavesMacroIdNull()
    {
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        var resource = new ShiftResource { Name = "Early shift", MacroId = null, ClientId = Guid.NewGuid() };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBeNull();
    }

    [Test]
    public async Task Handle_WithExplicitMacroId_KeepsItUntouched_AndDoesNotConsultResolver()
    {
        var explicitMacroId = Guid.NewGuid();
        var resource = new ShiftResource { Name = "Early shift", MacroId = explicitMacroId, ClientId = Guid.NewGuid() };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBe(explicitMacroId);
        await _defaultShiftMacroResolver.DidNotReceive().ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Handle_DraftWithoutCustomer_IsRefused()
    {
        var resource = new ShiftResource { Name = "Early shift", Status = ShiftStatus.OriginalOrder };

        var act = async () => await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        Should.ThrowAsync<BadRequestException>(act);
    }

    [Test]
    public async Task Handle_SealedOrderWithoutCustomer_AndComplete_IsCreated()
    {
        var resource = new ShiftResource { Name = "Ward night shift", Status = ShiftStatus.SealedOrder };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public void Handle_SealedOrderWithoutCustomer_AndIncomplete_IsRefused()
    {
        _orderSealingService.CollectMissingRequirements(Arg.Any<Shift>())
            .Returns(new[] { "group", "quantity" });
        var resource = new ShiftResource { Name = "Ward night shift", Status = ShiftStatus.SealedOrder };

        var act = async () => await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        Should.ThrowAsync<BadRequestException>(act);
    }

    [Test]
    public async Task Handle_SealedOrderWithCustomer_IsNotCheckedAgainstSealingRequirements()
    {
        _orderSealingService.CollectMissingRequirements(Arg.Any<Shift>())
            .Returns(new[] { "group" });
        var resource = new ShiftResource
        {
            Name = "Guard duty",
            Status = ShiftStatus.SealedOrder,
            ClientId = Guid.NewGuid()
        };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        _orderSealingService.DidNotReceive().CollectMissingRequirements(Arg.Any<Shift>());
    }

    [Test]
    public async Task Handle_ContainerWithoutCustomer_IsNotAffected()
    {
        var resource = new ShiftResource
        {
            Name = "Container",
            Status = ShiftStatus.OriginalOrder,
            ShiftType = ShiftType.IsContainer
        };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
    }
}
