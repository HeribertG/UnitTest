// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AgentTriggerService — verifies the severity routing matrix (high goes to the
/// live chat push, medium/low operational alerts only to the persisted inbox plus a lightweight
/// inbox-changed signal, companion events always reach the chat), that the recipient set derives
/// from the event audience so offline planners still get inbox rows, that preference, dedup and
/// rate-limit gates block before anything is persisted, that rows are persisted before the live
/// push so a failed push keeps the message reachable via the inbox, and that overlong summary
/// param values are capped so the persisted JSON never exceeds the database column limit.
/// The same cap applies to the content and dedup keys — without it an event whose summary exceeds
/// the column width aborts the insert instead of shortening the value, and the notification is lost
/// — the cut never splits a surrogate pair, and a lost row is reported as an error.
/// </summary>

using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AgentTriggerServiceTests
{
    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private IAgentTriggerPreferenceService _preferenceService = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IUserActivityTracker _activityTracker = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private IOfflineMessengerNotifier _offlineMessengerNotifier = null!;
    private IProactiveMessengerTextComposer _messengerTextComposer = null!;
    private SettableTimeProvider _timeProvider = null!;
    private RecordingLogger<AgentTriggerService> _logger = null!;
    private AgentTriggerService _sut = null!;

    private const string ComposedMessengerText = "Eine Schicht ist noch unbesetzt.";

    private static readonly DateTime FakeNow = new(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void Setup()
    {
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        _preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _activityTracker = Substitute.For<IUserActivityTracker>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _offlineMessengerNotifier = Substitute.For<IOfflineMessengerNotifier>();
        _messengerTextComposer = Substitute.For<IProactiveMessengerTextComposer>();
        _timeProvider = new SettableTimeProvider(FakeNow);
        _logger = new RecordingLogger<AgentTriggerService>();
        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _messengerTextComposer.ComposeAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>())
            .Returns(ComposedMessengerText);
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.ChannelUnavailable);
        SetPlanners("user-a", "user-b");
        _sut = new AgentTriggerService(_rateLimiter, _preferenceService, _notificationService,
            _dispatchRepository, _conditionRepository, _activityTracker, _planningAudienceResolver,
            _offlineMessengerNotifier, _messengerTextComposer, _timeProvider, _logger);
    }

    private void SetOfflineMessengerResult(OfflineMessengerDeliveryResult result) =>
        _offlineMessengerNotifier
            .TrySendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    /// <summary>
    /// The one group the shift-borne default event from MakeEvent belongs to.
    /// </summary>
    private static readonly Guid TestGroupId = Guid.NewGuid();

    /// <summary>
    /// Declares the planner audience for a dispatch test. It answers through the group-scoped resolver
    /// as well, because every shift-borne kind is group-scoped: without that, each test using MakeEvent
    /// would be asserting against an audience of nobody.
    /// </summary>
    private void SetPlanners(params string[] userIds)
    {
        var ids = (IReadOnlySet<string>)new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase);
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>()).Returns(ids);
        _planningAudienceResolver.GetPlanningUserIdsForGroupAsync(TestGroupId, Arg.Any<CancellationToken>()).Returns(ids);
    }

    private void SetAdmins(params string[] userIds) =>
        _planningAudienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase));

    private void SetGroupScopedPlanners(Guid groupId, params string[] userIds) =>
        _planningAudienceResolver.GetPlanningUserIdsForGroupAsync(groupId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase));

    private static UnstaffedShiftTriggerEvent MakeEvent(int daysUntil = 2) =>
        new(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysUntil)), daysUntil, new[] { TestGroupId });

    private sealed record PlainBroadcastEvent(string SeverityValue, string SummaryText) : IAgentTriggerEvent
    {
        public string Kind => "test_plain";
        public string Severity => SeverityValue;
        public string Summary => SummaryText;
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
    }

    private sealed record ParamsBroadcastEvent(IReadOnlyDictionary<string, string> Params) : IAgentTriggerEvent
    {
        public string Kind => "test_params";
        public string Severity => AgentTriggerSeverity.Low;
        public string Summary => "Params summary.";
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
        public IReadOnlyDictionary<string, string>? SummaryParams => Params;
    }

    private sealed record PlannerKindEvent(string Kind, string Severity) : IAgentTriggerEvent
    {
        public bool PlannersOnly => true;
        public string Summary => ProactiveMessageMarkers.I18nPrefix + "assistant.proactive.test";
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
    }

    private sealed record ActionBroadcastEvent(string? Route, IReadOnlyDictionary<string, string>? Params) : IAgentTriggerEvent
    {
        public string Kind => "test_action";
        public string Severity => AgentTriggerSeverity.Low;
        public string Summary => "Action summary.";
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
        public string? ActionRoute => Route;
        public IReadOnlyDictionary<string, string>? ActionParams => Params;
    }


    [Test]
    public async Task OnEventAsync_CompanionBroadcast_NoConnectedUsers_PersistsAndSendsNothing()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_HighSeverity_ConnectedPlanners_LivePushesAndRecordsFire()
    {
        var users = new[] { "user-a", "user-b" };
        _notificationService.GetConnectedUserIdsAsync().Returns(users);
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.Received(1).SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        _rateLimiter.Received(1).RecordFire("user-b", AgentTriggerKinds.UnstaffedShift);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_MediumSeverity_ConnectedPlanner_RecordsAndSignalsInboxWithoutChatPush()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _dispatchRepository.CountUnreadAsync("user-a", Arg.Any<CancellationToken>()).Returns(3);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 5));

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        await _notificationService.Received(1).SendProactiveInboxChangedAsync("user-a", 3);
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_OfflinePlanners_RecordsInboxRowsWithoutAnyPush()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        _rateLimiter.Received(1).RecordFire("user-b", AgentTriggerKinds.UnstaffedShift);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_LoudEvent_OfflinePlannersWithMessengerContact_ReachesThemOverMessenger()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _offlineMessengerNotifier.Received(1).TrySendAsync(
            "user-a", Arg.Any<string>(), AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>());
        await _offlineMessengerNotifier.Received(1).TrySendAsync(
            "user-b", Arg.Any<string>(), AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_LoudEvent_OfflinePlannerWithoutMessengerContact_StillRecordsInboxRowsAndStaysQuiet()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.NoContact);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        Assert.That(_logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.False,
            "A missing messenger identity is a normal state, not a failure worth a warning.");
    }

    [Test]
    public async Task OnEventAsync_LoudEvent_MessengerSendRefused_LogsWarningAndKeepsTheInboxRow()
    {
        const string providerError = "bot was blocked by the user";
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Failed("Telegram", providerError));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.That(warnings, Is.Not.Empty, "A refused send must be recorded, otherwise the report claims an alert that never went out.");
        Assert.That(warnings.Any(e => e.Message.Contains(providerError, StringComparison.Ordinal)), Is.True,
            "The provider's reason belongs in the log entry.");
        Assert.That(warnings.Any(e => e.Message.Contains("user-a", StringComparison.Ordinal)), Is.True,
            "The log entry must name the recipient that was not reached.");
    }

    [Test]
    public async Task OnEventAsync_LoudEvent_MessengerThrottled_IsLoggedAsWarningToo()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Throttled("Telegram", "429 too many requests"));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        Assert.That(_logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("rate-limited", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task OnEventAsync_QuietEvent_OfflinePlanner_RecordsInboxRowButNeverWakesAnybody()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");

        await _sut.OnEventAsync(MakeEvent(daysUntil: 5));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task OnEventAsync_MessengerThrows_DoesNotCostTheRemainingRecipientsTheirInboxRows()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _offlineMessengerNotifier
            .TrySendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<OfflineMessengerDeliveryResult>(_ => throw new InvalidOperationException("provider exploded"));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_MessengerText_IsTheComposedSentenceNotTheRawI18nKey()
    {
        string? sentText = null;
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _offlineMessengerNotifier
            .TrySendAsync(Arg.Any<string>(), Arg.Do<string>(text => sentText = text), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        Assert.That(sentText, Is.EqualTo(ComposedMessengerText));
        Assert.That(sentText!.StartsWith(ProactiveMessageMarkers.I18nPrefix, StringComparison.Ordinal), Is.False,
            "A messenger has no i18n runtime, so the raw marker must never reach the recipient.");
        Assert.That(sentText, Does.Not.Contain(ProactiveMessageI18nKeys.UnstaffedShift),
            "The recipient must read a sentence, not the translation key.");
    }

    [Test]
    public async Task OnEventAsync_MessengerTextComposerThrows_KeepsTheInboxRowAndSendsNothing()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _messengerTextComposer.ComposeAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("settings unreachable"));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    private sealed record CompanionTargetedEvent(string Kind, string Severity) : IAgentTriggerEvent
    {
        public string Summary => ProactiveMessageMarkers.I18nPrefix + "assistant.proactive.test";
        public Guid? TargetUserId => CompanionRecipient;
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
    }

    private static readonly Guid CompanionRecipient = Guid.NewGuid();

    [TestCase(AgentTriggerKinds.CuriosityQuestion, AgentTriggerSeverity.Low)]
    [TestCase(AgentTriggerKinds.MuteSuggestion, AgentTriggerSeverity.Low)]
    [TestCase(AgentTriggerKinds.SkillSequenceSuggestion, AgentTriggerSeverity.Low)]
    [TestCase(AgentTriggerKinds.PlanPausedForApproval, AgentTriggerSeverity.Medium)]
    public async Task OnEventAsync_CompanionEvent_OfflineRecipient_NeverGoesOutOverMessenger(string kind, string severity)
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(new CompanionTargetedEvent(kind, severity));

        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.TriggerKind == kind), Arg.Any<CancellationToken>());
        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    [TestCase(AgentTriggerKinds.ScenarioPending)]
    [TestCase(AgentTriggerKinds.PeriodOverdue)]
    [TestCase(AgentTriggerKinds.PeriodCloseDue)]
    [TestCase(AgentTriggerKinds.ContractExpiringSoon)]
    [TestCase(AgentTriggerKinds.TargetHoursDrift)]
    [TestCase(AgentTriggerKinds.AvailabilityGap)]
    [TestCase(AgentTriggerKinds.LockConflict)]
    public async Task OnEventAsync_HighSeverityButNotWakeWorthy_OfflinePlanner_StaysInTheInbox(string kind)
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(new PlannerKindEvent(kind, AgentTriggerSeverity.High));

        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    [TestCase(AgentTriggerKinds.UnstaffedShift)]
    [TestCase(AgentTriggerKinds.WorkDroppedByErpImport)]
    [TestCase(AgentTriggerKinds.OrderImportFailed)]
    public async Task OnEventAsync_WakeWorthyKindAtHighSeverity_OfflinePlanner_IsReachedOverMessenger(string kind)
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(new PlannerKindEvent(kind, AgentTriggerSeverity.High));

        await _offlineMessengerNotifier.Received(1).TrySendAsync(
            "user-a", ComposedMessengerText, kind, Arg.Any<CancellationToken>());
    }

    [TestCase(AgentTriggerSeverity.Medium)]
    [TestCase(AgentTriggerSeverity.Low)]
    public async Task OnEventAsync_WakeWorthyKindBelowHighSeverity_OfflinePlanner_StaysInTheInbox(string severity)
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(new PlannerKindEvent(AgentTriggerKinds.UnstaffedShift, severity));

        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task OnEventAsync_WakeWorthyEvent_MutedRecipient_IsNotWokenOverMessengerEither()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));
        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_WakeWorthyEvent_UnmutedRecipient_IsWokenOverMessenger()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        SetOfflineMessengerResult(OfflineMessengerDeliveryResult.Sent("Telegram"));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _offlineMessengerNotifier.Received(1).TrySendAsync(
            "user-a", ComposedMessengerText, AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_ConnectedRecipient_IsNeverAlsoContactedOverMessenger()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _offlineMessengerNotifier.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task OnEventAsync_RateLimited_BlocksThatUserBeforePersist()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire("user-a", Arg.Any<string>()).Returns(true);
        _rateLimiter.ShouldFire("user-b", Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_MutedByPreference_BlocksThatUserBeforePersist()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService.IsAllowedAsync("user-a", Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService.IsAllowedAsync("user-b", Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_AlreadyDispatched_DedupBlocksBeforePersist()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _dispatchRepository
            .WasDispatchedAsync("user-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
        _rateLimiter.DidNotReceiveWithAnyArgs().RecordFire(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_UserActiveInConversation_PersistsAndSignalsInboxInsteadOfChatPush()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _activityTracker.IsRecentlyActive("user-a", Arg.Any<TimeSpan>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveInboxChangedAsync("user-a", Arg.Any<int>());
    }

    [Test]
    public async Task OnEventAsync_PlainHighSeverityEvent_PrefixesWithTag()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.High, "Plain summary."));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a",
            Arg.Is<string>(s => s.StartsWith("[HIGH]")),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_I18nEvent_SendsBareKeyWithoutTag_AndForwardsParams()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a",
            Arg.Is<string>(s => s == ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.UnstaffedShift),
            Arg.Any<string?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(p => p != null && p.ContainsKey("date") && p.ContainsKey("days")),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_LivePushesOnlyToPlanners_NeverToConnectedEmployees()
    {
        const string planner = "planner-1";
        const string employee = "employee-1";
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { planner, employee });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners(planner);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            planner, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(
            employee, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == employee), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_OnlyEmployeesConnected_RecordsForOfflinePlannerWithoutPush()
    {
        const string offlinePlanner = "some-other-planner";
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "employee-1", "employee-2" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners(offlinePlanner);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == offlinePlanner), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_CompanionBroadcast_LivePushesToConnectedUsersEvenAtLowSeverity()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "employee-1" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("some-other-planner");

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "employee-1", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "employee-1"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_Dispatch_RecordsContentKeySeverityAndCouplesMessageIdToRowId()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        string? sentMessageId = null;
        ProactiveTriggerDispatchRow? recordedRow = null;
        _notificationService
            .When(n => n.SendProactiveMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>()))
            .Do(ci => sentMessageId = ci.ArgAt<string?>(4));
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));

        var triggerEvent = MakeEvent();
        await _sut.OnEventAsync(triggerEvent);

        Assert.That(sentMessageId, Is.Not.Null.And.Not.Empty);
        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.Id.ToString(), Is.EqualTo(sentMessageId));
        Assert.That(recordedRow.UserId, Is.EqualTo("user-a"));
        Assert.That(recordedRow.TriggerKind, Is.EqualTo(triggerEvent.Kind));
        Assert.That(recordedRow.DedupKey, Is.EqualTo(triggerEvent.DedupKey));
        Assert.That(recordedRow.ContentKey, Is.EqualTo(triggerEvent.Summary));
        Assert.That(recordedRow.Severity, Is.EqualTo(triggerEvent.Severity));
        Assert.That(recordedRow.ContentParamsJson, Does.Contain("date"));
        Assert.That(recordedRow.Reaction, Is.EqualTo(ProactiveReaction.None));
        Assert.That(recordedRow.ReactionAtUtc, Is.Null);
    }

    [Test]
    public async Task OnEventAsync_OverlongParamValue_CapsValueAndKeepsJsonWithinColumnLimit()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var overlongValue = new string('x', 6000);

        await _sut.OnEventAsync(new ParamsBroadcastEvent(new Dictionary<string, string> { ["error"] = overlongValue }));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ContentParamsJson, Is.Not.Null);
        Assert.That(recordedRow.ContentParamsJson!.Length, Is.LessThanOrEqualTo(ProactiveTriggerDispatchLimits.ContentParamsJsonMaxLength));
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(recordedRow.ContentParamsJson);
        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!["error"], Has.Length.EqualTo(ProactiveTriggerDispatchLimits.ContentParamValueMaxLength));
        Assert.That(deserialized["error"], Does.EndWith(ProactiveTriggerDispatchLimits.TruncationSuffix));
    }

    [Test]
    public async Task OnEventAsync_ParamsExceedColumnLimitEvenAfterCapping_StoresNullInsteadOfInvalidJson()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var manyParams = Enumerable.Range(0, 6)
            .ToDictionary(i => $"param{i}", i => new string('x', 900));

        await _sut.OnEventAsync(new ParamsBroadcastEvent(manyParams));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ContentParamsJson, Is.Null);
        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_SendFails_RowStaysPersistedAndBudgetCounted_MessageReachableViaInbox()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _notificationService
            .SendProactiveMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(Task.FromException(new InvalidOperationException("send failed")));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
    }

    [Test]
    public async Task OnEventAsync_PersistFails_DoesNotCountBudgetOrPush()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _dispatchRepository
            .RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("persist failed")));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        _rateLimiter.DidNotReceiveWithAnyArgs().RecordFire(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_TargetedCompanionEvent_LivePushesOnlyToConnectedTargetUser()
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { target.ToString(), other.ToString() });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new CuriosityQuestionTriggerEvent("sport", target));

        await _notificationService.Received(1).SendProactiveMessageAsync(target.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(other.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == other.ToString()), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_TargetedCompanionEvent_OfflineTarget_RecordsInboxRowWithoutPush()
    {
        var target = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new CuriosityQuestionTriggerEvent("sport", target));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == target.ToString()), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_SkillSequenceSuggestion_ReachesOnlyTheTargetUser_NeverOtherConnectedUsers()
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { target.ToString(), other.ToString() });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new SkillSequenceSuggestionTriggerEvent("Add a note", "Send an email", target));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == target.ToString()), Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == other.ToString()), Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveMessageAsync(target.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(other.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_AdminOnly_RecordsForOfflineAdminsViaAdminAudience_NeverForConnectedNonAdmins()
    {
        const string offlineAdmin = "admin-1";
        const string connectedEmployee = "employee-1";
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { connectedEmployee });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetAdmins(offlineAdmin);

        await _sut.OnEventAsync(new OrderImportFailedTriggerEvent(Guid.NewGuid(), "orders.csv", "parse error"));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == offlineAdmin), Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == connectedEmployee), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
        await _planningAudienceResolver.Received(1).GetAdminUserIdsAsync(Arg.Any<CancellationToken>());
        await _planningAudienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
    }

    [Test]
    public async Task OnEventAsync_PlannersOnlyEventWithGroupId_ResolvesGroupScopedAudience_NotTheUnscopedOne()
    {
        var groupId = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetGroupScopedPlanners(groupId, "scoped-planner");

        var triggerEvent = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), 2, new[] { groupId });

        await _sut.OnEventAsync(triggerEvent);

        await _planningAudienceResolver.Received(1).GetPlanningUserIdsForGroupAsync(groupId, Arg.Any<CancellationToken>());
        await _planningAudienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "scoped-planner"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_PlannersOnlyEventThatIsNotGroupScoped_UsesUnscopedAudience_NeverTheGroupScopedResolver()
    {
        // Installation-wide planner alerts (hours drift, expiring contract, missing client core data)
        // do not set RequiresGroupScope and must keep reaching every planner.
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("planner-a");

        await _sut.OnEventAsync(new PlannerKindEvent("test_unscoped", AgentTriggerSeverity.Medium));

        await _planningAudienceResolver.Received(1).GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
        await _planningAudienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsForGroupAsync(default, default);
        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "planner-a"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_EventInTwoGroups_ResolvesTheUnionOfBothGroupAudiences()
    {
        // A shift can be a member of several groups at once. Resolving only the first would deny the
        // finding to every planner scoped to the second - the whole point of GroupIds over GroupId.
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetGroupScopedPlanners(firstGroupId, "admin", "planner-first");
        SetGroupScopedPlanners(secondGroupId, "admin", "planner-second");

        var triggerEvent = new UnstaffedShiftTriggerEvent(
            Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), 2, new[] { firstGroupId, secondGroupId });

        await _sut.OnEventAsync(triggerEvent);

        await _planningAudienceResolver.Received(1).GetPlanningUserIdsForGroupAsync(firstGroupId, Arg.Any<CancellationToken>());
        await _planningAudienceResolver.Received(1).GetPlanningUserIdsForGroupAsync(secondGroupId, Arg.Any<CancellationToken>());
        await _planningAudienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
        foreach (var expectedRecipient in new[] { "admin", "planner-first", "planner-second" })
        {
            await _dispatchRepository.Received(1).RecordAsync(
                Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == expectedRecipient), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task OnEventAsync_EventInTwoGroups_DeliversOnceToAPlannerVisibleInBoth()
    {
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetGroupScopedPlanners(firstGroupId, "planner-both");
        SetGroupScopedPlanners(secondGroupId, "planner-both");

        var triggerEvent = new UnstaffedShiftTriggerEvent(
            Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), 2, new[] { firstGroupId, secondGroupId });

        await _sut.OnEventAsync(triggerEvent);

        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "planner-both"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_GroupScopeRequiredEventWithoutAnyGroup_ReachesAdminsOnly_NotEveryPlanner()
    {
        // The security-relevant case: before GroupIds existed these five kinds carried no group at all
        // and fell through to the unscoped planner broadcast, handing every planner the detail of a
        // shift they may not see. An unattributable shift finding now stops at the admins.
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("planner-a", "planner-b");
        SetAdmins("admin");

        var triggerEvent = new UnstaffedShiftTriggerEvent(
            Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), 2, Array.Empty<Guid>());

        await _sut.OnEventAsync(triggerEvent);

        await _planningAudienceResolver.Received(1).GetAdminUserIdsAsync(Arg.Any<CancellationToken>());
        await _planningAudienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "admin"), Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceive().RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "planner-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceive().RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "planner-b"), Arg.Any<CancellationToken>());
    }

    [TestCaseSource(nameof(SingleGroupKindCases))]
    public async Task OnEventAsync_KindsCarryingASingleGroupId_StillResolveThatOneGroupScopedAudience(
        Func<Guid, IAgentTriggerEvent> makeEvent)
    {
        // Regression guard for period_close_due, period_overdue and scenario_pending: they keep a
        // single GroupId and reach GroupIds through the interface default, so their audience must be
        // exactly what it was before the union existed.
        var groupId = Guid.NewGuid();
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetGroupScopedPlanners(groupId, "scoped-planner");

        var triggerEvent = makeEvent(groupId);

        Assert.That(triggerEvent.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(triggerEvent.RequiresGroupScope, Is.False);

        await _sut.OnEventAsync(triggerEvent);

        await _planningAudienceResolver.Received(1).GetPlanningUserIdsForGroupAsync(groupId, Arg.Any<CancellationToken>());
        await _planningAudienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsAsync(default);
        await _dispatchRepository.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "scoped-planner"), Arg.Any<CancellationToken>());
    }

    private static IEnumerable<TestCaseData> SingleGroupKindCases()
    {
        yield return new TestCaseData(new Func<Guid, IAgentTriggerEvent>(
            groupId => new PeriodCloseDueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 3)))
            .SetName("period_close_due");
        yield return new TestCaseData(new Func<Guid, IAgentTriggerEvent>(
            groupId => new PeriodOverdueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 10)))
            .SetName("period_overdue");
        yield return new TestCaseData(new Func<Guid, IAgentTriggerEvent>(
            groupId => new ScenarioPendingTriggerEvent(Guid.NewGuid(), 80, groupId, "GE")))
            .SetName("scenario_pending");
    }

    [Test]
    public async Task OnEventAsync_EventWithAction_PersistsActionAndForwardsItInLivePush()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        ProactiveTriggerDispatchRow? recordedRow = null;
        string? sentKind = null;
        string? sentActionRoute = null;
        IReadOnlyDictionary<string, string>? sentActionParams = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        _notificationService
            .When(n => n.SendProactiveMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>()))
            .Do(ci =>
            {
                sentKind = ci.ArgAt<string?>(5);
                sentActionRoute = ci.ArgAt<string?>(6);
                sentActionParams = ci.ArgAt<IReadOnlyDictionary<string, string>?>(7);
            });
        var groupId = Guid.NewGuid();
        // This event carries a GroupId, so ResolveRecipientsAsync now resolves the group-scoped
        // audience instead of the unscoped one (SetPlanners above) -- stub it too.
        SetGroupScopedPlanners(groupId, "user-a");
        var triggerEvent = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 1, new[] { groupId });

        await _sut.OnEventAsync(triggerEvent);

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ActionRoute, Is.EqualTo(ProactiveActionRoutes.Schedule));
        Assert.That(recordedRow.ActionParamsJson, Is.Not.Null);
        var persistedParams = JsonSerializer.Deserialize<Dictionary<string, string>>(recordedRow.ActionParamsJson!);
        Assert.That(persistedParams, Is.Not.Null);
        Assert.That(persistedParams![ProactiveActionParamKeys.GroupId], Is.EqualTo(groupId.ToString()));
        Assert.That(persistedParams[ProactiveActionParamKeys.Date], Is.EqualTo("2026-08-03"));
        Assert.That(sentKind, Is.EqualTo(AgentTriggerKinds.UnstaffedShift));
        Assert.That(sentActionRoute, Is.EqualTo(ProactiveActionRoutes.Schedule));
        Assert.That(sentActionParams, Is.Not.Null);
        Assert.That(sentActionParams![ProactiveActionParamKeys.GroupId], Is.EqualTo(groupId.ToString()));
    }

    [Test]
    public async Task OnEventAsync_EventWithoutAction_PersistsNullActionFields()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ActionRoute, Is.Null);
        Assert.That(recordedRow.ActionParamsJson, Is.Null);
    }

    [Test]
    public async Task OnEventAsync_OverlongActionParamValue_CapsValueAndKeepsJsonWithinColumnLimit()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var overlongValue = new string('x', 1500);

        await _sut.OnEventAsync(new ActionBroadcastEvent(
            ProactiveActionRoutes.Schedule,
            new Dictionary<string, string> { ["target"] = overlongValue }));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ActionParamsJson, Is.Not.Null);
        Assert.That(recordedRow.ActionParamsJson!.Length, Is.LessThanOrEqualTo(ProactiveTriggerDispatchLimits.ActionParamsJsonMaxLength));
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(recordedRow.ActionParamsJson);
        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!["target"], Has.Length.EqualTo(ProactiveTriggerDispatchLimits.ContentParamValueMaxLength));
        Assert.That(deserialized["target"], Does.EndWith(ProactiveTriggerDispatchLimits.TruncationSuffix));
    }

    [Test]
    public async Task OnEventAsync_ActionParamsExceedColumnLimitEvenAfterCapping_StoresNullActionParamsButKeepsRoute()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var manyParams = Enumerable.Range(0, 3)
            .ToDictionary(i => $"param{i}", i => new string('x', 900));

        await _sut.OnEventAsync(new ActionBroadcastEvent(ProactiveActionRoutes.Schedule, manyParams));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ActionRoute, Is.EqualTo(ProactiveActionRoutes.Schedule));
        Assert.That(recordedRow.ActionParamsJson, Is.Null);
    }

    [Test]
    public async Task OnEventAsync_LedgerTrackedEvent_LinksEveryDispatchRowToTheOpenConditionOfItsFingerprint()
    {
        var triggerEvent = MakeEvent(daysUntil: 1);
        var condition = new AgentCondition { Id = Guid.NewGuid() };
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _conditionRepository
            .FindOpenByFingerprintAsync(
                AgentConditionLedgerPolicy.FingerprintFor(triggerEvent),
                Arg.Any<CancellationToken>())
            .Returns(condition);
        var recordedRows = new List<ProactiveTriggerDispatchRow>();
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRows.Add(ci.ArgAt<ProactiveTriggerDispatchRow>(0)));

        await _sut.OnEventAsync(triggerEvent);

        Assert.That(recordedRows, Has.Count.EqualTo(2));
        Assert.That(recordedRows.Select(row => row.ConditionId), Is.All.EqualTo(condition.Id));
        await _conditionRepository.Received(1).FindOpenByFingerprintAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_CompanionBroadcast_NeverAsksTheLedgerAndLeavesTheLinkNull()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ConditionId, Is.Null);
        await _conditionRepository.DidNotReceiveWithAnyArgs()
            .FindOpenByFingerprintAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_LedgerLookupThrows_StillPersistsTheMessageWithoutALink()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _conditionRepository
            .FindOpenByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<AgentCondition?>>(_ => throw new InvalidOperationException("ledger down"));
        var recordedRows = new List<ProactiveTriggerDispatchRow>();
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRows.Add(ci.ArgAt<ProactiveTriggerDispatchRow>(0)));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        Assert.That(recordedRows, Has.Count.EqualTo(2));
        Assert.That(recordedRows.Select(row => row.ConditionId), Is.All.Null);
    }

    [Test]
    public async Task OnEventAsync_LedgerTrackedEvent_SchedulesTheFirstReminderOnTheBackoffLadder()
    {
        var triggerEvent = MakeEvent(daysUntil: 1);
        var condition = new AgentCondition { Id = Guid.NewGuid() };
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _conditionRepository
            .FindOpenByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(condition);
        var recordedRows = new List<ProactiveTriggerDispatchRow>();
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRows.Add(ci.ArgAt<ProactiveTriggerDispatchRow>(0)));

        await _sut.OnEventAsync(triggerEvent);

        var expectedFirstDue = ProactiveReminderSchedule.FirstDueAfter(FakeNow);
        Assert.That(recordedRows, Has.Count.EqualTo(2));
        Assert.That(recordedRows.Select(row => row.NextReminderAtUtc), Is.All.EqualTo(expectedFirstDue),
            "A condition-linked row joins the reminder loop with the first backoff step (one hour).");
        await _dispatchRepository.Received(1).WasDispatchedAsync(
            "user-a", triggerEvent.Kind, triggerEvent.DedupKey, condition.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_EventWithoutConditionLink_KeepsTheReminderScheduleEmpty()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ConditionId, Is.Null);
        Assert.That(recordedRow.NextReminderAtUtc, Is.Null,
            "A row without a condition link is a plain inbox message and must never re-fire.");
    }

    [Test]
    public async Task OnEventAsync_ReminderDueDate_ComesFromTheInjectedClock_NotTheSystemClock()
    {
        var distantPast = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _timeProvider.Now = distantPast;
        var condition = new AgentCondition { Id = Guid.NewGuid() };
        _notificationService.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _conditionRepository
            .FindOpenByFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(condition);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.NextReminderAtUtc, Is.EqualTo(distantPast.AddHours(1)),
            "The due date must be stamped from the injected TimeProvider, never from DateTime.UtcNow.");
    }

    [Test]
    public async Task OnEventAsync_OverlongSummary_CapsContentKeyAndDedupKeyToTheColumnLimits()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var overlongSummary = new string('x', 2050);

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, overlongSummary));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(recordedRow!.ContentKey, Has.Length.EqualTo(ProactiveTriggerDispatchLimits.ContentKeyMaxLength));
            Assert.That(recordedRow.ContentKey, Does.EndWith(ProactiveTriggerDispatchLimits.TruncationSuffix));
            Assert.That(recordedRow.DedupKey, Has.Length.EqualTo(ProactiveTriggerDispatchLimits.DedupKeyMaxLength));
        });
    }

    [Test]
    public async Task OnEventAsync_SummaryCutInsideASurrogatePair_KeepsTheStoredTextValidUtf8()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var emojiSummary = string.Concat(Enumerable.Repeat("\U0001F600", 400));

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, emojiSummary));

        Assert.That(recordedRow, Is.Not.Null);
        var contentKey = recordedRow!.ContentKey!;
        Assert.That(contentKey, Has.Length.LessThanOrEqualTo(ProactiveTriggerDispatchLimits.ContentKeyMaxLength));
        Assert.That(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(contentKey)), Is.EqualTo(contentKey),
            "Cutting a surrogate pair in half leaves a lone surrogate that cannot round-trip through UTF-8.");
    }

    [Test]
    public async Task OnEventAsync_PersistenceFails_LogsAnErrorAndReportsTheFailureInTheSummaryLine()
    {
        _notificationService.GetConnectedUserIdsAsync().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _dispatchRepository
            .RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("column overflow")));

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Plain summary."));

        var errors = _logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.That(errors, Has.Count.EqualTo(1),
            "A lost notification is not a warning: nothing else keeps the message reachable.");
        Assert.That(errors[0].Message, Does.Contain("persistence failed"));
        Assert.That(
            _logger.Entries.Any(e => e.Level == LogLevel.Information
                && e.Message.Contains("1 failed", StringComparison.Ordinal)),
            Is.True,
            "The per-event summary line must account for the recipients that lost their row.");
    }
}

