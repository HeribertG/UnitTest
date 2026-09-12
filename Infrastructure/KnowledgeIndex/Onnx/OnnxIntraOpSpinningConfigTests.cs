// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the intra-op spinning switch: its default (off, measured 2026-09-12), the config key that
/// overrides it, and the "1"/"0" wire value handed to the runtime. Loads no model — the score-neutral
/// behaviour of the option itself is covered by the benchmark data, not by a test.
/// </summary>

using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Microsoft.Extensions.Configuration;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
public class OnnxIntraOpSpinningConfigTests
{
    private const string ConfigKey = "KnowledgeIndex:OnnxAllowIntraOpSpinning";

    private static IConfiguration Configuration(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value != null)
        {
            values[KnowledgeIndexConstants.OnnxAllowIntraOpSpinningConfigKey] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Test]
    public void ConfigKey_HasTheDocumentedName()
    {
        // Arrange & Act & Assert
        KnowledgeIndexConstants.OnnxAllowIntraOpSpinningConfigKey.ShouldBe(ConfigKey);
    }

    // The whole point of the change: spinning is off unless a host measured otherwise.
    [Test]
    public void Unset_ResolvesToSpinningOff()
    {
        // Arrange & Act
        var allow = ServiceCollectionExtensions.ResolveOnnxAllowIntraOpSpinning(Configuration(null));

        // Assert
        allow.ShouldBeFalse();
        KnowledgeIndexConstants.DefaultOnnxAllowIntraOpSpinning.ShouldBeFalse();
    }

    [TestCase("true", true)]
    [TestCase("True", true)]
    [TestCase("false", false)]
    public void ParsableValue_IsHonoured(string configured, bool expected)
    {
        // Arrange & Act
        var allow = ServiceCollectionExtensions.ResolveOnnxAllowIntraOpSpinning(Configuration(configured));

        // Assert
        allow.ShouldBe(expected);
    }

    // A typo must not silently re-enable the 5-second spin-wait.
    [Test]
    public void UnparsableValue_FallsBackToTheDefault()
    {
        // Arrange & Act
        var allow = ServiceCollectionExtensions.ResolveOnnxAllowIntraOpSpinning(Configuration("yes"));

        // Assert
        allow.ShouldBe(KnowledgeIndexConstants.DefaultOnnxAllowIntraOpSpinning);
    }

    [Test]
    public void WireValue_MatchesTheRuntimesExpectedStrings()
    {
        // Arrange & Act & Assert
        OnnxSessionOptionsFactory.IntraOpSpinningValue(true).ShouldBe("1");
        OnnxSessionOptionsFactory.IntraOpSpinningValue(false).ShouldBe("0");
        OnnxRuntimeConfigKeys.AllowIntraOpSpinning.ShouldBe("session.intra_op.allow_spinning");
    }

    // The reranker is the only consumer; the profile must carry the flag and keep every other knob.
    [Test]
    public void RerankerProfile_KeepsEveryOtherKnobOfTheDefault()
    {
        // Arrange & Act
        var profile = OnnxRerankerRuntimeProfile.ForIntraOpSpinning(true);

        // Assert
        profile.ShrinkArenaAfterRun.ShouldBe(OnnxRerankerRuntimeProfile.Default.ShrinkArenaAfterRun);
        profile.MaxConcurrentRuns.ShouldBe(OnnxRerankerRuntimeProfile.Default.MaxConcurrentRuns);
        profile.CreateSessionOptions.ShouldNotBe(OnnxRerankerRuntimeProfile.Default.CreateSessionOptions);
    }
}
