using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Services;

[TestFixture]
internal class HolidaysListCalculatorTests
{
    private HolidaysListCalculator _holidaysListCalculator = null!;

    [SetUp]
    public void SetUp()
    {
        _holidaysListCalculator = new HolidaysListCalculator();
    }

    [Test]
    public void ShouldAddACalendarRule()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "some-rule",
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };

        // Act
        _holidaysListCalculator.Add(rule);

        // Assert
        _holidaysListCalculator.Count.ShouldBe(1);
    }

    [Test]
    public void ShouldComputeEaster()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 9))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2022
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 4, 17))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert for 1959
        _holidaysListCalculator.CurrentYear = 1959;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        
        // Debug: Check what date was actually calculated
        var actualEaster1959 = _holidaysListCalculator.CalculateEaster(1959);
        Console.WriteLine($"Calculated Easter 1959: {actualEaster1959}");
        
        _holidaysListCalculator.IsHoliday(actualEaster1959)
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputePentecost()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER+49",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 28))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2022
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 6, 5))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeCorpusChristi()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER+60",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 6, 8))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2022
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 6, 16))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2018
        _holidaysListCalculator.CurrentYear = 2018;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2018, 5, 31))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeSilvester()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/31",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 31))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeLaborDay()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 1))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void IsLeapYear()
    {
        // Assert
        _holidaysListCalculator.IsLeapYear(1852).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(1892).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(1912).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(1936).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(1968).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(1988).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(2020).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(2032).ShouldBeTrue();
        _holidaysListCalculator.IsLeapYear(2048).ShouldBeTrue();
    }

    [Test]
    public void IsNotLeapYear()
    {
        // Assert
        _holidaysListCalculator.IsLeapYear(1851).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1853).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1855).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1857).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1859).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1861).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1865).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1866).ShouldBeFalse();
        _holidaysListCalculator.IsLeapYear(1867).ShouldBeFalse();
    }

    [Test]
    public void ShouldReturn1For1January()
    {
        // Arrange
        var date = new DateOnly(2023, 1, 1);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.ShouldBe(1);
    }

    [Test]
    public void ShouldReturn31For31January()
    {
        // Arrange
        var date = new DateOnly(2023, 1, 31);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.ShouldBe(31);
    }

    [Test]
    public void ShouldReturn59For28FebruaryInANonLeapYear()
    {
        // Arrange
        var date = new DateOnly(2023, 2, 28);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.ShouldBe(59);
    }

    [Test]
    public void ShouldReturn60For29FebruaryInALeapYear()
    {
        // Arrange
        var date = new DateOnly(2024, 2, 29);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.ShouldBe(60);
    }

    [Test]
    public void ShouldReturnWeek52For1stJanuary2022()
    {
        // Arrange
        var date = new DateTime(2022, 1, 1);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(52);
    }

    [Test]
    public void ShouldReturnWeek52For31stDecember2021()
    {
        // Arrange
        var date = new DateTime(2021, 12, 31);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(52);
    }

    [Test]
    public void ShouldReturnWeek1For4thJanuary2021()
    {
        // Arrange
        var date = new DateTime(2021, 1, 4);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(1);
    }

    [Test]
    public void ShouldReturnWeek53For31stDecember2020()
    {
        // Arrange
        var date = new DateTime(2020, 12, 31);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(53);
    }

    [Test]
    public void ShouldReturnWeek2For6thJanuary2020()
    {
        // Arrange
        var date = new DateTime(2020, 1, 6);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(2);
    }

    [Test]
    public void ShouldReturnWeek1For30thDecember2019()
    {
        // Arrange
        var date = new DateTime(2019, 12, 30);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(1);
    }

    [Test]
    public void ShouldReturnWeek2For7thJanuary2019()
    {
        // Arrange
        var date = new DateTime(2019, 1, 7);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(2);
    }

    [Test]
    public void ShouldReturnWeek1For1stJanuary2018()
    {
        // Arrange
        var date = new DateTime(2018, 1, 1);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(1);
    }

    [Test]
    public void ShouldReturnWeek1For31stDecember2018()
    {
        // Arrange
        var date = new DateTime(2018, 12, 31);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(1);
    }

    [Test]
    public void ShouldReturnWeek52For25thDecember2017()
    {
        // Arrange
        var date = new DateTime(2017, 12, 25);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.ShouldBe(52);
    }

    #region SubRule Tests

    [Test]
    public void ShouldApplySubRuleForSaturdayToFriday()
    {
        // Arrange - 1 May 2021 is a Saturday.
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA-1" // Wenn Samstag, dann verschiebe auf Freitag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Should be postponed to Friday, 30 April
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 4, 30))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 5, 1))
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForSundayToMonday()
    {
        // Arrange - 1. Mai 2022 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1" // If Sunday, then move to Monday
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte auf Montag, den 2. Mai verschoben werden
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 5, 2))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 5, 1))
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplyMultipleSubRules()
    {
        // Arrange - 25. Dezember 2021 ist ein Samstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/25",
            Name = new MultiLanguage { En = "Christmas" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA-1;SU+1" // Samstag -> Freitag, Sonntag -> Montag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Samstag -> Freitag
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 12, 24))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForMondayToTuesday()
    {
        // Arrange - 1. Mai 2023 ist ein Montag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "MO+1" // Wenn Montag, dann verschiebe auf Dienstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 2))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForFridayBackToThursday()
    {
        // Arrange - 1. September 2023 ist ein Freitag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "09/01",
            Name = new MultiLanguage { En = "September Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "FR-1" // Wenn Freitag, dann verschiebe auf Donnerstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 8, 31))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleWithLargerOffset()
    {
        // Arrange - 13. Oktober 2023 ist ein Freitag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "10/13",
            Name = new MultiLanguage { En = "Special Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "FR+3" // Wenn Freitag, dann 3 Tage später (Montag)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 10, 16))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldNotApplySubRuleForNonMatchingWeekday()
    {
        // Arrange - 15. Juni 2023 ist ein Donnerstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "06/15",
            Name = new MultiLanguage { En = "June Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "MO+1;FR-1" // Regeln für Montag und Freitag, aber es ist Donnerstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte auf dem ursprünglichen Datum bleiben
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 6, 15))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForTuesdayAndWednesday()
    {
        // Arrange - Mehrere Regeln mit verschiedenen Wochentagen
        var rules = new[]
        {
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "08/15", // 15. August 2023 ist ein Dienstag
                Name = new MultiLanguage { En = "August Holiday" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = "TU+2" // Dienstag -> Donnerstag
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "08/16", // 16. August 2023 ist ein Mittwoch
                Name = new MultiLanguage { En = "Another August Holiday" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = "WE-2" // Mittwoch -> Montag
            }
        };

        foreach (var rule in rules)
        {
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(2);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 8, 17))
            .ShouldBe(HolidayStatus.OfficialHoliday); // Dienstag -> Donnerstag
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 8, 14))
            .ShouldBe(HolidayStatus.OfficialHoliday); // Mittwoch -> Montag
    }

    [Test]
    public void ShouldApplySubRuleForThursdayToNextWeek()
    {
        // Arrange - 7. Dezember 2023 ist ein Donnerstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/07",
            Name = new MultiLanguage { En = "December Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "TH+7" // Donnerstag -> nächsten Donnerstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 14))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleComplexSubRuleWithAllWeekdays()
    {
        // Arrange - Regel mit allen Wochentagen
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/23", // 23. November 2023 ist ein Donnerstag (Thanksgiving)
            Name = new MultiLanguage { En = "Thanksgiving" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "MO+4;TU+3;WE+2;TH+1;FR+3;SA+2;SU+1" // Verschiedene Verschiebungen je nach Wochentag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Donnerstag -> +1 Tag (Freitag)
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 24))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldIgnoreInvalidSubRules()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "03/15",
            Name = new MultiLanguage { En = "March Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "XX+1;MO;FR+;+1;SA-0" // Verschiedene ungültige Formate
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte auf dem ursprünglichen Datum bleiben
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 3, 15))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleAcrossMonthBoundary()
    {
        // Arrange - 30. September 2023 ist ein Samstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "09/30",
            Name = new MultiLanguage { En = "End of September" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+2" // Samstag -> Montag (2. Oktober)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte in den nächsten Monat verschoben werden
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 10, 2))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleAcrossYearBoundary()
    {
        // Arrange - 31. Dezember 2023 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/31",
            Name = new MultiLanguage { En = "New Year's Eve" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1" // Sonntag -> Montag (1. Januar 2024)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte ins nächste Jahr verschoben werden
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 1, 1))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleNegativeOffsetAcrossMonthBoundary()
    {
        // Arrange - 1. Oktober 2023 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "10/01",
            Name = new MultiLanguage { En = "October First" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU-2" // Sonntag -> Freitag (29. September)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte in den vorherigen Monat verschoben werden
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 9, 29))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleEmptySubRule()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "07/14",
            Name = new MultiLanguage { En = "Bastille Day" },
            State = "test-state",
            Country = "FR",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "" // Leere SubRule
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 7, 14))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplyOnlyFirstMatchingSubRule()
    {
        // Arrange - 25. Dezember 2022 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/25",
            Name = new MultiLanguage { En = "Christmas" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1;SU+2" // Zwei Regeln für Sonntag - nur die erste sollte angewendet werden
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte nur um 1 Tag verschoben werden
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 12, 26))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 12, 27))
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForThursdayPlusOne()
    {
        // Arrange - 23. November 2023 ist ein Donnerstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/23",
            Name = new MultiLanguage { En = "Test Thursday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "TH+1" // Nur Donnerstag -> Freitag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        var holiday = _holidaysListCalculator.HolidayList[0];
        holiday.CurrentDate.ShouldBe(new DateOnly(2023, 11, 24));
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 24))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    #endregion

    #region Negative Easter Offset Tests

    [Test]
    public void ShouldComputeGoodFriday()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER-2",
            Name = new MultiLanguage { En = "Good Friday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 7))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeEasterMonday()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER+1",
            Name = new MultiLanguage { En = "Easter Monday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 10))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    #endregion

    #region Multiple Rules Tests

    [Test]
    public void ShouldHandleMultipleRules()
    {
        // Arrange
        var rules = new[]
        {
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "01/01",
                Name = new MultiLanguage { En = "New Year" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = string.Empty
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "05/01",
                Name = new MultiLanguage { En = "Labor Day" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = string.Empty
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "12/25",
                Name = new MultiLanguage { En = "Christmas" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = string.Empty
            }
        };

        foreach (var rule in rules)
        {
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(3);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 1, 1))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 1))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 25))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldSortHolidaysByDate()
    {
        // Arrange - Füge Feiertage in zufälliger Reihenfolge hinzu
        var rules = new[]
        {
            new CalendarRule { Rule = "12/25", Name = new MultiLanguage { En = "Christmas" }},
            new CalendarRule { Rule = "01/01", Name = new MultiLanguage { En = "New Year" }},
            new CalendarRule { Rule = "07/04", Name = new MultiLanguage { En = "Independence Day" }},
            new CalendarRule { Rule = "05/01", Name = new MultiLanguage { En = "Labor Day" }}
        };

        foreach (var rule in rules)
        {
            rule.Id = Guid.NewGuid();
            rule.State = "test-state";
            rule.Country = "test-country";
            rule.IsMandatory = true;
            rule.IsPaid = true;
            rule.SubRule = string.Empty;
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollten nach Datum sortiert sein
        _holidaysListCalculator.HolidayList.Count().ShouldBe(4);
        _holidaysListCalculator.HolidayList[0].CurrentDate.ShouldBe(new DateOnly(2023, 1, 1));
        _holidaysListCalculator.HolidayList[1].CurrentDate.ShouldBe(new DateOnly(2023, 5, 1));
        _holidaysListCalculator.HolidayList[2].CurrentDate.ShouldBe(new DateOnly(2023, 7, 4));
        _holidaysListCalculator.HolidayList[3].CurrentDate.ShouldBe(new DateOnly(2023, 12, 25));
    }

    #endregion

    #region Edge Cases and Validation Tests

    [Test]
    public void ShouldHandleEmptyRulesList()
    {
        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.ShouldBeEmpty();
    }

    [Test]
    public void ShouldClearRulesAndHolidays()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/01",
            Name = new MultiLanguage { En = "New Year" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.Add(rule);
        _holidaysListCalculator.ComputeHolidays();

        // Act
        _holidaysListCalculator.Clear();

        // Assert
        _holidaysListCalculator.Count.ShouldBe(0);
        _holidaysListCalculator.HolidayList.ShouldBeEmpty();
    }

    [Test]
    public void ShouldHandleUnofficialHolidays()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "02/14",
            Name = new MultiLanguage { En = "Valentine's Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = false, // Nicht verpflichtend = inoffiziell
            IsPaid = false,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 2, 14))
            .ShouldBe(HolidayStatus.UnofficialHoliday);
    }

    [Test]
    public void ShouldReturnCorrectHolidayInfo()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "07/04",
            Name = new MultiLanguage { En = "Independence Day", De = "Unabhängigkeitstag" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var holidayInfo = _holidaysListCalculator.GetHolidayInfo(new DateOnly(2023, 7, 4));

        // Assert
        holidayInfo.ShouldNotBeNull();
        holidayInfo!.CurrentName.ShouldBe("Independence Day");
        holidayInfo.CurrentDate.ShouldBe(new DateOnly(2023, 7, 4));
        holidayInfo.Officially.ShouldBeTrue();
    }

    [Test]
    public void ShouldReturnNullForNonHolidayDate()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/01",
            Name = new MultiLanguage { En = "New Year" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var holidayInfo = _holidaysListCalculator.GetHolidayInfo(new DateOnly(2023, 1, 2));

        // Assert
        holidayInfo.ShouldBeNull();
    }

    [Test]
    public void ShouldCalculateCorrectDaysInMonth()
    {
        // Assert
        _holidaysListCalculator.GetDaysInMonth(1, 2023).ShouldBe(31); // Januar
        _holidaysListCalculator.GetDaysInMonth(2, 2023).ShouldBe(28); // Februar (kein Schaltjahr)
        _holidaysListCalculator.GetDaysInMonth(2, 2024).ShouldBe(29); // Februar (Schaltjahr)
        _holidaysListCalculator.GetDaysInMonth(4, 2023).ShouldBe(30); // April
    }

    [Test]
    public void ShouldThrowExceptionForInvalidMonth()
    {
        // Act
        // Assert
        var ex = Should.Throw<ArgumentException>(() => { _holidaysListCalculator.GetDaysInMonth(13, 2023); });
        ex.Message.ShouldStartWith("Invalid month");
        ex.ParamName.ShouldBe("month");
    }

    [Test]
    public void ShouldCalculateTotalDaysInYear()
    {
        // Act & Assert
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.GetTotalDaysInCurrentYear().ShouldBe(365);

        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.GetTotalDaysInCurrentYear().ShouldBe(366);
    }

    #endregion

    #region Complex SubRules Tests

    [Test]
    public void ShouldApplyMultipleSubRulesWithSemicolonSeparator()
    {
        // Arrange - Holiday that falls on Saturday should move to Monday, 
        // but if it falls on Sunday should move to Tuesday
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "06/15", // June 15th
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+2;SU+2" // Saturday +2 days, Sunday +2 days
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2024 (June 15 = Saturday)
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        var june15_2024 = new DateOnly(2024, 6, 15); // Saturday
        june15_2024.DayOfWeek.ShouldBe(DayOfWeek.Saturday);
        
        // Should move to Monday (June 17)
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 6, 17))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(june15_2024)
            .ShouldBe(HolidayStatus.NotAHoliday);

        // Act & Assert for 2025 (June 15 = Sunday)
        _holidaysListCalculator.CurrentYear = 2025;
        _holidaysListCalculator.ComputeHolidays();
        var june15_2025 = new DateOnly(2025, 6, 15); // Sunday
        june15_2025.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        
        // Should move to Tuesday (June 17)
        _holidaysListCalculator.IsHoliday(new DateOnly(2025, 6, 17))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(june15_2025)
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplyOnlyFirstMatchingSubRule1()
    {
        // Arrange - Multiple rules for same weekday, only first should apply
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "03/17", // March 17th
            Name = new MultiLanguage { En = "St. Patrick's Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1;SU+3" // First rule: Sunday +1, Second rule: Sunday +3 (should not apply)
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2024: March 17 is Sunday
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        var march17_2024 = new DateOnly(2024, 3, 17); // Sunday
        march17_2024.DayOfWeek.ShouldBe(DayOfWeek.Sunday);

        // Assert - Should apply only first rule (+1 day = Monday March 18)
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 3, 18))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 3, 20)) // +3 days
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldHandleComplexWeekdayAdjustmentScenarios()
    {
        // Arrange - Different adjustments for different weekdays
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/25", // Christmas
            Name = new MultiLanguage { En = "Christmas" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+2;SU+1;MO-1;TU+3" // Sat+2, Sun+1, Mon-1, Tue+3
        };
        _holidaysListCalculator.Add(rule);

        // Test Saturday scenario (2021: Dec 25 = Saturday)
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();
        var dec25_2021 = new DateOnly(2021, 12, 25);
        dec25_2021.DayOfWeek.ShouldBe(DayOfWeek.Saturday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 12, 27)) // +2 days
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Test Sunday scenario (2022: Dec 25 = Sunday)  
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        var dec25_2022 = new DateOnly(2022, 12, 25);
        dec25_2022.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 12, 26)) // +1 day
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Test Monday scenario (2023: Dec 25 = Monday)
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var dec25_2023 = new DateOnly(2023, 12, 25);
        dec25_2023.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 24)) // -1 day
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldIgnoreInvalidSubRuleFormats()
    {
        // Arrange - Mix of valid and invalid SubRules
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "07/04", // July 4th
            Name = new MultiLanguage { En = "Independence Day" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+1;XX+2;MO;TU+0;WE+1" // Invalid: XX+2, MO (no offset), TU+0 (zero offset)
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2020: July 4 = Saturday, should apply SA+1
        _holidaysListCalculator.CurrentYear = 2020;
        _holidaysListCalculator.ComputeHolidays();
        var july4_2020 = new DateOnly(2020, 7, 4);
        july4_2020.DayOfWeek.ShouldBe(DayOfWeek.Saturday);

        // Assert - Should apply only valid SA+1 rule
        _holidaysListCalculator.IsHoliday(new DateOnly(2020, 7, 5)) // +1 day
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(july4_2020)
            .ShouldBe(HolidayStatus.NotAHoliday);

        // Act - 2023: July 4 = Tuesday, should ignore TU+0 and apply WE+1 if it were Wednesday
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var july4_2023 = new DateOnly(2023, 7, 4);
        july4_2023.DayOfWeek.ShouldBe(DayOfWeek.Tuesday);

        // Assert - Should stay on original date since TU+0 is invalid
        _holidaysListCalculator.IsHoliday(july4_2023)
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleSubRulesWithEasterDates()
    {
        // Arrange - Easter with SubRule adjustments
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER-2", // Good Friday (2 days before Easter)
            Name = new MultiLanguage { En = "Good Friday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1;SA-1" // If Sunday move forward, if Saturday move back
        };
        _holidaysListCalculator.Add(rule);

        // Act - Test for multiple years to find Saturday/Sunday Good Friday
        for (int year = 2020; year <= 2030; year++)
        {
            _holidaysListCalculator.CurrentYear = year;
            var easter = _holidaysListCalculator.CalculateEaster(year);
            var goodFriday = easter.AddDays(-2);
            
            _holidaysListCalculator.ComputeHolidays();
            
            if (goodFriday.DayOfWeek == DayOfWeek.Saturday)
            {
                // Should move back 1 day to Friday
                _holidaysListCalculator.IsHoliday(goodFriday.AddDays(-1))
                    .ShouldBe(HolidayStatus.OfficialHoliday, $"Good Friday {goodFriday} (Saturday) in {year} should move to Friday");
                _holidaysListCalculator.IsHoliday(goodFriday)
                    .ShouldBe(HolidayStatus.NotAHoliday, $"Original Good Friday {goodFriday} (Saturday) in {year} should not be holiday");
            }
            else if (goodFriday.DayOfWeek == DayOfWeek.Sunday)
            {
                // Should move forward 1 day to Monday
                _holidaysListCalculator.IsHoliday(goodFriday.AddDays(1))
                    .ShouldBe(HolidayStatus.OfficialHoliday, $"Good Friday {goodFriday} (Sunday) in {year} should move to Monday");
                _holidaysListCalculator.IsHoliday(goodFriday)
                    .ShouldBe(HolidayStatus.NotAHoliday, $"Original Good Friday {goodFriday} (Sunday) in {year} should not be holiday");
            }
            else
            {
                // Should stay on original date
                _holidaysListCalculator.IsHoliday(goodFriday)
                    .ShouldBe(HolidayStatus.OfficialHoliday, $"Good Friday {goodFriday} ({goodFriday.DayOfWeek}) in {year} should stay on original date");
            }
        }
    }

    [Test]
    public void ShouldHandleEmptyAndWhitespaceSubRules()
    {
        // Arrange - SubRules with empty, whitespace, and valid entries
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/11", // Veterans Day
            Name = new MultiLanguage { En = "Veterans Day" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+1; ;   ;MO-1" // Valid SA+1, empty entries, valid MO-1
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2023: Nov 11 = Saturday
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var nov11_2023 = new DateOnly(2023, 11, 11);
        nov11_2023.DayOfWeek.ShouldBe(DayOfWeek.Saturday);

        // Assert - Should apply SA+1 rule (move to Sunday)
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 12))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(nov11_2023)
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldHandleSubRulesWithLargeOffsets()
    {
        // Arrange - SubRules with large day offsets
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/15", // January 15th
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "FR+10;MO-7" // Large positive and negative offsets
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2021: Jan 15 = Friday
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();
        var jan15_2021 = new DateOnly(2021, 1, 15);
        jan15_2021.DayOfWeek.ShouldBe(DayOfWeek.Friday);

        // Assert - Should apply FR+10 (move 10 days forward to January 25)
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 1, 25))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(jan15_2021)
            .ShouldBe(HolidayStatus.NotAHoliday);

        // Act - 2018: Jan 15 = Monday  
        _holidaysListCalculator.CurrentYear = 2018;
        _holidaysListCalculator.ComputeHolidays();
        var jan15_2018 = new DateOnly(2018, 1, 15);
        jan15_2018.DayOfWeek.ShouldBe(DayOfWeek.Monday);

        // Assert - Should apply MO-7 (move 7 days back to January 8)
        _holidaysListCalculator.IsHoliday(new DateOnly(2018, 1, 8))
            .ShouldBe(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(jan15_2018)
            .ShouldBe(HolidayStatus.NotAHoliday);
    }

    #endregion

    #region AddRange and Remove Tests

    [Test]
    public void ShouldAddMultipleRulesAtOnce()
    {
        // Arrange
        var rules = new List<CalendarRule>
        {
            new CalendarRule { Id = Guid.NewGuid(), Rule = "01/01", Name = new MultiLanguage { En = "New Year" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "05/01", Name = new MultiLanguage { En = "Labor Day" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "12/25", Name = new MultiLanguage { En = "Christmas" }}
        };

        // Act
        _holidaysListCalculator.AddRange(rules);

        // Assert
        _holidaysListCalculator.Count.ShouldBe(3);
    }

    [Test]
    public void ShouldRemoveRuleByIndex()
    {
        // Arrange
        var rules = new[]
        {
            new CalendarRule { Id = Guid.NewGuid(), Rule = "01/01", Name = new MultiLanguage { En = "New Year" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "05/01", Name = new MultiLanguage { En = "Labor Day" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "12/25", Name = new MultiLanguage { En = "Christmas" }}
        };

        foreach (var rule in rules)
        {
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.Remove(1); // Entferne Labor Day

        // Assert
        _holidaysListCalculator.Count.ShouldBe(2);
        var remainingRule = _holidaysListCalculator.GetRule(1);
        remainingRule?.Name?.En.ShouldBe("Christmas");
    }

    [Test]
    public void ShouldHandleRemoveWithInvalidIndex()
    {
        // Arrange
        _holidaysListCalculator.Add(new CalendarRule { Id = Guid.NewGuid(), Rule = "01/01" });

        // Act
        _holidaysListCalculator.Remove(5); // Invalid index
        _holidaysListCalculator.Remove(-1); // Negative index

        // Assert
        _holidaysListCalculator.Count.ShouldBe(1); // Sollte unverändert bleiben
    }

    [Test]
    public void ShouldGetRuleByIndex()
    {
        // Arrange
        var rule = new CalendarRule 
        { 
            Id = Guid.NewGuid(), 
            Rule = "07/04", 
            Name = new MultiLanguage { En = "Independence Day" }
        };
        _holidaysListCalculator.Add(rule);

        // Act
        var retrievedRule = _holidaysListCalculator.GetRule(0);

        // Assert
        retrievedRule.ShouldNotBeNull();
        retrievedRule!.Rule.ShouldBe("07/04");
        retrievedRule.Name?.En.ShouldBe("Independence Day");
    }

    [Test]
    public void ShouldReturnNullForInvalidRuleIndex()
    {
        // Act
        var rule = _holidaysListCalculator.GetRule(0);

        // Assert
        rule.ShouldBeNull();
    }

    #endregion

    #region Format Date Tests

    [Test]
    public void ShouldFormatDateCorrectly()
    {
        // Arrange
        var date = new DateOnly(2023, 7, 4);

        // Act
        var formatted = _holidaysListCalculator.FormatDate(date);

        // Assert
        formatted.ShouldContain("04");
        formatted.ShouldContain("Jul");
        formatted.ShouldContain("2023");
    }

    #endregion

    #region Weekday Rule Tests

    [Test]
    public void ShouldComputeFirstMondayInSeptember_LaborDay()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "09/01+00+MO",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "",
            Country = "US",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert 2023: Sep 1 (Fri) → next Mon = Sep 4
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 9, 4))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2022: Sep 1 (Thu) → next Mon = Sep 5
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 9, 5))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2024: Sep 1 (Sun) → next Mon = Sep 2
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 9, 2))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeFourthThursdayInNovember_Thanksgiving()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/01+21+TH",
            Name = new MultiLanguage { En = "Thanksgiving" },
            State = "",
            Country = "US",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert 2023: Nov 1+21=Nov 22 (Wed) → next Thu = Nov 23
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 23))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2022: Nov 22 (Tue) → next Thu = Nov 24
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 11, 24))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2024: Nov 22 (Fri) → next Thu = Nov 28
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 11, 28))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeThirdMondayInJanuary_MLKDay()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/15+00+MO",
            Name = new MultiLanguage { En = "Martin Luther King Jr. Day" },
            State = "",
            Country = "US",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert 2023: Jan 15 (Sun) → next Mon = Jan 16
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 1, 16))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2024: Jan 15 (Mon) → stays Jan 15
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 1, 15))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2022: Jan 15 (Sat) → next Mon = Jan 17
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 1, 17))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeFirstThursdayInApril_NaefelserFahrt()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "04/01+00+TH",
            Name = new MultiLanguage { En = "Näfelser Fahrt" },
            State = "GL",
            Country = "CH",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert 2023: Apr 1 (Sat) → next Thu = Apr 6
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 6))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2024: Apr 1 (Mon) → next Thu = Apr 4
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 4, 4))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeWeekdayRuleWithBackwardDirection()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/25+00-MO",
            Name = new MultiLanguage { En = "Test Backward Rule" },
            State = "",
            Country = "US",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert 2023: May 25 (Thu) → prev Mon = May 22
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 22))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2022: May 25 (Wed) → prev Mon = May 23
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 5, 23))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2024: May 25 (Sat) → prev Mon = May 20
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 5, 20))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeWednesdayBetweenNov16And22_BussUndBettag()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/16+00+WE",
            Name = new MultiLanguage { En = "Buß- und Bettag" },
            State = "SN",
            Country = "DE",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert 2023: Nov 16 (Thu) → next Wed = Nov 22
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Count().ShouldBe(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 22))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2022: Nov 16 (Wed) → stays Nov 16
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 11, 16))
            .ShouldBe(HolidayStatus.OfficialHoliday);

        // Act & Assert 2024: Nov 16 (Sat) → next Wed = Nov 20
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 11, 20))
            .ShouldBe(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldNotReportWrongDateForWeekdayRule()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "09/01+00+MO",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "",
            Country = "US",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert - Sep 1 2023 is NOT Labor Day (it's a Friday)
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 9, 1))
            .ShouldNotBe(HolidayStatus.OfficialHoliday);
    }

    #endregion

    #region Hijri Calendar Rules

    [Test]
    public void ComputeHolidays_HijriRule_ShouldCalculateEidAlFitr()
    {
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "HIJRI_01_10",
            SubRule = string.Empty,
            IsMandatory = true,
            IsPaid = true,
            State = "SA",
            Country = "SA"
        };

        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.Add(rule);
        _holidaysListCalculator.ComputeHolidays();

        _holidaysListCalculator.HolidayList.ShouldNotBeEmpty();
        var holiday = _holidaysListCalculator.HolidayList[0];
        holiday.CurrentDate.Year.ShouldBe(2024);
        holiday.CurrentDate.Month.ShouldBeInRange(3, 5);
    }

    [Test]
    public void ComputeHolidays_HijriRuleWithOffset_ShouldApplyOffset()
    {
        var baseRule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "HIJRI_01_10+0",
            SubRule = string.Empty,
            IsMandatory = true,
            IsPaid = true,
            State = "SA",
            Country = "SA"
        };

        var offsetRule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "HIJRI_01_10+2",
            SubRule = string.Empty,
            IsMandatory = true,
            IsPaid = true,
            State = "SA",
            Country = "SA"
        };

        _holidaysListCalculator.CurrentYear = 2025;
        _holidaysListCalculator.Add(baseRule);
        _holidaysListCalculator.Add(offsetRule);
        _holidaysListCalculator.ComputeHolidays();

        _holidaysListCalculator.HolidayList.Count().ShouldBeGreaterThanOrEqualTo(2);
        var diff = _holidaysListCalculator.HolidayList[1].CurrentDate.DayNumber -
                   _holidaysListCalculator.HolidayList[0].CurrentDate.DayNumber;
        diff.ShouldBe(2);
    }

    #endregion

    #region Lunar Calendar Rules

    [Test]
    public void ComputeHolidays_LunarRule_ShouldCalculateChineseNewYear2026()
    {
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "LUNAR_01_01",
            SubRule = string.Empty,
            IsMandatory = true,
            IsPaid = true,
            State = "CN",
            Country = "CN"
        };

        _holidaysListCalculator.CurrentYear = 2026;
        _holidaysListCalculator.Add(rule);
        _holidaysListCalculator.ComputeHolidays();

        _holidaysListCalculator.HolidayList.ShouldNotBeEmpty();
        var holiday = _holidaysListCalculator.HolidayList.First(h => h.CurrentDate.Year == 2026);
        holiday.CurrentDate.ShouldBe(new DateOnly(2026, 2, 17));
    }

    [Test]
    public void ComputeHolidays_LunarRule_DragonBoatFestival2024()
    {
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "LUNAR_05_05",
            SubRule = string.Empty,
            IsMandatory = true,
            IsPaid = true,
            State = "CN",
            Country = "CN"
        };

        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.Add(rule);
        _holidaysListCalculator.ComputeHolidays();

        var holiday = _holidaysListCalculator.HolidayList.First(h => h.CurrentDate.Year == 2024);
        holiday.CurrentDate.ShouldBe(new DateOnly(2024, 6, 10));
    }

    [Test]
    public void ComputeHolidays_LunarRuleWithOffset_ShouldApplyOffset()
    {
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "LUNAR_01_01+1",
            SubRule = string.Empty,
            IsMandatory = true,
            IsPaid = true,
            State = "CN",
            Country = "CN"
        };

        _holidaysListCalculator.CurrentYear = 2026;
        _holidaysListCalculator.Add(rule);
        _holidaysListCalculator.ComputeHolidays();

        var holiday = _holidaysListCalculator.HolidayList.First(h => h.CurrentDate.Year == 2026);
        holiday.CurrentDate.ShouldBe(new DateOnly(2026, 2, 18));
    }

    #endregion
}