// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Builds the passage corpus KnowledgeIndexSynchronizer hands to EmbedBatchAsync on an empty index:
/// 489 rows (464 skills + 25 recipes, measured 2026-08-20), with the character-length distribution of
/// knowledge_index reused from RerankerProbeCandidates and fanned out cyclically with a per-index
/// jitter so successive chunks present different (batch, maxLen) shapes. The corpus is a pure function
/// of its size: every bulk pass in a probe run gets byte-identical texts, so a rising high-water mark
/// between passes can only be allocator behaviour and never a new input shape.
/// </summary>
/// <param name="size">Number of passages to build; production value is the full index row count.</param>
public static class EmbeddingBulkProbeCorpus
{
    public const int DefaultSize = 489;

    private static readonly int[] Lengths =
        [3542, 2100, 1500, 1200, 1050, 980, 950, 930, 915, 903, 890, 870, 850, 820, 780, 740, 700, 650, 600, 540, 480, 410, 350, 220, 95];

    private static readonly string[] Fillers =
    [
        "create employee mitarbeiter anlegen employé collaboratore empleado pracownik zaměstnanec munkavállaló medarbejder werknemer ",
        "schicht dienstplan planning turno shift roster horaire orario dienst rooster vagt vakt zmiana směna műszak ",
        "abwesenheit ferien urlaub absence congé assenza ferie vacanza vakantie verlof ferie urlop dovolená szabadság ",
        "spesen kosten expense frais costi gastos onkosten udgifter koszty náklady költségek reisekosten verpflegung ",
        "gruppe abteilung group équipe gruppo reparto grupo groep afdeling gruppe grupa oddělení csoport bereich ",
    ];

    private const int JitterStride = 7;
    private const int JitterMaxChars = 60;

    public static string[] Build(int size)
    {
        return Enumerable.Range(0, size).Select(index =>
        {
            var target = Math.Max(1, Lengths[index % Lengths.Length] - index * JitterStride % JitterMaxChars);
            var filler = Fillers[index % Fillers.Length];
            var text = string.Concat(Enumerable.Repeat(filler, target / filler.Length + 1));
            return text[..target];
        }).ToArray();
    }
}
