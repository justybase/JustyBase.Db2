#!/usr/bin/env python3
"""Summarize benchmark-only replay and live block-size diagnostics."""
import csv
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
output = Path(sys.argv[2])
rows = []
for path in sorted(root.glob("live-*.jsonl")):
    samples = [json.loads(line) for line in path.read_text().splitlines() if line.startswith("{")]
    if len(samples) != 9:
        raise SystemExit(f"Expected 9 diagnostic samples in {path}; got {len(samples)}")
    first = samples[0]
    counters = [item["Counters"] for item in samples]
    rows.append({
        "origin": "LiveDb2", "fixture": first["Fixture"], "blockSize": first["BlockSize"],
        "samples": len(samples), "rows": first["Rows"],
        "meanMs": statistics.mean(item["Milliseconds"] for item in samples),
        "medianMs": statistics.median(item["Milliseconds"] for item in samples),
        "stddevMs": statistics.stdev(item["Milliseconds"] for item in samples),
        "minMs": min(item["Milliseconds"] for item in samples),
        "maxMs": max(item["Milliseconds"] for item in samples),
        "allocatedBytesMean": statistics.mean(item["AllocatedBytes"] for item in samples),
        "clientCpuMsMean": statistics.mean(item["ClientCpuMs"] for item in samples),
        **{key: f"{min(item[key] for item in counters)}..{max(item[key] for item in counters)}"
           for key in ("ReadCalls", "WriteCalls", "ReadBytes", "WriteBytes", "Fetches", "DssFrames",
                       "QueryDataPackets", "Continuations", "TransportCopyBytes", "RowBufferCopyBytes",
                       "DiscardCopyBytes")},
    })
for path in sorted(root.glob("replay-*.csv")):
    counters = [json.loads(line.removeprefix("clientCounters=")) for line in path.read_text().splitlines()
                if line.startswith("clientCounters=")]
    if len(counters) != 9:
        raise SystemExit(f"Expected 9 replay diagnostic samples in {path}; got {len(counters)}")
    fixture, size = path.stem.split("-")[1:]
    rows.append({"origin": "Replay", "fixture": fixture, "blockSize": int(size), "samples": len(counters),
                 "rows": 1_000_000 if fixture == "Int" else 100_000,
                 **{key: f"{min(item[key] for item in counters)}..{max(item[key] for item in counters)}"
                    for key in counters[0]}})
if not rows:
    raise SystemExit(f"No diagnostics found in {root}")
with output.open("w", newline="") as stream:
    writer = csv.DictWriter(stream, fieldnames=list(dict.fromkeys(key for row in rows for key in row)))
    writer.writeheader()
    writer.writerows(rows)
