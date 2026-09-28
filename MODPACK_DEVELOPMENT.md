# GTA V ModPack development notes

This document captures practical project knowledge for future work on the
ModPack.

Russian development guide and command reference: [DEVELOPMENT_RU.md](DEVELOPMENT_RU.md).

## Project layout

- `Source Code/Reloader` contains the .NET Framework 4.8 loader project.
- `Source Code/CrashWatcher` contains the out-of-process GTA crash monitor.
- `Source Code/Plugins` contains plugin source files.
- `Ready To Use/Reloader.dll` is the packaged loader for a full install.
- `Ready To Use/ReloaderPlugins/Plugins` is the packaged plugin folder.
- The live GTA plugin folder is usually:
  `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\scripts\ReloaderPlugins\Plugins`

The normal update path is plugin-source based: users replace files inside
`scripts/ReloaderPlugins/Plugins`. `Reloader.dll` should stay untouched during
ordinary plugin updates.

## Development workflow

When changing a plugin:

1. Edit the file in `Source Code/Plugins`.
2. Run `.\modpack.cmd Check` to validate all plugin sources with the live CodeDOM compiler.
   This command also builds `CrashWatcher.exe` for .NET Framework 4.8.
3. Run `.\modpack.cmd` to validate, copy changed sources, verify hashes and confirm
   the exact source fingerprint in the fresh game log. Configure the game path
   in git-ignored `modpack.local.json`; see `modpack.example.json`.
4. When modifying the loader itself, additionally run:

   ```powershell
   dotnet build "Source Code\Reloader\Reloader.csproj" --no-restore
   ```

The dotnet build validates only the loader, not plugin compilation.
The project intentionally has no post-build copy target. Update requires loader
file version 2.0.0.0 or newer; install it once with GTA closed. Routine updates
never copy Reloader.dll. See the Russian guide for timeout and recovery behavior.

After a GitHub release is created, local release zip files can be deleted. They
are generated artifacts and should not be committed.

## Release workflow

Release archives should contain the contents of:

```text
Ready To Use\ReloaderPlugins\Plugins\*
```

Do not include the parent `Plugins` folder and do not include `Reloader.dll`.

Suggested archive naming:

```text
ModPack-Plugins-X.Y.zip
```

Suggested GitHub release naming:

```text
ModPack X.Y
```

Example user update instruction:

```text
Download ModPack-Plugins-X.Y.zip and extract its contents with replacement into:
GTA V\scripts\ReloaderPlugins\Plugins
```

## Current release state

- Latest release target: `v1.5`
- Release title: `ModPack 1.5`
- Assets: `ModPack-Plugins-1.5.zip` and `Reloader-2.1.zip`
- `v1.5` adds GTA crash reports with a separate watcher process. Generated
  crash data is stored under `scripts/ReloaderPlugins/CrashLogger`.
- The loader 2.1 archive improves plugin exception logging. Existing loader
  2.0 users can install the plugin update without replacing `Reloader.dll`.

## Modded Camera notes

The intended camera behavior is close to Rockstar Editor while keeping old saved
paths compatible.

- Per-node interpolation modes:
  - `0` = Linear
  - `1` = SmoothStop
  - `2` = SmoothNoStop
- Old saves are versioned through path data. Legacy mode values are normalized
  so older paths do not suddenly change behavior.
- The path loops by holding on the last node for that node duration, then
  cutting sharply back to the first node. There is no smooth transition from the
  last node to the first.
- `SmoothNoStop` uses approximate curve behavior near smooth nodes. It should
  feel like Rockstar Editor: the camera can pass near markers rather than
  strictly through every smooth node.
- Rotation and FOV are interpolated with the same per-node intent as position.
- Avoid direction-vector rotation reconstruction for 180-degree turns if it
  causes flips. Euler angle interpolation with angle unwrapping was used to
  avoid the camera looking under itself or rolling over.
- The general camera settings menu no longer contains a global FOV item. FOV is
  still available per node.
- Slow-motion camera timing should use real elapsed time, not GTA scaled game
  time, because world time scale already applies the visual slowdown.
- The camera follow mode is intentionally separate from path playback. It is a
  runtime checkbox in the main `T` menu and is implemented in
  `ModdedCamera_FollowCameraService.cs`. When enabled, fresh damage from the
  player to a ped starts a scripted follow camera with a smoothed entry, sets
  time scale to `0.75`, applies configurable gravity, launches the target,
  disables player controls during the shot, then cuts back to gameplay camera
  and restores world state. Follow duration, gravity, and hit force live in a
  separate `Настройки следования` submenu.

## Rainbow Paint notes

Current user-facing behavior:

- Manual colors include red, orange, yellow, green, cyan, blue, purple, pink,
  white, gray, black, and rainbow.
- Randomizer colors are controlled by a submenu with a checkbox per color.
- The randomizer color selection persists in
  `scripts/ReloaderPlugins/RainbowPaintSettings.json`.
- The "tyre smoke matches primary color" checkbox affects manual painting and
  random painting.
- Custom plate text opens the GTA onscreen keyboard, stores up to 8 characters,
  and applies the text to all current world vehicles.
- The randomizer tries to reduce repeated colors among nearby vehicles by
  scoring already-painted neighbors instead of only avoiding the previous car in
  a sorted list.
- Existing randomizer model exclusions remain in
  `scripts/ReloaderPlugins/RainbowPaintExceptions.json`.

## MenyooStreamer notes

Streaming radii are horizontal-only. Distance is calculated from `X/Y` and
ignores `Z`, so the streaming region behaves like a vertical cylinder. This
applies both to initial scanning and to chunk load/unload checks.

## Installation requirements

The public README should mention:

- Script Hook V
- Script Hook V .NET 3
- LemonUI, with `LemonUI.SHVDN3.dll` placed in `GTA V\scripts`
