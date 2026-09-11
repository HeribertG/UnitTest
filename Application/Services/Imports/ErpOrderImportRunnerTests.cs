using System.Text;
using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpOrderImportRunnerTests
{
    private IErpDropPointRepository _dropPointRepository = null!;
    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IOrderImportParser _parser = null!;
    private IClientRepository _clientRepository = null!;
    private IShiftRepository _shiftRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IAgentTriggerService _triggerService = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private FixedCompanyClock _companyClock = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ErpImportRunState _runState = null!;
    private ErpOrderImportRunner _runner = null!;

    private static readonly ErpDropPoint DropPoint = new()
    {
        Id = Guid.NewGuid(),
        Name = "Test ERP",
        SourceSystemId = "erp-1",
        BucketPrefix = "customer-1",
        IsEnabled = true
    };

    [SetUp]
    public void SetUp()
    {
        _dropPointRepository = Substitute.For<IErpDropPointRepository>();
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _parser = Substitute.For<IOrderImportParser>();
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Client>>>())
            .Returns(ci => ci.Arg<Func<Task<Client>>>()());

        _objectStorageService.TryClaimAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _settingsRepository.TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        _dropPointRepository.List().Returns([DropPoint]);

        // Due immediately: NextRunUtc already in the past.
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new Settings { Type = ErpImportSettingsTypes.NextRunUtc, Value = DateTime.UtcNow.AddMinutes(-5).ToString("O") });

        _runState = new ErpImportRunState();

        var resolver = new ErpCustomerResolver(_clientRepository);
        var supersessionService = new OrderSupersessionService(_shiftRepository, _workRepository, _clientRepository, _triggerService, ShiftGroupScopeReaderStub.WithoutAnyGroups(), _unitOfWork, _companyClock, NullLogger<OrderSupersessionService>.Instance);
        _runner = new ErpOrderImportRunner(_dropPointRepository, _defaultDropPointProvider, _objectStorageService, _parser, resolver, _shiftRepository, supersessionService, _exceptionRepository, _triggerService, _settingsRepository, _companyClock, _unitOfWork, _runState, NullLogger<ErpOrderImportRunner>.Instance);
    }

    private static ImportedOrderPayload Order(string reference = "ORD-1") => new()
    {
        SourceSystemId = "erp-1",
        ExternalOrderReference = reference,
        Customer = new ImportedCustomerPayload { Company = "Spitex Musterhausen", Street = "Hauptstrasse 5", Zip = "4000" },
        FromDate = new DateOnly(2026, 8, 1),
        StartTime = new TimeOnly(7, 0),
        EndTime = new TimeOnly(15, 0)
    };

    [Test]
    public async Task RunAsync_NotYetDue_SkipsEntirely()
    {
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new Settings { Type = ErpImportSettingsTypes.NextRunUtc, Value = DateTime.UtcNow.AddHours(1).ToString("O") });

        await _runner.RunAsync();

        await _objectStorageService.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_FirstEverRun_ComputesScheduleWithoutFiring()
    {
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc).Returns((Settings?)null);

        await _runner.RunAsync();

        await _objectStorageService.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _settingsRepository.Received(1).AddSetting(Arg.Is<Settings>(s => s.Type == ErpImportSettingsTypes.NextRunUtc));
    }

    [Test]
    public async Task RunAsync_Due_CreatesDefaultDropPointBeforeListing()
    {
        await _runner.RunAsync();

        await _defaultDropPointProvider.Received(1).GetOrCreateDefaultAsync(Arg.Any<CancellationToken>());
        await _dropPointRepository.Received(1).List();
    }

    [Test]
    public async Task RunAsync_NotYetDue_DoesNotCreateDefaultDropPoint()
    {
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new Settings { Type = ErpImportSettingsTypes.NextRunUtc, Value = DateTime.UtcNow.AddHours(1).ToString("O") });

        await _runner.RunAsync();

        await _defaultDropPointProvider.DidNotReceive().GetOrCreateDefaultAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OccurrenceClaimedByAnotherInstance_SkipsEntirely()
    {
        _settingsRepository.TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await _runner.RunAsync();

        await _settingsRepository.Received(1).TryAdvanceSettingAsync(ErpImportSettingsTypes.NextRunUtc, Arg.Any<string>(), Arg.Any<string>());
        await _objectStorageService.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NewOrder_CreatesDraftShift()
    {
        SetupFile("customer-1/order-1.xml", Order());
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);

        await _runner.RunAsync();

        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s =>
            s.Status == ShiftStatus.OriginalOrder &&
            s.SourceSystemId == "erp-1" &&
            s.ExternalOrderReference == "ORD-1"));
    }

    [Test]
    public async Task RunAsync_ExistingDraftOrder_IsOverwritten()
    {
        SetupFile("customer-1/order-1.xml", Order());
        var existingDraft = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalOrder, SourceSystemId = "erp-1", ExternalOrderReference = "ORD-1" };
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns(existingDraft);

        await _runner.RunAsync();

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == existingDraft.Id));
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task RunAsync_ExistingSealedOrder_UnchangedPayload_IsNoOp()
    {
        var order = Order();
        SetupFile("customer-1/order-1.xml", order);
        var existingClient = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.Customer, Company = "Spitex Musterhausen" };
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>()).Returns(existingClient);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            SourceSystemId = "erp-1",
            ExternalOrderReference = "ORD-1",
            ClientId = existingClient.Id,
            Description = order.Description,
            WorkTime = ImportedOrderShiftMapper.ResolveWorkTimeHours(order),
            FromDate = order.FromDate,
            UntilDate = order.UntilDate,
            StartShift = order.StartTime,
            EndShift = order.EndTime,
            IsTimeRange = order.IsTimeRange,
            IsMonday = order.IsMonday,
            IsTuesday = order.IsTuesday,
            IsWednesday = order.IsWednesday,
            IsThursday = order.IsThursday,
            IsFriday = order.IsFriday,
            IsSaturday = order.IsSaturday,
            IsSunday = order.IsSunday,
            Quantity = order.Quantity,
            SumEmployees = order.SumEmployees
        };
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns(sealedOrder);

        await _runner.RunAsync();

        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _triggerService.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ProcessedFile_IsMovedToProcessedSegment()
    {
        SetupFile("customer-1/order-1.xml", Order());
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);

        await _runner.RunAsync();

        await _objectStorageService.Received(1).TryClaimAsync("customer-1/order-1.xml", "customer-1/processing/order-1.xml", Arg.Any<CancellationToken>());
        await _objectStorageService.Received(1).DownloadAsync("customer-1/processing/order-1.xml", Arg.Any<CancellationToken>());
        await _objectStorageService.Received(1).MoveAsync("customer-1/processing/order-1.xml", "customer-1/processed/order-1.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RootValidationFailure_MovesFileToErrorSegmentWithoutProcessingOrders()
    {
        _objectStorageService.ListAsync("customer-1/", Arg.Any<CancellationToken>()).Returns(["customer-1/broken.xml"]);
        _objectStorageService.DownloadAsync("customer-1/processing/broken.xml", Arg.Any<CancellationToken>()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("not xml")));
        _parser.Parse(Arg.Any<Stream>()).Returns(new OrderImportParseResult
        {
            Errors = [new OrderImportValidationError { Field = "root", Message = "malformed" }]
        });

        await _runner.RunAsync();

        await _objectStorageService.Received(1).MoveAsync("customer-1/processing/broken.xml", "customer-1/error/broken.xml", Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _exceptionRepository.Received(1).Add(Arg.Is<ErpImportException>(e => e.SourceSystemId == "erp-1" && e.FileKey == "customer-1/broken.xml"));
        await _triggerService.Received(1).OnEventAsync(Arg.Is<IAgentTriggerEvent>(e => e.Kind == AgentTriggerKinds.OrderImportFailed && e.AdminOnly), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OrderLevelRejection_RecordsExceptionAndQuarantinesFileWhileValidOrdersImport()
    {
        _objectStorageService.ListAsync("customer-1/", Arg.Any<CancellationToken>()).Returns(["customer-1/mixed.xml"]);
        _objectStorageService.DownloadAsync("customer-1/processing/mixed.xml", Arg.Any<CancellationToken>()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("<xml/>")));
        _parser.Parse(Arg.Any<Stream>()).Returns(new OrderImportParseResult
        {
            Orders = [Order()],
            Errors = [new OrderImportValidationError { Field = "Weekdays", Message = "no weekday", ExternalOrderReference = "ORD-2" }]
        });
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>()).Returns((Client?)null);
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);

        await _runner.RunAsync();

        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.ExternalOrderReference == "ORD-1"));
        await _exceptionRepository.Received(1).Add(Arg.Is<ErpImportException>(e =>
            e.ExternalOrderReference == "ORD-2" &&
            e.FileKey == "customer-1/mixed.xml" &&
            e.Reason.Contains("no weekday")));
        await _objectStorageService.Received(1).MoveAsync("customer-1/processing/mixed.xml", "customer-1/error/mixed.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_AllOrdersRejected_RecordsOneExceptionPerOrder()
    {
        _objectStorageService.ListAsync("customer-1/", Arg.Any<CancellationToken>()).Returns(["customer-1/all-bad.xml"]);
        _objectStorageService.DownloadAsync("customer-1/processing/all-bad.xml", Arg.Any<CancellationToken>()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("<xml/>")));
        _parser.Parse(Arg.Any<Stream>()).Returns(new OrderImportParseResult
        {
            Errors =
            [
                new OrderImportValidationError { Field = "Weekdays", Message = "no weekday", ExternalOrderReference = "ORD-1" },
                new OrderImportValidationError { Field = "Weekdays", Message = "no weekday", ExternalOrderReference = "ORD-2" }
            ]
        });

        await _runner.RunAsync();

        await _exceptionRepository.Received(1).Add(Arg.Is<ErpImportException>(e => e.ExternalOrderReference == "ORD-1"));
        await _exceptionRepository.Received(1).Add(Arg.Is<ErpImportException>(e => e.ExternalOrderReference == "ORD-2"));
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _objectStorageService.Received(1).MoveAsync("customer-1/processing/all-bad.xml", "customer-1/error/all-bad.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OrderProcessingFailure_RecordsExceptionAndQuarantinesFile()
    {
        SetupFile("customer-1/order-1.xml", Order());
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).ThrowsAsync(new InvalidOperationException("db down"));

        await _runner.RunAsync();

        await _exceptionRepository.Received(1).Add(Arg.Is<ErpImportException>(e =>
            e.ExternalOrderReference == "ORD-1" &&
            e.Reason.Contains("db down")));
        await _objectStorageService.Received(1).MoveAsync("customer-1/processing/order-1.xml", "customer-1/error/order-1.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_AlreadyProcessedFiles_AreSkipped()
    {
        _objectStorageService.ListAsync("customer-1/", Arg.Any<CancellationToken>()).Returns([
            "customer-1/processed/order-old.xml",
            "customer-1/error/order-bad.xml"
        ]);

        await _runner.RunAsync();

        await _objectStorageService.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_FilesAlreadyInProcessingSegment_AreSkipped()
    {
        _objectStorageService.ListAsync("customer-1/", Arg.Any<CancellationToken>()).Returns([
            "customer-1/processing/order-claimed.xml"
        ]);

        await _runner.RunAsync();

        await _objectStorageService.DidNotReceive().TryClaimAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ClaimLostToConcurrentRun_LeavesFileUntouched()
    {
        SetupFile("customer-1/order-1.xml", Order());
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);
        _objectStorageService.TryClaimAsync("customer-1/order-1.xml", "customer-1/processing/order-1.xml", Arg.Any<CancellationToken>()).Returns(false);

        await _runner.RunAsync();

        await _objectStorageService.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _parser.DidNotReceive().Parse(Arg.Any<Stream>());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _objectStorageService.DidNotReceive().MoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NotYetDue_LeavesRunStateFalse()
    {
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new Settings { Type = ErpImportSettingsTypes.NextRunUtc, Value = DateTime.UtcNow.AddHours(1).ToString("O") });

        await _runner.RunAsync();

        _runState.IsRunning.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_ProcessedFile_LeavesRunStateFalseAfterCompletion()
    {
        SetupFile("customer-1/order-1.xml", Order());
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);

        await _runner.RunAsync();

        _runState.IsRunning.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_OrderProcessingFailure_StillLeavesRunStateFalse()
    {
        SetupFile("customer-1/order-1.xml", Order());
        _shiftRepository.FindActiveByExternalReferenceAsync("erp-1", "ORD-1", Arg.Any<CancellationToken>()).Returns((Shift?)null);
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).ThrowsAsync(new InvalidOperationException("db down"));

        await _runner.RunAsync();

        _runState.IsRunning.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_DisabledDropPoint_IsSkipped()
    {
        var disabled = new ErpDropPoint { Id = Guid.NewGuid(), Name = "Disabled", SourceSystemId = "erp-2", BucketPrefix = "customer-2", IsEnabled = false };
        _dropPointRepository.List().Returns([disabled]);

        await _runner.RunAsync();

        await _objectStorageService.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void SetupFile(string key, ImportedOrderPayload order)
    {
        _objectStorageService.ListAsync("customer-1/", Arg.Any<CancellationToken>()).Returns([key]);
        _objectStorageService.DownloadAsync(ClaimedKey(key), Arg.Any<CancellationToken>()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("<xml/>")));
        _parser.Parse(Arg.Any<Stream>()).Returns(new OrderImportParseResult { Orders = [order] });
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>()).Returns((Client?)null);
    }

    private static string ClaimedKey(string key) => "customer-1/processing/" + key["customer-1/".Length..];
}
