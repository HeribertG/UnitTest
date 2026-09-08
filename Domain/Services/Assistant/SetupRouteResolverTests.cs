// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SetupRouteResolver — walks the route matrix, in particular the cautious paths: an
/// unknown attribution never offers to create anything, an ERP answer combined with clientless work
/// is reported as the contradiction it is, and a missing group blocks both routes rather than one.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SetupRouteResolverTests
{
    [Test]
    public void Resolve_ErpWithGroups_ChoosesErpImport()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: true, hasGroups: true),
            SetupAttributionAnswer.Customer,
            SetupOrderSourceAnswer.External,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.ErpImport);
        facts.MissingPrerequisites.ShouldBeEmpty();
    }

    [Test]
    public void Resolve_ErpWithoutGroups_DemandsAGroupFirst()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: true, hasGroups: false),
            SetupAttributionAnswer.Customer,
            SetupOrderSourceAnswer.External,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.ErpImportNeedsGroup);
        facts.MissingPrerequisites.ShouldContain(SetupPrerequisites.Group);
    }

    [Test]
    public void Resolve_ErpButClientless_ReportsTheContradiction()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: false, hasGroups: true),
            SetupAttributionAnswer.None,
            SetupOrderSourceAnswer.External,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.ErpImportContradictsClientless);
        facts.HandoffPhrase.ShouldBeNull();
    }

    [Test]
    public void Resolve_CustomerRouteFullyPrepared_OffersTheOrderRecipe()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: true, hasGroups: true),
            SetupAttributionAnswer.Customer,
            SetupOrderSourceAnswer.Manual,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.CustomerOrder);
        facts.HandoffPhrase.ShouldBe(SetupHandoffPhrases.CreateShiftOrder);
    }

    [Test]
    public void Resolve_CustomerRouteWithoutCustomers_DemandsACustomerAndOffersNoHandoff()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: false, hasGroups: true),
            SetupAttributionAnswer.Customer,
            SetupOrderSourceAnswer.Manual,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.CustomerOrderNeedsCustomer);
        facts.MissingPrerequisites.ShouldContain(SetupPrerequisites.Customer);
        facts.HandoffPhrase.ShouldBeNull();
    }

    [Test]
    public void Resolve_ClientlessWithGroups_RequiresAdminAndOffersNoHandoff()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: false, hasGroups: true),
            SetupAttributionAnswer.None,
            SetupOrderSourceAnswer.Manual,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.ClientlessDuty);
        facts.ShowTarget.ShouldBe("new-plannable-shift");
        facts.RequiresAdmin.ShouldBeTrue();
        facts.HandoffPhrase.ShouldBeNull();
    }

    [Test]
    public void Resolve_ClientlessAsNonAdmin_NamesTheMissingRight()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: false, hasGroups: true),
            SetupAttributionAnswer.None,
            SetupOrderSourceAnswer.Manual,
            isAdmin: false);

        facts.MissingPrerequisites.ShouldContain(SetupPrerequisites.AdminRights);
    }

    [Test]
    public void Resolve_UnknownAttribution_NeverOffersAHandoff()
    {
        var facts = SetupRouteResolver.Resolve(
            State(hasCustomers: true, hasGroups: true),
            SetupAttributionAnswer.Unknown,
            SetupOrderSourceAnswer.Unknown,
            isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.BothRoutesUnclear);
        facts.HandoffPhrase.ShouldBeNull();
    }

    [Test]
    public void Resolve_ShiftsExistButNobodyAssigned_SkipsTheMatrix()
    {
        var state = new ScheduleSetupState(
            HasOrders: true, HasShifts: true, HasWork: false, HasCustomers: true, HasGroups: true);

        var facts = SetupRouteResolver.Resolve(
            state, SetupAttributionAnswer.Customer, SetupOrderSourceAnswer.Manual, isAdmin: true);

        facts.Kind.ShouldBe(SetupRouteKind.ShiftsAwaitingAssignment);
    }

    private static ScheduleSetupState State(bool hasCustomers, bool hasGroups) =>
        new(HasOrders: false, HasShifts: false, HasWork: false, hasCustomers, hasGroups);
}
