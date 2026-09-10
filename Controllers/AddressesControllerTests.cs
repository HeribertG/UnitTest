// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests fuer AddressesController: Adress-Validierung via Geocoding, Missing-Fields-Pruefung
/// und Koordinaten-Zuweisung bei Post/Put.
/// </summary>
/// <param name="mockMediator">Mock des Mediators fuer Query/Command-Handling</param>
/// <param name="mockGeocodingService">Mock des Geocoding-Services fuer Adress-Validierung</param>
/// <param name="mockCoordinateWriter">Mock des Koordinaten-Writers</param>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AddressesControllerTests
{
    private IMediator _mockMediator = null!;
    private ILogger<AddressesController> _mockLogger = null!;
    private IGeocodingService _mockGeocodingService = null!;
    private IAddressCoordinateWriter _mockCoordinateWriter = null!;
    private StateAbbreviationResolver _stateResolver = null!;
    private AddressesController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _mockMediator = Substitute.For<IMediator>();
        _mockLogger = Substitute.For<ILogger<AddressesController>>();
        _mockGeocodingService = Substitute.For<IGeocodingService>();
        _mockCoordinateWriter = Substitute.For<IAddressCoordinateWriter>();
        var mockStateRepository = Substitute.For<Klacks.Api.Domain.Interfaces.Settings.IStateRepository>();
        _stateResolver = new StateAbbreviationResolver(mockStateRepository);
        _controller = new AddressesController(
            _mockMediator, _mockLogger, _mockGeocodingService, _mockCoordinateWriter, _stateResolver);
    }

    [Test]
    public async Task Validate_ValidAddress_ReturnsOk()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                ExactMatch = true,
                Latitude = 47.3769,
                Longitude = 8.5417,
                ReturnedAddress = "Bahnhofstrasse 1, 8001 Zürich, Switzerland",
                MatchType = "exact"
            });

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeTrue();
        response.MatchType.ShouldBe("exact");
        response.Latitude.ShouldBe(47.3769);
        response.Longitude.ShouldBe(8.5417);
    }

    [Test]
    public async Task Validate_InvalidAddress_ReturnsOkWithIsValidFalse()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Nonexistent Street 999",
            Zip = "0000",
            City = "Nowhere",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = false,
                MatchType = "not_found"
            });

        var suggestions = new List<AddressSuggestion>
        {
            new() { Latitude = 47.37, Longitude = 8.54, DisplayName = "Suggestion 1" },
            new() { Latitude = 47.38, Longitude = 8.55, DisplayName = "Suggestion 2" }
        };

        _mockGeocodingService.GetAddressSuggestionsAsync(
            resource.Street, resource.Zip, resource.City, "CH", Arg.Any<int>())
            .Returns(suggestions);

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeFalse();
        response.MatchType.ShouldBe("not_found");
        response.Suggestions.Count().ShouldBe(2);
    }

    [Test]
    public async Task Validate_CityOnlyMatchWithStreet_ReturnsInvalidWithSuggestions()
    {
        // Arrange: street is present but geocoding only matched the city (city_only). This must
        // surface as invalid with suggestions, mirroring AddressGeocodingValidator used on save,
        // so the interactive toast can offer corrections instead of a suggestion-less rejection.
        var resource = new AddressResource
        {
            Street = "ZZ Unverifiable Street 99999",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                ExactMatch = false,
                Latitude = 47.3769,
                Longitude = 8.5417,
                ReturnedAddress = "Zürich, Switzerland",
                MatchType = "city_only"
            });

        var suggestions = new List<AddressSuggestion>
        {
            new() { Latitude = 47.37, Longitude = 8.54, DisplayName = "Zürich, Switzerland" }
        };

        _mockGeocodingService.GetAddressSuggestionsAsync(
            resource.Street, resource.Zip, resource.City, "CH", Arg.Any<int>())
            .Returns(suggestions);

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeFalse();
        response.MatchType.ShouldBe("city_only");
        response.Suggestions.Count().ShouldBe(1);
    }

    [Test]
    public async Task Validate_CityOnlyMatchWithoutStreet_ReturnsValid()
    {
        // Arrange: no street given, only zip+city. A city-level match is acceptable here because
        // there is no street to verify (mirrors AddressGeocodingValidator's !hasStreet branch).
        var resource = new AddressResource
        {
            Street = string.Empty,
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                ExactMatch = false,
                Latitude = 47.3769,
                Longitude = 8.5417,
                ReturnedAddress = "Zürich, Switzerland",
                MatchType = "city_only"
            });

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeTrue();
    }

    [Test]
    public async Task Validate_MissingCity_ReturnsOkWithIsValidFalse()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "",
            Country = "CH"
        };

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeFalse();
        response.MatchType.ShouldBe("missing_fields");
    }

    [Test]
    public async Task Validate_MissingZip_ReturnsOkWithIsValidFalse()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Bahnhofstrasse 1",
            Zip = "",
            City = "Zürich",
            Country = "CH"
        };

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeFalse();
        response.MatchType.ShouldBe("missing_fields");
    }

    [Test]
    public async Task Validate_DefaultsCountryToCH_WhenEmpty()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = ""
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                Latitude = 47.3769,
                Longitude = 8.5417,
                MatchType = "exact"
            });

        // Act
        await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        await _mockGeocodingService.Received(1).ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH");
    }

    [Test]
    public async Task Validate_GeocodingException_ReturnsOkWithValidationError()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new HttpRequestException("Connection refused"));

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = resource.Street, Zip = resource.Zip, City = resource.City, Country = resource.Country });

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeTrue();
        response.MatchType.ShouldBe("validation_error");
    }

    [Test]
    public async Task Validate_GeocoderUnavailable_IsAcceptedLikeAnException_NotRejectedAsNotFound()
    {
        // Arrange
        _mockGeocodingService.ValidateExactAddressAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new GeocodingValidationResult { Found = false, ServiceUnavailable = true, MatchType = "error" });

        // Act
        var result = await _controller.Validate(new AddressValidationRequest { Street = "Bahnhofstrasse 1", Zip = "8001", City = "Zürich", Country = "CH" });

        // Assert
        var response = (result.Result as OkObjectResult)!.Value as AddressValidationResponse;
        response.ShouldNotBeNull();
        response!.IsValid.ShouldBeTrue();
        response.MatchType.ShouldBe("validation_error");
        await _mockGeocodingService.DidNotReceive().GetAddressSuggestionsAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Test]
    public async Task Post_InvalidAddress_ReturnsBadRequest()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Invalid Street",
            Zip = "0000",
            City = "Nowhere",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = false,
                MatchType = "not_found"
            });

        _mockGeocodingService.GetAddressSuggestionsAsync(
            resource.Street, resource.Zip, resource.City, "CH", Arg.Any<int>())
            .Returns(new List<AddressSuggestion>());

        // Act
        var result = await _controller.Post(resource);

        // Assert
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task Put_InvalidAddress_ReturnsBadRequest()
    {
        // Arrange
        var resource = new AddressResource
        {
            Id = Guid.NewGuid(),
            Street = "Invalid Street",
            Zip = "0000",
            City = "Nowhere",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = false,
                MatchType = "not_found"
            });

        _mockGeocodingService.GetAddressSuggestionsAsync(
            resource.Street, resource.Zip, resource.City, "CH", Arg.Any<int>())
            .Returns(new List<AddressSuggestion>());

        // Act
        var result = await _controller.Put(resource);

        // Assert
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task Post_CityOnlyMatchWithStreet_StaysLenientAndDoesNotReturnBadRequest()
    {
        // Direct address Post keeps the lenient geocoding behaviour: a city-only match (street
        // present) is accepted, unlike the strict interactive /Validate pre-check used during
        // client save. This pins the deliberate scoping of the exactness gate to /Validate only.
        var resource = new AddressResource
        {
            Street = "ZZ Unverifiable Street 99999",
            Zip = "8001",
            City = "Zürich",
            Country = "CH"
        };

        _mockGeocodingService.ValidateExactAddressAsync(
            resource.Street, resource.Zip, resource.City, "CH")
            .Returns(new GeocodingValidationResult
            {
                Found = true,
                ExactMatch = false,
                Latitude = 47.3769,
                Longitude = 8.5417,
                MatchType = "city_only"
            });

        // Act
        var result = await _controller.Post(resource);

        // Assert
        result.Result.ShouldNotBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task Post_MissingCity_ReturnsBadRequest()
    {
        // Arrange
        var resource = new AddressResource
        {
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "",
            Country = "CH"
        };

        // Act
        var result = await _controller.Post(resource);

        // Assert
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task Put_MissingCity_ReturnsBadRequest()
    {
        // Arrange
        var resource = new AddressResource
        {
            Id = Guid.NewGuid(),
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "",
            Country = "CH"
        };

        // Act
        var result = await _controller.Put(resource);

        // Assert
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }
}
