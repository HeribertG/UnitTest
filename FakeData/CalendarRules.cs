using Klacks_api.Models.Settings;
using Klacks_api.Resources.Filter;
using Newtonsoft.Json;

namespace UnitTest.FakeData
{
  internal static class CalendarRules
  {
    internal static List<CalendarRule> CalendarRuleList()
    {
      return JsonConvert.DeserializeObject<List<CalendarRule>>(FakeDateSerializeString.Data.calendarRuleList)!;
    }

    internal static CalendarRulesFilter CalendarRulesFilter()
    {
      return JsonConvert.DeserializeObject<CalendarRulesFilter>(FakeDateSerializeString.Data.filterCalendarRuleList)!;
    }

    internal static List<Countries> CountryList()
    {
      return JsonConvert.DeserializeObject<List<Countries>>(FakeDateSerializeString.Data.countryList)!;
    }

    internal static List<State> StateList()
    {
      return JsonConvert.DeserializeObject<List<State>>(FakeDateSerializeString.Data.stateList)!;
    }
  }
}
