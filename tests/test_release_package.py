"""Exercise the release archive consumed by the Home Assistant Marketplace."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile


PACKAGER = Path(__file__).resolve().parents[1] / "scripts/package_release.py"


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.integration = self.root / "hass_sharp"
        self.output = self.root / "hass_sharp.zip"
        self.files = {
            "__init__.py": "# integration",
            "manifest.json": '{"domain": "hass_sharp", "version": "1.0.0"}',
            "dotnet.runtimeconfig.json": "{}",
            "strings.json": "{}",
            "translations/en.json": "{}",
            "out/HassSharp.dll": "compiled library",
            "out/HassSharp.deps.json": "{}",
            "out/Python.Runtime.dll": "runtime dependency",
            "www/hass-sharp.js": "// frontend",
            "www/assets/editor.worker.js": "// worker",
        }
        for name, content in self.files.items():
            self.write_file(name, content)

    def write_file(self, name, content):
        path = self.integration / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    def package(self):
        return subprocess.run(
            [sys.executable, str(PACKAGER), "--integration-dir", str(self.integration),
             "--output", str(self.output), "--version", "v0.0.32-beta"],
            capture_output=True, text=True, check=False,
        )

    def test_packages_runtime_without_development_files_or_user_automations(self):
        for name in (
            "frontend/node_modules/monaco-editor/editor.js", "HassSharp/obj/cache",
            "HassSharp/Automation.cs", "user_scripts/private.cs", "dotnet/sdk/dotnet",
            "__pycache__/cache.pyc", "www/.DS_Store", "out/.DS_Store", ".DS_Store",
        ):
            self.write_file(name, "must not ship")
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        with ZipFile(self.output) as archive:
            self.assertEqual(set(archive.namelist()), set(self.files))
            manifest = json.loads(archive.read("manifest.json"))
            self.assertEqual(manifest["version"], "0.0.32-beta")
            self.assertEqual(archive.read("out/Python.Runtime.dll"), b"runtime dependency")
        self.assertEqual(json.loads((self.integration / "manifest.json").read_text())["version"], "1.0.0")

    def test_refuses_an_unbuilt_integration(self):
        for name in ("out/HassSharp.dll", "out/HassSharp.deps.json", "www/hass-sharp.js"):
            with self.subTest(missing=name):
                (self.integration / name).unlink()
                result = self.package()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(name, result.stderr)
                self.assertFalse(self.output.exists())
                self.write_file(name, self.files[name])

    def test_refuses_an_archive_exceeding_marketplace_unpacked_size_limit(self):
        with (self.integration / "out/oversized.dll").open("wb") as oversized:
            oversized.truncate(100 * 1024 * 1024)
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("100 MiB", result.stderr)
        self.assertFalse(self.output.exists())
