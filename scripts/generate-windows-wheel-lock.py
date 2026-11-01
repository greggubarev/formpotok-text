"""Build a hash-checked requirements file from a complete local wheelhouse."""

import argparse
import hashlib
from pathlib import Path
from zipfile import ZipFile

from packaging.tags import sys_tags
from packaging.utils import canonicalize_name, parse_wheel_filename
from packaging.version import Version


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pins", type=Path, required=True)
    parser.add_argument("--wheelhouse", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    lines = args.pins.read_text(encoding="utf-8").splitlines()
    pins = {}
    for line in lines:
        if line.count("==") != 1:
            raise ValueError(f"Expected an exact version pin: {line}")
        name, version = line.split("==")
        normalized = canonicalize_name(name)
        if normalized in pins:
            raise ValueError(f"Duplicate package pin: {name}")
        pins[normalized] = (name, Version(version))

    compatible = set(sys_tags())
    hashes = {}
    for wheel in sorted(args.wheelhouse.glob("*.whl")):
        name, version, _, tags = parse_wheel_filename(wheel.name)
        if name not in pins or version != pins[name][1] or not (tags & compatible):
            raise ValueError(f"Unexpected or incompatible wheel: {wheel.name}")
        if name in hashes:
            raise ValueError(f"Multiple wheels for one package: {name}")
        with ZipFile(wheel) as archive:
            bad_member = archive.testzip()
            if bad_member is not None:
                raise ValueError(f"Corrupt wheel member: {wheel.name}: {bad_member}")
        digest = hashlib.sha256()
        with wheel.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
        hashes[name] = digest.hexdigest()

    if set(hashes) != set(pins):
        missing = sorted(set(pins) - set(hashes))
        raise ValueError(f"Wheelhouse is incomplete: {missing}")
    output = []
    for line in lines:
        name, _ = line.split("==")
        output.append(f"{line} --hash=sha256:{hashes[canonicalize_name(name)]}")
    args.output.write_text("\n".join(output) + "\n", encoding="utf-8", newline="\n")
    print(f"Locked {len(output)} wheels: {args.output}")


if __name__ == "__main__":
    main()
