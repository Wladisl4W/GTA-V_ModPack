"""Verify the documented media inventory and the release boundary."""
import hashlib
from pathlib import Path
import re
import zipfile


root = Path(__file__).resolve().parents[1]
asset_root = root / "assets/models"
document = (root / "docs/ASSETS_RU.md").read_text(encoding="utf-8")
entries = re.findall(r"^\| `([^`]+\.(?:png|rpf))` \| (\d+) \| `([0-9A-F]{64})` \|", document, re.M)
assert len(entries) == 14, f"Expected 14 inventoried assets, got {len(entries)}"
local_files = {path.relative_to(asset_root).as_posix() for path in asset_root.rglob("*") if path.is_file()}
if local_files:
    assert {name for name, _, _ in entries} == local_files, "Asset list differs from inventory"
    for name, size, digest in entries:
        path = asset_root / name
        assert path.stat().st_size == int(size), f"Size changed: {name}"
        with path.open("rb") as stream:
            sha = hashlib.file_digest(stream, "sha256").hexdigest().upper()
        assert sha == digest, f"SHA-256 changed: {name}"

releases = root / "dist/releases"
archives = sorted(releases.glob("*.zip")) if releases.exists() else []
assert archives, "Build release archives before checking package contents"
for archive in archives:
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        assert not any(name.lower().endswith((".rpf", ".png")) for name in names), (
            f"Game media unexpectedly packaged: {archive.name}"
        )
print(f"Assets OK: {len(local_files)} local files verified, {len(archives)} release archives exclude game media.")
