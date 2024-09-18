
using Klacks_api.Datas;
using Klacks_api.Interfaces;
using Klacks_api.Models.Authentification;
using Klacks_api.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework.Internal;

namespace UnitTest.Repository;

[TestFixture]
public class AccountTests
{
  public IHttpContextAccessor _httpContextAccessor = null!;
  private readonly string Token = new RefreshTokenGenerator().GenerateRefreshToken();
  private AccountRepository _accountRepository = null!;
  private DataBaseContext _dbContext;
  private JwtSettings _jwtSettings = null!;
  private ITokenService? _tokenService = null!;
  private UserManager<AppUser> _userManager;

  [Test]
  public async Task ChangeRoleUser_ShouldChangeRole_WhenNewRoleIsValid()
  {
    // Arrange

    var changeRole = new ChangeRole
    {
      UserId = "672f77e8-e479-4422-8781-84d218377fb3",
      RoleName = "User",
      IsSelected = true
    };

    // Act
    var result = await _accountRepository.ChangeRoleUser(changeRole);

    // Assert
    Assert.That(result.Success, Is.True);
  }

  [Test]
  public async Task LogInUser_ShouldFailWhenUserNotFound()
  {
    // Arrange
    string email = "notfound@test.com";
    string password = "TestPassword123!";

    _userManager.FindByEmailAsync(email)!.Returns(Task.FromResult<AppUser>(null!));

    // Act
    var result = await _accountRepository.LogInUser(email, password);

    // Assert
    Assert.That(result.Success, Is.False, "Login should fail when user is not found.");
    Assert.That(result.ModelState, Contains.Key("Login failed"));
  }

  [Test]
  public async Task LogInUser_ShouldFailWithWrongPassword()
  {
    // Arrange
    var user = new AppUser
    {
      UserName = "MyUser",
      Email = "admin@test.com"
    };
    string wrongPassword = "WrongPassword!";

    _userManager.FindByEmailAsync(Arg.Is<string>(email => email == wrongPassword))
     .Returns(Task.FromResult(new AppUser { Email = wrongPassword, UserName = "knownUser" }));

    // Act
    var result = await _accountRepository.LogInUser(user.Email, wrongPassword);

    // Assert
    Assert.That(result.Success, Is.False, "Login should fail with the wrong password.");
    Assert.That(result.ModelState, Contains.Key("Login failed"));
  }

  [Test]
  public async Task LogInUser_ShouldLogInSuccessfully()
  {
    // Arrange
    var user = new AppUser
    {
      UserName = "MyUser",
      Email = "admin@test.com"
    };
    string _email = "admin@test.com";
    string password = "P@ssw0rt1";

    _userManager.FindByEmailAsync(Arg.Is<string>(email => email == _email))!
    .Returns(Task.FromResult(user));

    _userManager.CheckPasswordAsync(Arg.Any<AppUser>(), Arg.Is<string>(password => password == "P@ssw0rt1"))
        .Returns(Task.FromResult(true));

    // Act
    var result = await _accountRepository.LogInUser(_email, password);

    // Assert
    Assert.That(result.Success, Is.True, "User should be logged in successfully.");
    // Weitere Überprüfungen können hinzugefügt werden, z. B. ob ein Token zurückgegeben wurde.
  }

  [Test]
  public async Task RegisterUser_ShouldRegisterSuccessfully()
  {
    // Arrange
    _jwtSettings = new JwtSettings
    {
      Secret = "VerySecretKey",
      ValidIssuer = "Issuer",
      ValidAudience = "Audience"
    };

    var accountRepository = new AccountRepository(_dbContext, _userManager, _jwtSettings, _tokenService);
    var user = new AppUser
    {
      UserName = "MyUser",
      FirstName = "Test",
      LastName = "Test",
      Email = "123@test.com"
    };
    string password = "TestPassword123!";

    // Act
    var result = await accountRepository.RegisterUser(user, password);

    // Assert
    Assert.That(result.Success, Is.True, "User should be registered successfully.");
  }

  [SetUp]
  public void Setup()
  {
    _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
    _tokenService = Substitute.For<ITokenService>();
    var options = new DbContextOptionsBuilder<DataBaseContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

    _dbContext = new DataBaseContext(options, _httpContextAccessor);
    _userManager = MockUserManager();

    _tokenService.CreateToken(Arg.Any<AppUser>(), Arg.Any<DateTime>()).Returns(Task.FromResult("1234567890"));

    _dbContext.Database.EnsureCreated();

    // Seed the database with test data
    SeedDatabase();

    _jwtSettings = new JwtSettings
    {
      Secret = "ThisIsASampleKeyForJwtToken",
      ValidIssuer = "SampleIssuer",
      ValidAudience = "SampleAudience",
    };

    _accountRepository = new AccountRepository(_dbContext, _userManager, _jwtSettings, _tokenService);
  }

