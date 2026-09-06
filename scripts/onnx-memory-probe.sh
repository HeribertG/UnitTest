#!/usr/bin/env bash
# Runs an ONNX memory probe matrix, one variant per process, appending to a JSONL file.
# Usage (from WSL):  scripts/onnx-memory-probe.sh <phase> [out.jsonl]
#   phase A  : reranker, all variants, single caller (levers 1 + 2)
#   phase B  : reranker, baseline + $PROBE_BEST (or the explicit list $PROBE_B_VARIANTS) with 8 parallel
#              callers and gate 0 / 2 / 1 (lever 3); PROBE_THREADS pins the intra-op thread count
#   phase T  : reranker baseline with 2 threads (production container = cpus 1.5 -> ProcessorCount 2)
#   phase E  : embedding, all variants incl. the opt-all control, single caller
#   phase EB : embedding, $PROBE_EB_VARIANTS (default "frugal arena-on") with 8 parallel callers and
#              gate 0 / 2 / 1
#   phase BULK: embedding bulk path - one EmbedBatchAsync call over the whole index corpus, the way
#              KnowledgeIndexSynchronizer embeds an empty index. $PROBE_BULK_VARIANTS selects the
#              variants (default: all four), PROBE_CORPUS the corpus size, PROBE_BULKS the number of
#              passes over it. arena-on-threads runs with PROBE_THREADS, the others pin one thread.
# PROBE_ROUNDS overrides the number of measured rounds (default 10 in the fixture).
set -euo pipefail

PHASE="${1:?phase A|B|T|E|EB}"
OUT="${2:-/tmp/onnx-memory-probe/results.jsonl}"
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TEST_DLL="$PROJECT_DIR/bin/Release/net10.0/Klacks.UnitTest.dll"
RERANKER_FILTER="FullyQualifiedName~OnnxRerankerMemoryProbeTests"
EMBEDDING_FILTER="FullyQualifiedName~OnnxEmbeddingMemoryProbeTests"
EMBEDDING_BULK_FILTER="FullyQualifiedName~OnnxEmbeddingBulkMemoryProbeTests"
FILTER="$RERANKER_FILTER"
# In this WSL, plain "dotnet" is a symlink to the Windows dotnet.exe; point PROBE_DOTNET at a native
# Linux SDK (e.g. ~/.dotnet-linux/dotnet) so the probe really runs on Linux/glibc. The native SDK must
# also be first on PATH because vstest launches the test host through "dotnet" from PATH.
DOTNET="${PROBE_DOTNET:-dotnet}"
export PATH="$(dirname "$(readlink -f "$(command -v "$DOTNET")")"):$PATH"
# "dotnet test" of the 10.0.400 SDK ran silently on this Linux setup (no output, no diag log, exit 0);
# "dotnet vstest" on the built assembly works, so the runner drives vstest directly. Build first with
#   dotnet build -c Release
[[ -f "$TEST_DLL" ]] || { echo "missing $TEST_DLL - build Klacks.UnitTest in Release first" >&2; exit 1; }
VARIANTS=(baseline pattern-off arena-off shrink shrink-same-as-requested cap-256 cap-512 cap-16)
EMBEDDING_VARIANTS=(frugal frugal-threads arena-on arena-on-threads opt-all)
# The bulk default uses the -threads variants: only those read PROBE_THREADS, the plain names pin one
# thread inside the catalog. Two threads match the production container (cpus 1.5 -> ProcessorCount 2).
EMBEDDING_BULK_VARIANTS=(frugal-threads arena-on-threads arena-on-pattern-off-threads)

mkdir -p "$(dirname "$OUT")"
export PROBE_OUT="$OUT"
export PROBE_MODELS_ROOT="${PROBE_MODELS_ROOT:-/tmp/klacks-test-models}"
[[ -n "${PROBE_ROUNDS:-}" ]] && export PROBE_ROUNDS

run_one() {
  local variant="$1" parallel="$2" gate="$3" threads="$4"
  echo "=== $variant parallel=$parallel gate=$gate threads=$threads ==="
  # Host headroom is logged per run: a probe perturbed by another process competing for the 7.7 GB box
  # must be identifiable afterwards rather than mysterious.
  echo "--- host $(grep MemAvailable /proc/meminfo) at $(date +%H:%M:%S)"
  PROBE_VARIANT="$variant" PROBE_PARALLEL="$parallel" PROBE_GATE="$gate" PROBE_THREADS="$threads" \
    "$DOTNET" vstest "$TEST_DLL" --TestCaseFilter:"$FILTER" \
      --logger:"console;verbosity=normal" 2>&1 | grep -E 'round [0-9]+:|shapes:|FAILED|Passed |Failed |Test Run' || true
  sleep 2
}

# PROBE_THREADS overrides the core count for phases A and B (production container: cpus 1.5 -> 2).
THREADS="${PROBE_THREADS:-$(nproc)}"

case "$PHASE" in
  A)
    for v in "${VARIANTS[@]}"; do run_one "$v" 1 0 "$THREADS"; done ;;
  B)
    # PROBE_B_VARIANTS: space-separated list; defaults to baseline plus the phase-A winner(s) in PROBE_BEST.
    LIST="${PROBE_B_VARIANTS:-baseline ${PROBE_BEST:?set PROBE_BEST to the winning variant(s) of phase A}}"
    for v in $LIST; do
      for g in 0 2 1; do run_one "$v" 8 "$g" "$THREADS"; done
    done ;;
  T)
    run_one baseline 1 0 2 ;;
  E)
    FILTER="$EMBEDDING_FILTER"
    LIST="${PROBE_E_VARIANTS:-${EMBEDDING_VARIANTS[*]}}"
    for v in $LIST; do run_one "$v" 1 0 "$THREADS"; done ;;
  EB)
    FILTER="$EMBEDDING_FILTER"
    # Split across several invocations by passing PROBE_EB_VARIANTS: every process appends its own
    # JSONL line, so a run cut short costs only the variants it did not reach.
    LIST="${PROBE_EB_VARIANTS:-frugal arena-on}"
    for v in $LIST; do
      for g in ${PROBE_EB_GATES:-0 2 1}; do run_one "$v" 8 "$g" "$THREADS"; done
    done ;;
  BULK)
    FILTER="$EMBEDDING_BULK_FILTER"
    [[ -n "${PROBE_CORPUS:-}" ]] && export PROBE_CORPUS
    [[ -n "${PROBE_BULKS:-}" ]] && export PROBE_BULKS
    LIST="${PROBE_BULK_VARIANTS:-${EMBEDDING_BULK_VARIANTS[*]}}"
    # Only the -threads variant reads PROBE_THREADS; the others pin one thread inside the catalog, so
    # passing $THREADS everywhere is harmless and keeps the JSONL column honest for the one that uses it.
    for v in $LIST; do run_one "$v" 1 0 "$THREADS"; done ;;
  *) echo "unknown phase $PHASE" >&2; exit 1 ;;
esac

echo "results: $OUT"
