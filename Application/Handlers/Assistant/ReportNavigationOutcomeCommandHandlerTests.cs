// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the browser-reported navigation outcome handler: only the four browser-observable
/// outcomes (Scrolled/TargetMiss/PermissionDenied/FeatureDisabled) are accepted -
/// suspected-miss is server-detected and never a valid client report. A missing UserId or an
/// unknown outcome is a bad request; a valid report is forwarded to INavigationFeedbackLogger
/// with the utterance truncated to NavigationFeedbackLimits.MaxUtteranceLength.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class ReportNavigationOutcomeCommandHandlerTests
{
    private const string UserId = "0c9f5b51-9c2d-4a2d-8a4f-4b2a2f5f9c2d";
    private const string Route = "/workplace/settings";
    private const string TargetId = "erp-drop-points";
    private const string Locale = "de";

    private INavigationFeedbackLogger _logger = null!;
    private ReportNavigationOutcomeCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = Substitute.For<INavigationFeedbackLogger>();
        _handler = new ReportNavigationOutcomeCommandHandler(_logger);
    }

    private static ReportNavigationOutcomeCommand Command(
        string outcome, string userId = UserId, string? utterance = null) => new()
    {
        UserId = userId,
        Route = Route,
        Target = TargetId,
        Outcome = outcome,
        Locale = Locale,
        Utterance = utterance
    };

    [Test]
    public async Task Handle_throws_when_user_id_is_empty()
    {
        var command = Command(NavigationOutcomeKinds.Scrolled, userId: string.Empty);

        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(command, CancellationToken.None));
    }

    [Test]
    public async Task Handle_throws_when_user_id_is_whitespace()
    {
        var command = Command(NavigationOutcomeKinds.Scrolled, userId: "   ");

        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(command, CancellationToken.None));
    }

    [Test]
    public async Task Handle_throws_when_outcome_is_unknown()
    {
        var command = Command("banana");

        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(command, CancellationToken.None));
    }

    [Test]
    public async Task Handle_throws_when_outcome_is_suspected_miss()
    {
        var command = Command(NavigationOutcomeKinds.SuspectedMiss);

        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(command, CancellationToken.None));
    }

    [Test]
    public async Task Handle_records_a_valid_outcome_and_forwards_the_correct_values_to_the_logger()
    {
        var command = Command(NavigationOutcomeKinds.Scrolled, utterance: "zeige mir die einstellungen");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Recorded.ShouldBeTrue();
        await _logger.Received(1).LogOutcomeAsync(
            "zeige mir die einstellungen",
            Locale,
            TargetId,
            NavigationOutcomeKinds.Scrolled,
            Route,
            Guid.Parse(UserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_truncates_utterance_longer_than_the_max_length()
    {
        var overlong = new string('a', NavigationFeedbackLimits.MaxUtteranceLength + 50);
        var command = Command(NavigationOutcomeKinds.Scrolled, utterance: overlong);

        await _handler.Handle(command, CancellationToken.None);

        await _logger.Received(1).LogOutcomeAsync(
            Arg.Is<string?>(u => u!.Length == NavigationFeedbackLimits.MaxUtteranceLength),
            Locale,
            TargetId,
            NavigationOutcomeKinds.Scrolled,
            Route,
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_records_scrolled_outcome()
    {
        var command = Command(NavigationOutcomeKinds.Scrolled);

        await _handler.Handle(command, CancellationToken.None);

        await _logger.Received(1).LogOutcomeAsync(
            Arg.Any<string?>(), Locale, TargetId, NavigationOutcomeKinds.Scrolled, Route, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_records_target_miss_outcome()
    {
        var command = Command(NavigationOutcomeKinds.TargetMiss);

        await _handler.Handle(command, CancellationToken.None);

        await _logger.Received(1).LogOutcomeAsync(
            Arg.Any<string?>(), Locale, TargetId, NavigationOutcomeKinds.TargetMiss, Route, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_records_permission_denied_outcome()
    {
        var command = Command(NavigationOutcomeKinds.PermissionDenied);

        await _handler.Handle(command, CancellationToken.None);

        await _logger.Received(1).LogOutcomeAsync(
            Arg.Any<string?>(), Locale, TargetId, NavigationOutcomeKinds.PermissionDenied, Route, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_accepts_feature_disabled_outcome()
    {
        var command = Command(NavigationOutcomeKinds.FeatureDisabled);

        Func<Task> act = () => _handler.Handle(command, CancellationToken.None);

        await act.ShouldNotThrowAsync();
    }

    [Test]
    public async Task Handle_records_feature_disabled_outcome()
    {
        var command = Command(NavigationOutcomeKinds.FeatureDisabled);

        await _handler.Handle(command, CancellationToken.None);

        await _logger.Received(1).LogOutcomeAsync(
            Arg.Any<string?>(), Locale, TargetId, NavigationOutcomeKinds.FeatureDisabled, Route, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_keeps_feature_disabled_apart_from_permission_denied()
    {
        var command = Command(NavigationOutcomeKinds.FeatureDisabled);

        await _handler.Handle(command, CancellationToken.None);

        await _logger.DidNotReceive().LogOutcomeAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), NavigationOutcomeKinds.PermissionDenied, Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }
}
