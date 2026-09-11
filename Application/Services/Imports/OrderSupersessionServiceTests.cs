using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class OrderSupersessionServiceTests
{
    private IShiftRepository _shiftRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IAgentTriggerService _triggerService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private OrderSupersessionService _service = null!;
    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _service = new OrderSupersessionService(
            _shiftRepository, _workRepository, _clientRepository, _triggerService, _groupScopeReader, _unitOfWork,
            new FixedCompanyClock(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<OrderSupersessionService>.Instance);
    }

    private static Shift SealedOrder() => new()
    {
        Id = Guid.NewGuid(),
        Status = ShiftStatus.SealedOrder,
        SourceSystemId = "erp-1",
        ExternalOrderReference = "ORD-1",
        ClientId = ClientId,
        FromDate = new DateOnly(2026, 8, 1),
        StartShift = new TimeOnly(7, 0),
        EndShift = new TimeOnly(15, 0),
        WorkTime = 8,
        Quantity = 1,
        SumEmployees = 1
    };

    private static ImportedOrderPayload ChangedOrder() => new()
    {
        SourceSystemId = "erp-1",
        ExternalOrderReference = "ORD-1",
        Customer = new ImportedCustomerPayload { Company = "Spitex Musterhausen" },
        FromDate = new DateOnly(2026, 8, 1),
        StartTime = new TimeOnly(8, 0), // changed
        EndTime = new TimeOnly(16, 0) // changed
    };

    [Test]
    public async Task HandleAsync_UnchangedPayload_DoesNothing()
    {
        var sealedOrder = SealedOrder();
        var unchanged = new ImportedOrderPayload
        {
            SourceSystemId = sealedOrder.SourceSystemId!,
            ExternalOrderReference = sealedOrder.ExternalOrderReference!,
            Customer = new ImportedCustomerPayload(),
            FromDate = sealedOrder.FromDate,
            StartTime = sealedOrder.StartShift,
            EndTime = sealedOrder.EndShift
        };

        await _service.HandleAsync(sealedOrder, unchanged, ClientId);

        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _workRepository.DidNotReceive().GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_ChangedPayload_ClosesOldOrder()
    {
        var sealedOrder = SealedOrder();
        _shiftRepository.CutList(sealedOrder.Id, null, false).Returns([sealedOrder]);
        _workRepository.GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());

        await _service.HandleAsync(sealedOrder, ChangedOrder(), ClientId);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == sealedOrder.Id && s.UntilDate == new DateOnly(2026, 8, 15)));
    }

    [Test]
    public async Task HandleAsync_ChangedPayload_OpensNewDraftLinkedViaSupersedesOrderId()
    {
        var sealedOrder = SealedOrder();
        _shiftRepository.CutList(sealedOrder.Id, null, false).Returns([sealedOrder]);
        _workRepository.GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());

        await _service.HandleAsync(sealedOrder, ChangedOrder(), ClientId);

        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s =>
            s.Status == ShiftStatus.OriginalOrder &&
            s.SupersedesOrderId == sealedOrder.Id &&
            s.ExternalOrderReference == "ORD-1" &&
            s.StartShift == new TimeOnly(8, 0)));
    }

    [Test]
    public async Task HandleAsync_ChangedPayload_CancelsFutureUnlockedWorkAndNotifiesPlanners()
    {
        var sealedOrder = SealedOrder();
        var futureWork = new Work { Id = Guid.NewGuid(), ShiftId = sealedOrder.Id, ClientId = ClientId, CurrentDate = new DateOnly(2026, 9, 1), LockLevel = WorkLockLevel.None };
        _shiftRepository.CutList(sealedOrder.Id, null, false).Returns([sealedOrder]);
        _workRepository.GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([futureWork]);
        _clientRepository.GetNoTracking(ClientId).Returns(new Client { Id = ClientId, FirstName = "Jane", Name = "Doe" });

        await _service.HandleAsync(sealedOrder, ChangedOrder(), ClientId);

        await _workRepository.Received(1).Delete(futureWork.Id);
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e => e.Kind == AgentTriggerKinds.WorkDroppedByErpImport && e.PlannersOnly),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_CancelledWork_ScopesTheAlertToEveryGroupOfItsShift()
    {
        // Resolved by SHIFT id on purpose: the work has just been soft-deleted, and the work-keyed
        // lookup excludes deleted rows, so switching to it would silently narrow every ERP-supersede
        // alert to admins. Both groups must survive - a shift can be a member of several at once.
        var sealedOrder = SealedOrder();
        var futureWork = new Work { Id = Guid.NewGuid(), ShiftId = sealedOrder.Id, ClientId = ClientId, CurrentDate = new DateOnly(2026, 9, 1), LockLevel = WorkLockLevel.None };
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        _shiftRepository.CutList(sealedOrder.Id, null, false).Returns([sealedOrder]);
        _workRepository.GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([futureWork]);
        _clientRepository.GetNoTracking(ClientId).Returns(new Client { Id = ClientId, FirstName = "Jane", Name = "Doe" });
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (sealedOrder.Id, new[] { firstGroupId, secondGroupId }));

        await _service.HandleAsync(sealedOrder, ChangedOrder(), ClientId);

        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e =>
                e.RequiresGroupScope
                && e.GroupIds.Count == 2
                && e.GroupIds.Contains(firstGroupId)
                && e.GroupIds.Contains(secondGroupId)),
            Arg.Any<CancellationToken>());
        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _groupScopeReader.DidNotReceiveWithAnyArgs().GetGroupIdsByWorkIdsAsync(default!, default);
    }

    [Test]
    public async Task HandleAsync_CancelledWorkOnAnUngroupedShift_CarriesNoGroup_AndStaysGroupScopeRequired()
    {
        var sealedOrder = SealedOrder();
        var futureWork = new Work { Id = Guid.NewGuid(), ShiftId = sealedOrder.Id, ClientId = ClientId, CurrentDate = new DateOnly(2026, 9, 1), LockLevel = WorkLockLevel.None };
        _shiftRepository.CutList(sealedOrder.Id, null, false).Returns([sealedOrder]);
        _workRepository.GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([futureWork]);
        _clientRepository.GetNoTracking(ClientId).Returns(new Client { Id = ClientId, FirstName = "Jane", Name = "Doe" });

        await _service.HandleAsync(sealedOrder, ChangedOrder(), ClientId);

        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e => e.RequiresGroupScope && e.GroupIds.Count == 0),
            Arg.Any<CancellationToken>());
    }
}
