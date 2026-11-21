# Структура ModPack

## Переносимая папка

Содержимое [modules](../modules) соответствует `GTA V/scripts`: в корне находятся `Reloader.dll`, `README.md` и `ReloaderPlugins`. Папка `ReloaderPlugins/Plugins` содержит меню `Camera`, `Templates`, `Optimization`, `VehiclePaint`, `Misc`, `Animations`, `Prefabs`, `SlowMo`, `Catalog` и служебную `_System`. В `ReloaderPlugins/Tools` находятся Ped Importer и PedToProp. [Карта меню](MENU_CODE_RU.md) показывает клавиши и владельцев кода.

Внутри каждого меню `Code` содержит игровые `.cs`. Для сложных меню есть одинаково названные подразделы в `Code` и `Data`: например, `Misc/Code/Wildlife` и игровая `Misc/Data/Wildlife`. Пользовательских настроек, журналов и пролёток в репозитории нет. Папки `Data` создаются в установленной GTA. Сохранённые пролётки находятся в `Camera/Data/Paths`.

Исходники Reloader, проекты сборки, тесты и технические README лежат в [development](../development). Скрипты сборки и проверки — в [tools](../tools), реестр исходников — в [catalog/modules.json](../catalog/modules.json). `dist` и `.modpack-local` — локальные результаты и резервные копии, в Git не входят. Menyoo и AddonPeds находятся вне модпака.

## Команды

- `modpack.cmd Check` проверяет компиляцию игровых исходников и строит CrashWatcher в `development`, не обновляя переносимую папку.
- `modpack.cmd Build` после проверки обновляет `modules/Reloader.dll`, оба инструмента и файлы CrashWatcher.
- `modpack.cmd Map` строит локальную поисковую карту `.modpack-local/project-map.html`.
- `tools/package-release.ps1` создаёт один ZIP из содержимого `modules`, распаковывает его во временную папку и сравнивает все файлы по SHA-256.

## Обновление существующей GTA

Обычное копирование заменяет совпадающие имена и оставляет пользовательские `Data`, но не удаляет устаревшие `.cs`. При переходе со старой структуры используйте [инструкцию установки](INSTALL_RU.md) и [карту переноса](MODULES_MIGRATION_RU.md). Перед удалением старых исходников сохраните резервную копию. Прошлые версии и их пути описаны в `docs/history`.