/// <summary>
/// One container schedule shared by the fixtures that only need an EmptyContainerTriggerEvent to exist.
/// Its exact values are irrelevant to dedup keys and audience scoping; what matters is that the event
/// carries the schedule its Etappe-5b remediation binder reads out of the payload.
/// </summary>
internal static class EmptyContainerTestSchedule
{
    public static readonly ContainerScheduleSnapshot Value =
        new(new TimeOnly(8, 0), new TimeOnly(17, 0), new[] { 1 }, false, false);
}

[TestFixture]
public class OperationalTriggerEventDedupKeyTests
{
    [Test]
    public void AllOperationalEvents_HaveDiscriminatingDedupKey_NotEqualToSummary()
    {
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var endDate = new DateOnly(2026, 6, 30);

        var eventA = new PeriodCloseDueTriggerEvent(groupA, "Group A", endDate, 3);
        var eventB = new PeriodCloseDueTriggerEvent(groupB, "Group B", endDate, 3);

        Assert.That(eventA.DedupKey, Is.Not.EqualTo(eventA.Summary));
        Assert.That(eventA.DedupKey, Is.Not.EqualTo(eventB.DedupKey));
    }

    [Test]
    public void OperationalEvents_DedupKeyIsStableAcrossChangingMagnitude()
    {
        var groupId = Guid.NewGuid();
        var endDate = new DateOnly(2026, 6, 30);

        var threeDays = new PeriodCloseDueTriggerEvent(groupId, "Group", endDate, 3);
        var oneDay = new PeriodCloseDueTriggerEvent(groupId, "Group", endDate, 1);

        Assert.That(threeDays.DedupKey, Is.EqualTo(oneDay.DedupKey));
    }

