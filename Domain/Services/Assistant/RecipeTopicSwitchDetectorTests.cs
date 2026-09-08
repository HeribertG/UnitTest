// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RecipeTopicSwitchDetector — the guard that decides whether a user's reply during a
/// recipe's ask step is an independent question (must be answered with the full toolset, not raw-filled
/// into the pending slot) rather than a genuine slot answer. Live incident 2026-09-08: a user's ERP-import
/// question mid-flow was raw-filled into the slot of the pending setup-consultation ask step, and the
/// tool-less ask-step call (which has no skills) then falsely told the user no ERP import exists.
/// Positive cases cover the exact live repro plus multi-language independent questions and a bare
/// follow-up question ("Wie meinst du das?"). Negative cases cover the recipe's own chip-style slot values
/// (yes/no/unknown/create/show/none — several of which collide word-for-word with English question leads
/// read as bare imperatives) and realistic free-text slot answers from this and other recipes (opinions,
/// names, group names) that must never be mistaken for a topic switch.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTopicSwitchDetectorTests
{
    [TestCase(
        "Ich habe eine xml Datei mit allen Bestellungen drin. Wie kann ich es einbinden?",
        TestName = "IsTopicSwitch_True_LiveReproMessage")]
    [TestCase("Wie meinst du das?", TestName = "IsTopicSwitch_True_BareFollowUpQuestion")]
    [TestCase("Was bedeutet das genau?", TestName = "IsTopicSwitch_True_German")]
    [TestCase("How do I import an XML file with all my orders?", TestName = "IsTopicSwitch_True_English")]
    [TestCase("Comment puis-je importer un fichier XML avec toutes mes commandes ?", TestName = "IsTopicSwitch_True_French")]
    [TestCase("Come posso importare un file XML con tutti i miei ordini?", TestName = "IsTopicSwitch_True_Italian")]
    [TestCase("Übrigens, eine Frage nebenbei.\nWarum dauert der Import so lange?", TestName = "IsTopicSwitch_True_SecondLineIsTheQuestion")]
    public void IsTopicSwitch_True_For_Independent_Questions(string message)
    {
        RecipeTopicSwitchDetector.IsTopicSwitch(message).ShouldBeTrue(message);
    }

    [TestCase("yes")]
    [TestCase("no")]
    [TestCase("unknown")]
    [TestCase("create")]
    [TestCase("show")]
    [TestCase("none")]
    [TestCase("Ja")]
    [TestCase("Nein")]
    public void IsTopicSwitch_False_For_Single_Word_Chip_Values(string message)
    {
        RecipeTopicSwitchDetector.IsTopicSwitch(message).ShouldBeFalse(message);
    }

    [TestCase("Wir sind ein Spital, also eher ohne Kunden")]
    [TestCase("Ja, aus dem SAP")]
    [TestCase("Keine Ahnung")]
    [TestCase("Max Müller")]
    [TestCase("Gruppe Nord")]
    [TestCase("Montag")]
    [TestCase("14:00")]
    [TestCase("Frühdienst")]
    [TestCase("Wir sind ein mittelständisches Unternehmen mit rund 50 Mitarbeitenden")]
    public void IsTopicSwitch_False_For_Realistic_Slot_Answers(string message)
    {
        RecipeTopicSwitchDetector.IsTopicSwitch(message).ShouldBeFalse(message);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsTopicSwitch_False_For_Empty_Or_Whitespace(string? message)
    {
        RecipeTopicSwitchDetector.IsTopicSwitch(message).ShouldBeFalse(message ?? "<null>");
    }
}
