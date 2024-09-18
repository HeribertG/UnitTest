using AutoMapper;
using Klacks_api.Handlers.Clients;
using Klacks_api.Interfaces;
using Klacks_api.Queries.Clients;
using MediatR;

namespace UnitTest.Queries.Clients;

internal class GetTruncatedListQueryTests
{
  private IMapper _mapper = null!;
  private IMediator _mediator = null!;

  [Test]
  public async Task GetTruncatedListQueryHandler_Ok()
  {
    //Arrange
    var returns = FakeData.Clients.TruncatedClient();
    var filter = FakeData.Clients.Filter();

    var clientRepositoryMock = Substitute.For<IClientRepository>();
    clientRepositoryMock.Truncated(filter).Returns(returns);
    var query = new GetTruncatedListQuery(filter);
    var handler = new GetTruncatedListQueryHandler(clientRepositoryMock);

    //Act
    var result = await handler.Handle(query, default);
    //Assert
    result.Should().NotBeNull();
    //Assert.Pass();
  }

  [SetUp]
  public void Setup()
  {
    _mapper = Substitute.For<IMapper>();
    _mediator = Substitute.For<IMediator>();
  }
}
