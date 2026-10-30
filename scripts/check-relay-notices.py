#!/usr/bin/env python
"""Cross-check the packaged relay's shipped third-party components against
THIRD-PARTY-NOTICES.txt and the build venv's resolved dependencies.

Invoked by scripts/build-daily.ps1 after the PyInstaller step:

    .venv\\Scripts\\python.exe scripts\\check-relay-notices.py \
        --exe build\\dist-pyinstaller\\ClipBridgeRelay\\ClipBridgeRelay.exe \
        --notices THIRD-PARTY-NOTICES.txt

Both directions are checked; any problem exits 1 and lists the findings:

  1. every third-party top-level module shipped inside the packaged relay
     (PYZ archive + onedir *.dist-info folders) must map to a distribution
     installed in the build venv, and that distribution (name + version)
     must be declared in the notices file;
  2. every pinned "<name> <version> (<license>)" section title in the
     notices file must match the venv's actually resolved version.

The venv interpretation is the one running this script (build-daily.ps1
calls it with services/relay/.venv/Scripts/python.exe on purpose).
"""

import argparse
import importlib.metadata as metadata
import re
import sys
from pathlib import Path

# project-owned code: never a third-party notice obligation
PROJECT_MODULES = {"relay", "run"}

# vendored/renamed modules that do not normalize to their distribution name
MODULE_ALIASES = {
    "_distutils_hack": "setuptools",
    "_aix_support": "setuptools",
}

# platform-level bundles that are recorded in BUILD-MANIFEST.txt but are not
# Python distributions of the build venv; direction-A pin matching skips them
PLATFORM_BUNDLES = {"openssl", "python"}


def norm(name: str) -> str:
    return name.strip().lstrip("_").replace("-", "").replace("_", "").lower()


def shipped_top_levels(exe: Path) -> set[str]:
    """Top-level module names packaged into the relay exe (PYZ + onedir)."""
    from PyInstaller.archive.readers import CArchiveReader, ZlibArchiveReader

    tops: set[str] = set()
    reader = CArchiveReader(str(exe))
    pyz_names = [k for k in reader.toc if k.endswith(".pyz")]
    if not pyz_names:
        print("FAIL: no PYZ archive found inside " + str(exe))
        sys.exit(1)
    import os
    import tempfile

    for pyz_name in pyz_names:
        data = reader.extract(pyz_name)
        with tempfile.NamedTemporaryFile(suffix=".pyz", delete=False) as tmp:
            tmp.write(data)
            tmp_path = tmp.name
        try:
            z = ZlibArchiveReader(tmp_path)
            for key in z.toc:
                tops.add(key.split(".")[0])
        finally:
            os.remove(tmp_path)
    internal = exe.parent / "_internal"
    if internal.is_dir():
        for entry in internal.iterdir():
            if entry.name.endswith(".dist-info"):
                tops.add(entry.name.split("-")[0])
    return tops


def venv_dists() -> dict[str, str]:
    """normalized name -> (display name, version) for the running venv."""
    dists: dict[str, str] = {}
    for dist in metadata.distributions():
        name = dist.metadata.get("Name")
        version = dist.version
        if name:
            dists[norm(name)] = f"{name} {version}"
    return dists


def notices_pins(notices_path: Path) -> dict[str, str]:
    """normalized name -> pinned version from '<name> <version> (<license>)' titles."""
    pins: dict[str, str] = {}
    title = re.compile(r"^([A-Za-z][A-Za-z0-9._-]*) (\d[\w.]*) \(([^)]+)\)\s*$")
    for line in notices_path.read_text(encoding="utf-8").splitlines():
        m = title.match(line)
        if m:
            pins[norm(m.group(1))] = m.group(2)
    return pins


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--notices", required=True, type=Path)
    args = parser.parse_args()

    if not args.exe.is_file():
        print(f"FAIL: relay exe not found: {args.exe}")
        return 1
    if not args.notices.is_file():
        print(f"FAIL: notices file not found: {args.notices}")
        return 1

    dists = venv_dists()
    pins = notices_pins(args.notices)
    stdlib = set(sys.stdlib_module_names)

    problems: list[str] = []
    declared: list[str] = []

    for top in sorted(shipped_top_levels(args.exe)):
        if top in stdlib or top in PROJECT_MODULES:
            continue
        target = MODULE_ALIASES.get(top, top)
        display = dists.get(norm(target))
        if display is None:
            # prefix fallback, e.g. module "opentelemetry" -> dist "opentelemetry-api"
            matches = sorted(d for d, full in dists.items() if d.startswith(norm(target)))
            if len(matches) == 1:
                display = dists[matches[0]]
        if display is None:
            problems.append(f"shipped module '{top}' has no matching venv distribution")
            continue
        name, version = display.rsplit(" ", 1)
        pinned = pins.get(norm(name))
        if pinned is None:
            if display not in problems:
                problems.append(f"shipped component '{display}' is NOT declared in the notices file")
        elif pinned != version:
            problems.append(
                f"notices pin mismatch for {name}: notices says {pinned}, venv resolved {version}"
            )
        else:
            declared.append(f"{display} -> declared ({pinned})")

    for norm_name, pinned in sorted(pins.items()):
        if norm_name in PLATFORM_BUNDLES:
            continue
        display = dists.get(norm_name)
        if display is None:
            problems.append(
                f"notices declares '{norm_name}' {pinned} but the build venv has no such distribution"
            )
            continue
        version = display.rsplit(" ", 1)[1]
        if version != pinned:
            problems.append(
                f"notices pin mismatch for {display.rsplit(' ', 1)[0]}: notices says {pinned}, venv resolved {version}"
            )

    print(f"relay notices cross-check: {len(declared)} shipped components verified")
    for line in declared:
        print(f"  ok   {line}")
    if problems:
        print("relay notices cross-check FAILED:")
        for line in problems:
            print(f"  FAIL {line}")
        return 1
    print("relay notices cross-check PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
