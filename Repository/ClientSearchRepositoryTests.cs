// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientSearchRepository.SearchAsync — verifies that an IdNumber included in the
/// search term narrows an ambiguous name match to exactly one client, which is what lets
/// search_and_navigate resolve a disambiguated selection deterministically instead of relying on
/// the LLM to carry the internal GUID id across a chat turn. Also covers the city, zip-prefix and
/// qualification-validity filters used by fill_group_by_criteria.
/// <para>
/// 🔴 The group-visibility tests are load bearing. Until 2026-08-22 this fixture handed
/// SearchAsync a bare IClientGroupFilterService substitute and asserted nothing about it, so the
/// suite stayed green while SearchAsync never called the filter at all — every Klacksy search and
/// every name-based client edit crossed group boundaries. Any change that makes SearchAsync stop
/// routing its query (and its fuzzy fallback) through the filter, or that counts before filtering,
/// must fail here.
/// </para>
/// </summary>

using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientSearchRepositoryTests
{
    private const string RestrictedUserId = "restricted-user";

    private DataBaseContext _dbContext = null!;
    private IClientGroupFilterService _groupFilterService = null!;
    private Klacks.Api.Application.Interfaces.IClientFuzzySearchService _fuzzySearchService = null!;
    private ClientSearchRepository _repository = null!;
    private Guid _gasparoli6556Id;
    private Guid _gasparoli7001Id;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _gasparoli6556Id = Guid.NewGuid();
        _gasparoli7001Id = Guid.NewGuid();
        _dbContext.Client.AddRange(
            new Client { Id = _gasparoli6556Id, FirstName = "Heribert", Name = "Gasparoli", IdNumber = 6556, Type = EntityTypeEnum.Employee },
            new Client { Id = _gasparoli7001Id, FirstName = "Heribert", Name = "Gasparoli", IdNumber = 7001, Type = EntityTypeEnum.Employee });
        _dbContext.SaveChanges();

        _groupFilterService = Substitute.For<IClientGroupFilterService>();
        PassThrough(_groupFilterService);
        _fuzzySearchService = Substitute.For<Klacks.Api.Application.Interfaces.IClientFuzzySearchService>();
        _fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());

        _repository = new ClientSearchRepository(
            _dbContext, _groupFilterService, _fuzzySearchService, new FixedCompanyClock(DateTimeOffset.UtcNow));
    }

    private static void PassThrough(IClientGroupFilterService filterService)
    {
        filterService
            .FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>(), Arg.Any<bool>())
            .Returns(call => Task.FromResult((IQueryable<Client>)call[1]));
    }

    private ClientSearchRepository CreateRepositoryForRestrictedUser(params Guid[] visibleGroupIds)
    {
        return CreateRepositoryWithRealGroupFilter(
            RestrictedUserId, GroupVisibilityScope.Restricted(visibleGroupIds, visibleGroupIds));
    }

    private ClientSearchRepository CreateRepositoryWithRealGroupFilter(
        string? callingUserId, GroupVisibilityScope scope)
    {
        var groupVisibility = Substitute.For<IGroupVisibilityService>();
        groupVisibility.GetVisibilityScopeAsync().Returns(scope);

        var userService = Substitute.For<IUserService>();
        userService.GetIdString().Returns(callingUserId);

        var realGroupFilter = new ClientGroupFilterService(
            Substitute.For<Klacks.Api.Application.Interfaces.IGetAllClientIdsFromGroupAndSubgroups>(),
            groupVisibility,
            userService,
            Substitute.For<ILogger<ClientGroupFilterService>>());

        return new ClientSearchRepository(
            _dbContext, realGroupFilter, _fuzzySearchService, new FixedCompanyClock(DateTimeOffset.UtcNow));
    }

    private Guid AddClientInGroup(string firstName, string lastName, int idNumber, Guid groupId)
    {
        var clientId = Guid.NewGuid();
        _dbContext.Client.Add(new Client
        {
            Id = clientId,
            FirstName = firstName,
            Name = lastName,
            IdNumber = idNumber,
            Type = EntityTypeEnum.Employee
        });
        _dbContext.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            AnalyseToken = null
        });
        _dbContext.SaveChanges();

        return clientId;
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task SearchAsync_NameAlone_ReturnsBothAmbiguousMatches()
    {
        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10);

        result.TotalCount.ShouldBe(2);
    }

    [Test]
    public async Task SearchAsync_NamePlusIdNumber_NarrowsToExactSingleMatch()
    {
        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli 6556", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().IdNumber.ShouldBe(6556);
    }

    [Test]
    public async Task SearchAsync_IdNumberAlone_MatchesOnlyThatClient()
    {
        var result = await _repository.SearchAsync(searchTerm: "7001", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().IdNumber.ShouldBe(7001);
    }

    [Test]
    public async Task SearchAsync_CityFilter_MatchesExactly_CaseInsensitiveAndTrimmed()
    {
        var bernClientId = Guid.NewGuid();
        var zurichClientId = Guid.NewGuid();
        _dbContext.Client.AddRange(
            new Client { Id = bernClientId, FirstName = "A", Name = "One", Type = EntityTypeEnum.Employee },
            new Client { Id = zurichClientId, FirstName = "B", Name = "Two", Type = EntityTypeEnum.Employee });
        _dbContext.Address.AddRange(
            new Address { Id = Guid.NewGuid(), ClientId = bernClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3000" },
            new Address { Id = Guid.NewGuid(), ClientId = zurichClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Zürich", Zip = "8000" });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: " bern ", zipPrefix: null, qualificationId: null, qualificationValidityDate: null,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(bernClientId);
    }

    [Test]
    public async Task SearchAsync_ZipPrefixFilter_MatchesStartsWith()
    {
        var matchingClientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        _dbContext.Client.AddRange(
            new Client { Id = matchingClientId, FirstName = "A", Name = "One", Type = EntityTypeEnum.Employee },
            new Client { Id = otherClientId, FirstName = "B", Name = "Two", Type = EntityTypeEnum.Employee });
        _dbContext.Address.AddRange(
            new Address { Id = Guid.NewGuid(), ClientId = matchingClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3007" },
            new Address { Id = Guid.NewGuid(), ClientId = otherClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Basel", Zip = "4000" });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: null, zipPrefix: "30", qualificationId: null, qualificationValidityDate: null,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(matchingClientId);
    }

    [Test]
    public async Task SearchAsync_QualificationFilter_MatchesOnlyCurrentlyValidHolder()
    {
        var qualificationId = Guid.NewGuid();
        var referenceDate = new DateOnly(2026, 6, 1);
        var validHolderId = Guid.NewGuid();
        var expiredHolderId = Guid.NewGuid();
        var notYetValidHolderId = Guid.NewGuid();
        var nonHolderId = Guid.NewGuid();

        _dbContext.Client.AddRange(
            new Client { Id = validHolderId, FirstName = "A", Name = "Valid", Type = EntityTypeEnum.Employee },
            new Client { Id = expiredHolderId, FirstName = "B", Name = "Expired", Type = EntityTypeEnum.Employee },
            new Client { Id = notYetValidHolderId, FirstName = "C", Name = "Future", Type = EntityTypeEnum.Employee },
            new Client { Id = nonHolderId, FirstName = "D", Name = "None", Type = EntityTypeEnum.Employee });

        _dbContext.ClientQualification.AddRange(
            new ClientQualification { Id = Guid.NewGuid(), ClientId = validHolderId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = null },
            new ClientQualification { Id = Guid.NewGuid(), ClientId = expiredHolderId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = new DateOnly(2026, 1, 1) },
            new ClientQualification { Id = Guid.NewGuid(), ClientId = notYetValidHolderId, QualificationId = qualificationId, ValidFrom = new DateOnly(2027, 1, 1), ValidUntil = null });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: null, zipPrefix: null, qualificationId: qualificationId, qualificationValidityDate: referenceDate,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(validHolderId);
    }

    [Test]
    public async Task SearchAsync_CombinesCityZipPrefixAndQualification_WithAndSemantics()
    {
        var qualificationId = Guid.NewGuid();
        var referenceDate = new DateOnly(2026, 6, 1);
        var fullMatchId = Guid.NewGuid();
        var wrongCityId = Guid.NewGuid();
        var noQualificationId = Guid.NewGuid();

        _dbContext.Client.AddRange(
            new Client { Id = fullMatchId, FirstName = "A", Name = "Full", Type = EntityTypeEnum.Employee },
            new Client { Id = wrongCityId, FirstName = "B", Name = "WrongCity", Type = EntityTypeEnum.Employee },
            new Client { Id = noQualificationId, FirstName = "C", Name = "NoQualification", Type = EntityTypeEnum.Employee });

        _dbContext.Address.AddRange(
            new Address { Id = Guid.NewGuid(), ClientId = fullMatchId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3007" },
            new Address { Id = Guid.NewGuid(), ClientId = wrongCityId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Basel", Zip = "3007" },
            new Address { Id = Guid.NewGuid(), ClientId = noQualificationId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3007" });

        _dbContext.ClientQualification.AddRange(
            new ClientQualification { Id = Guid.NewGuid(), ClientId = fullMatchId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = null },
            new ClientQualification { Id = Guid.NewGuid(), ClientId = wrongCityId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = null });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: "Bern", zipPrefix: "30", qualificationId: qualificationId, qualificationValidityDate: referenceDate,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(fullMatchId);
    }

    [Test]
    public async Task SearchAsync_RoutesTheQueryThroughTheGroupFilter()
    {
        await _repository.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10);

        await _groupFilterService.Received(1)
            .FilterClientsByGroupId(null, Arg.Any<IQueryable<Client>>(), false);
    }

    [Test]
    public async Task SearchAsync_ClientRemovedByGroupFilter_IsMissingFromItemsAndFromTotalCount()
    {
        _groupFilterService
            .FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>(), Arg.Any<bool>())
            .Returns(call => Task.FromResult(
                ((IQueryable<Client>)call[1]).Where(c => c.Id == _gasparoli7001Id)));

        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10);

        result.Items.Select(i => i.Id).ShouldBe(new[] { _gasparoli7001Id });
        result.TotalCount.ShouldBe(1);
    }

    [Test]
    public async Task SearchAsync_RestrictedUser_DoesNotFindClientOfAnInvisibleGroup()
    {
        var visibleGroupId = Guid.NewGuid();
        var invisibleGroupId = Guid.NewGuid();
        var ownClientId = AddClientInGroup("Petra", "Steiner", 8001, visibleGroupId);
        AddClientInGroup("Petra", "Steinmann", 8002, invisibleGroupId);
        var repository = CreateRepositoryForRestrictedUser(visibleGroupId);

        var result = await repository.SearchAsync(searchTerm: "Petra", limit: 10);

        result.Items.Select(i => i.Id).ShouldBe(new[] { ownClientId });
        result.TotalCount.ShouldBe(1);
    }

    [Test]
    public async Task SearchAsync_RestrictedUser_StillFindsClientWithoutAnyGroup()
    {
        var repository = CreateRepositoryForRestrictedUser(Guid.NewGuid());

        var result = await repository.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10);

        result.TotalCount.ShouldBe(2);
    }

    // Background and system callers (AnswerGroundingNameResolver, ClientSlotEntityResolver, the
    // LLM background task path) reach SearchAsync without an HTTP user. There is nobody whose
    // visibility could apply, so the query must stay unrestricted. Were this branch to fall
    // through to the restricted one, scoping SearchAsync would silently blank out search for all
    // of them — a Betriebsbruch none of the restricted-user tests above would notice.
    [Test]
    public async Task SearchAsync_NoCallingUser_StaysUnrestricted()
    {
        var invisibleGroupId = Guid.NewGuid();
        AddClientInGroup("Petra", "Steinmann", 8005, invisibleGroupId);
        var repository = CreateRepositoryWithRealGroupFilter(
            callingUserId: null, GroupVisibilityScope.Restricted([], []));

        var result = await repository.SearchAsync(searchTerm: "Petra", limit: 10);

        result.TotalCount.ShouldBe(1);
    }

    [Test]
    public async Task SearchAsync_FuzzyFallback_RestrictedUser_DoesNotLeakClientOfAnInvisibleGroup()
    {
        var visibleGroupId = Guid.NewGuid();
        var invisibleGroupId = Guid.NewGuid();
        var foreignClientId = AddClientInGroup("Petra", "Mayer", 8003, invisibleGroupId);
        _fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = foreignClientId, FirstName = "Petra", Name = "Mayer", IdNumber = 8003 } });
        var repository = CreateRepositoryForRestrictedUser(visibleGroupId);

        var result = await repository.SearchAsync(searchTerm: "Meier", limit: 10);

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Test]
    public async Task SearchAsync_FuzzyFallback_RestrictedUser_StillFindsClientOfAVisibleGroup()
    {
        var visibleGroupId = Guid.NewGuid();
        var ownClientId = AddClientInGroup("Petra", "Mayer", 8004, visibleGroupId);
        _fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = ownClientId, FirstName = "Petra", Name = "Mayer", IdNumber = 8004 } });
        var repository = CreateRepositoryForRestrictedUser(visibleGroupId);

        var result = await repository.SearchAsync(searchTerm: "Meier", limit: 10);

        result.Items.Select(i => i.Id).ShouldBe(new[] { ownClientId });
    }
}
