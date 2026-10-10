#!/usr/bin/env python3
"""Generate a CycloneDX 1.5 SBOM for each shipped C# package (offline).

Reads the restored `project.assets.json` of each packable project and writes one document per package:
<output>/<PackageId>.cdx.json. Projects must be restored first (`dotnet restore <project>`).
The driver package declares no runtime PackageReference; the IBM compatibility package depends only on
JustyBase.Db2, which is recorded as a project dependency. SDK build tooling is recorded as build scope.

Usage: generate_cs_sbom.py [output-directory]   (default: <repo>/sbom-out)
"""
import json
import re
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PACKAGING_PROPS = ROOT / "src" / "Packaging.props"
PROJECTS = {
    "JustyBase.Db2": ROOT / "src" / "Db2.Driver",
    "JustyBase.Db2.IBMCompat": ROOT / "src" / "Db2.IBMCompat",
}


def package_version() -> str:
    match = re.search(r"<Version>([^<]+)</Version>", PACKAGING_PROPS.read_text(encoding="utf-8"))
    if not match:
        raise SystemExit("FAIL: <Version> not found in src/Packaging.props")
    return match.group(1).strip()


def build_sbom(package_id: str, project_dir: Path, version: str) -> dict:
    assets_path = project_dir / "obj" / "project.assets.json"
    if not assets_path.exists():
        raise SystemExit(f"FAIL: {assets_path} missing; run dotnet restore for {project_dir.name} first")
    data = json.loads(assets_path.read_text(encoding="utf-8"))
    libraries = data.get("libraries", {})

    components = []
    for key in sorted(libraries):
        if libraries[key].get("type") != "package":
            continue  # project references are recorded as dependencies below
        name, _, library_version = key.partition("/")
        scope = "optional" if name == "Microsoft.NET.ILLink.Tasks" else "excluded"
        components.append(
            {
                "type": "library",
                "bom-ref": f"pkg:nuget/{name}@{library_version}",
                "name": name,
                "version": library_version,
                "purl": f"pkg:nuget/{name}@{library_version}",
                "scope": scope,
                "properties": [
                    {
                        "name": "db2-drivers:role",
                        "value": "SDK build/AOT tooling, not part of the runtime package",
                    }
                ],
            }
        )

    framework = next(iter(data.get("projectFileDependencyGroups", {}) or {}), "unknown")
    project_refs = [
        f"pkg:nuget/{key.partition('/')[0]}@{version}"
        for key, library in libraries.items()
        if library.get("type") == "project" and key.partition("/")[0] == "JustyBase.Db2"
    ]
    dependencies = project_refs
    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid4()}",
        "version": 1,
        "metadata": {
            "tools": [{"vendor": "db2-drivers", "name": "generate_cs_sbom.py"}],
            "component": {
                "type": "library",
                "bom-ref": f"pkg:nuget/{package_id}@{version}",
                "name": package_id,
                "version": version,
                "purl": f"pkg:nuget/{package_id}@{version}",
                "properties": [
                    {"name": "db2-drivers:target-framework", "value": framework},
                    {"name": "db2-drivers:runtime-dependencies", "value": ", ".join(project_refs) or "none declared"},
                ],
            },
        },
        "components": components,
        "dependencies": [{"ref": f"pkg:nuget/{package_id}@{version}", "dependsOn": dependencies}],
    }


def main() -> int:
    if len(sys.argv) > 2:
        print(__doc__, file=sys.stderr)
        return 2
    out_dir = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "sbom-out"
    out_dir.mkdir(parents=True, exist_ok=True)
    version = package_version()
    for package_id, project_dir in PROJECTS.items():
        sbom = build_sbom(package_id, project_dir, version)
        out = out_dir / f"{package_id}.cdx.json"
        out.write_text(json.dumps(sbom, indent=2) + "\n", encoding="utf-8")
        print(f"wrote {out} with {len(sbom['components'])} component(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
