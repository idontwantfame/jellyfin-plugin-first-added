"""Package a pre-built Release DLL, excluding private state and debug symbols."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--repository", default="idontwantfame/jellyfin-plugin-first-added")
repository = parser.parse_args().repository
if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
    raise SystemExit("Repository must be owner/name.")
PROJECT = ROOT / "Jellyfin.Plugin.FirstAdded"
version = ET.parse(PROJECT / "Jellyfin.Plugin.FirstAdded.csproj").findtext(".//AssemblyVersion")
dll = PROJECT / "bin/Release/net10.0/Jellyfin.Plugin.FirstAdded.dll"
if not dll.is_file():
    raise SystemExit("Build the Release configuration before packaging.")
payload = dll.read_bytes()
for value in (str(Path.home()), str(ROOT)):
    for encoding in ("utf-8", "utf-16-le"):
        if value.encode(encoding) in payload:
            raise SystemExit("Refusing to package a DLL containing a local build path.")
output = ROOT / "dist"
output.mkdir(exist_ok=True)
archive = output / f"first-added_{version}.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as package:
    for source, name in ((dll, dll.name), (ROOT / "LICENSE", "LICENSE")):
        entry = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        entry.external_attr = 0o100644 << 16
        package.writestr(entry, source.read_bytes())
for algorithm in ("sha256", "md5"):
    digest = hashlib.new(algorithm, archive.read_bytes()).hexdigest()
    archive.with_suffix(archive.suffix + "." + algorithm).write_text(
        digest + "  " + archive.name + "\n", encoding="utf-8")
metadata = {
    "category": "Metadata",
    "description": "Preserve original date-added timestamps across movie and episode file replacements.",
    "guid": "c7d4af3e-1b9d-4f06-b0c8-cce9bc55ac91",
    "name": "First Added",
    "overview": "Preserve original date-added timestamps",
    "owner": repository.split("/")[0],
    "targetAbi": "12.0.0.0",
    "version": version,
    "changelog": "Record and preserve first-added dates for movie and episode upgrades, with library selection, preview, manual-edit protection and recovery rollback.",
    "timestamp": "2026-10-15T00:00:00Z",
}
(output / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
entry = {key: metadata[key] for key in ("version", "changelog", "targetAbi", "timestamp")}
entry["sourceUrl"] = f"https://github.com/{repository}/releases/download/v{version}/{archive.name}"
entry["checksum"] = hashlib.md5(archive.read_bytes()).hexdigest()
manifest = {key: metadata[key] for key in ("guid", "name", "description", "overview", "owner", "category")}
manifest["imageUrl"] = ""
manifest["versions"] = [entry]
(output / "manifest.json").write_text(json.dumps([manifest], indent=2) + "\n", encoding="utf-8")
print(archive.name)
