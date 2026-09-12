// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the role attributes of every endpoint Option B (spec 2.3, 2026-09-12) re-gated, so the
/// target model stays readable from the tests: a caller without any role (Planer) writes work,
/// absences and notes; everything that shapes the installation stays with Admin or Authorised.
/// Attribute-level only — the runtime effect of these attributes is the subject of
/// AuthorizeAttributeInheritanceTests and ControllerAuthorizeSchemeGuardTests.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Associations;
using Klacks.Api.Presentation.Controllers.UserBackend.Email;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Scheduling;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Authorization;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class OptionBEndpointAuthorizationTests
{
    private static readonly string AdminAndAuthorised = $"{Roles.Admin},{Roles.Authorised}";

    [TestCase(typeof(WorksController), nameof(WorksController.ClosePeriod))]
    [TestCase(typeof(WorksController), nameof(WorksController.ReopenPeriod))]
    [TestCase(typeof(SchedulingRulesController), nameof(SchedulingRulesController.CreateHolidayWorkExemption))]
    [TestCase(typeof(SchedulingRulesController), nameof(SchedulingRulesController.DeleteHolidayWorkExemption))]
    [TestCase(typeof(SchedulingRulesController), nameof(SchedulingRulesController.MigrateContracts))]
    [TestCase(typeof(SpamRulesController), nameof(SpamRulesController.CreateSpamRule))]
    [TestCase(typeof(SpamRulesController), nameof(SpamRulesController.UpdateSpamRule))]
    [TestCase(typeof(SpamRulesController), nameof(SpamRulesController.DeleteSpamRule))]
    public void AdminOnlyEndpoints(Type controller, string methodName)
    {
        RolesOf(controller, methodName).ShouldBe(
            Roles.Admin,
            $"{controller.Name}.{methodName} changes installation-wide state and is Admin-only per spec 2.3.");
    }

    [TestCase(typeof(WorksController), nameof(WorksController.ApproveDay))]
    [TestCase(typeof(WorksController), nameof(WorksController.RevokeDayApproval))]
    [TestCase(typeof(ClientAvailabilitiesController), nameof(ClientAvailabilitiesController.BulkUpdate))]
    [TestCase(typeof(ClientShiftPreferencesController), nameof(ClientShiftPreferencesController.SaveAll))]
    [TestCase(typeof(GroupsController), nameof(GroupsController.MoveGroup))]
    [TestCase(typeof(GroupItemsController), nameof(GroupItemsController.RemoveByClientAndGroup))]
    [TestCase(typeof(ContainersController), nameof(ContainersController.PostTemplates))]
    [TestCase(typeof(ContainersController), nameof(ContainersController.PutTemplates))]
    [TestCase(typeof(ContainersController), nameof(ContainersController.DeleteTemplates))]
    [TestCase(typeof(ContainersController), nameof(ContainersController.PostOverride))]
    [TestCase(typeof(ContainersController), nameof(ContainersController.PutOverride))]
    [TestCase(typeof(ContainersController), nameof(ContainersController.DeleteOverride))]
    public void AdminAndAuthorisedEndpoints(Type controller, string methodName)
    {
        RolesOf(controller, methodName).ShouldBe(
            AdminAndAuthorised,
            $"{controller.Name}.{methodName} is a supervisor action per spec 2.3 and must not be open " +
            "to a caller without any role.");
    }

    [TestCase(typeof(WorksController), nameof(WorksController.Post))]
    [TestCase(typeof(WorksController), nameof(WorksController.Put))]
    [TestCase(typeof(WorksController), nameof(WorksController.Delete))]
    [TestCase(typeof(WorksController), nameof(WorksController.BulkAdd))]
    [TestCase(typeof(WorksController), nameof(WorksController.BulkDelete))]
    [TestCase(typeof(WorksController), nameof(WorksController.Confirm))]
    [TestCase(typeof(WorksController), nameof(WorksController.Unconfirm))]
    [TestCase(typeof(BreaksController), nameof(BreaksController.Post))]
    [TestCase(typeof(BreaksController), nameof(BreaksController.Put))]
    [TestCase(typeof(BreaksController), nameof(BreaksController.Delete))]
    [TestCase(typeof(BreakPlaceholdersController), nameof(BreakPlaceholdersController.Post))]
    [TestCase(typeof(BreakPlaceholdersController), nameof(BreakPlaceholdersController.Put))]
    [TestCase(typeof(BreakPlaceholdersController), nameof(BreakPlaceholdersController.Delete))]
    [TestCase(typeof(AnnotationsController), nameof(AnnotationsController.Post))]
    [TestCase(typeof(AnnotationsController), nameof(AnnotationsController.Put))]
    [TestCase(typeof(AnnotationsController), nameof(AnnotationsController.Delete))]
    [TestCase(typeof(WorkChangesController), nameof(WorkChangesController.PostChange))]
    [TestCase(typeof(WorkChangesController), nameof(WorkChangesController.PutChange))]
    [TestCase(typeof(WorkChangesController), nameof(WorkChangesController.DeleteChange))]
    [TestCase(typeof(AnalyseScenariosController), nameof(AnalyseScenariosController.Create))]
    [TestCase(typeof(AnalyseScenariosController), nameof(AnalyseScenariosController.Delete))]
    [TestCase(typeof(ContainerLocksController), nameof(ContainerLocksController.Acquire))]
    [TestCase(typeof(ContainerLocksController), nameof(ContainerLocksController.Release))]
    public void EndpointsOpenToEveryAuthenticatedCaller(Type controller, string methodName)
    {
        var method = MethodOf(controller, methodName);

        foreach (var attribute in method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
        {
            attribute.Roles.ShouldBeNullOrEmpty(
                $"{controller.Name}.{methodName} must stay open to a caller without any role (Planer): " +
                "work, employee absences and client notes are the Planer's own write surface.");
        }
    }

    [Test]
    public void AbsencesController_TheAbsenceTypes_StayWithAdminAndAuthorised()
    {
        RolesOf(typeof(AbsencesController), "Post").ShouldBe(
            AdminAndAuthorised,
            "AbsencesController manages the absence TYPES, which are a settings concern — not the " +
            "employee absences a Planer records (those are BreakPlaceholders and Breaks).");
    }

    private static string? RolesOf(Type controller, string methodName)
    {
        var attributes = MethodOf(controller, methodName)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Where(a => !string.IsNullOrEmpty(a.Roles))
            .ToList();

        attributes.ShouldHaveSingleItem(
            $"{controller.Name}.{methodName} is expected to carry exactly one role-bearing [Authorize].");

        return attributes[0].Roles;
    }

    private static MethodInfo MethodOf(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull($"{controller.Name}.{methodName} was not found — the test is stale.");
        return method!;
    }
}
