using AutoMapper;
using Klacks.Api.BasicScriptInterpreter;
using Klacks.Api.Datas;
using Klacks.Api.Handlers.Clients;
using Klacks.Api.Models.Associations;
using Klacks.Api.Models.Staffs;
using Klacks.Api.Queries.Clients;
using Klacks.Api.Repositories;
using Klacks.Api.Resources.Filter;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UnitTest.FakeData;


namespace UnitTest.Repository;

internal class ClientTests
{

    public IHttpContextAccessor _httpContextAccessor = null!;
    public TruncatedClient _truncatedClient = null!;
    public DataBaseContext dbContext = null!;
    private IMapper _mapper = null!;

    [TestCase("ag", "", "", 12)]
    [TestCase("gmbh", "", "", 0)]
    [TestCase("sa", "", "", 2)]
    [TestCase("Dr", "", "", 1)]
    [TestCase("Zentrum", "", "", 3)]
    [TestCase("15205", "", "", 1)] // Id Number
    [TestCase("15215", "", "", 1)] // Id Number
    [TestCase("", "Male", "", 0)]
    [TestCase("", "Female", "", 0)]
    [TestCase("", "LegalEntity", "", 23)]
    [TestCase("", "", "SG", 3)]
    [TestCase("", "", "BE", 4)]
    [TestCase("", "", "ZH", 4)]
    public async Task GetTruncatedListQueryHandler_Filter_Ok(string searchString, string gender, string state, int sum)
    {
        //Arrange
        var returns = Clients.TruncatedClient();
        var filter = Clients.Filter();
        filter.SearchString = searchString;

        if (!string.IsNullOrEmpty(gender))
        {
            switch (gender)
            {
                case "Male":
                    filter.Male = true;
                    filter.Female = false;
                    filter.LegalEntity = false;
                    break;

                case "Female":
                    filter.Male = false;
                    filter.Female = true;
                    filter.LegalEntity = false;
                    break;

                case "LegalEntity":

                    filter.Male = false;
                    filter.Female = false;
                    filter.LegalEntity = true;
                    break;
            }
        }

        if (!string.IsNullOrEmpty(state))
        {
            foreach (var item in filter.FilteredStateToken)
            {
                if (item.State != state)
                {
                    item.Select = false;
                }
            }
        }

        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);
        var repository = new ClientRepository(dbContext, new MacroEngine());
        var query = new GetTruncatedListQuery(filter);
        var handler = new GetTruncatedListQueryHandler(repository, _mapper);
        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        result.Clients.Should().HaveCount(sum);
    }

    /// <summary>
    /// The mocked TruncatedClient result has 24 entries.
    /// </summary>
    [TestCase(10, 3)]
    [TestCase(15, 2)]
    [TestCase(24, 1)]
    public async Task GetTruncatedListQueryHandler_Pagination_NotOk(int numberOfItemsPerPage, int requiredPage)
    {
        //Arrange
        var returns = Clients.TruncatedClient();
        var filter = Clients.Filter();
        filter.NumberOfItemsPerPage = numberOfItemsPerPage;
        filter.RequiredPage = requiredPage;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);
        var repository = new ClientRepository(dbContext, new MacroEngine());
        var query = new GetTruncatedListQuery(filter);
        var handler = new GetTruncatedListQueryHandler(repository, _mapper);
        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        result.Clients.Should().HaveCount(0);
        result.FirstItemOnPage.Should().Be(-1);
    }

    /// <summary>
    /// The mocked TruncatedClient result has 24 entries.
    /// </summary>
    [TestCase(5, 0, 5)]
    [TestCase(10, 0, 10)]
    [TestCase(15, 0, 15)]
    [TestCase(20, 0, 20)]
    [TestCase(5, 1, 5)]
    [TestCase(10, 1, 10)]
    [TestCase(15, 1, 9)]
    [TestCase(20, 1, 4)]
    public async Task GetTruncatedListQueryHandler_Pagination_Ok(int numberOfItemsPerPage, int requiredPage, int maxItems)
    {
        //Arrange
        var returns = Clients.TruncatedClient();
        var filter = Clients.Filter();
        filter.NumberOfItemsPerPage = numberOfItemsPerPage;
        filter.RequiredPage = requiredPage;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);
        var repository = new ClientRepository(dbContext, new MacroEngine());
        var query = new GetTruncatedListQuery(filter);
        var handler = new GetTruncatedListQueryHandler(repository, _mapper);
        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        result.Clients.Should().HaveCount(maxItems);
        result.MaxItems.Should().Be(returns.Clients!.Count());
        result.CurrentPage.Should().Be(requiredPage);
        result.FirstItemOnPage.Should().Be(numberOfItemsPerPage * (requiredPage));
    }

    [SetUp]
    public void Setup()
    {
        _mapper = Substitute.For<IMapper>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _truncatedClient = FakeData.Clients.TruncatedClient();
    }

    [TearDown]
    public void TearDown()
    {
        dbContext.Database.EnsureDeleted();
        dbContext.Dispose();
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
