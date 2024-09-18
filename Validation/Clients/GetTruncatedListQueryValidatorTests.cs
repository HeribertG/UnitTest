using Klacks_api.Interfaces;
using Klacks_api.Queries.Clients;
using Klacks_api.Validation.Clients;

namespace UnitTest.Validation.Clients
{
  [TestFixture]
  internal class GetTruncatedListQueryValidatorTests
  {
    [Test]
    public async Task GetTruncatedListQueryHandler_Pagination_Ok()
    {
      //Arrange
      var returns = FakeData.Clients.TruncatedClient();
      var filter = FakeData.Clients.Filter();

      var clientRepositoryMock = Substitute.For<IClientRepository>();
      clientRepositoryMock.Truncated(filter).Returns(returns);
      var query = new GetTruncatedListQuery(filter);
      var validator = new GetTruncatedListQueryValidator(clientRepositoryMock);

      //Act
      var result = await validator.ValidateAsync(query);

      //Assert
      Assert.That(result.IsValid, Is.True);
    }

    [SetUp]
    public void Setup()
    {
    }
  }
}
