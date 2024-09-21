using AutoMapper;
using Klacks.Api.AutoMappers;

namespace UnitTest.Helper
{
    public static class TestHelper
  {
    public static MapperConfiguration GetFullMapperConfiguration() => new MapperConfiguration(cfg => cfg.AddMaps(typeof(MappingProfile)));
  }
}
