// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Domain.Interfaces;
using Klacks.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ShiftExpensesRepositoryTests
{
    private DataBaseContext _context;
    private ShiftRepository _repository;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        var mockLogger = Substitute.For<ILogger<Shift>>();
        var mockQueryPipeline = Substitute.For<IShiftQueryPipelineService>();
        var mockGroupManagement = Substitute.For<IShiftGroupManagementService>();
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var mockShiftValidator = Substitute.For<IShiftValidator>();
        var scheduleMapper = new ScheduleMapper();
        _repository = new ShiftRepository(
            _context, mockLogger, mockQueryPipeline, mockGroupManagement, collectionUpdateService, mockShiftValidator,
            scheduleMapper, new FixedCompanyClock(DateTimeOffset.UtcNow));
    }

    [Test]
    public async Task Add_ShiftWithExpenses_PersistsExpenses()
    {
        var shift = CreateShift();
        shift.ShiftExpenses =
        [
            new ShiftExpenses { Amount = 25.50m, Description = "Fahrtkosten", Taxable = true },
            new ShiftExpenses { Amount = 10.00m, Description = "Verpflegung", Taxable = false }
        ];

        await _repository.Add(shift);
        await _context.SaveChangesAsync();

        var saved = await _context.Shift
            .Include(s => s.ShiftExpenses)
            .FirstOrDefaultAsync(s => s.Id == shift.Id);

        saved.ShouldNotBeNull();
        saved!.ShiftExpenses.Count().ShouldBe(2);
        saved.ShiftExpenses.ShouldContain(e => e.Description == "Fahrtkosten" && e.Amount == 25.50m);
        saved.ShiftExpenses.ShouldContain(e => e.Description == "Verpflegung" && e.Taxable == false);
        saved.ShiftExpenses.ShouldAllBe(e => e.ShiftId == shift.Id);
    }

    [Test]
    public async Task Add_ShiftWithoutExpenses_PersistsEmptyList()
    {
        var shift = CreateShift();

        await _repository.Add(shift);
        await _context.SaveChangesAsync();

        var saved = await _context.Shift
            .Include(s => s.ShiftExpenses)
            .FirstOrDefaultAsync(s => s.Id == shift.Id);

        saved.ShouldNotBeNull();
        saved!.ShiftExpenses.ShouldBeEmpty();
    }

    [Test]
    public async Task Put_AddNewExpense_PersistsNewExpense()
    {
        var shift = CreateShift();
        shift.ShiftExpenses =
        [
            new ShiftExpenses { Amount = 25.50m, Description = "Fahrtkosten", Taxable = true }
        ];

        await _repository.Add(shift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updatedShift = CreateShiftCopy(shift);
        updatedShift.ShiftExpenses =
        [
            new ShiftExpenses { Id = shift.ShiftExpenses[0].Id, ShiftId = shift.Id, Amount = 25.50m, Description = "Fahrtkosten", Taxable = true },
            new ShiftExpenses { Amount = 15.00m, Description = "Parkgebühr", Taxable = true }
        ];

        await _repository.Put(updatedShift);
        await _context.SaveChangesAsync();

        var saved = await _context.Shift
            .Include(s => s.ShiftExpenses)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shift.Id);

        saved.ShouldNotBeNull();
        saved!.ShiftExpenses.Count().ShouldBe(2);
        saved.ShiftExpenses.ShouldContain(e => e.Description == "Parkgebühr");
    }

    [Test]
    public async Task Put_RemoveExpense_DeletesExpense()
    {
        var shift = CreateShift();
        shift.ShiftExpenses =
        [
            new ShiftExpenses { Amount = 25.50m, Description = "Fahrtkosten", Taxable = true },
            new ShiftExpenses { Amount = 10.00m, Description = "Verpflegung", Taxable = false }
        ];

        await _repository.Add(shift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updatedShift = CreateShiftCopy(shift);
        updatedShift.ShiftExpenses =
        [
            new ShiftExpenses { Id = shift.ShiftExpenses[0].Id, ShiftId = shift.Id, Amount = 25.50m, Description = "Fahrtkosten", Taxable = true }
        ];

        await _repository.Put(updatedShift);
        await _context.SaveChangesAsync();

        var saved = await _context.Shift
            .Include(s => s.ShiftExpenses)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shift.Id);

        saved.ShouldNotBeNull();
        saved!.ShiftExpenses.Count().ShouldBe(1);
        saved.ShiftExpenses[0].Description.ShouldBe("Fahrtkosten");
    }

    [Test]
    public async Task Put_UpdateExpenseAmount_PersistsChange()
    {
        var shift = CreateShift();
        shift.ShiftExpenses =
        [
            new ShiftExpenses { Amount = 25.50m, Description = "Fahrtkosten", Taxable = true }
        ];

        await _repository.Add(shift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updatedShift = CreateShiftCopy(shift);
        updatedShift.ShiftExpenses =
        [
            new ShiftExpenses { Id = shift.ShiftExpenses[0].Id, ShiftId = shift.Id, Amount = 30.00m, Description = "Fahrtkosten", Taxable = true }
        ];

        await _repository.Put(updatedShift);
        await _context.SaveChangesAsync();

        var saved = await _context.Shift
            .Include(s => s.ShiftExpenses)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shift.Id);

        saved.ShouldNotBeNull();
        saved!.ShiftExpenses.Count().ShouldBe(1);
        saved.ShiftExpenses[0].Amount.ShouldBe(30.00m);
    }

    [Test]
    public async Task Get_ShiftWithExpenses_IncludesExpenses()
    {
        var shift = CreateShift();
        shift.ShiftExpenses =
        [
            new ShiftExpenses { Amount = 25.50m, Description = "Fahrtkosten", Taxable = true }
        ];

        _context.Shift.Add(shift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var loaded = await _repository.Get(shift.Id);

        loaded.ShouldNotBeNull();
        loaded!.ShiftExpenses.Count().ShouldBe(1);
        loaded.ShiftExpenses[0].Description.ShouldBe("Fahrtkosten");
    }

    private static Shift CreateShift()
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Shift",
            Status = ShiftStatus.OriginalOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
        };
    }

    private static Shift CreateShiftCopy(Shift source)
    {
        return new Shift
        {
            Id = source.Id,
            Name = source.Name,
            Status = source.Status,
            FromDate = source.FromDate,
            StartShift = source.StartShift,
            EndShift = source.EndShift,
        };
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }
}
