using AutoMapper;
using Klacks_api.AutoMapper;

namespace UnitTest.Helper
{
  public static class TestHelper
  {
    public static MapperConfiguration GetFullMapperConfiguration() => new MapperConfiguration(cfg => cfg.AddMaps(typeof(MappingProfile)));
  }
}
