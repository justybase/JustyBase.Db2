#!/usr/bin/env python3
"""Derive throughput/GC columns from BDN JSON without losing zero-GC columns."""
import argparse
import csv
import gzip
import json
from pathlib import Path
from urllib.parse import parse_qs

ROWS = {"Int": 1_000_000, "Mixed": 100_000, "Wide": 100_000, "Discard": 250_000, "Small": 1}
EARLY = {"Read1_Dispose": 1, "Read10_Dispose": 10, "SingleRow": 1, "ExecuteScalar": 1,
         "NativeStream_Read1_Dispose": 1}


def load(root):
    results = {}
    paths = sorted([*Path(root).rglob("*-full.json"), *Path(root).rglob("*-full.json.gz")])
    for path in paths:
        data = json.loads(gzip.decompress(path.read_bytes()) if path.suffix == ".gz" else path.read_text())
        for benchmark in data["Benchmarks"]:
            stats = benchmark.get("Statistics")
            memory = benchmark.get("Memory")
            if not stats or not memory or stats["Mean"] <= 0:
                raise SystemExit(f"Invalid/failed benchmark in {path}: {benchmark['DisplayInfo']}")
            params = {k: v[0] for k, v in parse_qs(benchmark["Parameters"]).items()}
            fixture = params["Fixture"]
            method = benchmark["Method"]
            rows = min(ROWS[fixture], EARLY.get(method, ROWS[fixture]))
            allocated = memory["BytesAllocatedPerOperation"]
            operations = memory["TotalOperations"]
            key = (benchmark["Type"], method, fixture, params["BlockSize"])
            if key in results:
                raise SystemExit(f"Duplicate benchmark key: {key}; provide one complete run directory")
            results[key] = {
                "suite": key[0], "method": method, "fixture": fixture, "blockSize": key[3],
                "samples": stats["N"], "meanMs": stats["Mean"] / 1e6,
                "medianMs": stats["Median"] / 1e6, "stddevMs": stats["StandardDeviation"] / 1e6,
                "minMs": stats["Min"] / 1e6, "maxMs": stats["Max"] / 1e6,
                "allocatedBytes": allocated, "consumedRows": rows, "bytesPerConsumedRow": allocated / rows,
                "fixtureRows": ROWS[fixture], "bytesPerFixtureRow": allocated / ROWS[fixture],
                "consumedRowsPerSecond": rows * 1e9 / stats["Mean"],
                "gen0Per1000Ops": memory["Gen0Collections"] * 1000 / operations,
                "gen1Per1000Ops": memory["Gen1Collections"] * 1000 / operations,
                "gen2Per1000Ops": memory["Gen2Collections"] * 1000 / operations,
            }
    if not results:
        raise SystemExit("No full BDN JSON reports found")
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results")
    parser.add_argument("output")
    parser.add_argument("--before")
    parser.add_argument("--minimum-samples", type=int, default=45)
    args = parser.parse_args()
    results = load(args.results)
    before = load(args.before) if args.before else {}
    rows = []
    for key, result in sorted(results.items()):
        if result["samples"] < args.minimum_samples:
            raise SystemExit(f"Insufficient measurements for {key}: {result['samples']}")
        if args.before:
            old = before.get(key)
            if old is None:
                raise SystemExit(f"Missing baseline for {key}")
            for metric in ("meanMs", "allocatedBytes"):
                result[metric + "ChangePercent"] = 100 * (result[metric] / old[metric] - 1)
        rows.append(result)
    with open(args.output, "w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


if __name__ == "__main__":
    main()
