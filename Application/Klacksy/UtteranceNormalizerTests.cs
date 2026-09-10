// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using Shouldly;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using NUnit.Framework;

[TestFixture]
public class UtteranceNormalizerTests
{
    private IUtteranceNormalizer _sut = null!;

    [SetUp]
    public void SetUp() => _sut = new UtteranceNormalizer();

    [TestCase("Klacksy, zeig mir LLM Provider", "de", "llm provider", true)]
    [TestCase("Hey Klacksy wo ist LLM", "en", "wo ist llm", true)]
    [TestCase("hallo klacksy settings bitte", "de", "settings", true)]
    [TestCase("Salut Klacksy", "fr", "", true)]
    [TestCase("klaxy llm provider", "de", "llm provider", true)]
    [TestCase("LLM Provider", "de", "llm provider", false)]
    [TestCase("초과근무 보여줘", "ko", "초과근무", false)]
    [TestCase("초과근무 좀 보여 주세요", "ko", "초과근무", false)]
    [TestCase("마을", "ko", "마을", false)]
    public void Normalize_strips_wake_words_and_fillers(string raw, string locale, string expected, bool stripped)
    {
        var result = _sut.Normalize(raw, locale);
        result.Normalized.ShouldBe(expected);
        result.WakeWordStripped.ShouldBe(stripped);
        result.Original.ShouldBe(raw);
    }

    [Test]
    public void Normalize_flags_empty_after_stripping()
    {
        var result = _sut.Normalize("Klacksy", "de");
        result.IsEmptyAfterNormalization.ShouldBeTrue();
    }

    [Test]
    public void Normalize_preserves_input_with_no_wake_word_and_no_leading_prefix()
    {
        var result = _sut.Normalize("mitarbeiterliste öffnen", "de");
        result.Normalized.ShouldBe("mitarbeiterliste öffnen");
        result.WakeWordStripped.ShouldBeFalse();
    }

    [TestCase("zeig mir mal die uploadfläche", "de", "uploadfläche")]
    [TestCase("wo ist die Einstellung", "de", "einstellung")]
    [TestCase("show me the overtime", "en", "overtime")]
    public void Normalize_strips_leading_command_prefix_and_article(string raw, string locale, string expected)
    {
        var result = _sut.Normalize(raw, locale);
        result.Normalized.ShouldBe(expected);
    }

    [Test]
    public void Normalize_does_not_strip_prefix_that_only_matches_mid_word()
    {
        var result = _sut.Normalize("öffnen sie das protokoll", "de");
        result.Normalized.ShouldBe("öffnen sie das protokoll");
    }

    [Test]
    public void Normalize_never_strips_input_down_to_empty()
    {
        var result = _sut.Normalize("zeig mir", "de");
        result.Normalized.ShouldBe("zeig mir");
        result.IsEmptyAfterNormalization.ShouldBeFalse();
    }
}
