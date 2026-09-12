// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class CompetingSkillIntentDetectorTests
{
    private const string CompanyRuleSkill = "start_company_rule";
    private const string CreateShiftStepSkill = "create_shift";
    private const string BugMessage =
        "Klacksy, wir haben eine neue Firmenregel: maximal 3 Nachtschichten pro Woche, hart blockieren.";

    private static readonly RecipeTrigger CreateShiftOrderLikeTrigger = new()
    {
        AllOf =
        [
            new RecipeCondition { AnyWordStart = ["erstell", "neu"] },
            new RecipeCondition { AnySubstring = ["schicht", "dienst"] }
        ],
        NoneOf = []
    };

    private static AgentSkill Skill(string name, params string[] keywords) => new()
    {
        Name = name,
        TriggerKeywords = System.Text.Json.JsonSerializer.Serialize(keywords)
    };

    [Test]
    public void ForeignMultiwordPhraseInMessage_IsReportedAsCompeting()
    {
        var skills = new List<AgentSkill> { Skill(CompanyRuleSkill, "firmenregel", "neue firmenregel") };

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            skills, BugMessage, "de", CreateShiftOrderLikeTrigger, null, []);

        competing.ShouldBe([CompanyRuleSkill]);
    }

    [Test]
    public void PhraseTheRecipeTriggerOwns_IsNotCompeting()
    {
        // "neue schicht" alone fully matches the trigger (verb "neu" + noun "schicht"): the recipe
        // legitimately owns that phrase, so the skill carrying it must not force a confirmation.
        var skills = new List<AgentSkill> { Skill("place_work", "neue schicht") };

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            skills, "Bitte eine neue Schicht anlegen", "de", CreateShiftOrderLikeTrigger, null, []);

        competing.ShouldBeEmpty();
    }

    [Test]
    public void RecipeStepSkill_IsExemptEvenWithForeignPhrase()
    {
        var skills = new List<AgentSkill> { Skill(CreateShiftStepSkill, "neue firmenregel") };

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            skills, BugMessage, "de", CreateShiftOrderLikeTrigger, null, [CreateShiftStepSkill]);

        competing.ShouldBeEmpty();
    }

    [Test]
    public void SingleWordKeywords_NeverCompete()
    {
        // Single words over-fire on incidental vocabulary; only a verbatim multiword phrase is a
        // strong enough signal to gate a deterministic recipe start behind a confirmation.
        var skills = new List<AgentSkill> { Skill(CompanyRuleSkill, "firmenregel", "nachtschichten") };

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            skills, BugMessage, "de", CreateShiftOrderLikeTrigger, null, []);

        competing.ShouldBeEmpty();
    }

    [Test]
    public void MultiwordSynonymOfDetectedLanguage_Competes()
    {
        var skill = Skill(CompanyRuleSkill);
        skill.Synonyms = new Dictionary<string, List<string>>
        {
            ["pl"] = ["nowa reguła firmowa"]
        };

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            [skill], "Mamy nowa reguła firmowa: maksymalnie trzy zmiany nocne.", "pl",
            CreateShiftOrderLikeTrigger, null, []);

        competing.ShouldBe([CompanyRuleSkill]);
    }

    [Test]
    public void MultiwordSynonymOfAnotherLanguage_DoesNotCompete()
    {
        var skill = Skill(CompanyRuleSkill);
        skill.Synonyms = new Dictionary<string, List<string>>
        {
            ["pl"] = ["nowa reguła firmowa"]
        };

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            [skill], "Mamy nowa reguła firmowa: maksymalnie trzy zmiany nocne.", "de",
            CreateShiftOrderLikeTrigger, null, []);

        competing.ShouldBeEmpty();
    }

    [Test]
    public void BlankMessage_ReturnsEmpty()
    {
        var skills = new List<AgentSkill> { Skill(CompanyRuleSkill, "neue firmenregel") };

        CompetingSkillIntentDetector.FindCompetingSkillNames(
            skills, "   ", "de", CreateShiftOrderLikeTrigger, null, []).ShouldBeEmpty();
    }

    [Test]
    public async Task FindCompetingSkillNamesAsync_LoadsEnabledSkillsFromCache()
    {
        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentSkill>)new List<AgentSkill> { Skill(CompanyRuleSkill, "neue firmenregel") });
        var detector = new CompetingSkillIntentDetector(skillCache);

        var competing = await detector.FindCompetingSkillNamesAsync(
            BugMessage, "de", CreateShiftOrderLikeTrigger, null, []);

        competing.ShouldBe([CompanyRuleSkill]);
    }
}
