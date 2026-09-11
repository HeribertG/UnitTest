// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the automatic description sharpening. The gate is the whole feature: a description is the
/// largest part of a skill's index text, so a change that helps one skill can push a neighbouring skill
/// out of reach for a query nobody proposed anything about. The only honest check is to apply it, replay
/// the goldset and roll it back when something turned red - and the rollback is what these tests pin.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillDescriptionSharpenerTests
{
    private const string Before = "Lists everything about clients.";
    private const string After = "Lists the contract data of one client.";

    private ISkillDescriptionOptimizer _optimizer = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private ISkillRoutingOracle _oracle = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private ILogger<SkillDescriptionSharpener> _logger = null!;
    private SkillDescriptionSharpener _sharpener = null!;
    private AgentSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _optimizer = Substitute.For<ISkillDescriptionOptimizer>();
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _skills = Substitute.For<IAgentSkillRepository>();
        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        _oracle = Substitute.For<ISkillRoutingOracle>();
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _refresher = Substitute.For<ISkillCatalogRefresher>();
        _logger = Substitute.For<ILogger<SkillDescriptionSharpener>>();

        _skill = new AgentSkill { Id = Guid.NewGuid(), Name = "list_clients", Description = Before, Version = 3 };
        _skills.GetByIdAsync(_skill.Id, Arg.Any<CancellationToken>()).Returns(_skill);

        _sharpener = new SkillDescriptionSharpener(
            _optimizer, _proposals, _skills, _goldenCases, _oracle, _refresher, _logger);
    }

    private ProposedSkillChange GivenPending(string valueBefore = Before)
    {
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = _skill.Id,
            SkillName = _skill.Name,
            Field = ProposedChangeFields.Description,
            ValueBefore = valueBefore,
            ValueAfter = After,
            Status = ProposedChangeStatuses.Pending
        };

        _proposals.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([proposal]);
        return proposal;
    }

    [Test]
    public async Task NewProposalsAreAskedForBeforeTheOpenOnesAreDecided()
    {
        _proposals.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await _sharpener.RunAsync();

        await _optimizer.Received(1).GenerateProposalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutOpenProposals_NothingIsReplayed()
    {
        _proposals.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var (applied, blocked) = await _sharpener.RunAsync();

        applied.ShouldBe(0);
        blocked.ShouldBe(0);
        await _oracle.DidNotReceive().FindFailingGoldenCasesAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AChangeThatBreaksNothing_IsAppliedAutomatically()
    {
        var proposal = GivenPending();

        var (applied, blocked) = await _sharpener.RunAsync();

        applied.ShouldBe(1);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(After);
        proposal.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
        proposal.ReviewedBy.ShouldBe(SkillLearningDefaults.AutomaticReviewer);
        proposal.ReviewedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task AChangeThatBreaksAGoldenCase_IsRolledBackAndBlocked()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([], ["'kunde anlegen' no longer reaches 'create_client'"]);

        var (applied, blocked) = await _sharpener.RunAsync();

        applied.ShouldBe(0);
        blocked.ShouldBe(1);
        _skill.Description.ShouldBe(Before);
        proposal.Status.ShouldBe(ProposedChangeStatuses.BlockedRegression);
        proposal.Justification.ShouldContain("create_client");
        await _refresher.Received(2).RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A case that was red before anything was touched is not this proposal's doing.
    [Test]
    public async Task AGoldenCaseThatWasAlreadyFailing_DoesNotBlockTheChange()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(["already broken"]);

        var (applied, _) = await _sharpener.RunAsync();

        applied.ShouldBe(1);
        proposal.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
    }

    // Applying a proposal written against a description that has since changed would silently discard
    // whatever changed it.
    [Test]
    public async Task AProposalWrittenAgainstAnOlderDescription_IsLeftAlone()
    {
        var proposal = GivenPending("Something else entirely.");

        var (applied, blocked) = await _sharpener.RunAsync();

        applied.ShouldBe(0);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(Before);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    // The gate is measured on a live description, so a probe that throws leaves a description in the
    // catalogue that nothing ever judged. It has to come back out again, and the proposal has to stay
    // open rather than be recorded as a verdict nobody reached.
    [Test]
    public async Task AGateThatThrows_PutsTheDescriptionBackAndLeavesTheProposalPending()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(_ => [], _ => throw new InvalidOperationException("the index is unreachable"));

        var (applied, blocked) = await _sharpener.RunAsync();

        applied.ShouldBe(0);
        blocked.ShouldBe(0);
        _skill.Description.ShouldBe(Before);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        proposal.ReviewedAt.ShouldBeNull();
        await _refresher.Received(2).RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // One unreachable probe must not cost the proposals queued behind it.
    [Test]
    public async Task AGateThatThrowsOnOneProposal_DoesNotAbortTheRest()
    {
        var first = GivenPending();
        var second = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = _skill.Id,
            SkillName = _skill.Name,
            Field = ProposedChangeFields.Description,
            ValueBefore = Before,
            ValueAfter = After,
            Status = ProposedChangeStatuses.Pending
        };
        _proposals.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([first, second]);

        var throws = true;
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns(_ => [], _ =>
            {
                if (throws)
                {
                    throws = false;
                    throw new InvalidOperationException("the index is unreachable");
                }

                return [];
            });

        var (applied, _) = await _sharpener.RunAsync();

        applied.ShouldBe(1);
        first.Status.ShouldBe(ProposedChangeStatuses.Pending);
        second.Status.ShouldBe(ProposedChangeStatuses.AppliedAuto);
    }

    [Test]
    public async Task AnAppliedChangeReachesRetrievalThroughACatalogueRefresh()
    {
        GivenPending();

        await _sharpener.RunAsync();

        await _refresher.Received(1).RefreshAndWaitForIndexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A restore that fails must not pass silently: the never-judged description would stay live and
    // look like a reviewed one. The run therefore logs an error and fails loudly instead.
    [Test]
    public async Task ARestoreThatThrows_FailsTheRunLoudly()
    {
        var proposal = GivenPending();
        _oracle.FindFailingGoldenCasesAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>())
            .Returns([], ["'kunde anlegen' no longer reaches 'create_client'"]);

        var updateCalls = 0;
        _skills.When(x => x.UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (++updateCalls == 2)
                {
                    throw new InvalidOperationException("the database went away");
                }
            });

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => _sharpener.RunAsync());

        exception!.Message.ShouldContain("database went away");
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
