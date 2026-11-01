import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "generate-windows-wheel-lock.py"
SPEC = importlib.util.spec_from_file_location("wheel_lock_generator", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class WheelLockGeneratorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pins = self.root / "pins.lock"
        self.wheels = self.root / "wheels"
        self.wheels.mkdir()
        self.output = self.root / "hashes.lock"
        self.pins.write_text("sample_pkg==1.2.3\n", encoding="utf-8")

    def generate(self):
        argv = [str(SCRIPT), "--pins", str(self.pins), "--wheelhouse", str(self.wheels), "--output", str(self.output)]
        with patch.object(sys, "argv", argv):
            MODULE.main()

    def test_generates_hash_for_matching_wheel(self):
        wheel = self.wheels / "sample_pkg-1.2.3-py3-none-any.whl"
        with ZipFile(wheel, "w") as archive:
            archive.writestr("sample_pkg-1.2.3.dist-info/WHEEL", "Wheel-Version: 1.0\n")
        self.generate()
        self.assertRegex(self.output.read_text(encoding="utf-8"), r"^sample_pkg==1\.2\.3 --hash=sha256:[0-9a-f]{64}\n$")

    def test_rejects_missing_wheel(self):
        with self.assertRaisesRegex(ValueError, "incomplete"):
            self.generate()

    def test_rejects_corrupt_wheel(self):
        (self.wheels / "sample_pkg-1.2.3-py3-none-any.whl").write_bytes(b"not a zip")
        with self.assertRaises(Exception):
            self.generate()

    def test_rejects_wrong_version(self):
        wheel = self.wheels / "sample_pkg-1.2.4-py3-none-any.whl"
        with ZipFile(wheel, "w") as archive:
            archive.writestr("sample_pkg-1.2.4.dist-info/WHEEL", "Wheel-Version: 1.0\n")
        with self.assertRaisesRegex(ValueError, "Unexpected"):
            self.generate()


if __name__ == "__main__":
    unittest.main()
