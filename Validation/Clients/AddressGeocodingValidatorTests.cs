using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class AddressGeocodingValidatorTests
{
    private IGeocodingService _geocoding = null!;
    private ICountryResolver _countryResolver = null!;
    private IAddressRepository _addressRepository = null!;
    private StateAbbreviationResolver _stateResolver = null!;
    private AddressGeocodingValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _geocoding = Substitute.For<IGeocodingService>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _addressRepository = Substitute.For<IAddressRepository>();
        _stateResolver = new StateAbbreviationResolver(Substitute.For<IStateRepository>());
        _validator = new AddressGeocodingValidator(_geocoding, _stateResolver, _countryResolver, _addressRepository);
    }

    [Test]
    public async Task UnchangedAddress_IsNotReGeocoded()
    {
        var id = Guid.NewGuid();
        _addressRepository.GetNoTracking(id).Returns(new Address
        {
            Id = id,
            Street = "Ottostrasse 80",
            Zip = "8032",
            City = "Zürich",
            Country = "CH"
        });

        var address = new AddressResource
        {
            Id = id,
            Street = "Ottostrasse 80",
            Zip = "8032",
            City = "Zürich",
            Country = "CH"
        };

        var result = await _validator.ValidateAsync(new List<AddressResource> { address });

        Assert.That(result.IsValid, Is.True);
        await _geocoding.DidNotReceive().ValidateExactAddressAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ChangedAddress_IsReGeocoded()
    {
        var id = Guid.NewGuid();
        _addressRepository.GetNoTracking(id).Returns(new Address
        {
            Id = id,
            Street = "Ottostrasse 80",
            Zip = "8032",
            City = "Zürich",
            Country = "CH"
        });
        _geocoding.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult { Found = false });

        var address = new AddressResource
        {
            Id = id,
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        var result = await _validator.ValidateAsync(new List<AddressResource> { address });

        Assert.That(result.IsValid, Is.False);
        await _geocoding.Received().ValidateExactAddressAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task NewAddress_IsGeocoded_WithoutStoredLookup()
    {
        _geocoding.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult { Found = false });

        var address = new AddressResource
        {
            Id = Guid.Empty,
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        var result = await _validator.ValidateAsync(new List<AddressResource> { address });

        Assert.That(result.IsValid, Is.False);
        await _geocoding.Received().ValidateExactAddressAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _addressRepository.DidNotReceive().GetNoTracking(Arg.Any<Guid>());
    }

    [Test]
    public async Task GeocoderUnavailable_DoesNotBlockTheSave()
    {
        _geocoding.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult { Found = false, ServiceUnavailable = true, MatchType = "error" });

        var address = new AddressResource
        {
            Id = Guid.Empty,
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        var result = await _validator.ValidateAsync(new List<AddressResource> { address });

        Assert.That(result.IsValid, Is.True);
        Assert.That(address.Latitude, Is.Null);
    }
}
