#!/usr/bin/env python3
"""Validate the NuGet package contents against per-package allowlists (offline, no publishing).

Usage: check_package_contents.py [package-directory]

Without an argument the packages are produced into a temporary directory with `dotnet pack`. With a
directory argument the already-built .nupkg/.snupkg files are checked, so a workflow can pack once and
reuse the artifacts. Every entry must be on the allowlist for its package. Keys, environment files,
IBM CLI/native binaries, test fixtures and unexpected assemblies fail the check.
"""
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PACKAGE_FILE_PREFIXES = {"JustyBase.Db2": "JustyBase.Db2.", "JustyBase.Db2.IBMCompat": "JustyBase.Db2.IBMCompat."}
PROJECTS = {
    "JustyBase.Db2": "src/Db2.Driver/Db2.Driver.csproj",
    "JustyBase.Db2.IBMCompat": "src/Db2.IBMCompat/Db2.IBMCompat.csproj",
}
COMMON_METADATA = {
    "[Content_Types].xml",
    "_rels/.rels",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
}
METADATA_PREFIX = "package/services/metadata/core-properties/"

# Exact allowlists per package. Anything else in the archive is an error.
NUPKG_ALLOWLIST = {
    "JustyBase.Db2": COMMON_METADATA
    | {
        "README.md",
        "icon.png",
        "JustyBase.Db2.nuspec",
        "lib/net10.0/JustyBase.Db2.dll",
        "lib/net10.0/JustyBase.Db2.xml",
        "lib/net11.0/JustyBase.Db2.dll",
        "lib/net11.0/JustyBase.Db2.xml",
    },
    "JustyBase.Db2.IBMCompat": COMMON_METADATA
    | {
        "README.md",
        "icon.png",
        "IBM-DATA-DB2-MIGRATION.md",
        "JustyBase.Db2.IBMCompat.nuspec",
        "lib/net10.0/JustyBase.Db2.IBMCompat.dll",
        "lib/net10.0/JustyBase.Db2.IBMCompat.xml",
        "lib/net11.0/JustyBase.Db2.IBMCompat.dll",
        "lib/net11.0/JustyBase.Db2.IBMCompat.xml",
    },
}
SNUPKG_ALLOWLIST = {
    "JustyBase.Db2": COMMON_METADATA
    | {
        "JustyBase.Db2.nuspec",
        "lib/net10.0/JustyBase.Db2.pdb",
        "lib/net11.0/JustyBase.Db2.pdb",
    },
    "JustyBase.Db2.IBMCompat": COMMON_METADATA
    | {
        "JustyBase.Db2.IBMCompat.nuspec",
        "lib/net10.0/JustyBase.Db2.IBMCompat.pdb",
        "lib/net11.0/JustyBase.Db2.IBMCompat.pdb",
    },
}
FORBIDDEN_SUBSTRINGS = ("clidriver", ".pem", ".key", ".env", ".pfx", ".p12", "keytab", "ccache")
REQUIRED_NUPKG = {"README.md", "icon.png", "LICENSE", "THIRD-PARTY-NOTICES.md"}


def pack(output: Path) -> None:
    for project in PROJECTS.values():
        result = subprocess.run(
            ["dotnet", "pack", project, "-c", "Release", "--nologo", "-v", "q", "-o", str(output)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            raise SystemExit(f"FAIL: dotnet pack {project} exited {result.returncode}\n{result.stdout}\n{result.stderr}")


def package_id(path: Path) -> str:
    # File names are <id>.<version>.<ext>; the version starts at the first ".<digit>" segment.
    match = re.match(r"^(JustyBase\.Db2(?:\.IBMCompat)?)\.\d", path.name)
    if not match:
        raise SystemExit(f"FAIL: unexpected package file name {path.name}")
    return match.group(1)


def check(path: Path, allowlist: set[str], required: set[str]) -> list[str]:
    with zipfile.ZipFile(path) as archive:
        entries = archive.namelist()
    errors = []
    for name in sorted(required):
        if name not in entries:
            errors.append(f"missing required entry: {name}")
    for entry in entries:
        if entry in allowlist or entry.startswith(METADATA_PREFIX):
            continue
        errors.append(f"unexpected entry: {entry}")
    for entry in entries:
        lower = entry.lower()
        if any(token in lower for token in FORBIDDEN_SUBSTRINGS):
            errors.append(f"forbidden content: {entry}")
    print(f"  {path.name}: {len(entries)} entries")
    return errors


def main() -> int:
    if len(sys.argv) > 2:
        print(__doc__, file=sys.stderr)
        return 2
    if len(sys.argv) == 2:
        directory = Path(sys.argv[1]).resolve()
        errors = check_directory(directory)
    else:
        with tempfile.TemporaryDirectory(prefix="db2-package-check-") as tmp:
            pack(Path(tmp))
            errors = check_directory(Path(tmp))

    if errors:
        print("FAIL: package contents", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    print("PASS: package contents (allowlisted entries only, no keys, IBM binaries or fixtures)")
    return 0


def check_directory(directory: Path) -> list[str]:
    nupkgs = sorted(directory.glob("JustyBase.Db2*.nupkg"))
    snupkgs = sorted(directory.glob("JustyBase.Db2*.snupkg"))
    found = {package_id(path) for path in nupkgs}
    errors = []
    if found != set(PROJECTS):
        errors.append(f"expected packages {sorted(PROJECTS)}, found {sorted(found)}")
    print("checking packages:")
    for path in nupkgs:
        pid = package_id(path)
        required = REQUIRED_NUPKG | ({"README.md"} if pid == "JustyBase.Db2.IBMCompat" else set())
        errors += check(path, NUPKG_ALLOWLIST[pid], required)
    for path in snupkgs:
        pid = package_id(path)
        errors += check(
            path,
            SNUPKG_ALLOWLIST[pid],
            {"lib/net10.0/" + pid + ".pdb", "lib/net11.0/" + pid + ".pdb"},
        )
    if len(snupkgs) != len(nupkgs):
        errors.append(f"expected one .snupkg per .nupkg, found {len(snupkgs)} and {len(nupkgs)}")
    return errors


if __name__ == "__main__":
    raise SystemExit(main())
