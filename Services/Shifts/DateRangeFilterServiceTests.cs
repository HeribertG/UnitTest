using Shouldly;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class DateRangeFilterServiceTests
{
    private DateRangeFilterService _service;
    private List<Shift> _testShifts;
    private DateOnly _today;

    [SetUp]
    public void SetUp()
    {
        _service = new DateRangeFilterService();

        _today = DateOnly.FromDateTime(DateTime.UtcNow);
        var today = _today;

        _testShifts = new List<Shift>
        {
            // Active shift (started yesterday, ends tomorrow)
            new Shift { Id = Guid.NewGuid(), Name = "Active Shift", FromDate = today.AddDays(-1), UntilDate = today.AddDays(1) },

            // Former shift (ended yesterday)
            new Shift { Id = Guid.NewGuid(), Name = "Former Shift", FromDate = today.AddDays(-10), UntilDate = today.AddDays(-1) },

            // Future shift (starts tomorrow)
            new Shift { Id = Guid.NewGuid(), Name = "Future Shift", FromDate = today.AddDays(1), UntilDate = today.AddDays(5) },

            // Active shift with no end date
            new Shift { Id = Guid.NewGuid(), Name = "Active No End", FromDate = today.AddDays(-5), UntilDate = null },

            // Edge case: starts today
            new Shift { Id = Guid.NewGuid(), Name = "Starts Today", FromDate = today, UntilDate = today.AddDays(3) },

            // Edge case: ends today
            new Shift { Id = Guid.NewGuid(), Name = "Ends Today", FromDate = today.AddDays(-3), UntilDate = today }
        };
    }

    [Test]
    public void ApplyDateRangeFilter_ActiveOnly_ReturnsOnlyActiveShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: true, formerDateRange: false, futureDateRange: false, today: _today);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(4); // Active Shift, Active No End, Starts Today, Ends Today
        shifts.ShouldContain(s => s.Name == "Active Shift");
        shifts.ShouldContain(s => s.Name == "Active No End");
        shifts.ShouldContain(s => s.Name == "Starts Today");
        shifts.ShouldContain(s => s.Name == "Ends Today");
    }

    [Test]
    public void ApplyDateRangeFilter_FormerOnly_ReturnsOnlyFormerShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: false, formerDateRange: true, futureDateRange: false, today: _today);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(1);
        shifts.ShouldContain(s => s.Name == "Former Shift");
    }

    [Test]
    public void ApplyDateRangeFilter_FutureOnly_ReturnsOnlyFutureShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: false, formerDateRange: false, futureDateRange: true, today: _today);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(1);
        shifts.ShouldContain(s => s.Name == "Future Shift");
    }

    [Test]
    public void ApplyDateRangeFilter_AllFlags_ReturnsAllShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: true, formerDateRange: true, futureDateRange: true, today: _today);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(_testShifts.Count);
    }

    [Test]
    public void ApplyDateRangeFilter_NoFlags_ReturnsEmpty()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: false, formerDateRange: false, futureDateRange: false, today: _today);
        var shifts = result.ToList();

        // Assert
        shifts.ShouldBeEmpty();
    }

    [Test]
    public void ApplyDateRangeFilter_ActiveAndFormer_ReturnsActiveAndFormerShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: true, formerDateRange: true, futureDateRange: false, today: _today);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(5); // All except Future Shift (4 active + 1 former)
        shifts.ShouldNotContain(s => s.Name == "Future Shift");
    }

    [Test]
    public void ApplyDateRangeFilter_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // The company day (Pacific/Auckland) is 2026-06-28 at the UTC instant 2026-06-27T23:30Z. A
        // shift starting exactly on 2026-06-28 must be classified active once the caller resolves
        // "today" as the company day, even though the UTC day is still 2026-06-27.
        var companyDay = new DateOnly(2026, 6, 28);
        var shift = new Shift { Id = Guid.NewGuid(), Name = "Starts On Company Day", FromDate = companyDay, UntilDate = companyDay.AddDays(2) };
        var query = new List<Shift> { shift }.AsQueryable();

        var activeUnderCompanyDay = _service.ApplyDateRangeFilter(query, true, false, false, companyDay).ToList();
        var activeUnderWrongUtcDay = _service.ApplyDateRangeFilter(query, true, false, false, companyDay.AddDays(-1)).ToList();

        activeUnderCompanyDay.ShouldContain(s => s.Id == shift.Id);
        activeUnderWrongUtcDay.ShouldNotContain(s => s.Id == shift.Id);
    }
}
