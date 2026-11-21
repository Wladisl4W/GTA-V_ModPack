# Разработка ModPack

## Где менять код

Игровые `.cs` находятся в [modules/ReloaderPlugins/Plugins](../modules/ReloaderPlugins/Plugins). Папка верхнего уровня соответствует меню в GTA; подразделы внутри `Code` — отдельные функции или плагины. Общий код и пользовательские названия меню — в `_System/Shared/Code`. Технические проекты загрузчика, CrashWatcher, Ped Importer и PedToProp, а также тесты лежат в [development](../development). Все игровые `.cs` перечислены в [реестре](../catalog/modules.json).

Загрузчик компилирует файлы во всех вложенных `Code`, пропускает `Data` и `Tools`. Изменяя пункт меню, сохраняйте связь его названия с `ModuleMenus.cs` и проверьте F5. У функции с данными используйте соответствующую папку в `Data`; не записывайте пользовательские файлы в `modules`.

## Проверка и сборка

Команды из корня репозитория:

```text
modpack.cmd Check
modpack.cmd Build
modpack.cmd Map
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/package-release.ps1
```

`Check` проверяет исходники компилятором, который использует GTA, и строит CrashWatcher в проекте разработки; переносимая папка не меняется. `Build` после успешной проверки копирует собранный `Reloader.dll` в `modules`, публикует Ped Importer и PedToProp в `modules/ReloaderPlugins/Tools` и обновляет CrashWatcher. Архив релиза содержит содержимое `modules` и повторно проверяется после распаковки.

Профильные проверки: `development/Loader/tests/test-reloader.ps1`, `development/Core/tests/test-modpack.ps1`, `development/Core/tests/test-storage.ps1`, `development/Camera/tests/test-camera-zero-duration.ps1`, `development/Paint/tests/test-rainbow-diagnostics.ps1`, а также `tools/test-docs.py`, `tools/test-data-ownership.py`, `tools/test-update.ps1` и `tools/test-project-map.ps1`. Сборка не подтверждает игровое поведение; после изменения меню проверяйте его в GTA и свежий журнал `_System/Diagnostics/Data/Loader/Logs/Reloader.log`.

## Данные и доставка

`modules` не содержит пользовательских `Data`. У установленной игры настройки и журналы создаются рядом с кодом соответствующего раздела. Сохранённые пролётки камеры находятся в `Camera/Data/Paths`; при переходе со старой версии все файлы и `.bak` копируются с проверкой SHA-256, старый путь сохраняется до проверки в игре. Menyoo и AddonPeds не входят в модпак.

Для существующей установки читайте [инструкцию обновления](INSTALL_RU.md): копирование папки заменяет файлы с тем же именем, но не удаляет прежние `.cs`. `Reloader.dll` заменяйте только при закрытой GTA. Команда `modpack.cmd Update -Offline` служит для локальной разработки: она проверяет код и синхронизирует игровые `.cs`, но не устанавливает новый загрузчик. Отдельные бинарные инструменты публикуются командой `Build` и копируются вместе с `modules`.

При изменении CrashWatcher учитывайте внешние системные пути дампов. Переименование папки инструмента в игре само по себе не меняет пути в реестре Windows. Не удаляйте старую папку Crash/Data, пока захват дампов не перенастроен и не проверен.
