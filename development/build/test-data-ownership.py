"""Guard the compatibility boundary between old storage keys and module IDs."""
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[2]
STORAGE = ROOT / "scripts/ReloaderPlugins/Plugins/_System/Shared/Code/PluginStorage.cs"
ALIASES = {
    "ModdedCamera": "Camera",
    "RainbowPaint": "Paint",
    "SharkRider": "Wildlife",
    "RemoveDroppedPeds": "Cleanup",
    "FrozenDynamic": "PedControl",
    "MenyooStreamer": "Streamer",
    "Prefabs": "Prefabs",
    "Development": "Dev",
    "Logs": "Core",
}
CODE_PREFIXES = {
    "ModdedCamera": ("Camera/Code/",),
    "RainbowPaint": ("VehiclePaint/Code/", "Templates/Code/"),
    "SharkRider": ("Misc/Code/Wildlife/",),
    "RemoveDroppedPeds": ("Optimization/Code/Cleanup/",),
    "FrozenDynamic": ("Optimization/Code/PedControl/",),
    "MenyooStreamer": ("Optimization/Code/Streamer/",),
    "Prefabs": ("Prefabs/Code/",),
    "Development": ("_System/Diagnostics/Code/Dev/", "_System/Shared/Code/"),
    "Logs": ("_System/Shared/Code/",),
}


def check():
    source = STORAGE.read_text(encoding="utf-8-sig")
    actual = dict(re.findall(r'case "([^"]+)": return "([^"]+)";', source))
    assert actual == ALIASES, f"Storage aliases changed: {actual}"
    count = 0
    plugins = ROOT / "scripts/ReloaderPlugins/Plugins"
    for file in plugins.rglob("*.cs"):
        if "Code" not in file.relative_to(plugins).parts:
            continue
        relative = file.relative_to(plugins).as_posix()
        text = file.read_text(encoding="utf-8-sig")
        for owner, name in re.findall(r'PluginStorage\.FilePath\("([^"]+)",\s*"([^"]+)"\)', text):
            assert owner in ALIASES, f"Unknown storage key: {file}: {owner}"
            assert relative.startswith(CODE_PREFIXES[owner]), f"Wrong owner: {relative}: {owner}"
            assert Path(name).name == name and name, f"Nested storage name: {file}: {name}"
            count += 1
    assert count >= 15, f"Storage calls unexpectedly missing: {count}"
    print(f"Data ownership OK: {len(ALIASES)} aliases, {count} call sites.")


if __name__ == "__main__":
    check()
