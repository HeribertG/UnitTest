#!/usr/bin/env python3
"""Tabulates onnx-memory-probe JSONL results and checks neutrality against a reference run.

Usage: onnx-memory-probe-summary.py [results.jsonl] [reference-variant]

The reference variant defaults to "baseline" (reranker phases). For the embedding phases pass
"frugal", which is the variant production runs today. Round0Scores holds reranker scores in the
reranker rows and the first components of the round-0 query vector in the embedding rows; the
element-wise max absolute delta is meaningful for both.
"""
import json
import sys

SCORE_TOLERANCE = 1e-4

path = sys.argv[1] if len(sys.argv) > 1 else "/tmp/onnx-memory-probe/results.jsonl"
reference_variant = sys.argv[2] if len(sys.argv) > 2 else "baseline"
rows = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]

baseline = next((r for r in rows if r["Variant"] == reference_variant and r["Parallel"] == 1 and r["Gate"] == 0), None)
if baseline is None:
    print(f"note: no reference row for variant '{reference_variant}' with parallel=1 gate=0 - "
          f"neutrality column stays empty\n")

header = ("variant", "par", "gate", "thr", "base", "load", "warm", "rounds", "gc", "PEAK", "p50ms", "p95ms", "max", "fail", "maxDelta")
print("{:<26}{:>4}{:>5}{:>4}{:>6}{:>7}{:>7}{:>8}{:>7}{:>7}{:>8}{:>8}{:>8}{:>6}{:>11}".format(*header))
for r in rows:
    delta = ""
    if baseline and r["Round0Scores"] and baseline["Round0Scores"]:
        delta = "{:.2e}".format(max(abs(a - b) for a, b in zip(r["Round0Scores"], baseline["Round0Scores"])))
    print("{:<26}{:>4}{:>5}{:>4}{:>6.0f}{:>7.0f}{:>7.0f}{:>8.0f}{:>7.0f}{:>7.0f}{:>8.0f}{:>8.0f}{:>8.0f}{:>6}{:>11}".format(
        r["Variant"], r["Parallel"], r["Gate"], r["Threads"], r["RssBaselineMb"],
        r["RssAfterLoadMb"], r["RssAfterWarmupMb"], r["RssAfterRoundsMb"], r["RssAfterGcMb"], r["PeakRssMb"],
        r["LatencyP50Ms"], r["LatencyP95Ms"], r["LatencyMaxMs"], "yes" if r["Failed"] else "", delta))

bulk_rows = [r for r in rows if "CorpusSize" in r]
if bulk_rows:
    print("\nbulk path (one EmbedBatchAsync over the whole corpus, repeated):")
    bulk_header = ("variant", "thr", "corpus", "chunks", "atCap", "tokMax", "load", "peak1", "PEAK", "bulk ms series", "rss series")
    print("{:<24}{:>4}{:>8}{:>8}{:>7}{:>8}{:>7}{:>8}{:>8}  {:<26}{}".format(*bulk_header))
    for r in bulk_rows:
        series_ms = " ".join("{:.0f}".format(v) for v in r["BulkMs"])
        series_rss = " ".join("{:.0f}".format(v) for v in r["BulkRssMb"])
        series_peak = " ".join("{:.0f}".format(v) for v in r["BulkPeakMb"])
        print("{:<24}{:>4}{:>8}{:>8}{:>7}{:>8}{:>7.0f}{:>8.0f}{:>8.0f}  {:<26}{}".format(
            r["Variant"], r["Threads"], r["CorpusSize"], r["ChunkCount"], r["ChunksAtCap"], r["TokenMax"],
            r["RssAfterLoadMb"], r["PeakAfterBulk1Mb"], r["PeakRssMb"], series_ms, series_rss))
        print("{:<24}{}".format("", "  hwm series: " + series_peak))

if baseline:
    bad = [r["Variant"] for r in rows if r["Round0Scores"] and
           max(abs(a - b) for a, b in zip(r["Round0Scores"], baseline["Round0Scores"])) > SCORE_TOLERANCE]
    print(f"\nneutrality vs {reference_variant}: " + ("OK" if not bad else "VIOLATED by " + ", ".join(sorted(set(bad)))))
for r in rows:
    if r["Failed"]:
        print(f"FAILED {r['Variant']} par={r['Parallel']} gate={r['Gate']}: {r['Error']}")
