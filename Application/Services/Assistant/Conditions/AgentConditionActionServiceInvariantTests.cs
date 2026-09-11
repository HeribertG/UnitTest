// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Property tests for two of the seven autonomy invariants from the Testspezifikation
/// (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md, §5): a deterministically seeded
/// world - 1-3 groups, 0-5 planners, 0-50 conditions, randomised per-group governance and a randomised
/// global autonomy level - is run once through the real AgentConditionActionService and
/// AgentConditionLedgerService pair over FakeAgentConditionRepository, and every row the run left
/// Executed is checked against the ledger's own event history rather than against RunAsync's return
/// value, so a violation is caught even if the tally the tick reports happens to look right.
///
/// Each seeded condition starts Reported with a synthetic prior "Reported" audit event already in its
/// history, standing in for the real Detected-to-Reported transition a full tick of the ledger would
/// have written before this test ever runs: FakeAgentConditionRepository.Seed() places a row directly
/// into a status without writing that history, and invariant #7 ("no Executed without a preceding
/// Reported") is meaningless without it. EventsFor() hands events back in append order, which this test
/// relies on for ordering instead of AtUtc, because the claim and the execution both stamp the one frozen
/// "now" the SettableTimeProvider holds for the whole tick.
///
/// The world is otherwise kept clean on purpose: the skill executor always succeeds, the identity
/// provider always resolves, and no payload is ever rewritten mid-claim, so RecordFailureAsync and the
/// stale-reclaim path never fire. That is deliberate - what is varied here is exactly the §5 generator's
/// list (level, per-kind governance, kill switch, budget), not the unrelated failure paths several other
/// fixtures in this directory already pin.
///
/// The other five §5 invariants are out of scope per the coordinator's instructions: #1/#6 are covered by
/// ProactiveGovernanceResolverTests' 48-cell resolver matrix, #3 by Az3
/// (Klacks.IntegrationTest/Assistant/Proactive/EmptyContainerActionBudgetScenarioTests.cs), #5 by Az2
/// (EmptyContainerActionScenarioTests.cs), and #4 is deliberately left unbuilt.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceInvariantTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string SkillName = "test_remediation_skill";
    private const string RequiredArgument = "containerId";
    private const int RunsPerInvariant = 200;
    private const int Invariant2SeedBase = 202608300;
    private const int Invariant7SeedBase = 202608700;

    private static readonly DateTime NowUtc = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Severities =
        [AgentTriggerSeverity.High, AgentTriggerSeverity.Medium, AgentTriggerSeverity.Low];
    private static readonly int[] WindowMinuteChoices = [30, 60, 120];

    [Test]
    public async Task Invariant2_EveryExecutedRow_HasExactlyOneClaimEventAndExactlyOneReport()
    {
        var failures = new List<string>();
        var totalExecuted = 0;
        var totalConsidered = 0;
        var totalSeeded = 0;

        for (var iteration = 0; iteration < RunsPerInvariant; iteration++)
        {
            var seed = Invariant2SeedBase + iteration;
            var (repository, reporter, result, seededCount) = await RunWorldAsync(seed);
            totalConsidered += result.Considered;
            totalSeeded += seededCount;

            foreach (var condition in repository.Conditions.Where(c => c.Status == AgentConditionStatus.Executed))
            {
                totalExecuted++;

                var claimEvents = repository.EventsFor(condition.Id).Count(IsClaimEvent);
                if (claimEvents != 1)
                {
                    failures.Add(
                        $"Seed={seed} Condition={condition.Id}: expected exactly 1 claim event, found {claimEvents}.");
                }

                var reportCount = reporter.Reports.Count(report =>
                    report.Message.Contains(condition.Id.ToString(), StringComparison.Ordinal));
                if (reportCount != 1)
                {
                    failures.Add(
                        $"Seed={seed} Condition={condition.Id}: expected exactly 1 report naming this condition, "
                        + $"found {reportCount}.");
                }
            }
        }

        AssertExercisedTheDispatcher(totalExecuted, totalConsidered, totalSeeded);

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, failures));
        }
    }

    [Test]
    public async Task Invariant7_NoExecutedEventWithoutAPrecedingReportedEvent()
    {
        var failures = new List<string>();
        var totalExecuted = 0;
        var totalConsidered = 0;
        var totalSeeded = 0;

        for (var iteration = 0; iteration < RunsPerInvariant; iteration++)
        {
            var seed = Invariant7SeedBase + iteration;
            var (repository, _, result, seededCount) = await RunWorldAsync(seed);
            totalConsidered += result.Considered;
            totalSeeded += seededCount;

            foreach (var condition in repository.Conditions.Where(c => c.Status == AgentConditionStatus.Executed))
            {
                totalExecuted++;

                var events = repository.EventsFor(condition.Id).ToList();
                var reportedIndex = events.FindIndex(IsReportedTransition);
                var preparedIndex = events.FindIndex(IsClaimEvent);
                var executedIndex = events.FindIndex(IsExecutedTransition);

                if (executedIndex < 0)
                {
                    failures.Add(
                        $"Seed={seed} Condition={condition.Id}: status is Executed but no Executed event exists "
                        + "in its history.");
                    continue;
                }

                if (reportedIndex < 0 || reportedIndex >= executedIndex)
                {
                    failures.Add(
                        $"Seed={seed} Condition={condition.Id}: no Reported event precedes the Executed event "
                        + $"(ReportedIndex={reportedIndex}, ExecutedIndex={executedIndex}).");
                }

                if (preparedIndex < 0 || preparedIndex <= reportedIndex || preparedIndex >= executedIndex)
                {
                    failures.Add(
                        $"Seed={seed} Condition={condition.Id}: the claim (Prepared) event does not sit strictly "
                        + $"between Reported and Executed (ReportedIndex={reportedIndex}, PreparedIndex={preparedIndex}, "
                        + $"ExecutedIndex={executedIndex}) - AgentConditionStateMachine.AllowedTransitions was not "
                        + "honoured in order.");
                }
            }
        }

        AssertExercisedTheDispatcher(totalExecuted, totalConsidered, totalSeeded);

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, failures));
        }
    }

    /// <summary>
    /// Guards against the exact way this generator could go silently untested: RunAsync catches every
    /// per-kind exception and just logs it (see its class summary), so a systemic setup bug - e.g. a
    /// group whose governance was never stubbed - would not fail loudly, it would just make every run
    /// produce an empty tick. Considered is incremented for every candidate BEFORE governance is even
    /// resolved, so it stays a large share of what was seeded even when the absolute tick cap
    /// legitimately truncates a busy kind's walk early; a total that collapsed towards zero would not.
    /// </summary>
    private static void AssertExercisedTheDispatcher(int totalExecuted, int totalConsidered, int totalSeeded)
    {
        Assert.That(
            totalExecuted,
            Is.GreaterThan(0),
            "The generator produced zero Executed rows across 200 runs - invariant #2/#7 were never actually exercised.");
        Assert.That(
            totalConsidered,
            Is.GreaterThanOrEqualTo(totalSeeded / 2),
            $"Only {totalConsidered} of {totalSeeded} seeded conditions were ever considered by RunAsync across "
            + "200 runs - a swallowed exception (e.g. an unstubbed governance scope) may be silently emptying ticks.");
    }

    private static bool IsClaimEvent(AgentConditionEvent conditionEvent) =>
        conditionEvent.EventType == AgentConditionStatus.Prepared.ToString()
        && conditionEvent.Detail != null
        && conditionEvent.Detail.StartsWith(AgentConditionActionDefaults.ActionClaimDetailPrefix, StringComparison.Ordinal);

    private static bool IsReportedTransition(AgentConditionEvent conditionEvent) =>
        conditionEvent.EventType == AgentConditionStatus.Reported.ToString();

    private static bool IsExecutedTransition(AgentConditionEvent conditionEvent) =>
        conditionEvent.EventType == AgentConditionStatus.Executed.ToString();

    /// <summary>
    /// Builds one random world per §5's generator (1-3 groups, 0-5 planners, 0-50 conditions, randomised
    /// per-group governance and one randomised global autonomy level for the whole world) and runs a
    /// single tick of AgentConditionActionService.RunAsync over it. System.Random with the given seed is
    /// the ONLY source of randomness, so a failing seed reproduces the exact same world on replay.
    /// </summary>
    private static async Task<(
        FakeAgentConditionRepository Repository,
        RecordingReporter Reporter,
        AgentConditionActionTickResult Result,
        int SeededCount)> RunWorldAsync(int seed)
    {
        var random = new Random(seed);
        var repository = new FakeAgentConditionRepository();
        var timeProvider = new SettableTimeProvider(NowUtc);
        var companyClock = new FixedCompanyClock(NowUtc);
        var ledger = new AgentConditionLedgerService(
            repository, timeProvider, NullLogger<AgentConditionLedgerService>.Instance);
        var governance = Substitute.For<IProactiveGovernanceResolver>();

        var groupCount = random.Next(1, 4);
        var plannerCount = random.Next(0, 6);
        var containerCount = random.Next(0, 51);

        var planners = Enumerable.Range(0, plannerCount).Select(_ => Guid.NewGuid()).ToList();
        var groups = Enumerable.Range(0, groupCount).Select(_ => Guid.NewGuid()).ToList();

        var globalLevel = (AutonomyLevel)random.Next(0, 4);
        var globalCap = globalLevel switch
        {
            AutonomyLevel.Propose => ProactiveMaxAction.Hint,
            AutonomyLevel.Assisted => ProactiveMaxAction.Prepare,
            AutonomyLevel.Autonomous => ProactiveMaxAction.Execute,
            AutonomyLevel.FullyAutonomous => ProactiveMaxAction.Execute,
            _ => ProactiveMaxAction.Hint
        };

        foreach (var groupId in groups)
        {
            var configuredMaxAction = (ProactiveMaxAction)random.Next(0, 3);
            var enabled = random.NextDouble() < 0.9;
            var killSwitch = random.NextDouble() < 0.2;
            var dailyBudget = random.Next(0, 9);
            var windowLimit = random.Next(0, 9);
            var windowMinutes = WindowMinuteChoices[random.Next(WindowMinuteChoices.Length)];
            var ownerUserId = planners.Count > 0 && random.NextDouble() < 0.85
                ? planners[random.Next(planners.Count)]
                : (Guid?)null;

            var levelCapped = configuredMaxAction < globalCap ? configuredMaxAction : globalCap;
            var effectiveMaxAction = killSwitch || !enabled ? ProactiveMaxAction.Hint : levelCapped;

            governance
                .ResolveAsync(Kind, groupId, Arg.Any<CancellationToken>())
                .Returns(new ProactiveGovernanceDecision(
                    TriggerKind: Kind,
                    GroupId: groupId,
                    EffectiveMaxAction: effectiveMaxAction,
                    ConfiguredMaxAction: configuredMaxAction,
                    Enabled: enabled,
                    KillSwitchActive: killSwitch,
                    ResponsibleOwnerUserId: ownerUserId,
                    DailyActionBudget: dailyBudget,
                    WindowActionLimit: windowLimit,
                    WindowMinutes: windowMinutes,
                    IsStored: true,
                    GlobalAutonomyCap: globalCap));
        }

        for (var index = 0; index < containerCount; index++)
        {
            var groupId = groups[random.Next(groups.Count)];
            var detectedAtUtc = NowUtc.AddHours(-random.Next(1, 240));
            var condition = repository.Seed(Kind, Guid.NewGuid().ToString(), AgentConditionStatus.Reported, detectedAtUtc);
            condition.Severity = Severities[random.Next(Severities.Length)];
            condition.GroupId = groupId;
            condition.EntityId = Guid.NewGuid();
            condition.PayloadJson = "{}";

            // Stands in for the real Detected -> Reported transition event a full ledger tick would have
            // written before this test's tick ever runs - see the class summary.
            await repository.InsertEventAsync(new AgentConditionEvent
            {
                Id = Guid.NewGuid(),
                ConditionId = condition.Id,
                EventType = AgentConditionStatus.Reported.ToString(),
                AtUtc = detectedAtUtc
            });
        }

        var quietWindow = Substitute.For<IQuietWindowService>();
        quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        var identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = callInfo.ArgAt<Guid?>(0) ?? Guid.NewGuid(),
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = ["some.permission"],
                    BypassAutonomyGate = true
                },
                ["some.permission"]));

        var skillExecutor = Substitute.For<ISkillExecutor>();
        skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Template created."));

        var reporter = new RecordingReporter();
        var registry = new WorldRemediationRegistry();

        var result = await new AgentConditionActionService(
            repository,
            ledger,
            governance,
            registry,
            quietWindow,
            identityProvider,
            skillExecutor,
            reporter,
            timeProvider,
            companyClock,
            NullLogger<AgentConditionActionService>.Instance)
            .RunAsync(CancellationToken.None);

        return (repository, reporter, result, containerCount);
    }

    /// <summary>Records every report call verbatim, so a test can count how many named one condition.</summary>
    private sealed class RecordingReporter : IProactiveActionReporter
    {
        public List<ReportEntry> Reports { get; } = new();

        public Task<bool> ReportAsync(Guid recipientUserId, string message, CancellationToken cancellationToken = default)
        {
            Reports.Add(new ReportEntry(recipientUserId, message));
            return Task.FromResult(true);
        }

        public sealed record ReportEntry(Guid RecipientUserId, string Message);
    }

    /// <summary>
    /// One registry entry for Kind, always binding - IsScenarioCapable defaults to false, matching the
    /// real empty_container entry, so a group whose governance lands on Prepare skips like Hint rather
    /// than staging a scenario nobody can accept back out.
    /// </summary>
    private sealed class WorldRemediationRegistry : IConditionRemediationRegistry
    {
        public IReadOnlyCollection<string> RegisteredKinds => [Kind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == Kind
                ? new ConditionRemediationEntry(SkillName, new AlwaysBindsBinder(), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == Kind ? configuredMaxAction : ProactiveMaxAction.Hint;

        private sealed class AlwaysBindsBinder : IConditionRemediationParameterBinder
        {
            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [RequiredArgument] = Guid.NewGuid().ToString()
                };
        }
    }
}
