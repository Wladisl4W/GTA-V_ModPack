# Структура ModPack

## Переносимая папка

Содержимое [scripts](../scripts) соответствует `GTA V/scripts`: в корне находятся `Reloader.dll` и `ReloaderPlugins`. Папка `ReloaderPlugins/Plugins` содержит игровые меню и служебную `_System`. В `ReloaderPlugins/Tools` находятся Ped Importer и PedToProp. У каждого инструмента в корне только файл запуска `.cmd` и папка `Files` с `Runtime`, `Source`, `Tests` и инструкциями. [Карта меню](MENU_CODE_RU.md) показывает клавиши и владельцев кода.

Внутри каждого меню `Code` содержит игровые `.cs`. Для сложных меню есть одинаково названные подразделы в `Code` и `Data`: например, `Misc/Code/Wildlife` и игровая `Misc/Data/Wildlife`. Пользовательских настроек, журналов и пролёток в репозитории нет. Папки `Data` создаются в установленной GTA. Сохранённые пролётки находятся в `Camera/Data/Paths`.

Исходники Reloader, CrashWatcher, общие тесты и сборочная инфраструктура лежат в [development](../development). Исходники и тесты отдельных программ находятся рядом с ними в `scripts/ReloaderPlugins/Tools`. Скрипты сборки и проверки — в [development/build](../development/build), реестр исходников — в [development/registry/modules.json](../development/registry/modules.json). `development/.build` и `development/.local` — локальные результаты и резервные копии, в Git не входят. Menyoo и AddonPeds находятся вне модпака.

## Команды

- `modpack.cmd Check` проверяет компиляцию игровых исходников и строит CrashWatcher в `development`, не обновляя переносимую папку.
- `modpack.cmd Build` после проверки обновляет `scripts/Reloader.dll`, оба инструмента и файлы CrashWatcher.
- `modpack.cmd Map` строит локальную поисковую карту `development/.local/project-map.html`.
- `development/build/package-release.ps1` создаёт один ZIP из содержимого `scripts`, распаковывает его во временную папку и сравнивает все файлы по SHA-256.

## Обновление существующей GTA

Обычное копирование заменяет совпадающие имена и оставляет пользовательские `Data`, но не удаляет устаревшие `.cs`. При переходе со старой структуры используйте [инструкцию установки](INSTALL_RU.md) и [карту переноса](MODULES_MIGRATION_RU.md). Перед удалением старых исходников сохраните резервную копию. Прошлые версии и их пути описаны в `docs/history`.
