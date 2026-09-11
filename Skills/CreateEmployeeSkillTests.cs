// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreateEmployeeSkill — the mandatory-onboarding-data guard (address/email/phone),
/// the explicit "user declined" escape (proceedWithoutContact) and the complete happy path.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;

using Klacks.Api.Application.DTOs.Staffs;

using Klacks.Api.Application.Mappers;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateEmployeeSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private FakeSelfApi _api = null!;
    private ICountryResolver _countryResolver = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private CreateEmployeeSkill _skill = null!;
    private Client? _persistedClient;

    private static Countries MakeCountry(string abbr, string prefix, string nameDe, string nameEn) => new()
    {
        Abbreviation = abbr,
        Prefix = prefix,
        Name = new MultiLanguage { De = nameDe, En = nameEn }
    };

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _confirmationStore = Substitute.For<IPendingConfirmationStore>();
        _confirmationStore
            .Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns("confirm-token");

        var ch = MakeCountry("CH", "+41", "Schweiz", "Switzerland");
        var de = MakeCountry("DE", "+49", "Deutschland", "Germany");

        _countryResolver.ResolveAsync("CH", Arg.Any<CancellationToken>()).Returns(ch);
        _countryResolver.ResolveAsync("DE", Arg.Any<CancellationToken>()).Returns(de);
        _countryResolver.ResolveAsync(Arg.Is<string?>(s => string.IsNullOrWhiteSpace(s)), Arg.Any<CancellationToken>())
            .Returns((Countries?)null);
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(ch);

        _persistedClient = null;
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, "api/backend/Clients", new ClientResource { Id = Guid.NewGuid() });

        _skill = new CreateEmployeeSkill(
            _clientRepository, _searchRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver(),
            _countryResolver, _confirmationStore,
            new FixedCompanyClock(new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero)));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanCreateClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> CompleteParameters() => new()
    {
        ["firstName"] = "Heribert",
        ["lastName"] = "Gasparoli",
        ["gender"] = "Male",
        ["memberSince"] = "2026-05-29",
        ["street"] = "Bahnhofstrasse 1",
        ["zip"] = "3097",
        ["city"] = "Liebefeld",
        ["email"] = "heribert@example.com",
        ["phone"] = "+41 79 123 45 67"
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task ReturnsError_AndDoesNotPersist_WhenAddressEmailAndPhoneMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Heribert",
            ["lastName"] = "Gasparoli",
            ["gender"] = "Male",
            ["memberSince"] = "2026-05-29"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("address").And.Contain("email").And.Contain("phone"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ReturnsError_WhenOnlyEmailMissing()
    {
        var parameters = CompleteParameters();
        parameters.Remove("email");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("email"));
        }

    [Test]
    public async Task ReturnsError_WhenOnlyPhoneMissing()
    {
        var parameters = CompleteParameters();
        parameters.Remove("phone");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("phone"));
        }

    [Test]
    public async Task ReturnsError_WhenAddressGivenWithoutZip_EvenIfContactSkipped()
    {
        // Regression: "ohne Kontaktdaten" set proceedWithoutContact=true, which used to skip the WHOLE
        // address check and create an address with an empty zip. An address must always be complete.
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Biel",
            ["lastName"] = "GmbH",
            ["gender"] = "LegalEntity",
            ["company"] = "Biel GmbH",
            ["entityType"] = "Customer",
            ["memberSince"] = "2026-06-01",
            ["street"] = "Bahnhofstr 100",
            ["city"] = "Biel",
            ["proceedWithoutContact"] = true
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("zip"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task CreatesCustomer_WithCompleteAddress_AndNoContact()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Biel",
            ["lastName"] = "GmbH",
            ["gender"] = "LegalEntity",
            ["company"] = "Biel GmbH",
            ["entityType"] = "Customer",
            ["memberSince"] = "2026-06-01",
            ["street"] = "Bahnhofstr 100",
            ["zip"] = "2500",
            ["city"] = "Biel",
            ["proceedWithoutContact"] = true
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }

    [Test]
    public async Task ReturnsError_WhenAddressIncomplete()
    {
        var parameters = CompleteParameters();
        parameters.Remove("zip");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("address"));
        }

    [Test]
    public async Task CreatesClient_WhenProceedWithoutContactIsTrue_DespiteMissingContact()
    {

        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Heribert",
            ["lastName"] = "Gasparoli",
            ["gender"] = "Male",
            ["memberSince"] = "2026-05-29",
            ["proceedWithoutContact"] = true
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        var captured = _api.BodyOf<ClientResource>();
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Addresses, Is.Empty);
        Assert.That(captured.Communications, Is.Empty);
        Assert.That(captured!.Membership, Is.Not.Null);
    }

    [Test]
    public async Task CreatesClient_WithAddressAndCommunications_WhenDataComplete()
    {

        var result = await _skill.ExecuteAsync(Ctx(), CompleteParameters());

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        var captured = _api.BodyOf<ClientResource>();
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.FirstName, Is.EqualTo("Heribert"));
        Assert.That(captured.Name, Is.EqualTo("Gasparoli"));
        Assert.That(captured.Gender, Is.EqualTo(GenderEnum.Male));
        Assert.That(captured.Addresses, Has.Count.EqualTo(1));
        Assert.That(captured.Communications, Has.Count.EqualTo(2));
        Assert.That(captured.Communications.Any(c => c.Type == CommunicationTypeEnum.PrivateMail), Is.True);
        Assert.That(captured.Communications.Any(c => c.Type == CommunicationTypeEnum.PrivateCellPhone), Is.True);
        Assert.That(captured!.Membership, Is.Not.Null);
    }

    [Test]
    public async Task ReturnsError_WhenMemberSinceMissing()
    {
        var parameters = CompleteParameters();
        parameters.Remove("memberSince");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("memberSince"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ReturnsError_WhenMemberSinceUnparseable()
    {
        var parameters = CompleteParameters();
        parameters["memberSince"] = "not-a-date";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("memberSince"));
        }

    [Test]
    public async Task SetsMembershipValidFromFromMemberSince()
    {
        var parameters = CompleteParameters();
        parameters["memberSince"] = "2026-07-01";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var captured = _api.BodyOf<ClientResource>();
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Membership, Is.Not.Null);
        Assert.That(captured.Membership!.ValidFrom.Date, Is.EqualTo(new DateTime(2026, 7, 1)));
        Assert.That(captured.Membership!.ValidFrom.Kind, Is.EqualTo(DateTimeKind.Utc), "ValidFrom is written to a timestamptz column and must carry Kind=Utc, or Npgsql rejects it at SaveChanges.");
    }

    [TestCase("", "CH")]
    [TestCase("CH", "CH")]
    [TestCase("DE", "DE")]
    public async Task DefaultsCountryToCh_AndKeepsProvidedCode(string input, string expected)
    {
        var parameters = CompleteParameters();
        parameters["country"] = input;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var captured = _api.BodyOf<ClientResource>();
        Assert.That(captured!.Addresses.Single().Country, Is.EqualTo(expected));
    }

    [Test]
    public async Task DerivesStateFromZip_WhenStateMissing()
    {
        _searchRepository.FindStatePostCode("3097").Returns("BE");
        var parameters = CompleteParameters();

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var captured = _api.BodyOf<ClientResource>();
        Assert.That(captured!.Addresses.Single().State, Is.EqualTo("BE"));
    }


    [Test]
    public async Task SuccessMessage_CarriesVerifiedMarker()
    {
        var result = await _skill.ExecuteAsync(Ctx(), CompleteParameters());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
    }

    [Test]
    public async Task RequiresConfirmation_AndDoesNotPersist_WhenValidFromMoreThan50YearsInPast()
    {
        var parameters = CompleteParameters();
        parameters["memberSince"] = "1850-01-01";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.Message, Does.Contain("50 years in the past"));
        _api.Calls.ShouldBeEmpty();
        _confirmationStore.Received(1).Create(
            Arg.Any<Guid>(), "create_employee", Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Test]
    public async Task RequiresConfirmation_WhenValidFromBeforeBirthdate()
    {
        var parameters = CompleteParameters();
        parameters["memberSince"] = "2000-01-01";
        parameters["birthdate"] = "2005-06-15";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.Message, Does.Contain("before the employee's birthdate"));
        }

    [Test]
    public async Task Persists_WhenImplausibleValidFrom_ButOverrideFlagConfirmsIt()
    {
        var parameters = CompleteParameters();
        parameters["memberSince"] = "1850-01-01";
        parameters["validFromPlausibilityConfirmed"] = "true";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Type, Is.Not.EqualTo(SkillResultType.Confirmation));
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        _confirmationStore.DidNotReceive().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [TestCase("0791021402", "CH", "+41", "791021402")]
    [TestCase("+41 79 102 14 02", "CH", "+41", "791021402")]
    [TestCase("0044 20 7946 0958", "CH", "", "+442079460958")]
    public async Task SplitsPhoneIntoPrefixAndNumber(string input, string country, string expectedPrefix, string expectedValue)
    {
        var parameters = CompleteParameters();
        parameters["phone"] = input;
        parameters["country"] = country;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var captured = _api.BodyOf<ClientResource>();
        var phone = captured!.Communications.Single(c => c.Type == CommunicationTypeEnum.PrivateCellPhone);
        Assert.That(phone.Prefix, Is.EqualTo(expectedPrefix));
        Assert.That(phone.Value, Is.EqualTo(expectedValue));
    }
}
