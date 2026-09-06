#!/usr/bin/env bash
# Stage 2 of the ONNX embedding memory investigation: arena on plus arena shrinkage on the bulk path
# only. Runs the four measurements strictly one after another - the ORT arena, the VmHWM counter and
# the 7.7 GB host are all shared, so two probes at once would measure each other.
#   1) BULK arena-on-shrink-bulk-threads, corpus 160, 2 passes  -> does the shrink give the 909 MB back
#   2) BULK frugal-threads, corpus 160, 4 passes                -> does frugal grow over more passes (open question of 11.3)
#   3) EB   arena-on-shrink-bulk-threads, gate 2, 8 callers     -> is the query path untouched
#   4) EB   arena-on-threads, gate 2, 8 callers                 -> 2-thread control for 3; the 1823 MB
#      reference of section 5 was measured with ONE thread, so 3 has no comparand without this row.
set -euo pipefail

export DOTNET_ROOT="$HOME/.dotnet-linux"
export PATH="$HOME/.dotnet-linux:$PATH"
export PROBE_DOTNET="$HOME/.dotnet-linux/dotnet"
export PROBE_MODELS_ROOT=/tmp/klacks-test-models

RUNNER=/mnt/c/SourceCode/Klacks.UnitTest/scripts/onnx-memory-probe.sh
BULK_OUT=/tmp/onnx-memory-probe/bulk160.jsonl
EB_OUT=/tmp/onnx-memory-probe/stage2-eb.jsonl

cd /mnt/c/SourceCode/Klacks.UnitTest

echo "########## 1) BULK arena-on-shrink-bulk-threads corpus=160 threads=2 bulks=2 $(date +%H:%M:%S)"
PROBE_CORPUS=160 PROBE_THREADS=2 PROBE_BULKS=2 \
  PROBE_BULK_VARIANTS="arena-on-shrink-bulk-threads" "$RUNNER" BULK "$BULK_OUT"

echo "########## 2) BULK frugal-threads corpus=160 threads=2 bulks=4 $(date +%H:%M:%S)"
PROBE_CORPUS=160 PROBE_THREADS=2 PROBE_BULKS=4 \
  PROBE_BULK_VARIANTS="frugal-threads" "$RUNNER" BULK "$BULK_OUT"

echo "########## 3) EB arena-on-shrink-bulk-threads gate=2 threads=2 $(date +%H:%M:%S)"
PROBE_THREADS=2 PROBE_EB_VARIANTS="arena-on-shrink-bulk-threads" PROBE_EB_GATES="2" \
  "$RUNNER" EB "$EB_OUT"

echo "########## 4) EB arena-on-threads gate=2 threads=2 (control) $(date +%H:%M:%S)"
PROBE_THREADS=2 PROBE_EB_VARIANTS="arena-on-threads" PROBE_EB_GATES="2" \
  "$RUNNER" EB "$EB_OUT"

echo "########## done $(date +%H:%M:%S)"
