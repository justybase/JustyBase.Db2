#!/usr/bin/env python3
"""Restore, build and run consumer projects against the packed NuGet packages.

Usage: run_package_consumer_smoke.py <package-directory>

The directory must contain the .nupkg files produced by `dotnet pack` for this revision.
Each consumer is copied to a temporary folder and restored from a local-only feed (nuget.org is
cleared), so the test uses exactly the packaged artifacts. The consumer projects never reference the
driver source. No Db2 server is contacted.
"""
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONSUMERS = {
    "Core": "JustyBase.Db2",
    "IbmCompat": "JustyBase.Db2.IBMCompat",
}


def package_version() -> str:
    props = (ROOT / "src" / "Packaging.props").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", props)
    if not match:
        raise SystemExit("FAIL: <Version> not found in src/Packaging.props")
    return match.group(1).strip()


def run(command: list[str], cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(command, cwd=cwd, capture_output=True, text=True, check=False)


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    packages = Path(sys.argv[1]).resolve()
    if not any(packages.glob("JustyBase.Db2*.nupkg")):
        print(f"FAIL: no JustyBase.Db2 .nupkg files in {packages}", file=sys.stderr)
        return 1
    version = package_version()
    failures = []

    with tempfile.TemporaryDirectory(prefix="db2-consumer-") as scratch:
        scratch_path = Path(scratch)
        feed = scratch_path / "NuGet.Config"
        feed.write_text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            "<configuration>\n"
            "  <packageSources>\n"
            "    <clear />\n"
            f"    <add key=\"local-packages\" value=\"{packages}\" />\n"
            "  </packageSources>\n"
            "</configuration>\n",
            encoding="utf-8",
        )

        for folder, package in CONSUMERS.items():
            source = ROOT / "tests" / "PackageConsumer" / folder
            target = scratch_path / folder
            shutil.copytree(source, target, ignore=shutil.ignore_patterns("bin", "obj"))
            shutil.copy(feed, target / "NuGet.Config")
            properties = [f"-p:ConsumerPackageVersion={version}"]

            restore = run(["dotnet", "restore", *properties, "--nologo", "-v", "q"], target)
            if restore.returncode != 0:
                failures.append(f"{folder}: restore failed\n{restore.stdout}\n{restore.stderr}")
                continue

            assets = (target / "obj" / "project.assets.json").read_text(encoding="utf-8")
            if folder == "Core" and "JustyBase.Db2.IBMCompat" in assets:
                failures.append("Core: consumer graph unexpectedly contains JustyBase.Db2.IBMCompat")
            if package not in assets:
                failures.append(f"{folder}: restore graph does not contain {package}")

            run_result = run(["dotnet", "run", *properties, "--nologo", "--no-restore"], target)
            output = run_result.stdout.strip()
            if run_result.returncode != 0 or "PASS" not in output:
                failures.append(f"{folder}: smoke run failed (exit {run_result.returncode})\n{output}\n{run_result.stderr}")
            else:
                print(output.splitlines()[-1])

    if failures:
        print("FAIL: package consumer smoke", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1
    print(f"PASS: package consumer smoke ({len(CONSUMERS)} consumers, version {version})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
