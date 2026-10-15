"""Verify the distributable contract and refusal to expose build paths."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory(prefix="first-added-package-")
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)
        project = "Jellyfin.Plugin.FirstAdded"
        for relative in (
            "scripts/package.py", "LICENSE", f"{project}/{project}.csproj",
            f"{project}/bin/Release/net10.0/{project}.dll",
        ):
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, target)

    def run_package(self, repository="example/plugin"):
        return subprocess.run(
            [sys.executable, str(self.root / "scripts/package.py"), "--repository", repository],
            capture_output=True, text=True, check=False,
        )

    def test_archive_and_catalogue_match(self):
        self.assertEqual(self.run_package().returncode, 0)
        archive = self.root / "dist/first-added_0.1.0.0.zip"
        payload = archive.read_bytes()
        with zipfile.ZipFile(archive) as package:
            self.assertEqual(sorted(package.namelist()), ["Jellyfin.Plugin.FirstAdded.dll", "LICENSE"])
            self.assertFalse(package.comment)
        for algorithm in ("sha256", "md5"):
            digest = archive.with_suffix(archive.suffix + "." + algorithm).read_text().split()[0]
            self.assertEqual(digest, hashlib.new(algorithm, payload).hexdigest())
        catalogue = json.loads((self.root / "dist/manifest.json").read_text())
        self.assertEqual(catalogue[0]["owner"], "example")
        self.assertEqual(catalogue[0]["guid"], "c7d4af3e-1b9d-4f06-b0c8-cce9bc55ac91")
        entry = catalogue[0]["versions"][0]
        self.assertEqual(entry["checksum"], hashlib.md5(payload).hexdigest())
        self.assertEqual(entry["targetAbi"], "12.0.0.0")
        self.assertEqual(entry["sourceUrl"], "https://github.com/example/plugin/releases/download/v0.1.0.0/" + archive.name)

    def test_private_build_path_is_rejected(self):
        dll = self.root / "Jellyfin.Plugin.FirstAdded/bin/Release/net10.0/Jellyfin.Plugin.FirstAdded.dll"
        dll.write_bytes(str(Path.home()).encode("utf-16-le"))
        result = self.run_package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("local build path", result.stderr)
        self.assertFalse((self.root / "dist").exists())

    def test_invalid_repository_is_rejected(self):
        self.assertNotEqual(self.run_package("https://example.invalid/project").returncode, 0)


if __name__ == "__main__":
    unittest.main()
