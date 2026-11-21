"""Guard the compatibility boundary between old storage keys and module IDs."""
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
STORAGE = ROOT / "modules/Core/src/Code/PluginStorage.cs"
ALIASES = {
    "ModdedCamera": "Camera",
    "RainbowPaint": "Paint",
    "SharkRider": "Wildlife",
    "RemoveDroppedPeds": "Cleanup",
    "FrozenDynamic": "PedControl",
    "MenyooStreamer": "Streamer",
    "Development": "Dev",
    "Logs": "Core",
}


def check():
    source = STORAGE.read_text(encoding="utf-8-sig")
    actual = dict(re.findall(r'case "([^"]+)": return "([^"]+)";', source))
    assert actual == ALIASES, f"Storage aliases changed: {actual}"
    count = 0
    for module in (ROOT / "modules").iterdir():
        code = module / "src/Code"
        if not code.is_dir():
            continue
        for file in code.glob("*.cs"):
            text = file.read_text(encoding="utf-8-sig")
            for owner, name in re.findall(r'PluginStorage\.FilePath\("([^"]+)",\s*"([^"]+)"\)', text):
                assert owner in ALIASES, f"Unknown storage key: {file}: {owner}"
                assert ALIASES[owner] == module.name or (module.name == "Core" and owner == "Development"), (
                    f"Wrong owner: {file}: {owner} -> {ALIASES[owner]}"
                )
                assert Path(name).name == name and name, f"Nested storage name: {file}: {name}"
                count += 1
    assert count >= 15, f"Storage calls unexpectedly missing: {count}"
    print(f"Data ownership OK: {len(ALIASES)} aliases, {count} call sites.")


if __name__ == "__main__":
    check()
