# Разработка ModPack

## Где менять код

Игровые `.cs` находятся в [scripts/ReloaderPlugins/Plugins](../scripts/ReloaderPlugins/Plugins). Папка верхнего уровня соответствует меню в GTA; подразделы внутри `Code` — отдельные функции или плагины. Общий код и пользовательские названия меню — в `_System/Shared/Code`. Исходники и тесты Ped Importer и PedToProp находятся в `scripts/ReloaderPlugins/Tools/<инструмент>/Files`; проекты загрузчика и CrashWatcher, общие тесты и сборка — в [development](../development). Все игровые `.cs` перечислены в [реестре](../development/registry/modules.json).

Загрузчик компилирует файлы во всех вложенных `Code`, пропускает `Data` и `Tools`. Изменяя пункт меню, сохраняйте связь его названия с `ModuleMenus.cs` и проверьте F5. У функции с данными используйте соответствующую папку в `Data`; не записывайте пользовательские файлы в `scripts`.

## Проверка и сборка

Команды из корня репозитория:

```text
modpack.cmd Check
modpack.cmd Build
modpack.cmd Map
powershell.exe -NoProfile -ExecutionPolicy Bypass -File development/build/package-release.ps1
```

`Check` проверяет исходники компилятором, который использует GTA, и строит CrashWatcher в проекте разработки; переносимая папка не меняется. `Build` после успешной проверки копирует собранный `Reloader.dll` в `scripts`, публикует Ped Importer и PedToProp в `scripts/ReloaderPlugins/Tools` и обновляет CrashWatcher. Архив релиза содержит содержимое `scripts` и повторно проверяется после распаковки.

Профильные проверки: `development/Loader/tests/test-reloader.ps1`, `development/tests/Core/test-modpack.ps1`, `development/tests/Core/test-storage.ps1`, `development/tests/Camera/test-camera-zero-duration.ps1`, `development/tests/Paint/test-rainbow-diagnostics.ps1`, а также `development/build/test-docs.py`, `development/build/test-data-ownership.py`, `development/build/test-update.ps1` и `development/build/test-project-map.ps1`. Сборка не подтверждает игровое поведение; после изменения меню проверяйте его в GTA и свежий журнал `_System/Diagnostics/Data/Loader/Logs/Reloader.log`.

## Данные и доставка

`scripts` не содержит пользовательских `Data`. У установленной игры настройки и журналы создаются рядом с кодом соответствующего раздела. Сохранённые пролётки камеры находятся в `Camera/Data/Paths`; при переходе со старой версии все файлы и `.bak` копируются с проверкой SHA-256, старый путь сохраняется до проверки в игре. Menyoo и AddonPeds не входят в модпак.

Для существующей установки читайте [инструкцию обновления](INSTALL_RU.md): копирование папки заменяет файлы с тем же именем, но не удаляет прежние `.cs`. `Reloader.dll` заменяйте только при закрытой GTA. Команда `modpack.cmd Update -Offline` служит для локальной разработки: она проверяет код и синхронизирует игровые `.cs`, но не устанавливает новый загрузчик. Отдельные бинарные инструменты публикуются командой `Build` и копируются вместе с `scripts`.

При изменении CrashWatcher учитывайте внешние системные пути дампов. Переименование папки инструмента в игре само по себе не меняет пути в реестре Windows. Не удаляйте старую папку Crash/Data, пока захват дампов не перенастроен и не проверен.
