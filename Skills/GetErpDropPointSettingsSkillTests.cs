// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_erp_drop_point_settings: reports the drop point's configuration plus the
/// resolved absolute on-disk folder path, its processing/processed/error sub-folder names and the
/// import's poll schedule, resolving the path through the same IObjectStorageService.ResolvePath
/// the folder-health check and the runner use.
/// </summary>

using Klacks.Api.Application.DTOs.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetErpDropPointSettingsSkillTests
{
    private const string ResolvedPath = @"C:\erp-import\erp\orders\";

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private static object GetProperty(object? data, string name)
    {
        data.ShouldNotBeNull();
        var property = data!.GetType().GetProperty(name);
        property.ShouldNotBeNull($"Result data has no property '{name}'.");
        return property!.GetValue(data)!;
    }

    [Test]
    public async Task ExecuteAsync_ReturnsResolvedAbsolutePath()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource
            {
                Id = Guid.NewGuid(),
                Name = "Default",
                SourceSystemId = "default",
                BucketPrefix = "erp/orders",
                IsEnabled = true
            });
        var objectStorageService = Substitute.For<IObjectStorageService>();
        objectStorageService.ResolvePath("erp/orders/").Returns(ResolvedPath);
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetErpDropPointSettingsSkill(mediator, objectStorageService, settingsReader, companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        GetProperty(result.Data, "AbsolutePath").ShouldBe(ResolvedPath);
        result.Message.ShouldContain(ResolvedPath);
    }

    [Test]
    public async Task ExecuteAsync_ReturnsSubFolderNames()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { BucketPrefix = "erp/orders", IsEnabled = true });
        var objectStorageService = Substitute.For<IObjectStorageService>();
        objectStorageService.ResolvePath(Arg.Any<string>()).Returns(ResolvedPath);
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetErpDropPointSettingsSkill(mediator, objectStorageService, settingsReader, companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var subFolders = GetProperty(result.Data, "SubFolders");
        GetProperty(subFolders, "Processing").ShouldBe("processing");
        GetProperty(subFolders, "Processed").ShouldBe("processed");
        GetProperty(subFolders, "Error").ShouldBe("error");
    }

    [Test]
    public async Task ExecuteAsync_UsesConfiguredCronSchedule()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { BucketPrefix = "erp/orders", IsEnabled = true });
        var objectStorageService = Substitute.For<IObjectStorageService>();
        objectStorageService.ResolvePath(Arg.Any<string>()).Returns(ResolvedPath);
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronExpression)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronExpression, Value = "*/15 * * * *" });
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Zurich" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetErpDropPointSettingsSkill(mediator, objectStorageService, settingsReader, companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        GetProperty(result.Data, "CronExpression").ShouldBe("*/15 * * * *");
        GetProperty(result.Data, "TimeZone").ShouldBe("Europe/Zurich");
        result.Message.ShouldContain("*/15 * * * *");
    }

    [Test]
    public async Task ExecuteAsync_MissingCronSettings_FallsBackToDefaultCronAndCompanyZone()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { BucketPrefix = "erp/orders", IsEnabled = true });
        var objectStorageService = Substitute.For<IObjectStorageService>();
        objectStorageService.ResolvePath(Arg.Any<string>()).Returns(ResolvedPath);
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        var skill = new GetErpDropPointSettingsSkill(mediator, objectStorageService, settingsReader, companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        GetProperty(result.Data, "CronExpression").ShouldBe(ErpImportSettingsTypes.DefaultCronExpression);
        GetProperty(result.Data, "TimeZone").ShouldBe("Asia/Tokyo");
    }

    [Test]
    public async Task ExecuteAsync_ResolvesPathFromNormalizedBucketPrefix()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { BucketPrefix = "erp/orders", IsEnabled = true });
        var objectStorageService = Substitute.For<IObjectStorageService>();
        objectStorageService.ResolvePath(Arg.Any<string>()).Returns(ResolvedPath);
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetErpDropPointSettingsSkill(mediator, objectStorageService, settingsReader, companyClock);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        objectStorageService.Received(1).ResolvePath("erp/orders/");
    }
}
