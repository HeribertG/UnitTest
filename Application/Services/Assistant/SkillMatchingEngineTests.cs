// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillMatchingEngine.TopKeywordMatchedSkillNames — the deterministic keyword
/// guarantee feeding both skill-selection pipelines. Verifies keyword and synonym matching across
/// languages, the minimum-length noise guard, the ranking (distinct matched terms, then longest
/// match, then read-only before mutating, then name) and the guarantee cap.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillMatchingEngineTests
{
    private const string QueryCategory = "query";
    private const string CrudCategory = "crud";

    private static AgentSkill Skill(
        string name,
        string[]? keywords = null,
        Dictionary<string, List<string>>? synonyms = null,
        string category = CrudCategory)
    {
        return new AgentSkill
        {
            Name = name,
            Category = category,
            TriggerKeywords = keywords == null ? "[]" : System.Text.Json.JsonSerializer.Serialize(keywords),
            Synonyms = synonyms
        };
    }

    [Test]
    public void TopKeywordMatchedSkillNames_MatchesTriggerKeyword_CaseInsensitive()
    {
        var skills = new[] { Skill("create_employee", ["mitarbeiter erstellen"]) };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "Bitte MITARBEITER ERSTELLEN für Team Ost");

        result.ShouldBe(["create_employee"]);
    }

    [Test]
    public void TopKeywordMatchedSkillNames_MatchesSynonym_InAnyLanguage()
    {
        var skills = new[]
        {
            Skill("show_schedule", synonyms: new Dictionary<string, List<string>>
            {
                ["fr"] = ["horaire de travail"],
                ["de"] = ["dienstplan"]
            })
        };

        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "montre-moi l'horaire de travail")
            .ShouldBe(["show_schedule"]);
        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "zeig mir den Dienstplan")
            .ShouldBe(["show_schedule"]);
    }

    [Test]
    public void TopKeywordMatchedSkillNames_IgnoresKeywordsShorterThanFourChars()
    {
        var skills = new[] { Skill("noisy_skill", ["ein", "ab"]) };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "ein Termin ab morgen");

        result.ShouldBeEmpty();
    }

    [Test]
    public void TopKeywordMatchedSkillNames_RanksLongestMatchFirst_AndAppliesCap()
    {
        var skills = new[]
        {
            Skill("skill_a", ["plan"]),
            Skill("skill_b", ["planung"]),
            Skill("skill_c", ["planungsansicht"]),
            Skill("skill_d", ["plan"]),
            Skill("skill_e", ["plan"]),
            Skill("skill_f", ["plan"]),
            Skill("skill_g", ["plan"])
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "öffne die planungsansicht", cap: 3);

        result.Count.ShouldBe(3);
        result[0].ShouldBe("skill_c");
        result[1].ShouldBe("skill_b");
    }

    [Test]
    public void TopKeywordMatchedSkillNames_EmptyMessage_ReturnsEmpty()
    {
        var skills = new[] { Skill("any_skill", ["irgendwas"]) };

        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "  ").ShouldBeEmpty();
    }

    [Test]
    public void TopKeywordMatchedSkillNames_NoMatch_ReturnsEmpty()
    {
        var skills = new[] { Skill("create_employee", ["mitarbeiter erstellen"]) };

        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "wie ist das Wetter heute?").ShouldBeEmpty();
    }

    [Test]
    public void TopKeywordMatchedSkillNames_EntitySignalTie_ReadOnlySkillsAreNotDisplacedByMutatingOnes()
    {
        string[] sharedKeyword = ["mitarbeiter"];
        var skills = new[]
        {
            Skill("add_client_to_group", sharedKeyword, category: CrudCategory),
            Skill("assign_contract_to_client", sharedKeyword, category: CrudCategory),
            Skill("create_employee", sharedKeyword, category: CrudCategory),
            Skill("delete_client", sharedKeyword, category: CrudCategory),
            Skill("email_schedule_to_client", sharedKeyword, category: CrudCategory),
            Skill("get_client_details", sharedKeyword, category: QueryCategory),
            Skill("search_employees", sharedKeyword, category: QueryCategory),
            Skill("update_client", sharedKeyword, category: CrudCategory)
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "zeige mir die mitarbeiter im team ost");

        result.Count.ShouldBe(SkillMatchingEngine.GuaranteedMatchCap);
        result[0].ShouldBe("get_client_details");
        result[1].ShouldBe("search_employees");
    }

    [Test]
    public void TopKeywordMatchedSkillNames_MoreDistinctMatchedKeywords_BeatSingleLongerMatch()
    {
        var skills = new[]
        {
            Skill("skill_one_long_hit", ["mitarbeiterverzeichnis"]),
            Skill("skill_two_hits", ["suche", "mitarbeiter"])
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "suche im mitarbeiterverzeichnis");

        result.ShouldBe(["skill_two_hits", "skill_one_long_hit"]);
    }

    [Test]
    public void TopKeywordMatchedSkillNames_EqualMatches_PrefersReadOnlyOverMutating()
    {
        var skills = new[]
        {
            Skill("aaa_mutating_skill", ["dienstplan"], category: CrudCategory),
            Skill("zzz_readonly_skill", ["dienstplan"], category: QueryCategory)
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "zeig mir den dienstplan");

        result.ShouldBe(["zzz_readonly_skill", "aaa_mutating_skill"]);
    }

    [Test]
    public void TopKeywordMatchedSkillNames_EqualMatchesAndRisk_FallsBackToNameOrder()
    {
        var skills = new[]
        {
            Skill("skill_beta", ["dienstplan"], category: QueryCategory),
            Skill("skill_alpha", ["dienstplan"], category: QueryCategory)
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "zeig mir den dienstplan");

        result.ShouldBe(["skill_alpha", "skill_beta"]);
    }

    private static AgentSkill AnchorSkill(string name, params string[] anchors) =>
        new()
        {
            Name = name,
            Category = CrudCategory,
            TriggerKeywords = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string[]> { [SkillPhraseLanguages.Multiple] = anchors })
        };

    [Test]
    public void ShortAnchor_InSynonymsUnderMultipleTag_MatchesAsWholeWord()
    {
        var skills = new[]
        {
            Skill("configure_smtp", synonyms: new Dictionary<string, List<string>>
            {
                [SkillPhraseLanguages.Multiple] = ["api"]
            })
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "wo trage ich den API-Key ein");

        result.ShouldBe(["configure_smtp"]);
    }

    [Test]
    public void ShortSynonym_UnderRealLanguage_StaysSilenced()
    {
        var skills = new[]
        {
            Skill("configure_smtp", synonyms: new Dictionary<string, List<string>>
            {
                ["de"] = ["api"]
            })
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "wo trage ich den API-Key ein");

        result.ShouldBeEmpty();
    }

    [Test]
    public void ShortAnchor_MatchesAsWholeWord()
    {
        var skills = new[] { AnchorSkill("configure_smtp", "api") };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "wo trage ich den API-Key ein");

        result.ShouldBe(["configure_smtp"]);
    }

    [Test]
    public void ShortAnchor_DoesNotMatchInsideAnotherWord()
    {
        var skills = new[] { AnchorSkill("configure_smtp", "api") };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "das ging sehr rapide");

        result.ShouldBeEmpty();
    }

    [Test]
    public void ShortAnchor_MatchesWhenFollowedByNonLatinScript()
    {
        var skills = new[] { AnchorSkill("configure_smtp", "api") };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "apiキーはどこで設定しますか");

        result.ShouldBe(["configure_smtp"]);
    }

    [Test]
    public void ShortAnchor_DoesNotMatchAtStartOfLongerLatinWord()
    {
        var skills = new[] { AnchorSkill("get_identity", "id") };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "das war eine gute Idee");

        result.ShouldBeEmpty();
    }

    [Test]
    public void ShortPhrase_WithoutAnchorTag_StaysSilenced()
    {
        var skills = new[]
        {
            Skill("noisy_skill", null, new Dictionary<string, List<string>> { ["de"] = ["ein", "ab"] })
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "ein Termin ab morgen");

        result.ShouldBeEmpty();
    }

    [Test]
    public void ShortAnchor_BelowTwoCharacters_StaysSilenced()
    {
        var skills = new[] { AnchorSkill("noisy_skill", "a") };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "a b c");

        result.ShouldBeEmpty();
    }
}
