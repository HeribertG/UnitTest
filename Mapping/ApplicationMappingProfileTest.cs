using AutoMapper;
using Klacks.Api.AutoMappers;

namespace UnitTest.Mapping
{
    public class ApplicationMappingProfileTest
  {
    [Test]
    public void Automapper_Configuration_IsValid()
    {
      var _config = new MapperConfiguration(configure => configure.AddProfile<MappingProfile>());
      _config.AssertConfigurationIsValid();
    }
  }
}
