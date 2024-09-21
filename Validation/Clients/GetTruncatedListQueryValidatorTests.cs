using Klacks.Api.Interfaces;
using Klacks.Api.Queries.Clients;
using Klacks.Api.Validation.Clients;

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