    [Test]
    public void EachOperationalEventType_ProducesAnI18nSummary()
    {
        var drift = new TargetHoursDriftTriggerEvent(Guid.NewGuid(), "Jane", -170m, "2026-06");
        var period = new PeriodCloseDueTriggerEvent(Guid.NewGuid(), "GE", new DateOnly(2026, 6, 30), 3);
        var unstaffed = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 6, 30), 2, Array.Empty<Guid>());
        var lockConflict = new LockConflictDetectedTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 6, 30), 2, Array.Empty<Guid>());
        var scenario = new ScenarioPendingTriggerEvent(Guid.NewGuid(), 80, null, "GE");
        var contract = new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), "Jane", new DateOnly(2026, 6, 30), 5);
        var availabilityGap = new AvailabilityGapTriggerEvent(Guid.NewGuid(), "Jane", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 10);
        var periodOverdue = new PeriodOverdueTriggerEvent(Guid.NewGuid(), "GE", new DateOnly(2026, 6, 30), 10);
        var missingCoreData = new ClientMissingCoreDataTriggerEvent(Guid.NewGuid(), "Jane", ClientMissingCoreDataTriggerEvent.AddressField);
        var openOrder = new OpenOrderTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 30), null, 3, Array.Empty<Guid>());
        var uncutFulldayShift = new UncutFullDayShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 6, 30), 3, Array.Empty<Guid>());
        var emptyContainer = new EmptyContainerTriggerEvent(
            Guid.NewGuid(), "Container A", new DateOnly(2026, 6, 30), null, Array.Empty<Guid>(),
            EmptyContainerTestSchedule.Value, IsPeriodActive: true);

        foreach (var ev in new IAgentTriggerEvent[] { drift, period, unstaffed, lockConflict, scenario, contract, availabilityGap, periodOverdue, missingCoreData, openOrder, uncutFulldayShift, emptyContainer })
        {
            Assert.That(ev.PlannersOnly, Is.True, $"{ev.Kind} must be planners-only");
            Assert.That(ev.Summary, Does.StartWith(ProactiveMessageMarkers.I18nPrefix), $"{ev.Kind} must use an i18n summary");
            Assert.That(ev.SummaryParams, Is.Not.Null.And.Not.Empty, $"{ev.Kind} must carry summary params");
        }
    }

    [Test]
    public void DataQualityEvents_HaveDiscriminatingDedupKeys()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var august = new DateOnly(2026, 8, 1);
        var september = new DateOnly(2026, 9, 1);

        var gapA = new AvailabilityGapTriggerEvent(clientA, "A", august, new DateOnly(2026, 8, 31), 10);
        var gapB = new AvailabilityGapTriggerEvent(clientB, "B", august, new DateOnly(2026, 8, 31), 10);
        var gapNextMonth = new AvailabilityGapTriggerEvent(clientA, "A", september, new DateOnly(2026, 9, 30), 10);
        Assert.That(gapA.DedupKey, Is.Not.EqualTo(gapA.Summary));
        Assert.That(gapA.DedupKey, Is.Not.EqualTo(gapB.DedupKey));
        Assert.That(gapA.DedupKey, Is.Not.EqualTo(gapNextMonth.DedupKey));

        var overdueA = new PeriodOverdueTriggerEvent(clientA, "GE", new DateOnly(2026, 6, 30), 10);
        var overdueB = new PeriodOverdueTriggerEvent(clientB, "BE", new DateOnly(2026, 6, 30), 10);
        var overdueNextPeriod = new PeriodOverdueTriggerEvent(clientA, "GE", new DateOnly(2026, 7, 31), 10);
        Assert.That(overdueA.DedupKey, Is.Not.EqualTo(overdueA.Summary));
        Assert.That(overdueA.DedupKey, Is.Not.EqualTo(overdueB.DedupKey));
        Assert.That(overdueA.DedupKey, Is.Not.EqualTo(overdueNextPeriod.DedupKey));

        var missingAddress = new ClientMissingCoreDataTriggerEvent(clientA, "A", ClientMissingCoreDataTriggerEvent.AddressField);
        var missingContact = new ClientMissingCoreDataTriggerEvent(clientA, "A", ClientMissingCoreDataTriggerEvent.ContactField);
        var missingAddressOther = new ClientMissingCoreDataTriggerEvent(clientB, "B", ClientMissingCoreDataTriggerEvent.AddressField);
        Assert.That(missingAddress.DedupKey, Is.Not.EqualTo(missingAddress.Summary));
        Assert.That(missingAddress.DedupKey, Is.Not.EqualTo(missingContact.DedupKey));
        Assert.That(missingAddress.DedupKey, Is.Not.EqualTo(missingAddressOther.DedupKey));
    }

    [Test]
    public void DataQualityEvents_DedupKeysAreStableAcrossChangingMagnitude()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        var gapFar = new AvailabilityGapTriggerEvent(clientId, "A", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 20);
        var gapNear = new AvailabilityGapTriggerEvent(clientId, "A", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 3);
        Assert.That(gapFar.DedupKey, Is.EqualTo(gapNear.DedupKey));

        var overdueYoung = new PeriodOverdueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 8);
        var overdueOld = new PeriodOverdueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 25);
        Assert.That(overdueYoung.DedupKey, Is.EqualTo(overdueOld.DedupKey));
    }

    [Test]
    public void DataQualityEvents_CarryContractedI18nKeysAndParams()
    {
        var gap = new AvailabilityGapTriggerEvent(Guid.NewGuid(), "Jane", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 10);
        Assert.That(gap.Summary, Is.EqualTo("i18n:assistant.proactive.availabilityGap"));
        Assert.That(gap.SummaryParams.Keys, Is.EquivalentTo(new[] { "name", "from", "until" }));
        Assert.That(gap.SummaryParams["from"], Is.EqualTo("01.08.2026"));
        Assert.That(gap.SummaryParams["until"], Is.EqualTo("31.08.2026"));

        var overdue = new PeriodOverdueTriggerEvent(Guid.NewGuid(), "GE", new DateOnly(2026, 6, 30), 10);
        Assert.That(overdue.Summary, Is.EqualTo("i18n:assistant.proactive.periodOverdue"));
        Assert.That(overdue.SummaryParams.Keys, Is.EquivalentTo(new[] { "group", "periodEnd", "days" }));
        Assert.That(overdue.SummaryParams["periodEnd"], Is.EqualTo("30.06.2026"));
        Assert.That(overdue.SummaryParams["days"], Is.EqualTo("10"));

        var missingAddress = new ClientMissingCoreDataTriggerEvent(Guid.NewGuid(), "Jane", ClientMissingCoreDataTriggerEvent.AddressField);
        Assert.That(missingAddress.Summary, Is.EqualTo("i18n:assistant.proactive.clientMissingAddress"));
        Assert.That(missingAddress.SummaryParams.Keys, Is.EquivalentTo(new[] { "name" }));
        Assert.That(missingAddress.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));

        var missingContact = new ClientMissingCoreDataTriggerEvent(Guid.NewGuid(), "Jane", ClientMissingCoreDataTriggerEvent.ContactField);
        Assert.That(missingContact.Summary, Is.EqualTo("i18n:assistant.proactive.clientMissingContact"));
        Assert.That(missingContact.SummaryParams.Keys, Is.EquivalentTo(new[] { "name" }));
        Assert.That(missingContact.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
    }

    [Test]
    public void OperationalEvents_CarryContractedActionRoutesAndParams()
    {
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var scenarioId = Guid.NewGuid();

        var unstaffed = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { groupId });
        Assert.That(unstaffed.ActionRoute, Is.EqualTo("/workplace/schedule"));
        Assert.That(unstaffed.ActionParams, Is.Not.Null);
        Assert.That(unstaffed.ActionParams!["groupId"], Is.EqualTo(groupId.ToString()));
        Assert.That(unstaffed.ActionParams["date"], Is.EqualTo("2026-08-03"));

        var unstaffedWithoutGroup = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, Array.Empty<Guid>());
        Assert.That(unstaffedWithoutGroup.ActionParams!.Keys, Is.EquivalentTo(new[] { "date" }));

        var lockConflict = new LockConflictDetectedTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { groupId });
        Assert.That(lockConflict.ActionRoute, Is.EqualTo("/workplace/schedule"));
        Assert.That(lockConflict.ActionParams!.Keys, Is.EquivalentTo(new[] { "date", "groupId" }));

        var drift = new TargetHoursDriftTriggerEvent(clientId, "Jane", -20m, "2026-06");
        Assert.That(drift.ActionRoute, Is.EqualTo("/workplace/schedule"));
        Assert.That(drift.ActionParams!["clientId"], Is.EqualTo(clientId.ToString()));
        Assert.That(drift.ActionParams["period"], Is.EqualTo("2026-06"));

        var periodClose = new PeriodCloseDueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 3);
        Assert.That(periodClose.ActionRoute, Is.EqualTo("/workplace/period-closing"));
        Assert.That(periodClose.ActionParams!["groupId"], Is.EqualTo(groupId.ToString()));
        Assert.That(periodClose.ActionParams["date"], Is.EqualTo("2026-06-30"));

        var periodOverdue = new PeriodOverdueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 10);
        Assert.That(periodOverdue.ActionRoute, Is.EqualTo("/workplace/period-closing"));
        Assert.That(periodOverdue.ActionParams!["groupId"], Is.EqualTo(groupId.ToString()));

        var scenario = new ScenarioPendingTriggerEvent(scenarioId, 80, groupId, "GE");
        Assert.That(scenario.ActionRoute, Is.EqualTo("/workplace/schedule"));
        Assert.That(scenario.ActionParams!["scenarioId"], Is.EqualTo(scenarioId.ToString()));
        Assert.That(scenario.ActionParams["groupId"], Is.EqualTo(groupId.ToString()));

        var contract = new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), clientId, "Jane", new DateOnly(2026, 6, 30), 5);
        Assert.That(contract.ActionRoute, Is.EqualTo("/workplace/edit-address"));
        Assert.That(contract.ActionParams!.Keys, Is.EquivalentTo(new[] { "clientId" }));
        Assert.That(contract.ActionParams["clientId"], Is.EqualTo(clientId.ToString()));

        var missingCoreData = new ClientMissingCoreDataTriggerEvent(clientId, "Jane", ClientMissingCoreDataTriggerEvent.AddressField);
        Assert.That(missingCoreData.ActionRoute, Is.EqualTo("/workplace/edit-address"));
        Assert.That(missingCoreData.ActionParams!["clientId"], Is.EqualTo(clientId.ToString()));

        var gap = new AvailabilityGapTriggerEvent(clientId, "Jane", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 10);
        Assert.That(gap.ActionRoute, Is.EqualTo("/workplace/client-availability"));
        Assert.That(gap.ActionParams!["clientId"], Is.EqualTo(clientId.ToString()));
        Assert.That(gap.ActionParams["date"], Is.EqualTo("2026-08-01"));
    }

    [Test]
    public void GroupCarryingOperationalEvents_ExposeGroupIdThroughTheInterface_ForAudienceScoping()
    {
        // AgentTriggerService.ResolveRecipientsAsync reads triggerEvent.GroupIds through the
        // IAgentTriggerEvent interface, not the concrete record type. The three single-group kinds
        // reach GroupIds through the interface DEFAULT, which derives it from their GroupId -- and
        // PeriodCloseDue/PeriodOverdue carry a non-nullable Guid GroupId that needs an explicit bridge
        // (Guid? IAgentTriggerEvent.GroupId => GroupId), because a plain public Guid property does not
        // implicitly satisfy a Guid? interface member. Asserting via the concrete type alone would
        // catch neither the missing bridge nor a broken default.
        var groupId = Guid.NewGuid();

        IAgentTriggerEvent unstaffed = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { groupId });
        IAgentTriggerEvent lockConflict = new LockConflictDetectedTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { groupId });
        IAgentTriggerEvent scenario = new ScenarioPendingTriggerEvent(Guid.NewGuid(), 80, groupId, "GE");
        IAgentTriggerEvent periodClose = new PeriodCloseDueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 3);
        IAgentTriggerEvent periodOverdue = new PeriodOverdueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 10);

        foreach (var groupCarrying in new[] { unstaffed, lockConflict, scenario, periodClose, periodOverdue })
        {
            Assert.That(groupCarrying.GroupIds, Is.EqualTo(new[] { groupId }), $"{groupCarrying.Kind} must expose its group");
        }

        Assert.That(scenario.GroupId, Is.EqualTo(groupId));
        Assert.That(periodClose.GroupId, Is.EqualTo(groupId));
        Assert.That(periodOverdue.GroupId, Is.EqualTo(groupId));

        IAgentTriggerEvent unstaffedWithoutGroup = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, Array.Empty<Guid>());
        Assert.That(unstaffedWithoutGroup.GroupIds, Is.Empty);
    }

    [Test]
    public void ShiftBorneEvents_RequireGroupScope_WhileInstallationWideOnesDoNot()
    {
        // The flag is what separates "no group means admins only" from "no group means everybody":
        // getting it wrong on a new kind silently re-opens the leak this guard exists for.
        var groupId = Guid.NewGuid();

        IAgentTriggerEvent[] shiftBorne =
        [
            new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { groupId }),
            new LockConflictDetectedTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { groupId }),
            new OpenOrderTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 8, 3), null, 3, new[] { groupId }),
            new UncutFullDayShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 8, 3), 3, new[] { groupId }),
            new EmptyContainerTriggerEvent(
                Guid.NewGuid(), "Container A", new DateOnly(2026, 8, 3), null, new[] { groupId },
                EmptyContainerTestSchedule.Value, IsPeriodActive: true)
        ];

        IAgentTriggerEvent[] installationWide =
        [
            new TargetHoursDriftTriggerEvent(Guid.NewGuid(), "Jane", -20m, "2026-06"),
            new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), "Jane", new DateOnly(2026, 6, 30), 5),
            new PeriodCloseDueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 3),
            new PeriodOverdueTriggerEvent(groupId, "GE", new DateOnly(2026, 6, 30), 10),
            new ScenarioPendingTriggerEvent(Guid.NewGuid(), 80, groupId, "GE")
        ];

        foreach (var triggerEvent in shiftBorne)
        {
            Assert.That(triggerEvent.RequiresGroupScope, Is.True, $"{triggerEvent.Kind} is shift-borne and must require group scope");
        }

        foreach (var triggerEvent in installationWide)
        {
            Assert.That(triggerEvent.RequiresGroupScope, Is.False, $"{triggerEvent.Kind} must keep its previous fallback behaviour");
        }
    }

    [Test]
    public void LedgerGroupIdFor_TakesTheFirstOfSeveralGroups_AndNullWhenThereIsNone()
    {
        // AgentCondition has one GroupId column. The representative it stores is a reporting attribute
        // only; the dispatch audience is recomputed from the full GroupIds set every time.
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();

        var twoGroups = new UnstaffedShiftTriggerEvent(
            Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, new[] { firstGroupId, secondGroupId });
        var noGroup = new UnstaffedShiftTriggerEvent(
            Guid.NewGuid(), new DateOnly(2026, 8, 3), 2, Array.Empty<Guid>());
        var singleGroupKind = new PeriodCloseDueTriggerEvent(firstGroupId, "GE", new DateOnly(2026, 6, 30), 3);

        Assert.That(AgentConditionLedgerPolicy.LedgerGroupIdFor(twoGroups), Is.EqualTo(firstGroupId));
        Assert.That(AgentConditionLedgerPolicy.LedgerGroupIdFor(noGroup), Is.Null);
        Assert.That(AgentConditionLedgerPolicy.LedgerGroupIdFor(singleGroupKind), Is.EqualTo(firstGroupId));
    }

    [Test]
    public void EventsWithoutAnOwnGroupIdConcept_DefaultToNoGroupScoping_ThroughTheInterface()
    {
        // Drift and contract alerts are Client-scoped, not Group-scoped; they must keep the
        // unscoped planner broadcast (IAgentTriggerEvent.GroupId defaults to null, GroupIds to empty).
        IAgentTriggerEvent drift = new TargetHoursDriftTriggerEvent(Guid.NewGuid(), "Jane", -20m, "2026-06");
        IAgentTriggerEvent contract = new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), "Jane", new DateOnly(2026, 6, 30), 5);

        Assert.That(drift.GroupId, Is.Null);
        Assert.That(contract.GroupId, Is.Null);
        Assert.That(drift.GroupIds, Is.Empty);
        Assert.That(contract.GroupIds, Is.Empty);
    }

    [Test]
    public void MuteSuggestionEvent_IsTargetedCompanionEvent_WithPerKindDedupKey()
    {
        var userId = Guid.NewGuid();
        IAgentTriggerEvent suggestion = new MuteSuggestionTriggerEvent(AgentTriggerKinds.TargetHoursDrift, userId);
        IAgentTriggerEvent otherKindSuggestion = new MuteSuggestionTriggerEvent(AgentTriggerKinds.UnstaffedShift, userId);

        Assert.That(suggestion.Kind, Is.EqualTo(AgentTriggerKinds.MuteSuggestion));
        Assert.That(suggestion.PlannersOnly, Is.False);
        Assert.That(suggestion.AdminOnly, Is.False);
        Assert.That(suggestion.TargetUserId, Is.EqualTo(userId));
        Assert.That(suggestion.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
        Assert.That(suggestion.Summary, Is.EqualTo("i18n:assistant.proactive.muteSuggestion"));
        Assert.That(suggestion.SummaryParams, Is.Not.Null);
        Assert.That(suggestion.SummaryParams!["kind"], Is.EqualTo(AgentTriggerKinds.TargetHoursDrift));
        Assert.That(suggestion.DedupKey, Is.EqualTo("mute-suggestion:target_hours_drift"));
        Assert.That(suggestion.DedupKey, Is.Not.EqualTo(otherKindSuggestion.DedupKey));
        Assert.That(suggestion.DedupKey, Is.Not.EqualTo(suggestion.Summary));
        Assert.That(suggestion.ActionRoute, Is.Null);
        Assert.That(suggestion.ActionParams, Is.Null);
    }
}

