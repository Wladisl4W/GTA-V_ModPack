---
name: gta-modpack
description: "Work on the GTA V ModPack repository: Reloader plugins, loader, crash diagnostics, Ped Importer, PedToProp, validation, and delivery. Use for changes or investigations in this repository, not for unrelated GTA modding."
---

# GTA V ModPack

Use this skill only in the GTA-V_ModPack checkout. Confirm the repository root and current files before acting; paths, installed game state, and documentation may have changed. Read the relevant section of `docs/DEVELOPMENT_RU.md` first; use `docs/history/MODPACK_DEVELOPMENT.md` for historical release details and each standalone tool's README for its workflow. Do not repeat those guides in answers.

For module ownership and naming, consult `catalog/modules.json` and the relevant `modules/<id>/README.md`. `docs/STRUCTURE_RU.md` describes the current layout and migration status. The searchable map is generated locally with `modpack.cmd Map`; `catalog/map.template.html` contains no module data and is not the map. Treat the manifest as a catalog, not evidence that code or data has moved: verify source, packaged, and installed paths before editing or reporting delivery.

## Route the work

- Game plugins: locate ownership in `catalog/modules.json`, then edit the listed source in `modules/<id>/src/Code`. The live loader compiles these files together with .NET Framework CodeDOM; a successful SDK build alone does not establish compatibility. Use `modpack.cmd Check` for final compilation validation. Run focused tests in `modules/<id>/tests` or shared `tools/` when the changed behavior warrants them.
- Plugin delivery: when the user requested an implementation to be installed, inspect the source-to-target differences before `modpack.cmd Update`; it can synchronize changes beyond the one plugin being worked on. Do not deploy unrelated or unfinished changes without the user's direction. After `Check`, use `Update` when its full change set is appropriate. It synchronizes plugin sources and diagnostic artifacts to `dist` and the configured GTA installation, then checks hashes and the loader log. A timeout means delivery may have happened without confirmed reload; report that distinction. Never replace `Reloader.dll` as part of an ordinary plugin update.
- Loader changes: work in `modules/Loader/src`; read the loader-update procedure in `docs/DEVELOPMENT_RU.md`. Its DLL has a separate build and explicit installation procedure. Do not treat `modpack.cmd Update` as a loader installer.
- Crash diagnostics and system capture: inspect `modules/Crash/src`, its focused tests, and the current game logs. System capture settings may require administrator rights and an explicit user request. Do not promise that a dump identifies the original faulty mod or that every failure produces a dump.
- Ped Importer and PedToProp: use `modules/PedImporter/src` and `modules/PedToProp/src`, READMEs, build scripts, and focused tests. `modpack.cmd Update` is not their delivery path. These tools must not silently change GTA archives or install converted assets.

## Verify and report

Preserve unrelated working-tree changes. Keep generated output, local `modpack.local.json`, and unlicensed game media in `assets/models` out of commits. Current installation instructions are in `docs/INSTALL_RU.md`; `docs/history/README_1.6.txt` describes historical release 1.6. Before claiming a successful change, distinguish source validation, delivery, loader confirmation, and in-game behavior; verify only the stages relevant to the request and name any stage not tested. For visual or gameplay changes, build success is not a substitute for checking the result in GTA. Do not create a release, commit, or push to GitHub unless the user requested it.
