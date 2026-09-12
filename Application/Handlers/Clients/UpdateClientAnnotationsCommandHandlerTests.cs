// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The note-only write path of PUT api/backend/Clients, against a real in-memory DataBaseContext and
/// the real ClientRepository: a caller who may write notes but not clients sends the whole client
/// resource, and only the notes out of it may reach the database. The decisive assertion is the
/// negative one — the client's own fields keep their stored values although the caller changed them in
/// the request.
/// </summary>

using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class UpdateClientAnnotationsCommandHandlerTests
{
    private const string StoredName = "Müller";
    private const string StoredFirstName = "Hans";
    private const string StoredCompany = "Wachdienst AG";

    private DataBaseContext _context = null!;
    private ClientRepository _clientRepository = null!;
    private UpdateClientAnnotationsCommandHandler _handler = null!;
    private Guid _clientId;

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

        _handler = new UpdateClientAnnotationsCommandHandler(
            _clientRepository,
            new ClientMapper(),
            new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<ILogger<UpdateClientAnnotationsCommandHandler>>());

        _clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = _clientId,
            Name = StoredName,
            FirstName = StoredFirstName,
            Company = StoredCompany,
            Gender = GenderEnum.Male,
            LdapExternalId = "ldap-42"
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    private async Task<Client> StoredClient()
        => await _context.Client
            .Include(c => c.Annotations)
            .AsNoTracking()
            .SingleAsync(c => c.Id == _clientId);

    [Test]
    public async Task NewNote_IsStored()
    {
        var result = await _handler.Handle(
            new UpdateClientAnnotationsCommand(
                _clientId,
                [new AnnotationResource { ClientId = _clientId, Note = "Called in sick" }]),
            CancellationToken.None);

        result.ShouldNotBeNull();

        var stored = await StoredClient();
        stored.Annotations.Count.ShouldBe(1);
        stored.Annotations.Single().Note.ShouldBe("Called in sick");
        stored.Annotations.Single().ClientId.ShouldBe(_clientId);
    }

    [Test]
    public async Task ChangedClientFields_AreNotPartOfTheCommandAndStayAsStored()
    {
        await _handler.Handle(
            new UpdateClientAnnotationsCommand(
                _clientId,
                [new AnnotationResource { ClientId = _clientId, Note = "Called in sick" }]),
            CancellationToken.None);

        var stored = await StoredClient();
        stored.Name.ShouldBe(StoredName);
        stored.FirstName.ShouldBe(StoredFirstName);
        stored.Company.ShouldBe(StoredCompany);
        stored.LdapExternalId.ShouldBe("ldap-42");
    }

    [Test]
    public async Task ExistingNote_IsUpdatedInPlace()
    {
        var annotationId = Guid.NewGuid();
        _context.Annotation.Add(new Annotation { Id = annotationId, ClientId = _clientId, Note = "first" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _handler.Handle(
            new UpdateClientAnnotationsCommand(
                _clientId,
                [new AnnotationResource { Id = annotationId, ClientId = _clientId, Note = "corrected" }]),
            CancellationToken.None);

        var stored = await StoredClient();
        stored.Annotations.Count.ShouldBe(1);
        stored.Annotations.Single().Id.ShouldBe(annotationId);
        stored.Annotations.Single().Note.ShouldBe("corrected");
    }

    [Test]
    public async Task NoteMissingFromTheList_IsRemoved()
    {
        _context.Annotation.Add(new Annotation { Id = Guid.NewGuid(), ClientId = _clientId, Note = "obsolete" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _handler.Handle(
            new UpdateClientAnnotationsCommand(_clientId, []),
            CancellationToken.None);

        (await StoredClient()).Annotations.ShouldBeEmpty();
    }

    [Test]
    public async Task Response_CarriesTheClientRelationsTheNoteCardReloads()
    {
        _context.Address.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            Type = AddressTypeEnum.Workplace,
            ValidFrom = DateTime.UtcNow,
            Street = "Bahnhofstrasse 1",
            Zip = "3000",
            City = "Bern"
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _handler.Handle(
            new UpdateClientAnnotationsCommand(
                _clientId,
                [new AnnotationResource { ClientId = _clientId, Note = "Called in sick" }]),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.Name.ShouldBe(StoredName);
        result.Annotations.Count.ShouldBe(1);
        result.Addresses.Count.ShouldBe(1);
    }

    [Test]
    public async Task UnknownClient_ReturnsNull()
    {
        var result = await _handler.Handle(
            new UpdateClientAnnotationsCommand(Guid.NewGuid(), []),
            CancellationToken.None);

        result.ShouldBeNull();
    }
}
