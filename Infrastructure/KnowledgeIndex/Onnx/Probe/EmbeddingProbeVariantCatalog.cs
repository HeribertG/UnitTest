// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Maps a probe variant name to an embedding runtime profile. Only the arena, the memory pattern and
/// the intra-op thread count are varied: the fp16 export cannot be loaded at ORT_ENABLE_ALL, so the
/// graph optimization level is load-bearing rather than a knob (see OnnxSessionOptionsFactory).
/// </summary>
/// <param name="name">Variant name as passed in PROBE_VARIANT.</param>
/// <param name="threads">IntraOpNumThreads for the session.</param>
/// <param name="gate">MaxConcurrentRuns; 0 disables the gate.</param>
public static class EmbeddingProbeVariantCatalog
{
    // Frugal is what production runs today: arena off, pattern off, BASIC, one thread.
    public const string Frugal = "frugal";
    public const string FrugalThreads = "frugal-threads";
    public const string ArenaOn = "arena-on";
    public const string ArenaOnThreads = "arena-on-threads";

    // Arena on, memory pattern off. Only meaningful on the bulk path: the memory pattern is cached per
    // input shape, and a full index pass runs ~31 chunks whose maxLen is set by whatever landed in the
    // chunk. This variant isolates whether those cached per-shape plans, rather than the arena itself,
    // drive the bulk peak.
    public const string ArenaOnPatternOff = "arena-on-pattern-off";
    public const string ArenaOnPatternOffThreads = "arena-on-pattern-off-threads";

    // Arena on, plus the arena shrinkage run option on the BULK path only. The arena's drawback on the
    // bulk path is that it keeps its high-water mark resident (measured 2026-09-05: 2058 MB against
    // frugal's 1149 MB after the same pass); shrinking after every chunk asks it to give the free
    // regions back. The query path of this same variant runs without the run option, so a query probe
    // on this name has to reproduce arena-on-threads exactly.
    public const string ArenaOnShrinkBulkThreads = "arena-on-shrink-bulk-threads";

    // Control: the level the XML doc of OnnxSessionOptionsFactory says the fp16 export cannot load at
    // (measured on ORT 1.27.1). If this variant does NOT fail on the ORT version under test, either the
    // restriction is gone or CreateSessionOptions is not reaching the session, and every other row here
    // needs re-reading before it is believed.
    public const string OptimizeAll = "opt-all";

    public static readonly string[] All =
    [
        Frugal, FrugalThreads, ArenaOn, ArenaOnThreads, ArenaOnPatternOff, ArenaOnPatternOffThreads,
        ArenaOnShrinkBulkThreads, OptimizeAll
    ];

    private const int SingleThread = 1;
    private const bool NoShrink = false;
    private const bool ShrinkBulk = true;

    public static OnnxEmbeddingRuntimeProfile Resolve(string name, int threads, int gate)
    {
        return name switch
        {
            Frugal => new(() => Session(arena: false, pattern: false, SingleThread), NoShrink, gate),
            FrugalThreads => new(() => Session(arena: false, pattern: false, threads), NoShrink, gate),
            ArenaOn => new(() => Session(arena: true, pattern: true, SingleThread), NoShrink, gate),
            ArenaOnThreads => new(() => Session(arena: true, pattern: true, threads), NoShrink, gate),
            ArenaOnPatternOff => new(() => Session(arena: true, pattern: false, SingleThread), NoShrink, gate),
            ArenaOnPatternOffThreads => new(() => Session(arena: true, pattern: false, threads), NoShrink, gate),
            ArenaOnShrinkBulkThreads => new(() => Session(arena: true, pattern: true, threads), ShrinkBulk, gate),
            OptimizeAll => new(() => Session(arena: false, pattern: false, SingleThread, GraphOptimizationLevel.ORT_ENABLE_ALL), NoShrink, gate),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown embedding probe variant"),
        };
    }

    private static SessionOptions Session(
        bool arena,
        bool pattern,
        int threads,
        GraphOptimizationLevel level = GraphOptimizationLevel.ORT_ENABLE_BASIC) => new()
    {
        EnableCpuMemArena = arena,
        EnableMemoryPattern = pattern,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = level,
        InterOpNumThreads = 1,
        IntraOpNumThreads = threads,
    };
}
