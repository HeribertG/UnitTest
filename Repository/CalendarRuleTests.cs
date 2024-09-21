using AutoMapper;
using Klacks.Api.Datas;
using Klacks.Api.Handlers.Settings.CalendarRules;
using Klacks.Api.Queries.Settings.CalendarRules;
using Klacks.Api.Repositories;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UnitTest.FakeData;

namespace UnitTest.Repository
{
  internal class CalendarRuleTests
  {
    public IHttpContextAccessor _httpContextAccessor = null!;
    public DataBaseContext dbContext = null!;
    private IMapper _mapper = null!;
    private IMediator _mediator = null!;

    [TestCase(5, 0, 5)]
    [TestCase(10, 0, 0)]
    [TestCase(15, 0, 15)]
    [TestCase(20, 0, 20)]
    [TestCase(5, 1, 5)]
    [TestCase(10, 1, 0)]
    [TestCase(15, 1, 0)]
    [TestCase(20, 1, 0)]
    public async Task GetTruncatedListQueryHandler_Pagination_Ok(int numberOfItemsPerPage, int requiredPage, int maxItems)
    {
      //Arrange
      var filter = CalendarRules.CalendarRulesFilter();
      filter.NumberOfItemsPerPage = numberOfItemsPerPage;
      filter.RequiredPage = requiredPage;
      var options = new DbContextOptionsBuilder<DataBaseContext>()
     .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
      dbContext = new DataBaseContext(options, _httpContextAccessor);

      dbContext.Database.EnsureCreated();
      DataSeed();
      var repository = new SettingsRepository(dbContext);
      var query = new TruncatedListQuery(filter);
      var handler = new TruncatedListQueryHandler(repository);
      //Act
      var result = await handler.Handle(query, default);
      //Assert
      result.Should().NotBeNull();
      result.CurrentPage.Should().Be(requiredPage);
      result.FirstItemOnPage.Should().Be(numberOfItemsPerPage * (requiredPage));
    }

    [SetUp]
    public void Setup()
    {
      _mapper = Substitute.For<IMapper>();
      _mediator = Substitute.For<IMediator>();
    }

    [TearDown]
    public void TearDown()
    {
      dbContext.Database.EnsureDeleted();
      dbContext.Dispose();
    }

    private void DataSeed()
    {
      var calendarRule = CalendarRules.CalendarRuleList();
      var state = CalendarRules.StateList();
      var countries = CalendarRules.CountryList();

      dbContext.CalendarRule.AddRange(calendarRule);
      dbContext.State.AddRange(state);
      dbContext.Countries.AddRange(countries);

      dbContext.SaveChanges();
    }
  }
}
