"""Package a pre-built Release DLL, excluding private state and debug symbols."""
from pathlib import Path
import hashlib
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
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
print(archive.name)
