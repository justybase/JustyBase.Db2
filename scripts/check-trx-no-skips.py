#!/usr/bin/env python3
"""Fail when a TRX test run contains tests that were skipped or not runnable."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--allow-skips",
        action="store_true",
        help="allow intentionally skipped tests while still requiring at least one executed test",
    )
    parser.add_argument("trx", nargs="+", type=Path, help="TRX test result file(s)")
    args = parser.parse_args()

    failed = False
    for path in args.trx:
        if not path.is_file():
            print(f"FAIL: TRX results file not found: {path}", file=sys.stderr)
            failed = True
            continue

        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as error:
            print(f"FAIL: could not read TRX results {path}: {error}", file=sys.stderr)
            failed = True
            continue

        counters = next(
            (element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "Counters"),
            None,
        )
        if counters is None:
            print(f"FAIL: TRX results contain no counters: {path}", file=sys.stderr)
            failed = True
            continue

        total = int(counters.get("total", "0"))
        executed = int(counters.get("executed", "0"))
        skipped = int(counters.get("notExecuted", "0"))
        not_runnable = int(counters.get("notRunnable", "0"))

        # xUnit's TRX logger records skipped cases as UnitTestResult outcome
        # "NotExecuted", but some versions leave Counters.notExecuted at zero.
        # Read per-test outcomes as well so those skips cannot pass unnoticed.
        result_outcomes: dict[str, int] = {}
        for element in root.iter():
            if element.tag.rsplit("}", 1)[-1] != "UnitTestResult":
                continue
            outcome = (element.get("outcome") or "").casefold()
            result_outcomes[outcome] = result_outcomes.get(outcome, 0) + 1

        skipped = max(
            skipped,
            result_outcomes.get("notexecuted", 0),
            result_outcomes.get("skipped", 0),
        )
        not_runnable = max(
            not_runnable,
            result_outcomes.get("notrunnable", 0),
            result_outcomes.get("notfound", 0),
            result_outcomes.get("disconnected", 0),
        )
        non_execution_outcomes = {
            "notexecuted",
            "skipped",
            "notrunnable",
            "notfound",
            "disconnected",
            "pending",
            "inprogress",
        }
        executed = max(
            executed,
            sum(
                count
                for outcome, count in result_outcomes.items()
                if outcome not in non_execution_outcomes
            ),
        )

        if total == 0 or executed == 0:
            print(f"FAIL: {path}: TRX contains no executed tests (total={total}, executed={executed})", file=sys.stderr)
            failed = True
        elif not_runnable or (skipped and not args.allow_skips):
            print(
                f"FAIL: {path}: {skipped} skipped and {not_runnable} not-runnable test(s)",
                file=sys.stderr,
            )
            failed = True
        else:
            print(
                f"PASS: {path}: {executed}/{total} tests executed; "
                f"{skipped} skipped, {not_runnable} not-runnable"
            )

    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
