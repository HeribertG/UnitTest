using AutoMapper;
using Klacks_api.Commands;
using Klacks_api.Datas;
using Klacks_api.Handlers.CalendarSelections;
using Klacks_api.Queries;
using Klacks_api.Repositories;
using Klacks_api.Resources.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnitTest.Helper;

namespace UnitTest.Repository
{
  internal class CalendarSelectionTest
  {
    public IHttpContextAccessor _httpContextAccessor = null!;
    public DataBaseContext dbContext = null!;
    private ILogger<PostCommandHandler> _logger = null!;
    private ILogger<PutCommandHandler> _logger2 = null!;
    private ILogger<DeleteCommandHandler> _logger3 = null!;
    private IMapper _mapper = null!;

    [Test]
    public async Task AddAndReReadCalendarSelection_Ok()
    {
      //Arrange Post
      var fakeCalendarSelection = createNew();

      var options = new DbContextOptionsBuilder<DataBaseContext>()
     .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
      dbContext = new DataBaseContext(options, _httpContextAccessor);

      dbContext.Database.EnsureCreated();

      var unitOfWork = new UnitOfWork(dbContext);
      var repository = new CalendarSelectionRepository(dbContext);
      var queryPost = new PostCommand<CalendarSelectionResource>(fakeCalendarSelection);
      var handlerPost = new PostCommandHandler(_mapper, repository, unitOfWork, _logger);

      //Act Post
      var resultPost = await handlerPost.Handle(queryPost, default);

      //Assert Post
      resultPost.Should().NotBeNull();
      resultPost!.SelectedCalendars.Should().NotBeNull();
      resultPost.SelectedCalendars.Should().HaveCount(fakeCalendarSelection.SelectedCalendars.Count);

      //Arrange Get
      var id = resultPost.Id;
      var queryGet = new GetQuery<CalendarSelectionResource>(id);
      var handlerGet = new GetQueryHandler(_mapper, repository);

      //Act Get
      var resultGet = await handlerGet.Handle(queryGet, default);

      //Assert Get
      resultGet.Should().NotBeNull();
      resultGet!.SelectedCalendars.Should().NotBeNull();
      resultGet.SelectedCalendars.Should().HaveCount(fakeCalendarSelection.SelectedCalendars.Count);

      //Arrange Put
      var fakeCalendarSelectionUpdate = resultGet!;
      var fakeSelectedCalendar = new SelectedCalendarResource()
      {
        Id = Guid.Empty,
        Country = "USA",
        State = "NY"
      };
      fakeCalendarSelectionUpdate.SelectedCalendars.Add(fakeSelectedCalendar);
      var queryPut = new PutCommand<CalendarSelectionResource>(fakeCalendarSelectionUpdate);
      var handlerPut = new PutCommandHandler(_mapper, repository, unitOfWork, _logger2);

      //Act Put
      var resultPut = await handlerPut.Handle(queryPut, default);

      //Assert Put
      resultPut.Should().NotBeNull();
      resultPut!.SelectedCalendars.Should().NotBeNull();
      resultPut.SelectedCalendars.Should().HaveCount(fakeCalendarSelectionUpdate.SelectedCalendars.Count);

      //Arrange Delete
      var queryDelete = new DeleteCommand<CalendarSelectionResource>(resultPut.Id);
      var handlerDelete = new DeleteCommandHandler(_mapper, repository, unitOfWork, _logger3);

      //Act Delete
      var resultDelete = await handlerDelete.Handle(queryDelete, default);

      //Assert Delete
      resultDelete.Should().NotBeNull();

      var repositorySelectedCalendar = new SelectedCalendarRepository(dbContext);

      foreach (var item in resultDelete!.SelectedCalendars)
      {
        var res = await repositorySelectedCalendar.Get(item.Id);
        res.Should().BeNull();
      }
    }

    [SetUp]
    public void Setup()
    {
      _mapper = TestHelper.GetFullMapperConfiguration().CreateMapper();
      _logger = Substitute.For<ILogger<PostCommandHandler>>();
      _logger2 = Substitute.For<ILogger<PutCommandHandler>>();
      _logger3 = Substitute.For<ILogger<DeleteCommandHandler>>();
    }

    [TearDown]
    public void TearDown()
    {
      dbContext.Database.EnsureDeleted();
      dbContext.Dispose();
    }

    private CalendarSelectionResource createNew()
    {
      var fakeCalendarSelection = FakeData.CalendarSelections.GenerateFakeCalendarSelections(1).FirstOrDefault();
      fakeCalendarSelection!.Id = Guid.Empty;

      foreach (var item in fakeCalendarSelection.SelectedCalendars)
      {
        item!.Id = Guid.Empty;
      }

      return fakeCalendarSelection;
    }
  }
}
