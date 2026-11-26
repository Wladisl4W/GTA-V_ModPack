# Инвентаризация GTA V ModPack

Исторический снимок перед этапом 3, на 20 ноября 2026 года. **Пути и имена ниже устарели.** Текущая структура описана в [`STRUCTURE_RU.md`](STRUCTURE_RU.md). Машинный реестр: [`development/registry/modules.json`](../development/registry/modules.json). Предыдущие предложения по именам находятся в [`NAMING_MAP_RU.md`](NAMING_MAP_RU.md).

## Модули

Пути данных ниже относительны к установленному `scripts/ReloaderPlugins`. Для игровых плагинов исходники находятся в `Source Code/Plugins/<ID>/Code`, копии для установки — в `Ready To Use/ReloaderPlugins/Plugins/<ID>/Code`. У отдельной программы `src` означает её каталог в `Source Code`, поставка находится в `Ready To Use/Tools`.

| ID | Название и назначение | Клавиша / текущее меню | Код | Данные и внешние зависимости | Проверки / поставка |
| --- | --- | --- | --- | --- | --- |
| Core | Общее ядро: интерфейс, управление, хранение, ошибки | Фон | `Core/Code` (5 файлов) | `Plugins/Core/Data/Logs/PluginErrors.log`, `StorageMigration.log`; `Plugins/Newtonsoft.Json.dll`; ScriptHookVDotNet/LemonUI | `test-storage`, `test-regular-data`, `Check`; игровая поставка |
| Loader | Компиляция и перезагрузка | Фон, F5 для перезагрузки | `Source Code/Reloader` | `Plugins/Loader/Data/Logs/Reloader.log`, `compile_errors.txt`; .NET/ScriptHookVDotNet | `test-reloader`, `test-update`, `Check`; `Ready To Use/Reloader.dll` |
| Catalog | Каталог модов | F5; «ModPack / Моды в паке» | `Catalog/Code` (1) | Нет собственных данных; зависит от Core и LemonUI | `test-project-map`, `Check`; игровая поставка |
| Camera | Пролётки, пути, следящая камера | T; «ModdedCamera» | `Camera/Code` (13) | `Plugins/Camera/Data/Logs/ModdedCamera.log`, `Data/Saves/Paths/*.json`; Core, LemonUI | `test-camera-zero-duration`, `Check`; игровая поставка |
| SlowMo | Замедление и камера | Mouse4 | `SlowMo/Code` (1) | Нет собственных файлов; общий сервис управления/камеры | `Check`; игровая поставка |
| Paint | Покраска, обмен моделей машин, пассажиры мотоциклов, пресеты | I; «Радужная покраска» | `Paint/Code` (3) | `Plugins/Paint/Data/Settings/RainbowPaintSettings.json`, `RainbowPaintExceptions.json`, `RainbowPaintPresets.json`, `RainbowPaintPassengerModels.json`; Core, LemonUI | `test-rainbow-diagnostics`, `Check`; игровая поставка |
| Animations | Анимации выбранных педов | Y в Object Spooner; «Анимации педов» | `Animations/Code` (2) | Читает внешние `menyooStuff/PedAnimList.txt`, `FavouriteAnims.xml`; не владеет ими | `test-ped-animations`, `Check`; игровая поставка |
| Weapons | Управление оружием и ударами | L; «Weapon Manager» | `Weapons/Code` (1) | Нет собственных файлов; Core, LemonUI | `Check`; игровая поставка |
| Wildlife | Акула и крокодил | O; «Shark Rider» | `Wildlife/Code` (3) | `Plugins/Wildlife/Data/Settings/SharkRiderSettings.json`, `Data/Logs/SharkRider.log`; модель крокодила установлена отдельно | `Check`; игровая поставка |
| Streamer | Потоковая загрузка педов | U; «MenyooStreamer» | `Streamer/Code` (1) | `Plugins/Streamer/Data/Settings/MenyooStreamer.ini`, `Data/Logs/MenyooStreamer.log`, `error.log`; внешние данные Menyoo | `Check`; игровая поставка |
| Prefabs | Конструкции в Object Spooner | `;`; «Заготовки» | `Prefabs/Code` (1) | Нет собственных файлов; Core, LemonUI, Object Spooner | `Check`; игровая поставка |
| PedControl | Управление NPC | K; «Frozen Dynamic» | `PedControl/Code` (1) | `Plugins/PedControl/Data/Logs/FrozenDynamic.log`; Core, LemonUI | `Check`; игровая поставка |
| Cleanup | Удаление упавших педов | H; «Remove Dropped Peds» | `Cleanup/Code` (1) | `Plugins/Cleanup/Data/Settings/RemoveDroppedPeds.json`, `Data/Logs/RemoveDroppedPeds.log` | `Check`; игровая поставка |
| Dev | Игровая диагностика | Ctrl+F10; «ModPack / Разработка» | `Dev/Code` (1) | `Plugins/Dev/Data/Settings/DevelopmentSettings.json`, `Data/Logs/Development.log` | `Check`; игровая поставка |
| Crash | Захват сбоев | Фон | `Crash/Code` (1), `Source Code/CrashWatcher` | `Plugins/Crash/Data` (сеансы, отчёты, дампы), `Plugins/Crash/Tools`; системные пути дампов Windows | `test-crash-watcher`, `test-crash-capture`, `Check`; игровая поставка |
| PedImporter | Избранное Menyoo; отдельно полный импорт | Окно Windows | `Source Code/PedImporter` | `%LOCALAPPDATA%/ModPack/PedImporter`; по явной команде пишет внешний `menyooStuff/FavouritePeds.xml`; полный режим управляет внешним AddonPeds Editor | `test-ped-importer`, `test-ped-importer-worker`, `test-publish-tool`; `Ready To Use/Tools/PedImporter` |
| PedToProp | Пед в статичный объект | Окно Windows | `Source Code/PedToProp` | `%LOCALAPPDATA%/ModPack/PedToProp`; Blender, Sollumz, PyMateria; GTA не устанавливает | `test-ped-to-prop`, `test-publish-tool`; `Ready To Use/Tools/PedToProp` |

