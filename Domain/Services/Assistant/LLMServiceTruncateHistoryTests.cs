// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMService.TruncateHistory: the no-summary path stays byte-identical (same
/// list instance returned when it already fits), legacy free-text summaries render exactly as
/// before inside the wrapper markers, and structured summaries render as a compact section block.
/// Also covers the count and token-budget truncation limits.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceTruncateHistoryTests
{
    private const int LargeBudget = 100_000;

    private const string SummaryOpenMarker = "[Conversation Summary (earlier messages)]";
    private const string SummaryCloseMarker = "[/Conversation Summary]";

    private static ProviderLLMMessage Msg(string role, string content) => new()
    {
        Role = role,
        Content = content
    };

    private static List<ProviderLLMMessage> History(int count)
    {
        var list = new List<ProviderLLMMessage>();
        for (var i = 0; i < count; i++)
        {
            list.Add(Msg(i % 2 == 0 ? "user" : "assistant", $"message-{i}"));
        }
        return list;
    }

    [Test]
    public void NoSummary_WithinBudget_ReturnsSameInstance()
    {
        var history = History(3);

        var result = LLMService.TruncateHistory(history, LargeBudget, null);

        result.ShouldBeSameAs(history);
    }

    [Test]
    public void NoSummary_WhitespaceSummary_ReturnsSameInstance()
    {
        var history = History(3);

        var result = LLMService.TruncateHistory(history, LargeBudget, "   ");

        result.ShouldBeSameAs(history);
    }

    [Test]
    public void FreeTextSummary_InsertsVerbatimWrappedSystemMessageFirst()
    {
        var history = History(3);
        const string freeText = "The user is a nurse in Bern and works night shifts.";

        var result = LLMService.TruncateHistory(history, LargeBudget, freeText);

        result.Count.ShouldBe(4);
        result[0].Role.ShouldBe("system");
        result[0].Content.ShouldBe($"{SummaryOpenMarker}\n{freeText}\n{SummaryCloseMarker}");
        result[1].Content.ShouldBe("message-0");
        result[3].Content.ShouldBe("message-2");
    }

    [Test]
    public void StructuredSummary_RendersSectionBlockNotRawJson()
    {
        var history = History(2);
        const string json =
            "{\"openTasks\":[\"Finish the roster\"],\"facts\":[\"Prefers mornings\"]}";

        var result = LLMService.TruncateHistory(history, LargeBudget, json);

        result[0].Role.ShouldBe("system");
        result[0].Content.ShouldStartWith(SummaryOpenMarker);
        result[0].Content.ShouldEndWith(SummaryCloseMarker);
        result[0].Content.ShouldContain("Open tasks:");
        result[0].Content.ShouldContain("- Finish the roster");
        result[0].Content.ShouldContain("Facts:");
        result[0].Content.ShouldNotContain("\"openTasks\"");
    }

    [Test]
    public void ManyMessages_NoSummary_TruncatesToMaxAndInsertsMarker()
    {
        var history = History(25);

        var result = LLMService.TruncateHistory(history, LargeBudget, null);

        result.Count.ShouldBe(21);
        result[0].Role.ShouldBe("system");
        result[0].Content.ShouldBe(LLMService.TruncationNotice);
        result[^1].Content.ShouldBe("message-24");
    }

    [Test]
    public void TightBudget_KeepsNewestDropsOldest()
    {
        var history = new List<ProviderLLMMessage>
        {
            Msg("user", new string('a', 40)),
            Msg("assistant", new string('b', 40)),
            Msg("user", new string('c', 40)),
            Msg("assistant", new string('d', 40)),
            Msg("user", "newest-kept")
        };

        var result = LLMService.TruncateHistory(history, 25, null);

        result[0].Role.ShouldBe("system");
        result[0].Content.ShouldBe(LLMService.TruncationNotice);
        result[^1].Content.ShouldBe("newest-kept");
        result.ShouldNotContain(m => m.Content == new string('a', 40));
    }

    [Test]
    public void AdaptiveMaxHistoryMessages_DefaultsTo20_WhenNotPassed()
    {
        var history = History(25);

        var result = LLMService.TruncateHistory(history, LargeBudget, null);

        result.Count.ShouldBe(21);
        result[0].Content.ShouldBe(LLMService.TruncationNotice);
    }

    [Test]
    public void AdaptiveMaxHistoryMessages_TighterCap_TruncatesToSmallerCount()
    {
        var history = History(25);

        var result = LLMService.TruncateHistory(history, LargeBudget, null, maxHistoryMessages: 8);

        result.Count.ShouldBe(9);
        result[0].Role.ShouldBe("system");
        result[0].Content.ShouldBe(LLMService.TruncationNotice);
        result[^1].Content.ShouldBe("message-24");
    }

    [Test]
    public void AdaptiveMaxHistoryMessages_LargerCap_KeepsMoreMessages()
    {
        var history = History(45);

        var result = LLMService.TruncateHistory(history, LargeBudget, null, maxHistoryMessages: 40);

        result.Count.ShouldBe(41);
        result[0].Content.ShouldBe(LLMService.TruncationNotice);
    }

    // The notice sits at position 0 of every truncated history, i.e. inside the prompt prefix that
    // providers with automatic prefix caching hash. It must therefore be byte-identical no matter how
    // many messages were dropped — a turn-varying count invalidated the cache on every call.
    [Test]
    public void TruncationNotice_IsIdenticalAcrossDifferentTruncationSizes()
    {
        var small = LLMService.TruncateHistory(History(25), LargeBudget, null, maxHistoryMessages: 8);
        var large = LLMService.TruncateHistory(History(45), LargeBudget, null, maxHistoryMessages: 40);

        small[0].Content.ShouldBe(large[0].Content);
        small[0].Content.ShouldNotContain("8");
        small[0].Content.ShouldNotContain("25");
    }
}
