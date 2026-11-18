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
            "visual_node_catalog.json": "{}",
            "authoring_examples.json": "[]",
            "translations/en.json": "{}",
            "out/HassSharp.dll": "compiled library",
            "out/HassSharp.deps.json": "{}",
            "out/Python.Runtime.dll": "runtime dependency",
            "www/hass-sharp.js": "// frontend",
            "www/assets/editor.worker.js": "// worker",
            # The licences of the font and the managed library a release carries that are not HassSharp's own.
            "licenses/DejaVu-LICENSE.txt": "Bitstream Vera",
            "licenses/StbTrueTypeSharp-LICENSE.txt": "Unlicense OR MIT",
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
        for name in ("out/HassSharp.dll", "out/HassSharp.deps.json", "www/hass-sharp.js", "visual_node_catalog.json", "authoring_examples.json"):
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

    def test_ships_the_linux_native_runtimes_and_no_other_operating_systems(self):
        # Home Assistant runs on Linux only. SQLite's packages also publish native libraries for Android, iOS, macOS,
        # Windows and WebAssembly, which together take the package past the Marketplace's size limit.
        linux = {
            "out/runtimes/linux-x64/native/libe_sqlite3.so": "glibc x64",
            "out/runtimes/linux-arm64/native/libe_sqlite3.so": "glibc arm64",
            "out/runtimes/linux-arm/native/libe_sqlite3.so": "glibc arm",
            "out/runtimes/linux-musl-x64/native/libe_sqlite3.so": "musl x64",
            "out/runtimes/linux-musl-arm64/native/libe_sqlite3.so": "musl arm64",
            "out/runtimes/linux-musl-arm/native/libe_sqlite3.so": "musl arm",
        }
        for name, content in linux.items():
            self.write_file(name, content)
        for name in ("out/runtimes/osx-arm64/native/libe_sqlite3.dylib", "out/runtimes/win-x64/native/e_sqlite3.dll",
                     "out/runtimes/win/lib/net11.0/System.Diagnostics.EventLog.dll",
                     "out/runtimes/ios-arm64/native/e_sqlite3.a", "out/runtimes/iossimulator-x64/native/e_sqlite3.a",
                     "out/runtimes/android-arm64/native/libe_sqlite3.so", "out/runtimes/maccatalyst-x64/native/libe_sqlite3.dylib",
                     "out/runtimes/browser-wasm/nativeassets/net11.0/e_sqlite3.a"):
            self.write_file(name, "another operating system")
        # A library of HassSharp's own that only looks like a runtime folder still ships.
        self.write_file("out/runtimes.json", "{}")
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        with ZipFile(self.output) as archive:
            self.assertEqual(set(archive.namelist()), set(self.files) | set(linux) | {"out/runtimes.json"})
            self.assertEqual(archive.read("out/runtimes/linux-musl-arm64/native/libe_sqlite3.so"), b"musl arm64")

    def test_ships_the_licences_of_what_it_bundles(self):
        # The font and the managed libraries a release carries that are not HassSharp's own come with their licences.
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        with ZipFile(self.output) as archive:
            self.assertEqual(archive.read("licenses/DejaVu-LICENSE.txt"), b"Bitstream Vera")
            self.assertEqual(archive.read("licenses/StbTrueTypeSharp-LICENSE.txt"), b"Unlicense OR MIT")

    def test_refuses_a_tree_without_a_licence(self):
        for name in ("licenses/DejaVu-LICENSE.txt", "licenses/StbTrueTypeSharp-LICENSE.txt"):
            with self.subTest(missing=name):
                (self.integration / name).unlink()
                result = self.package()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(name, result.stderr)
                self.assertFalse(self.output.exists())
                self.write_file(name, self.files[name])

    def test_skips_test_pages_and_the_preview_build_under_www(self):
        # The mock preview pages are served without authentication; a release built from a working copy must not hold them.
        for name in ("www/visual-v2-check-baby/tests/preview-app.html", "www/visual-v2-check-baby/assets/preview.js",
                     "www/visual-v2-check-baby/codicon.ttf", "www/index.html", "www/assets/page.HTML",
                     "www/old.htm", "www/assets/page.xhtml"):
            self.write_file(name, "test page")
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        with ZipFile(self.output) as archive:
            self.assertEqual(set(archive.namelist()), set(self.files))
