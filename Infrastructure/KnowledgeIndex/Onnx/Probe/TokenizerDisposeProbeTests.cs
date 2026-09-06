// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Constants;
using NUnit.Framework;
using Tokenizers.DotNet;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Checks whether Tokenizers.DotNet actually releases the native tokenizer handle on Dispose, by
/// repeatedly constructing and disposing a tokenizer and watching resident memory. Reporting only;
/// never asserts on the growth number, since the decision rule is applied by a human reading the log.
/// </summary>
[TestFixture]
[Explicit("Checks whether Tokenizers.DotNet actually releases the native tokenizer on Dispose.")]
[Category("MemoryProbe")]
public class TokenizerDisposeProbeTests
{
    private const int Iterations = 20;

    [Test]
    public void ConstructAndDispose_RepeatedlyDoesNotGrowResidentMemory_Reranker()
    {
        var tokenizerPath = Path.Combine(Path.GetTempPath(), "klacks-test-models",
            KnowledgeIndexConstants.RerankerModelName, KnowledgeIndexConstants.RerankerTokenizerFileName);
        MeasureConstructDisposeCycles(tokenizerPath);
    }

    [Test]
    public void ConstructAndDispose_RepeatedlyDoesNotGrowResidentMemory_Embedding()
    {
        var tokenizerPath = Path.Combine(Path.GetTempPath(), "klacks-test-models",
            KnowledgeIndexConstants.EmbeddingModelName, KnowledgeIndexConstants.EmbeddingTokenizerFileName);
        MeasureConstructDisposeCycles(tokenizerPath);
    }

    private static void MeasureConstructDisposeCycles(string tokenizerPath)
    {
        if (!File.Exists(tokenizerPath))
        {
            Assert.Ignore($"Tokenizer file not found under {tokenizerPath}.");
        }

        using (var warm = new Tokenizer(vocabPath: tokenizerPath)) { warm.Encode("warm"); }
        var before = ProcessMemoryProbe.Read();

        for (var i = 0; i < Iterations; i++)
        {
            using var tokenizer = new Tokenizer(vocabPath: tokenizerPath);
            tokenizer.Encode("probe text");
        }

        var after = ProcessMemoryProbe.Read();
        TestContext.WriteLine($"rss {before.ResidentMb:F0} -> {after.ResidentMb:F0} MB over {Iterations} construct/dispose cycles");
        Assert.Pass();
    }
}
