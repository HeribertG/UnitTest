// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Builds the 25-candidate set the reranker sees per request, with the character-length distribution
/// measured in knowledge_index on 2026-08-03 (478 rows, median 903 chars, max 3542). A per-round
/// jitter trims each candidate by a few characters so successive rounds present different
/// (batch, maxLen) input shapes, as real queries do — memory patterns are cached per shape.
/// </summary>
/// <param name="round">Zero-based round index; round 0 is jitter-free so scores compare across variants.</param>
public static class RerankerProbeCandidates
{
    private static readonly int[] Lengths =
        [3542, 2100, 1500, 1200, 1050, 980, 950, 930, 915, 903, 890, 870, 850, 820, 780, 740, 700, 650, 600, 540, 480, 410, 350, 220, 95];

    private const string Filler =
        "create employee mitarbeiter anlegen employé collaboratore empleado pracownik zaměstnanec munkavállaló medarbejder werknemer ";

    private const int JitterRoundStride = 13;
    private const int JitterIndexStride = 3;
    private const int JitterMaxChars = 60;

    public const string Query = "Lege einen neuen Mitarbeiter Hans Muster an";

    public static string[] Build(int round)
    {
        return Lengths.Select((len, index) =>
        {
            var trim = round == 0 ? 0 : (round * JitterRoundStride + index * JitterIndexStride) % JitterMaxChars;
            var target = Math.Max(1, len - trim);
            var text = string.Concat(Enumerable.Repeat(Filler, target / Filler.Length + 1));
            return text[..target];
        }).ToArray();
    }
}