[TestFixture]
public class AgentTriggerRateLimiterTests
{
    private AgentTriggerRateLimiter _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new AgentTriggerRateLimiter(TimeProvider.System);
    }

    [Test]
    public void FreshUser_HasFullDailyBudget()
    {
        Assert.That(_sut.ShouldFire("user-a", "kind"), Is.True);
        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(5));
    }

    [Test]
    public void RecordFire_DecrementsBudget()
    {
        _sut.RecordFire("user-a", "kind");

        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(4));
    }

    [Test]
    public void ExceededBudget_ShouldFireReturnsFalse()
    {
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", "kind");
        }

        Assert.That(_sut.ShouldFire("user-a", "kind"), Is.False);
        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(0));
    }

    [Test]
    public void IndependentKindsHaveIndependentBudgets()
    {
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", "kind-1");
        }

        Assert.That(_sut.ShouldFire("user-a", "kind-1"), Is.False);
        Assert.That(_sut.ShouldFire("user-a", "kind-2"), Is.True);
    }

    [Test]
    public void CuriosityKind_IsCappedAtOnePerDay()
    {
        Assert.That(_sut.GetRemainingBudget("user-a", AgentTriggerKinds.CuriosityQuestion), Is.EqualTo(1));

        _sut.RecordFire("user-a", AgentTriggerKinds.CuriosityQuestion);

        Assert.That(_sut.ShouldFire("user-a", AgentTriggerKinds.CuriosityQuestion), Is.False);
        Assert.That(_sut.GetRemainingBudget("user-a", AgentTriggerKinds.CuriosityQuestion), Is.EqualTo(0));
    }

    /// <summary>
    /// AgentConditionDigestService (Etappe 3h) relies on daily_digest never competing with the 13 other
    /// trigger kinds for a user's 5/day budget. It has no entry in PerKindDailyBudget, so - like any
    /// other unlisted kind - it falls back to the same default and is keyed independently by
    /// (userId, triggerKind), exactly as IndependentKindsHaveIndependentBudgets already proves generically.
    /// </summary>
    [Test]
    public void DailyDigestKind_HasTheDefaultBudget_IndependentOfEveryOtherKind()
    {
        Assert.That(_sut.GetRemainingBudget("user-a", AgentTriggerKinds.DailyDigest), Is.EqualTo(5));

        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", AgentTriggerKinds.OpenOrder);
        }

        Assert.That(_sut.ShouldFire("user-a", AgentTriggerKinds.OpenOrder), Is.False);
        Assert.That(_sut.ShouldFire("user-a", AgentTriggerKinds.DailyDigest), Is.True);
        Assert.That(_sut.GetRemainingBudget("user-a", AgentTriggerKinds.DailyDigest), Is.EqualTo(5));
    }
}