  [TearDown]
  public void Teardown()
  {
    if (_dbContext != null)
    {
      _dbContext.Database.EnsureDeleted();
      _dbContext.Dispose();
    }
    _userManager.Dispose();
  }

  [Test]
  public async Task ValidateRefreshTokenAsync_ShouldReturnTrue()
  {
    // Arrange
    var user = await _dbContext.Users.FirstAsync() as AppUser;
    var validToken = Token;

    // Act
    var result = await _accountRepository.ValidateRefreshTokenAsync(user!, validToken);

    // Assert
    Assert.That(result, Is.True);
  }

  private static UserManager<AppUser> MockUserManager()
  {
    var store = Substitute.For<IUserStore<AppUser>>();
    var optionsAccessor = Substitute.For<IOptions<IdentityOptions>>();
    var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
    var userValidators = new List<IUserValidator<AppUser>>();
    var passwordValidators = new List<IPasswordValidator<AppUser>>();
    var keyNormalizer = Substitute.For<ILookupNormalizer>();
    var errors = Substitute.For<IdentityErrorDescriber>();
    var services = Substitute.For<IServiceProvider>();
    var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

    var userManager = Substitute.For<UserManager<AppUser>>(
        store,
        optionsAccessor,
        passwordHasher,
        userValidators,
        passwordValidators,
        keyNormalizer,
        errors,
        services,
        logger);

    userManager.FindByEmailAsync(Arg.Any<string>())!
        .Returns(Task.FromResult<AppUser>(null!));

    userManager.CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>())
        .Returns(Task.FromResult(IdentityResult.Success));

    userManager.IsInRoleAsync(Arg.Is<AppUser>(user => user != null && user.UserName == "Admin"), "Admin")
        .Returns(Task.FromResult(true));

    userManager.IsInRoleAsync(Arg.Is<AppUser>(user => user != null && user.UserName == "Authorised"), "Authorised")
        .Returns(Task.FromResult(true));

    userManager.CheckPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(Task.FromResult(false));

    userManager.FindByIdAsync("672f77e8-e479-4422-8781-84d218377fb3")!.Returns(Task.FromResult(new AppUser() { Id = "672f77e8-e479-4422-8781-84d218377fb3" }));

    userManager.IsInRoleAsync(Arg.Any<AppUser>(), "User").Returns(Task.FromResult(false));

    userManager.AddToRoleAsync(Arg.Any<AppUser>(), "User").Returns(Task.FromResult(IdentityResult.Success));

    return userManager;
  }

  [TearDown]
  public void Dispose()
  {
    _userManager.Dispose();
    _tokenService = null;
  }

  private void SeedDatabase()
  {
    var roles = new[]
    {
        new IdentityRole { Id = "9c05bb10-5855-4201-a755-1d92ed9df000", ConcurrencyStamp = "d94790da-0103-4ade-b715-29526b2b1fc7", Name = "Authorised", NormalizedName = "AUTHORISED" },
        new IdentityRole { Id = "e32d7319-6861-4c9a-b096-08a77088cadd", ConcurrencyStamp = "402b8312-92a7-43f4-be73-b3400ccc2a7b", Name = "Admin", NormalizedName = "ADMIN" }
    };

    _dbContext.Roles.AddRange(roles);

    var user = new AppUser
    {
      Id = "672f77e8-e479-4422-8781-84d218377fb3",
      AccessFailedCount = 0,
      ConcurrencyStamp = "217b0216-5440-4e51-a6e4-ea79d0da9155",
      Email = "admin@test.com",
      EmailConfirmed = true,
      FirstName = "admin",
      LastName = "admin",
      LockoutEnabled = false,
      NormalizedEmail = "ADMIN@TEST.COM",
      NormalizedUserName = "ADMIN",
      PasswordHash = "AQAAAAEAACcQAAAAEM4rFqzwCkNDdqC7P5XDITL1ub4TLm1MPZMru7BlKyFLNSRfaamO4BUl/fAV4aNNlA==",
      PhoneNumber = "123456789",
      PhoneNumberConfirmed = false,
      SecurityStamp = "a04e4667-082e-43df-b82a-3ff914fc7db7",
      TwoFactorEnabled = false,
      UserName = "admin"
    };

    _dbContext.Users.Add(user);

    var userRoles = new[]
    {
        new IdentityUserRole<string> { RoleId = "9c05bb10-5855-4201-a755-1d92ed9df000", UserId = "672f77e8-e479-4422-8781-84d218377fb3" },
        new IdentityUserRole<string> { RoleId = "e32d7319-6861-4c9a-b096-08a77088cadd", UserId = "672f77e8-e479-4422-8781-84d218377fb3" }
    };

    _dbContext.UserRoles.AddRange(userRoles);

    var refreshToken = new RefreshToken
    {
      AspNetUsersId = user.Id,
      Token = Token,
      ExpiryDate = DateTime.UtcNow.AddHours(1),
    };

    _dbContext.RefreshToken.Add(refreshToken);

    _dbContext.SaveChanges();
  }
}
