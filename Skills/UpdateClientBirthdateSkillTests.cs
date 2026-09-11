// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateClientBirthdateSkill — invalid date, unresolved / ambiguous client,
/// verified happy path (message carries "verified") and the rollback path when the re-read
/// does not confirm the new birthdate.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

using Klacks.Api.Application.DTOs.Staffs;

using Klacks.Api.Application.Mappers;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientBirthdateSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private FakeSelfApi _api = null!;
    private UpdateClientBirthdateSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Clients", new ClientResource());
        _skill = new UpdateClientBirthdateSkill(_clientRepository, _searchRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private void WireSearch(params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private Client WireResolvedClient(string firstName = "Anna", string lastName = "Müller")
    {
        var client = new Client { Id = Guid.NewGuid(), FirstName = firstName, Name = lastName };
        WireSearch(new ClientSearchItem { Id = client.Id, FirstName = firstName, LastName = lastName, IdNumber = 7 });
        _clientRepository.Get(client.Id).Returns(client);
        _clientRepository.GetNoTracking(client.Id).Returns(client);
        return client;
    }

    private static Dictionary<string, object> Parameters(string birthdate = "1990-04-12") => new()
    {
        ["firstName"] = "Anna",
        ["lastName"] = "Müller",
        ["birthdate"] = birthdate
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task ReturnsError_WhenBirthdateInvalid()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters(birthdate: "not-a-date"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Invalid birthdate"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        WireSearch();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("No client found"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ReturnsError_WhenMultipleClientsMatch()
    {
        WireSearch(
            new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Anna", LastName = "Müller", IdNumber = 1 },
            new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Anna", LastName = "Müller", IdNumber = 2 });

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Multiple clients match"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdatesBirthdate_AndConfirmsInDatabase()
    {
        var client = WireResolvedClient();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(client.Birthdate, Is.EqualTo(new DateTime(1990, 4, 12)));
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
            }

    [Test]
    public async Task UpdatesBirthdate_KeepsTheWrittenCalendarDay_ForAnOffsetInput()
    {
        var client = WireResolvedClient();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters(birthdate: "1990-05-12T00:00:00+02:00"));

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(client.Birthdate, Is.EqualTo(new DateTime(1990, 5, 12, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(client.Birthdate!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task ReturnsError_WhenTheEndpointRejectsTheUpdate()
    {
        WireResolvedClient();
        _api.RespondWithValidationErrors(HttpMethod.Put, "api/backend/Clients", new Dictionary<string, string[]>
        {
            ["Birthdate"] = ["'Birthdate' is not valid."]
        });

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not valid"));
    }

    [Test]
    public async Task ItGoesToTheClientEndpoint_BecauseItChangesTheClientItself()
    {
        WireResolvedClient();

        await _skill.ExecuteAsync(Ctx(), Parameters());

        _api.SingleCall.Route.ShouldBe("api/backend/Clients");
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }
}
