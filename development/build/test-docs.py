"""Check canonical module README files and the short index against the catalog."""

import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
modules = json.loads((root / "development/registry/modules.json").read_text(encoding="utf-8"))["modules"]
index = (root / "docs/MODS_RU.md").read_text(encoding="utf-8")
checked = [
    root / "README.md",
    root / "docs/DEVELOPMENT_RU.md",
    root / "docs/STRUCTURE_RU.md",
    root / "docs/history/MODPACK_DEVELOPMENT.md",
    root / "docs/MODS_RU.md",
    root / "docs/PLUGIN_LIFECYCLE_RU.md",
    root / "docs/DATA_AND_DELIVERY_RU.md",
    root / "docs/ASSETS_RU.md",
    root / "docs/INSTALL_RU.md",
    root / "docs/FINAL_AUDIT_RU.md",
    root / "development/assets/README.md",
]

for module in modules:
    if module["kind"] == "tool":
        readme = root / "scripts/ReloaderPlugins/Tools" / module["id"] / "Files/DEVELOPMENT.md"
    elif module["kind"] == "loader" or module["id"] == "Crash":
        readme = root / "development" / module["id"] / "README.md"
    else:
        readme = root / "docs/modules" / f"{module['id']}.md"
    if not readme.is_file():
        raise AssertionError(f"Missing README: {module['id']}")
    checked.append(readme)
    body = readme.read_text(encoding="utf-8")
    heading = body.splitlines()[0]
    if module["id"] not in heading or module["name"] not in heading:
        raise AssertionError(f"README heading differs from catalog: {module['id']}")
    if module["key"] and module["key"] not in body:
        raise AssertionError(f"Missing key {module['key']} in {module['id']}")
    if module["sources"] and module["sources"][0] not in body:
        raise AssertionError(f"Missing main source in {module['id']}")
    if module["kind"] in {"plugin", "tool"}:
        link = (f"../scripts/ReloaderPlugins/Tools/{module['id']}/Files/DEVELOPMENT.md" if module["kind"] == "tool"
                else f"../development/Crash/README.md" if module["id"] == "Crash"
                else f"modules/{module['id']}.md")
        if index.count(f"]({link})") != 1:
            raise AssertionError(f"Index link missing or duplicated: {module['id']}")
        if module["name"] not in index:
            raise AssertionError(f"Index name differs from catalog: {module['id']}")

for name in ("PedImporter", "PedToProp"):
    tool = root / "scripts/ReloaderPlugins/Tools" / name
    if {item.name for item in tool.iterdir()} != {f"{name}.cmd", "Files"}:
        raise AssertionError(f"Unexpected top-level files in {name}")
    for folder in ("Runtime", "Source", "Tests"):
        if not (tool / "Files" / folder).is_dir():
            raise AssertionError(f"Missing {name}/Files/{folder}")
    if not (tool / "Files/Runtime" / f"{name}.exe").is_file():
        raise AssertionError(f"Missing executable: {name}")
    if f"Files\\Runtime\\{name}.exe" not in (tool / f"{name}.cmd").read_text(encoding="ascii"):
        raise AssertionError(f"Launcher does not point to Runtime: {name}")
    if any((tool / "Files/Source" / item).exists() for item in ("bin", "obj")):
        raise AssertionError(f"Build output was written into {name}/Files/Source")

checked = list(dict.fromkeys(checked + list((root / "docs").rglob("*.md")) +
                             list((root / "scripts").rglob("*.md"))))
for document in checked:
    body = document.read_text(encoding="utf-8")
    for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", body):
        if target.startswith(("https://", "http://", "#")):
            continue
        local = (document.parent / target.split("#", 1)[0]).resolve()
        if not local.exists():
            raise AssertionError(f"Broken link in {document.relative_to(root)}: {target}")

print(f"Documentation OK: {len(modules)} modules, {len(checked)} Markdown files checked.")
