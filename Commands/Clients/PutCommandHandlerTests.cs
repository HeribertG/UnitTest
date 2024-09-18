using AutoMapper;
using MediatR;

namespace UnitTest.Commands.Clients
{
  internal class PutCommandHandlerTests
  {
    private IMapper _mapper = null!;
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
      _mapper = Substitute.For<IMapper>();
      _mediator = Substitute.For<IMediator>();
    }

    [Test]
    public void Test1()
    {
      //Arrange

      //Act

      //Assert

      Assert.Pass();
    }
  }
}
