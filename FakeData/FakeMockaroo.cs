using Newtonsoft.Json;

namespace UnitTest.FakeData
{
  internal static class FakeMockaroo
  {
    internal static List<MockarooClient> FakeClient()
    {
      return JsonConvert.DeserializeObject<List<MockarooClient>>(FakeDateSerializeString.Data.fakeTonicList)!;
    }

    public class MockarooClient
    {
      public string Email { get; set; } = string.Empty;
      public string First_name { get; set; } = string.Empty;
      public string Gender { get; set; } = string.Empty;
      public int Id { get; set; }
      public string Ip_address { get; set; } = string.Empty;
      public string Last_name { get; set; } = string.Empty;
    }
  }
}
