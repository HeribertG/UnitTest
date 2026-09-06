// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Microsoft.ML.OnnxRuntime;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Maps a probe variant name to a reranker runtime profile. Variants that need an environment
/// allocator (memory cap, extend strategy) register it on OrtEnv first; that registration is
/// process-global and only allowed once, which is why every variant runs in its own process.
/// </summary>
/// <param name="name">Variant name as passed in PROBE_VARIANT.</param>
/// <param name="threads">IntraOpNumThreads for the session.</param>
/// <param name="gate">MaxConcurrentRuns; 0 disables the gate.</param>
public static class ProbeVariantCatalog
{
    public const string Baseline = "baseline";
    public const string PatternOff = "pattern-off";
    public const string ArenaOff = "arena-off";
    public const string Shrink = "shrink";
    public const string ShrinkSameAsRequested = "shrink-same-as-requested";
    public const string Cap256 = "cap-256";
    public const string Cap512 = "cap-512";

    // Control: a cap far below any plausible activation footprint. If this variant does NOT fail,
    // the environment allocator is not the one serving the session and the cap-* results are void.
    public const string Cap16 = "cap-16";

    public static readonly string[] All = [Baseline, PatternOff, ArenaOff, Shrink, ShrinkSameAsRequested, Cap256, Cap512, Cap16];

    private const uint MegabytesToBytes = 1024 * 1024;
    private const uint Cap16Bytes = 16 * MegabytesToBytes;
    private const uint Cap256Bytes = 256 * MegabytesToBytes;
    private const uint Cap512Bytes = 512 * MegabytesToBytes;

    public static OnnxRerankerRuntimeProfile Resolve(string name, int threads, int gate)
    {
        return name switch
        {
            Baseline => Profile(() => Session(arena: true, pattern: true, threads), shrink: false, gate),
            PatternOff => Profile(() => Session(arena: true, pattern: false, threads), shrink: false, gate),
            ArenaOff => Profile(() => Session(arena: false, pattern: false, threads), shrink: false, gate),
            Shrink => Profile(() => Session(arena: true, pattern: true, threads), shrink: true, gate),
            ShrinkSameAsRequested => EnvArena(
                OnnxRuntimeConfigKeys.ArenaNoMemoryCap, OnnxRuntimeConfigKeys.ArenaExtendSameAsRequested, threads, shrink: true, gate),
            Cap256 => EnvArena(Cap256Bytes, OnnxRuntimeConfigKeys.ArenaExtendNextPowerOfTwo, threads, shrink: false, gate),
            Cap512 => EnvArena(Cap512Bytes, OnnxRuntimeConfigKeys.ArenaExtendNextPowerOfTwo, threads, shrink: false, gate),
            Cap16 => EnvArena(Cap16Bytes, OnnxRuntimeConfigKeys.ArenaExtendNextPowerOfTwo, threads, shrink: false, gate),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown probe variant"),
        };
    }

    private static OnnxRerankerRuntimeProfile Profile(Func<SessionOptions> factory, bool shrink, int gate) =>
        new(factory, shrink, gate);

    private static OnnxRerankerRuntimeProfile EnvArena(uint maxMemory, int extendStrategy, int threads, bool shrink, int gate)
    {
        var arenaCfg = new OrtArenaCfg(
            maxMemory,
            extendStrategy,
            OnnxRuntimeConfigKeys.ArenaRuntimeDefault,
            OnnxRuntimeConfigKeys.ArenaRuntimeDefault);
        OrtEnv.Instance().CreateAndRegisterAllocator(OrtMemoryInfo.DefaultInstance, arenaCfg);

        return new OnnxRerankerRuntimeProfile(
            () =>
            {
                var options = Session(arena: true, pattern: true, threads);
                options.AddSessionConfigEntry(OnnxRuntimeConfigKeys.SessionUseEnvAllocators, OnnxRuntimeConfigKeys.Enabled);
                return options;
            },
            shrink,
            gate);
    }

    private static SessionOptions Session(bool arena, bool pattern, int threads) => new()
    {
        EnableCpuMemArena = arena,
        EnableMemoryPattern = pattern,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        InterOpNumThreads = 1,
        IntraOpNumThreads = threads,
    };
}
