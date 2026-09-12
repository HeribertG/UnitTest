// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// ClientRepository.Put against a real in-memory DataBaseContext, exercising the columns ClientResource
/// cannot carry. The incident: a client mapped from the resource leaves LdapExternalId,
/// IdentityProviderId, SourceSystemId and ExternalCustomerReference at null, and
/// entry.CurrentValues.SetValues(...) wrote those nulls over the stored values, so every ordinary client
/// save unlinked the client from its identity provider and from its ERP customer record. The second
/// test is the other half: a caller that does supply a value must still be able to change it, otherwise
/// the LDAP sync and the ERP import would be frozen out.
/// </summary>

using Klacks.Api.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientRepositoryPutPreservedColumnsTests
{
    private const string StoredLdapExternalId = "ldap-object-guid";
    private const string StoredSourceSystemId = "ERP-01";
    private const string StoredExternalCustomerReference = "CUST-4711";

    private DataBaseContext _context = null!;
    private ClientRepository _clientRepository = null!;
    private UnitOfWork _unitOfWork = null!;
    private Guid _clientId;
    private Guid _identityProviderId;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _clientRepository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IClientValidator>(),
            Substitute.For<ILogger<ClientRepository>>());

        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());

        _clientId = Guid.NewGuid();
        _identityProviderId = Guid.NewGuid();

        _context.Client.Add(new Client
        {
            Id = _clientId,
            Name = "Müller",
            FirstName = "Hans",
            Gender = GenderEnum.Male,
            LdapExternalId = StoredLdapExternalId,
            IdentityProviderId = _identityProviderId,
            SourceSystemId = StoredSourceSystemId,
            ExternalCustomerReference = StoredExternalCustomerReference
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    private async Task<Client> StoredClient()
        => await _context.Client.AsNoTracking().SingleAsync(c => c.Id == _clientId);

    [Test]
    public async Task Put_WithAClientMappedFromTheResource_KeepsTheColumnsTheResourceCannotCarry()
    {
        // Exactly what ClientMapper.ToEntity produces: every column without a resource source at null.
        var incoming = new Client
        {
            Id = _clientId,
            Name = "Meier",
            FirstName = "Hans",
            Gender = GenderEnum.Male
        };

        await _clientRepository.Put(incoming);
        await _unitOfWork.CompleteAsync();

        var stored = await StoredClient();
        stored.Name.ShouldBe("Meier");
        stored.LdapExternalId.ShouldBe(StoredLdapExternalId);
        stored.IdentityProviderId.ShouldBe(_identityProviderId);
        stored.SourceSystemId.ShouldBe(StoredSourceSystemId);
        stored.ExternalCustomerReference.ShouldBe(StoredExternalCustomerReference);
    }

    [Test]
    public async Task Put_WithAClientThatCarriesTheColumns_StillWritesThem()
    {
        var otherProviderId = Guid.NewGuid();
        var incoming = new Client
        {
            Id = _clientId,
            Name = "Müller",
            FirstName = "Hans",
            Gender = GenderEnum.Male,
            LdapExternalId = "ldap-moved",
            IdentityProviderId = otherProviderId,
            SourceSystemId = "ERP-02",
            ExternalCustomerReference = "CUST-9999"
        };

        await _clientRepository.Put(incoming);
        await _unitOfWork.CompleteAsync();

        var stored = await StoredClient();
        stored.LdapExternalId.ShouldBe("ldap-moved");
        stored.IdentityProviderId.ShouldBe(otherProviderId);
        stored.SourceSystemId.ShouldBe("ERP-02");
        stored.ExternalCustomerReference.ShouldBe("CUST-9999");
    }
}
