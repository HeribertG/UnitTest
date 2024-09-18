using AutoMapper;
using Klacks_api.AutoMapper;

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
