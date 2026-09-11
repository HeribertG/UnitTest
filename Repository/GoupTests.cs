using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Application.Commands;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Groups;
using NSubstitute;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Application.DTOs.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.FakeData;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Repository;

[TestFixture]
internal class GoupTests
{
    private IHttpContextAccessor _httpContextAccessor = null!;
    private TruncatedClient _truncatedClient = null!;
    private DataBaseContext dbContext = null!;
    private ILogger<PostCommandHandler> _logger = null!;
    private ILogger<UnitOfWork> _unitOfWorkLogger = null!;
    private ILogger<Group> _groupLogger = null!;
    private GroupMapper _groupMapper = null!;
    private ClientMapper _clientMapper = null!;
    private FilterMapper _filterMapper = null!;
    private IClientGroupFilterService _clientGroupFilterService = null!;

    [Test]
    public async Task PostGroup_Ok()
    {
        //Arrange
        var options = new DbContextOptionsBuilder<DataBaseContext>()
          .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
          .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)) // Ignoriere Transaktionswarnung
          .Options;

        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);

        // Use real domain services for proper filtering behavior in integration tests
        var clientFilterService = new Klacks.Api.Domain.Services.Clients.ClientFilterService();
        var membershipFilterService = new Klacks.Api.Domain.Services.Clients.ClientMembershipFilterService();
        var searchService = new Klacks.Api.Domain.Services.Clients.ClientSearchService();
        var sortingService = new Klacks.Api.Domain.Services.Clients.ClientSortingService();
        var changeTrackingService = new Klacks.Api.Infrastructure.Services.Clients.ClientChangeTrackingService(dbContext, sortingService);
        var clientValidator = new Klacks.Api.Domain.Services.Clients.ClientValidator();
        var entityManagementService = new Klacks.Api.Domain.Services.Clients.ClientEntityManagementService(clientValidator);
        var workFilterService = new Klacks.Api.Domain.Services.Clients.ClientWorkFilterService();
        var collectionUpdateService = new EntityCollectionUpdateService(dbContext);

        var mockLogger = Substitute.For<ILogger<ClientRepository>>();
        var clientRepository = new ClientRepository(dbContext, new MacroEngine(),
            changeTrackingService, entityManagementService, collectionUpdateService, clientValidator, mockLogger);

        var clientFilterRepository = new ClientFilterRepository(dbContext, _clientGroupFilterService,
            clientFilterService, membershipFilterService, searchService, sortingService,
            Substitute.For<Klacks.Api.Application.Interfaces.IClientFuzzySearchService>());

        var mockTreeService = Substitute.For<IGroupTreeService>();
        var mockHierarchyService = Substitute.For<IGroupHierarchyService>();
        var mockSearchService = Substitute.For<IGroupSearchService>();  
        var mockValidityService = Substitute.For<IGroupValidityService>();
        var mockMembershipService = Substitute.For<IGroupMembershipService>();
        var mockIntegrityService = Substitute.For<IGroupIntegrityService>();

        mockTreeService.AddChildNodeAsync(Arg.Any<Guid>(), Arg.Any<Group>()).Returns(info =>
        {
            var parentId = info.ArgAt<Guid>(0);
            var newGroup = info.ArgAt<Group>(1);

            if (parentId == Guid.Empty)
            {
                var maxRgt = dbContext.Group.Max(g => (int?)g.Rgt) ?? 0;
                newGroup.Lft = maxRgt + 1;
                newGroup.Rgt = maxRgt + 2;
                newGroup.Parent = null;
                newGroup.Root = null;
                newGroup.CreateTime = DateTime.UtcNow;
                
                dbContext.Group.Add(newGroup);
                return Task.FromResult(newGroup);
            }
            
            var parent = dbContext.Group.FirstOrDefault(g => g.Id == parentId);
            if (parent == null) throw new KeyNotFoundException($"Parent group with ID {parentId} not found");
            
            newGroup.Parent = parentId;
            newGroup.Root = parent.Root ?? parent.Id;
            newGroup.Lft = parent.Rgt;
            newGroup.Rgt = parent.Rgt + 1;
            newGroup.CreateTime = DateTime.UtcNow;
            
            dbContext.Group.Add(newGroup);
            return Task.FromResult(newGroup);
        });
        
        mockTreeService.AddRootNodeAsync(Arg.Any<Group>()).Returns(info =>
        {
            var newGroup = info.ArgAt<Group>(0);
            var maxRgt = dbContext.Group.Max(g => (int?)g.Rgt) ?? 0;
            newGroup.Lft = maxRgt + 1;
            newGroup.Rgt = maxRgt + 2;
            newGroup.Parent = null;
            newGroup.Root = null;
            newGroup.CreateTime = DateTime.UtcNow;
            
            dbContext.Group.Add(newGroup);
            return Task.FromResult(newGroup);
        });

        mockSearchService.ApplyFilters(Arg.Any<IQueryable<Group>>(), Arg.Any<GroupFilter>(), Arg.Any<DateOnly>())
            .Returns(info => info.Arg<IQueryable<Group>>());

        mockValidityService.ApplyDateRangeFilter(Arg.Any<IQueryable<Group>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(info => info.Arg<IQueryable<Group>>());

        var mockGroupVisibilityService = Substitute.For<IGroupVisibilityService>();
        var mockGroupServiceFacade = Substitute.For<IGroupServiceFacade>();
        mockGroupServiceFacade.VisibilityService.Returns(mockGroupVisibilityService);
        mockGroupServiceFacade.TreeService.Returns(mockTreeService);
        mockGroupServiceFacade.HierarchyService.Returns(mockHierarchyService);
        mockGroupServiceFacade.SearchService.Returns(mockSearchService);
        mockGroupServiceFacade.ValidityService.Returns(mockValidityService);
        mockGroupServiceFacade.MembershipService.Returns(mockMembershipService);
        mockGroupServiceFacade.IntegrityService.Returns(mockIntegrityService);

        var mockGroupCacheService = Substitute.For<IGroupCacheService>();
        var groupRepository = new GroupRepository(
            dbContext, mockGroupServiceFacade, mockGroupCacheService, _groupLogger, new FixedCompanyClock(DateTimeOffset.UtcNow));
        var unitOfWork = new UnitOfWork(dbContext, _unitOfWorkLogger);
        var group = await CreateGroupAsync(1, clientRepository, clientFilterRepository);
        var command = new PostCommand<GroupResource>(group);
        var groupGeocodingQueue = Substitute.For<IGroupGeocodingQueue>();
        var handler = new PostCommandHandler(groupRepository, _groupMapper, groupGeocodingQueue, unitOfWork, _logger);

        //Act
        var result = await handler.Handle(command, default);

        //Assert
        result.ShouldNotBeNull();
        result!.GroupItems.Count().ShouldBe(7);
    }

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _logger = Substitute.For<ILogger<PostCommandHandler>>();
        _unitOfWorkLogger = Substitute.For<ILogger<UnitOfWork>>();
        _groupLogger = Substitute.For<ILogger<Group>>();
        _truncatedClient = FakeData.Clients.TruncatedClient();
        _groupMapper = new GroupMapper();
        _clientMapper = new ClientMapper();
        _filterMapper = new FilterMapper();

        _clientGroupFilterService = Substitute.For<IClientGroupFilterService>();
        _clientGroupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
    }

    [TearDown]
    public void TearDown()
    {
        dbContext.Database.EnsureDeleted();
        dbContext.Dispose();
    }

    private async Task<GroupResource> CreateGroupAsync(int index, ClientRepository clientRepository, ClientFilterRepository clientFilterRepository)
    {
        var idNumberList = new List<int>()
                                       { 15205,
                                         15215,
                                         15216,
                                         15217,
                                         15220,
                                         15229,
                                         15403 };
        var filter = Clients.Filter();
        filter.Male = true;
        filter.Female = false;
        filter.LegalEntity = false;
        var logger = Substitute.For<ILogger<Klacks.Api.Application.Handlers.Clients.GetTruncatedListQueryHandler>>();
        var handler = new Klacks.Api.Application.Handlers.Clients.GetTruncatedListQueryHandler(clientFilterRepository, clientRepository, _clientMapper, _filterMapper, Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), logger);

        var group = new GroupResource();
        group.Name = $"FakeName{index}";
        group.ValidFrom = DateTime.Now.AddMonths(index * -1);
        group.Description = "Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore et dolore magna aliquyam erat, sed diam voluptua. At vero eos et accusam et justo duo dolores et ea rebum. Stet clita kasd gubergren, no sea takimata sanctus est Lorem ipsum dolor sit amet. Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore et dolore magna aliquyam erat, sed diam voluptua. At vero eos et accusam et justo duo dolores et ea rebum. Stet clita kasd gubergren, no sea takimata sanctus est Lorem ipsum dolor sit amet.";
        group.GroupItems = new List<GroupItemResource>();

        foreach (var id in idNumberList)
        {
            filter.SearchString = id.ToString();
            var command = new Klacks.Api.Application.Queries.Clients.GetTruncatedListQuery(filter);
            var result = await handler.Handle(command, default);
            if (result != null && result.Clients != null && result.Clients.Any())
            {
                var clientId = result.Clients.First().Id;
                var item = new GroupItemResource()
                {
                    ClientId = string.IsNullOrEmpty(clientId) ? null : Guid.Parse(clientId)
                };
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
                }
            }

            if (item.Communications.Any())
            {
                foreach (var communication in item.Communications)
                {
                    communications.Add(communication);
                }
            }

            if (item.Annotations.Any())
            {
                foreach (var annotation in item.Annotations)
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