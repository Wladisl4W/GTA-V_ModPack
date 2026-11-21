# Данные и доставка ModPack

Этот реестр отделяет исходники, поставку и файлы, возникающие во время игры. Пути ниже даны относительно `GTA V/scripts`, если не указан другой корень. `dist` находится в репозитории и создаётся сборкой; это не папка пользовательских данных.

## Игровые данные

| Владелец | Текущие данные | Исторический ключ и прежнее место |
| --- | --- | --- |
| Camera | `ReloaderPlugins/Plugins/Camera/Data/Saves/Paths/*`, `Data/Logs/ModdedCamera.log` | `ModdedCamera`, прежде `ReloaderPlugins/ModdedCamera/Paths/*` и файл журнала в этой папке; ещё более старый каталог `ReloaderPlugins/Paths` |
| Paint | `Plugins/Paint/Data/Settings/RainbowPaint{Settings,Exceptions,Presets,PassengerModels}.json` | `RainbowPaint`, прежняя одноимённая папка в `ReloaderPlugins` или его корень |
| Wildlife | `Plugins/Wildlife/Data/Settings/SharkRiderSettings.json`, `Data/Logs/SharkRider.log` | `SharkRider` |
| Cleanup | `Plugins/Cleanup/Data/Settings/RemoveDroppedPeds.json`, `Data/Logs/RemoveDroppedPeds.log` | `RemoveDroppedPeds` |
| PedControl | `Plugins/PedControl/Data/Logs/FrozenDynamic.log` | `FrozenDynamic` |
| Streamer | `Plugins/Streamer/Data/Settings/MenyooStreamer.ini`, `Data/Logs/MenyooStreamer.log` и `error.log` | `MenyooStreamer` |
| Dev | `Plugins/Dev/Data/Settings/DevelopmentSettings.json`, `Data/Logs/Development.log` | `Development` |
| Core | `Plugins/Core/Data/Logs/PluginErrors.log` и `StorageMigration.log` | `Logs` и старые файлы в корне `ReloaderPlugins` |
| Loader | `Plugins/Loader/Data/Logs/Reloader.log`, `compile_errors.txt` | Загрузчик пишет непосредственно в эту папку; `Reloader.dll` находится в `scripts` |
| Crash | `Plugins/Crash/Data` — контекст запуска, heartbeat, отчёты, дампы и runtime-копии; `Plugins/Crash/Tools` — поставляемый watcher и установщики | Старый `ReloaderPlugins/CrashLogger`; системные пути захвата дампов настраиваются отдельно |

В таблице для строк Paint и ниже префикс `Plugins/` означает `ReloaderPlugins/Plugins/`. Резервные `.bak`, повреждённые `.corrupt-*` и предыдущие копии принадлежат тому же модулю. `PluginStorage` переносит семейство файлов при первом обращении; при отличии нового файла помещает старый в `Data/Legacy`. При ошибке перенос прекращается и текущий сеанс использует старый путь. Исторические ключи в исходниках сохранены именно для совместимости. Проверка ключей: `python tools/test-data-ownership.py`.

Модули Catalog, SlowMo, Animations, Weapons и Prefabs не сохраняют собственные настройки. Animations **читает** `GTA V/menyooStuff/PedAnimList.txt` и `FavouriteAnims.xml`; Streamer читает карты Menyoo. Это внешние данные, переносить или менять их при обновлении ModPack нельзя.

## Отдельные программы и временные файлы

- Ped Importer хранит журнал, настройки управления редактором и временные задания в `%LOCALAPPDATA%/ModPack/PedImporter`. Режим «Избранное Menyoo» изменяет `GTA V/menyooStuff/FavouritePeds.xml` только после явного нажатия кнопки; при замене создаётся резервная копия. Полный импорт отдельно управляет AddonPeds Editor по команде пользователя.
- PedToProp хранит свою копию зависимостей Sollumz, рабочие файлы и журналы ошибок в `%LOCALAPPDATA%/ModPack/PedToProp`. Итог записывает в выбранный пользователем каталог. Исходные педы и GTA не меняет.
- `modpack.local.json` содержит локальный путь игры и не попадает в Git или релиз. `.modpack-local` содержит локальную карту и резервные копии этапов переноса. Сборочные `bin`, `obj` и `dist` пересоздаются инструментами.
- `ReloaderPlugins/Plugins/Newtonsoft.Json.dll` — общая поставляемая зависимость Core, не пользовательские данные. Копия в корне `scripts` относится к отдельной установке и командой `Update` не заменяется.

## Доставка и откат

1. Проверить `modpack.cmd Check` и профильные тесты, затем сравнить исходники с `dist` и игрой. `Update` синхронизирует игровые `.cs` и инструменты Crash, проверяет SHA-256 и отпечаток исходников. При ошибке копирования восстанавливает прежние байты; тайм-аут ожидания журнала означает, что доставка могла состояться, но перезагрузка не подтверждена.
2. Для закрытой GTA применять `modpack.cmd Update -Offline`; при открытой игре использовать обычный `Update` и дождаться `Reload OK` в **новых** строках журнала. Если другая часть рабочего дерева не синхронизирована, обновлять конкретный модуль только после проверки полного набора изменений.
3. `Reloader.dll` собирать отдельно, закрыть GTA, сохранить старый DLL и заменить его вручную с проверкой хеша. `Update` DLL не копирует. Откат загрузчика — вернуть сохранённый DLL при закрытой GTA вместе с совместимым набором исходников.
4. Ped Importer и PedToProp собирать своими скриптами; они публикуются в `dist/Tools/<модуль>`. Закрыть программу перед заменой её EXE. Старую копию программы хранить для отката отдельно от `%LOCALAPPDATA%`.
5. Релиз создаёт пять архивов: единый установщик для GTA, отдельно плагины и загрузчик, а также две программы. Проверка упаковки распаковывает их во временную папку и сравнивает хеши. Пользовательские `Data`, `%LOCALAPPDATA%`, карты Menyoo, AddonPeds и игровые архивы в них не включаются.

При восстановлении старой версии данных не удалять текущие настройки. Сначала сделать отдельную копию; затем сверить SHA-256 резервной копии и восстановленного файла. Инструменты переноса и установки системных дампов запускать только по их отдельным инструкциям.

Подробные команды находятся в [руководстве разработчика](DEVELOPMENT_RU.md), правила модулей — в [структуре проекта](STRUCTURE_RU.md).
Собственные модели и изображения перечислены отдельно в [реестре ресурсов](ASSETS_RU.md); текущие пять архивов их не содержат.
