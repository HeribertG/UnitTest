using AutoMapper;
using Klacks.Api.BasicScriptInterpreter;
using Klacks.Api.Datas;
using Klacks.Api.Handlers.Breaks;
using Klacks.Api.Models.Associations;
using Klacks.Api.Models.Schedules;
using Klacks.Api.Models.Staffs;
using Klacks.Api.Repositories;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UnitTest.FakeData;
using UnitTest.Helper;

namespace UnitTest.Repository;

[TestFixture]
internal class BreakTests
{
  public IHttpContextAccessor _httpContextAccessor = null!;
  public DataBaseContext dbContext = null!;
  private IMapper _mapper = null!;
  private IMediator _mediator = null!;

  [Test]
  public async Task GetClientList_Ok()
  {
    //Arrange
    var clients = Clients.GenerateClients(500, 2023, true);
    var absence = Clients.GenerateAbsences(20);
    var breaks = Clients.GenerateBreaks(clients, absence, 2023, 200);
    var filter = Clients.GenerateBreakFilter(absence, 2023);

    var options = new DbContextOptionsBuilder<DataBaseContext>()
    .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
    dbContext = new DataBaseContext(options, _httpContextAccessor);

    dbContext.Database.EnsureCreated();

    DataSeed(clients, absence, breaks);
    var repository = new ClientRepository(dbContext, new MacroEngine());
    var query = new Klacks.Api.Queries.Breaks.ListQuery(filter);
    var handler = new GetListQueryHandler(_mapper, repository);

    //Act
    var result = await handler.Handle(query, default);
    //Assert
    result.Should().NotBeNull();
    result.Count().Should().Be(500);

    var tmpBreaks = new List<Break>();

    foreach (var c in result)
    {
      if (c.Breaks.Any())
      {
        tmpBreaks.AddRange(c.Breaks);
      }
    }

    tmpBreaks.Should().NotBeNull();
    tmpBreaks.Count().Should().Be(200);
  }

  [SetUp]
  public void Setup()
  {
    _mapper = TestHelper.GetFullMapperConfiguration().CreateMapper();
    _mediator = Substitute.For<IMediator>();
  }

  [TearDown]
  public void TearDown()
  {
    dbContext.Database.EnsureDeleted();
    dbContext.Dispose();
  }

  private void DataSeed(List<Client> clients, List<Absence> absences, List<Break> breaks)
  {
    var addresses = new List<Address>();
    var memberships = new List<Membership>();
    var communications = new List<Communication>();

    foreach (var item in clients!)
    {
      if (item.Addresses.Any())
      {
        foreach (var address in item.Addresses)
        {
          addresses.Add(address);
        };
      }
      if (item.Communications.Any())
      {
        foreach (var communication in communications)
        {
          communications.Add(communication);
        }
      }

      if (item.Membership != null)
      {
        memberships.Add(item.Membership);
      }
      item.Addresses.Clear();
      item.Communications.Clear();
      item.Membership = null;
    }

    dbContext.Client.AddRange(clients);
    dbContext.Address.AddRange(addresses);
    dbContext.Membership.AddRange(memberships);
    dbContext.Communication.AddRange(communications);
    dbContext.Absence.AddRange(absences);
    dbContext.Break.AddRange(breaks);

    dbContext.SaveChanges();
  }
}
