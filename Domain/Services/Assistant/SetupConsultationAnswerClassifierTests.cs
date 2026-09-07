// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SetupConsultationAnswerClassifier — verifies free-text answers to the two setup
/// questions are mapped deterministically, that a domain noun beats a stray affirmation token
/// ("Bitte intern" is not a yes), that a leading negation wins, and that anything unclear degrades
/// to Unknown rather than guessing.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SetupConsultationAnswerClassifierTests
{
    [TestCase("yes", SetupAttributionAnswer.Customer)]
    [TestCase("no", SetupAttributionAnswer.None)]
    [TestCase("unknown", SetupAttributionAnswer.Unknown)]
    public void ClassifyAttribution_ChipValues_AreTakenVerbatim(string message, SetupAttributionAnswer expected)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(expected);
    }

    [TestCase("Ja, wir haben Kunden die das bestellen")]
    [TestCase("Die Stunden gehen an den Auftraggeber")]
    [TestCase("Wird dem Kunden verrechnet")]
    [TestCase("Yes, billed to a customer")]
    [TestCase("Pour un client")]
    public void ClassifyAttribution_CustomerAnswers_ReturnCustomer(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(SetupAttributionAnswer.Customer);
    }

    [TestCase("Wir sind ein Spital, das ist alles intern")]
    [TestCase("Bitte intern")]
    [TestCase("Eigenbetrieb, Station und Küche")]
    [TestCase("Our own workshop")]
    public void ClassifyAttribution_ClientlessAnswers_ReturnNone(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(SetupAttributionAnswer.None);
    }

    [TestCase("Nein")]
    [TestCase("Nein, niemand bestellt bei uns")]
    public void ClassifyAttribution_LeadingNegation_ReturnsNone(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(SetupAttributionAnswer.None);
    }

    [TestCase("Wir haben keine Kunden")]
    [TestCase("Es gibt keinen Auftraggeber")]
    [TestCase("Nein, keine Kunden")]
    [TestCase("Kein Kunde bestellt bei uns")]
    public void ClassifyAttribution_NegatedDomainNoun_ReturnsNone(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(SetupAttributionAnswer.None);
    }

    [TestCase("Ja")]
    [TestCase("Genau")]
    [TestCase("Stimmt")]
    public void ClassifyAttribution_BareAffirmation_ReturnsCustomer(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(SetupAttributionAnswer.Customer);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Weiss ich nicht so genau")]
    [TestCase("Kommt drauf an")]
    [TestCase("Keine Ahnung")]
    public void ClassifyAttribution_UnclearAnswers_ReturnUnknown(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyAttribution(message).ShouldBe(SetupAttributionAnswer.Unknown);
    }

    [TestCase("Ja, aus dem SAP")]
    [TestCase("Wir haben ein ERP")]
    [TestCase("Kommt per XML aus dem Fremdsystem")]
    [TestCase("From our external system")]
    public void ClassifyOrderSource_ExternalAnswers_ReturnExternal(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyOrderSource(message).ShouldBe(SetupOrderSourceAnswer.External);
    }

    [TestCase("Nein, wir tippen das manuell ein")]
    [TestCase("Alles von Hand")]
    [TestCase("We enter it manually")]
    public void ClassifyOrderSource_ManualAnswers_ReturnManual(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyOrderSource(message).ShouldBe(SetupOrderSourceAnswer.Manual);
    }

    [TestCase("Nein")]
    public void ClassifyOrderSource_LeadingNegation_ReturnsManual(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyOrderSource(message).ShouldBe(SetupOrderSourceAnswer.Manual);
    }

    [TestCase("Wir haben kein ERP")]
    [TestCase("Nein, keine Schnittstelle")]
    public void ClassifyOrderSource_NegatedDomainNoun_ReturnsManual(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyOrderSource(message).ShouldBe(SetupOrderSourceAnswer.Manual);
    }

    [TestCase("")]
    [TestCase("Keine Ahnung")]
    public void ClassifyOrderSource_UnclearAnswers_ReturnUnknown(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyOrderSource(message).ShouldBe(SetupOrderSourceAnswer.Unknown);
    }

    [TestCase("create", SetupNextStepChoice.Create)]
    [TestCase("show", SetupNextStepChoice.Show)]
    [TestCase("none", SetupNextStepChoice.None)]
    public void ClassifyNextStep_ChipValues_AreTakenVerbatim(string message, SetupNextStepChoice expected)
    {
        SetupConsultationAnswerClassifier.ClassifyNextStep(message).ShouldBe(expected);
    }

    [TestCase("Leg es bitte zusammen mit mir an")]
    [TestCase("Create it with me")]
    public void ClassifyNextStep_CreateAnswers_ReturnCreate(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyNextStep(message).ShouldBe(SetupNextStepChoice.Create);
    }

    [TestCase("Zeig mir nur wo das geht")]
    [TestCase("Just show me where")]
    public void ClassifyNextStep_ShowAnswers_ReturnShow(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyNextStep(message).ShouldBe(SetupNextStepChoice.Show);
    }

    [TestCase("Nein, lass mal")]
    [TestCase("Nicht jetzt")]
    public void ClassifyNextStep_Decline_ReturnsNone(string message)
    {
        SetupConsultationAnswerClassifier.ClassifyNextStep(message).ShouldBe(SetupNextStepChoice.None);
    }
}
