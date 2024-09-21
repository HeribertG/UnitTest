using Klacks.Api.Resources.Schedules;

namespace UnitTest.FakeData
{
  internal class CalendarSelections
  {
    private static readonly string CountryName = "CH";

    private static readonly List<string> SwissCantons = new List<string>
    {
        "AG", "AR", "AI", "BL",
        "BS", "BE", "FR", "GE", "GL", "GR", "JU",
        "LU", "NE", "NW", "OW", "SH", "SZ",
        "SO", "SG", "TI", "TG", "UR", "VD",
        "VS", "ZG", "ZH"
    };

    public static List<CalendarSelectionResource> GenerateFakeCalendarSelections(int count)
    {
      var random = new Random();
      var calendarSelections = new List<CalendarSelectionResource>();

      for (int i = 0; i < count; i++)
      {
        var calendarSelection = new CalendarSelectionResource
        {
          Id = Guid.NewGuid(),
          Name = $"Kalender-{i + 1}",
          SelectedCalendars = new List<SelectedCalendarResource>()
        };

        int selectedCalendarsCount = random.Next(1, 20);
        for (int j = 0; j < selectedCalendarsCount; j++)
        {
          var selectedCalendar = new SelectedCalendarResource
          {
            Id = Guid.NewGuid(),
            Country = CountryName,
            State = SwissCantons[random.Next(SwissCantons.Count)],
            CalendarSelectionId = calendarSelection.Id
          };
          calendarSelection.SelectedCalendars.Add(selectedCalendar);
        }

        calendarSelections.Add(calendarSelection);
      }

      return calendarSelections;
    }
  }
}
