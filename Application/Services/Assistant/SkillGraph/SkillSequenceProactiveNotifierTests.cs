// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Phase 4a (hero, live) tests: after a skill executes, an active sequential successor raises a
/// proactive suggestion event into the trigger pipeline with human-readable labels, targeted at
/// the executing user only; no successor raises nothing.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Klacks.Api.Domain.Constants;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillSequenceProactiveNotifierTests
{
    private static SkillRelation Seq(string a, string b, double confidence)
        => new() { SkillAName = a, SkillBName = b, Type = SkillRelationType.Sequential, Status = SkillRelationStatus.Active, Confidence = confidence };

    private static AgentSkill Skill(string name, string description) => new() { Name = name, Description = description };

    private static (SkillSequenceProactiveNotifier Sut, IAgentTriggerService Trigger) Build(
        List<SkillRelation> edges, List<AgentSkill> skills)
    {
        var relationRepo = Substitute.For<ISkillRelationRepository>();
        relationRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(edges);
        var skillRepo = Substitute.For<IAgentSkillRepository>();
        skillRepo.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(skills);
        var trigger = Substitute.For<IAgentTriggerService>();
        return (new SkillSequenceProactiveNotifier(relationRepo, skillRepo, trigger), trigger);
    }

    [Test]
    public async Task Notify_WithActiveSuccessor_RaisesSuggestionEventWithLabelsTargetedAtExecutingUser()
    {
        var executingUserId = Guid.NewGuid();
        var (sut, trigger) = Build(
            new List<SkillRelation> { Seq("aa", "bb", 0.85) },
            new List<AgentSkill> { Skill("aa", "Add a note"), Skill("bb", "Send an email") });
        IAgentTriggerEvent? captured = null;
        trigger.When(t => t.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<IAgentTriggerEvent>());

        await sut.NotifyAfterSkillAsync("aa", executingUserId);

        captured.ShouldNotBeNull();
        captured!.Kind.ShouldBe(AgentTriggerKinds.SkillSequenceSuggestion);
        captured.Summary.ShouldContain("Add a note");
        captured.Summary.ShouldContain("Send an email");
        captured.TargetUserId.ShouldBe(executingUserId);
    }

    [Test]
    public async Task Notify_WithVeryLongSkillDescriptions_KeepsTheSuggestionInsideTheDispatchColumnLimits()
    {
        const string suggestedAction = "Send an email";
        var (sut, trigger) = Build(
            new List<SkillRelation> { Seq("aa", "bb", 0.85) },
            new List<AgentSkill>
            {
                Skill("aa", new string('f', 995)),
                Skill("bb", suggestedAction + new string('t', 982)),
            });
        IAgentTriggerEvent? captured = null;
        trigger.When(t => t.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<IAgentTriggerEvent>());

        await sut.NotifyAfterSkillAsync("aa", Guid.NewGuid());

        captured.ShouldNotBeNull();
        captured!.Summary.Length.ShouldBeLessThanOrEqualTo(ProactiveTriggerDispatchLimits.ContentKeyMaxLength);
        captured.DedupKey.Length.ShouldBeLessThanOrEqualTo(ProactiveTriggerDispatchLimits.DedupKeyMaxLength);
        captured.Summary.ShouldContain(suggestedAction);
    }

    [Test]
    public async Task Notify_WithoutSuccessor_RaisesNothing()
    {
        var (sut, trigger) = Build(new List<SkillRelation>(), new List<AgentSkill> { Skill("aa", "Add a note") });

        await sut.NotifyAfterSkillAsync("aa", Guid.NewGuid());

        await trigger.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }
}
