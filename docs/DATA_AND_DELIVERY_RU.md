# Данные и доставка ModPack

Пути в таблице даны относительно `GTA V/scripts/ReloaderPlugins/Plugins`. В репозитории папки `Data` не содержат пользовательских файлов. Исторические ключи (`ModdedCamera`, `RainbowPaint` и другие) сохраняются в коде для чтения старых настроек.

| Игровое меню | Данные в установленной GTA | Прежнее место |
| --- | --- | --- |
| Camera | `Camera/Data/Paths/*`, `Camera/Data/Logs/ModdedCamera.log` | `Camera/Data/Saves/Paths/*`, ещё раньше `ReloaderPlugins/ModdedCamera/Paths/*` |
| Templates | `Templates/Data/Settings/RainbowPaintPresets.json`, `RainbowPaintPassengerModels.json` | `Paint/Data/Settings/*` |
| VehiclePaint | `VehiclePaint/Data/Settings/RainbowPaintSettings.json`, `RainbowPaintExceptions.json` | `Paint/Data/Settings/*` |
| Misc → Wildlife | `Misc/Data/Wildlife/Settings/SharkRiderSettings.json`, `Logs/SharkRider.log` | `Wildlife/Data/*` |
| Optimization → Streamer | `Optimization/Data/Streamer/Settings/MenyooStreamer.ini`, `Logs/*` | `Streamer/Data/*` |
| Optimization → Cleanup | `Optimization/Data/Cleanup/Settings/RemoveDroppedPeds.json`, `Logs/*` | `Cleanup/Data/*` |
| Optimization → PedControl | `Optimization/Data/PedControl/Logs/FrozenDynamic.log` | `PedControl/Data/*` |
| _System → Shared | `_System/Shared/Data/Logs/*` | `Core/Data/Logs/*` |
| _System → Diagnostics | `_System/Diagnostics/Data/Loader/Logs/*`, `Data/Crash/*`, `Data/Dev/*` | `Loader/Data/*`, `Crash/Data/*`, `Dev/Data/*` |

Перед переносом сохранённых пролёток создаётся резервная копия. Новый и прежний наборы сверяются по относительным именам и SHA-256; прежний путь остаётся для отката до проверки в игре. Другие старые настройки могут оставаться в прежних папках как резервная копия. Наличие двух разных файлов с одним именем требует ручного разбора; не перезаписывайте пользовательские данные вслепую.

`modpack.cmd Check` проверяет код, `modpack.cmd Build` обновляет готовые файлы в `scripts`, `development/build/package-release.ps1` создаёт единый архив. Копирование его содержимого в `GTA V/scripts` заменяет одноимённые файлы, но не удаляет устаревшие `.cs`; список прежних путей приведён в [инструкции](INSTALL_RU.md). `Reloader.dll` заменяется только при закрытой GTA.

Готовые Ped Importer и PedToProp поставляются в `ReloaderPlugins/Tools`. Они не изменяют игровые файлы при установке. Menyoo, AddonPeds, карты и архивы GTA не входят в модпак. Системные пути дампов CrashWatcher настраиваются отдельно и не меняются от простого копирования папки.
