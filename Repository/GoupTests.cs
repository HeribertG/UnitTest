using AutoMapper;
using Klacks_api.BasicScriptInterpreter;
using Klacks_api.Commands;
using Klacks_api.Datas;
using Klacks_api.Handlers.Groups;
using Klacks_api.Models.Associations;
using Klacks_api.Models.Staffs;
using Klacks_api.Repositories;
using Klacks_api.Resources.Associations;
using Klacks_api.Resources.Filter;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnitTest.FakeData;
using UnitTest.Helper;

namespace UnitTest.Repository;

[TestFixture]
internal class GoupTests
{
  public IHttpContextAccessor _httpContextAccessor = null!;
  public TruncatedClient _truncatedClient = null!;
  public DataBaseContext dbContext = null!;
  private ILogger<PostCommandHandler> _logger = null!;
  private IMapper _mapper = null!;

  [Test]
  public async Task PostGroup_Ok()
  {
    //Arrange
    var options = new DbContextOptionsBuilder<DataBaseContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
    dbContext = new DataBaseContext(options, _httpContextAccessor);

    dbContext.Database.EnsureCreated();
    DataSeed(_truncatedClient);
    var clientRepository = new ClientRepository(dbContext, new MacroEngine());
    var groupRepository = new GroupRepository(dbContext);
    var unitOfWork = new UnitOfWork(dbContext);
    var group = await CreateGroupAsync(1, clientRepository);
    var command = new PostCommand<GroupResource>(group);
    var handler = new PostCommandHandler(_mapper, groupRepository, unitOfWork, _logger);
    //Act
    var result = await handler.Handle(command, default);
    //Assert
    result.Should().NotBeNull();
    result!.GroupItems.Count.Should().Be(7);
  }

  [SetUp]
  public void Setup()
  {
    _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
    _logger = Substitute.For<ILogger<PostCommandHandler>>();
    _truncatedClient = FakeData.Clients.TruncatedClient();
    _mapper = TestHelper.GetFullMapperConfiguration().CreateMapper();
  }

  [TearDown]
  public void TearDown()
  {
    dbContext.Database.EnsureDeleted();
    dbContext.Dispose();
  }

  private async Task<GroupResource> CreateGroupAsync(int index, ClientRepository clientRepository)
  {
    var idNumberList = new List<int>() { 15205,
                                         15215,
                                         15216,
                                         15217,
                                         15220,
                                         15229,
                                         15403};

    var filter = Clients.Filter();
    filter.Male = true;
    filter.Female = false;
    filter.LegalEntity = false;
    var handler = new Klacks_api.Handlers.Clients.GetTruncatedListQueryHandler(clientRepository);

    var group = new GroupResource();
    group.Name = $"FakeName{index}";
    group.ValidFrom = DateTime.Now.AddMonths(index * -1);
    group.Description = "Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore et dolore magna aliquyam erat, sed diam voluptua. At vero eos et accusam et justo duo dolores et ea rebum. Stet clita kasd gubergren, no sea takimata sanctus est Lorem ipsum dolor sit amet. Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore et dolore magna aliquyam erat, sed diam voluptua. At vero eos et accusam et justo duo dolores et ea rebum. Stet clita kasd gubergren, no sea takimata sanctus est Lorem ipsum dolor sit amet.";

    foreach (var id in idNumberList)
    {
      filter.SearchString = id.ToString();
      var command = new Klacks_api.Queries.Clients.GetTruncatedListQuery(filter);
      var result = await handler.Handle(command, default);
      if (result != null && result.Clients != null)
      {
        var item = new GroupItemResource() { ClientId = result.Clients.First().Id };
        group.GroupItems.Add(item);
      }
    }
    return group;
  }

  private void DataSeed(TruncatedClient truncated)
  {
    var clients = new List<Client>();
    var addresses = new List<Address>();
    var memberships = new List<Membership>();
    var communications = new List<Communication>();
    var annotations = new List<Annotation>();

    foreach (var item in truncated.Clients!)
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
      if (item.Annotations.Any())
      {
        foreach (var annotation in annotations)
        {
          annotations.Add(annotation);
        }
      }
      if (item.Membership != null)
      {
        memberships.Add(item.Membership);
      }
      item.Addresses.Clear();
      item.Annotations.Clear();
      item.Communications.Clear();
      clients.Add(item);
    }

    dbContext.Client.AddRange(clients);
    dbContext.Address.AddRange(addresses);
    dbContext.Membership.AddRange(memberships);
    dbContext.Communication.AddRange(communications);
    dbContext.Annotation.AddRange(annotations);

    dbContext.SaveChanges();
  }
}
