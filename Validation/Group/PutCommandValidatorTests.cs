using FluentValidation.Results;
using Klacks.Api.Commands;
using Klacks.Api.Resources.Associations;
using Klacks.Api.Validation.Groups;

namespace UnitTest.Validation.Group;

[TestFixture]
public class PutCommandValidatorTests
{
  private PutCommandValidator _validator;

  [SetUp]
  public void Setup()
  {
    _validator = new PutCommandValidator();
  }

  [Test]
  public async Task Validate_ShouldBeInvalid_WhenNameIsEmpty()
  {
    // Arrange
    var groupResource = new GroupResource { Name = string.Empty, ValidFrom = DateTime.Now };
    var command = new PutCommand<GroupResource>(groupResource);

    // Act
    var result = await _validator.ValidateAsync(command);

    // Assert
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Name is required"));
  }

  [Test]
  public async Task Validate_ShouldBeInvalid_WhenValidFromIsDefault()
  {
    // Arrange
    var groupResource = new GroupResource { Name = "Valid Name", ValidFrom = default };
    var command = new PutCommand<GroupResource>(groupResource);

    // Act
    var result = await _validator.ValidateAsync(command);

    // Assert
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "ValidFrom: Valid date is required"));
  }
}
