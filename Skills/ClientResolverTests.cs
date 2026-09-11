// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientResolver, driven by the 2026-07-07 skill-usage failure classes: genuine
/// duplicate names (same first and last name, different client numbers) are resolvable via the
/// optional idNumber parameter and never by silently picking one, the ambiguity error tells the
/// model to retry with idNumber, an exact full-name match is preferred over looser search hits,
/// and the legacy overload keeps its self-complete instruction without advertising idNumber.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ClientResolverTests
{
    private IClientSearchRepository _searchRepository = null!;
    private IClientRepository _clientRepository = null!;

    private static readonly Guid FirstDuplicateId = Guid.NewGuid();
    private static readonly Guid SecondDuplicateId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
    }

    private void SearchReturns(params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private void SearchReturnsDuplicateLya()
    {
        SearchReturns(
            new ClientSearchItem { Id = FirstDuplicateId, FirstName = "Lya", LastName = "Ackermann", IdNumber = 1238 },
            new ClientSearchItem { Id = SecondDuplicateId, FirstName = "Lya", LastName = "Ackermann", IdNumber = 1450 });
    }

    [Test]
    public async Task DuplicateNames_WithIdNumber_ResolvesThatExactClient()
    {
        SearchReturnsDuplicateLya();
        _clientRepository.Get(SecondDuplicateId)
            .Returns(new Client { Id = SecondDuplicateId, FirstName = "Lya", Name = "Ackermann" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", 1450, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(SecondDuplicateId));
    }

    [Test]
    public async Task DuplicateNames_WithoutIdNumber_ErrorNamesTheIdNumberParameterAndCandidates()
    {
        SearchReturnsDuplicateLya();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("Multiple clients match"));
        Assert.That(error, Does.Contain("#1238"));
        Assert.That(error, Does.Contain("#1450"));
        Assert.That(error, Does.Contain(ClientResolver.IdNumberParameterName));
        Assert.That(error, Does.Contain("edit page"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task DuplicateNames_WithUnknownIdNumber_ListsTheRealClientNumbers()
    {
        SearchReturnsDuplicateLya();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", 9999, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("9999"));
        Assert.That(error, Does.Contain("#1238"));
        Assert.That(error, Does.Contain("#1450"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task LegacyOverload_KeepsSelfCompleteInstruction_WithoutAdvertisingIdNumber()
    {
        SearchReturnsDuplicateLya();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("complete the requested action"));
        Assert.That(error, Does.Not.Contain(ClientResolver.IdNumberParameterName));
    }

    [Test]
    public async Task ExactFullNameMatch_IsPreferredOverLooserSearchHits()
    {
        var exactId = Guid.NewGuid();
        SearchReturns(
            new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Heribert", LastName = "E2EGasparoli348927", IdNumber = 2424 },
            new ClientSearchItem { Id = exactId, FirstName = "Heribert", LastName = "Gasparoli", IdNumber = 2667 });
        _clientRepository.Get(exactId)
            .Returns(new Client { Id = exactId, FirstName = "Heribert", Name = "Gasparoli" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Heribert", "Gasparoli", null, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(exactId));
    }

    [Test]
    public async Task NoMatch_TellsModelNotToRetryWithTheSameName()
    {
        SearchReturns();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Nemo", "Niemand", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("No client found matching"));
        Assert.That(error, Does.Contain("do not call this skill again with the same name"));
    }

    // ClientResolver is the entry point of every name-based write skill (add phone/email/note,
    // assign contract, remove from group/qualification). It never validates visibility itself —
    // its whole guarantee is that it only ever loads an id that came out of SearchAsync. This
    // test wires the real search repository behind it so that guarantee is checked end to end
    // instead of assumed: a restricted caller must not be able to mutate a foreign client.
    [Test]
    public async Task RestrictedUser_CannotResolveClientOfAnInvisibleGroup_AndNeverLoadsIt()
    {
        var visibleGroupId = Guid.NewGuid();
        var invisibleGroupId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        using var dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        dbContext.Database.EnsureCreated();

        var foreignClientId = Guid.NewGuid();
        dbContext.Client.Add(new Client
        {
            Id = foreignClientId,
            FirstName = "Petra",
            Name = "Steinmann",
            IdNumber = 9101,
            Type = EntityTypeEnum.Employee
        });
        dbContext.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = foreignClientId,
            GroupId = invisibleGroupId,
            AnalyseToken = null
        });
        dbContext.SaveChanges();

        var groupVisibility = Substitute.For<IGroupVisibilityService>();
        groupVisibility.GetVisibilityScopeAsync()
            .Returns(GroupVisibilityScope.Restricted(new[] { visibleGroupId }, new[] { visibleGroupId }));
        var userService = Substitute.For<IUserService>();
        userService.GetIdString().Returns("restricted-user");

        var fuzzySearchService = Substitute.For<IClientFuzzySearchService>();
        fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());

        var searchRepository = new ClientSearchRepository(
            dbContext,
            new ClientGroupFilterService(
                Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(),
                groupVisibility,
                userService,
                Substitute.For<ILogger<ClientGroupFilterService>>()),
            fuzzySearchService,
            new Klacks.UnitTest.TestHelpers.FixedCompanyClock(DateTimeOffset.UtcNow));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            searchRepository, _clientRepository, "Petra", "Steinmann", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("No client found matching"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }
}