`Check` проверяет сборку, а не подтверждает визуальную работу функции в GTA. Перечисленные отдельные тесты проверяют только названную часть поведения.

## Полное владение игровыми исходниками

Список составлен по реестру; каждому из 35 `.cs` соответствует ровно один модуль.

| Владелец | Файлы в `Code` |
| --- | --- |
| Core | `IGtaPlugin.cs`, `ControlSession.cs`, `CoreServices.cs`, `PluginStorage.cs`, `PluginLog.cs` |
| Catalog | `CatalogPlugin.cs` |
| Camera | `CameraPlugin.cs`, `CameraBase.cs`, `CameraInterpolator.cs`, `CameraService.cs`, `CameraData.cs`, `FollowCameraService.cs`, `CameraInput.cs`, `InputService.cs`, `MenuService.cs`, `CameraPath.cs`, `PositionSelector.cs`, `SaveService.cs`, `SplineCamera.cs` |
| SlowMo | `SlowMoPlugin.cs` |
| Paint | `PaintPlugin.cs`, `PaintPlugin.MotorcyclePassengers.cs`, `PaintPlugin.Presets.cs` |
| Animations | `AnimationsPlugin.cs`, `AnimationCatalog.cs` |
| Weapons | `WeaponsPlugin.cs` |
| Wildlife | `WildlifePlugin.cs`, `SharkBiteController.cs`, `CrocodileHunter.cs` |
| Streamer | `StreamerPlugin.cs` |
| Prefabs | `PrefabsPlugin.cs` |
| PedControl | `PedControlPlugin.cs` |
| Cleanup | `CleanupPlugin.cs` |
| Dev | `DevPlugin.cs` |
| Crash | `CrashPlugin.cs` |

## Карта корня разработки

| Место | Статус и владелец |
| --- | --- |
| `Source Code/Plugins` | Действующие игровые исходники; сейчас главный экземпляр кода. |
| `Source Code/Reloader`, `Source Code/CrashWatcher`, `Source Code/PedImporter`, `Source Code/PedToProp` | Действующие проекты загрузчика, диагностики и двух программ. |
| `Source Code/Backup` | Исторические `.bak`; не собирать и не поставлять. |
| `Source Code/build.bat` | Действующая сборочная точка старой структуры; учесть при переносе. |
| `Ready To Use` | Локальная поставка с копиями `.cs` и бинарниками; пока присутствует в Git, устранение дублирования запланировано. |
| `Modules` | Текущие страницы и заготовки модулей; каноническое расположение README ещё предстоит определить. |
| `catalog` | Реестр 17 модулей и карта проекта; карта создаётся скриптом. |
| `tools` | Сборка, доставка, тесты, миграции и старые аналитические заметки. |
| `MODELS` | Исходные ресурсы собственного контента (`epic_mc`, `mc_sign_*`); отдельно проверить судьбу при переносе. |
| `release-notes`, `README.txt`, `DEVELOPMENT_RU.md`, `MODPACK_DEVELOPMENT.md`, `STRUCTURE_RU.md`, `ROADMAP_RU.md` | Документация разных уровней; свести ссылки и убрать дубли после утверждения канонических мест. |
| `.github/workflows` | Действующая автоматизация GitHub, должна оставаться в корне репозитория. |
| `.agents/skills/gta-modpack` | Локальные инструкции работы с проектом; обновлять вместе с реальными путями. |
| `development/.local`, `development/config/modpack.local.json`, локальные логи и резервные копии | Данные рабочего ПК, не включать в релиз. `ScriptHookV.log` сейчас виден как локальный неотслеживаемый файл. |

## Жёсткие пути и границы

- `development/build/modpack.ps1` использует `Ready To Use/ReloaderPlugins/Plugins`; `development/build/build-project-map.ps1` — `Source Code/Plugins`; `Source Code/Reloader/PluginCompiler.cs` ищет исходники в корне и `<Module>/Code`. При переносе менять согласованно с тестами.
- `.github/workflows/release.yml` сейчас выбирает только файлы непосредственно из корня `Ready To Use/ReloaderPlugins/Plugins`. Вложенные каталоги модулей в релизный архив так не попадут. Исправление и проверка состава ZIP — задача этапа 3 **до публикации**.
- Старые имена `ModdedCamera`, `RainbowPaint`, `SharkRider`, `RemoveDroppedPeds`, `FrozenDynamic`, `MenyooStreamer`, `Development`, `Logs` обслуживаются `PluginStorage`; переименование этих ключей требует проверки миграции и резервных копий.
- Menyoo и AddonPeds — внешние программы и данные. Их дерево и файлы не перестраивать. Документировать только интеграцию и изменения, которые инструменты модпака делают по команде пользователя.
