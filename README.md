# GTA V ModPack

Скриптовые моды и инструменты для GTA V. [Установка и обновление](docs/INSTALL_RU.md) · [Моды и клавиши](docs/MODS_RU.md) · [Структура](docs/STRUCTURE_RU.md) · [Разработка](docs/DEVELOPMENT_RU.md) · [Дорожная карта](docs/ROADMAP_RU.md).

## Где что лежит

| Папка | Назначение |
| --- | --- |
| [`scripts`](scripts) | Переносимая папка: её содержимое копируется в `GTA V/scripts`. |
| [`docs`](docs) | Инструкции, карта меню, соглашения и история проекта. |
| [`development`](development) | Исходники загрузчика и инструментов, сборка, тесты, реестр, ресурсы и локальные результаты. |

В `development/Loader` находятся исходники `Reloader.dll`; готовый DLL лежит в `scripts`. Скрипты сборки — в `development/build`, машинный реестр — в `development/registry`, исходники Ped Importer и PedToProp — в `scripts/ReloaderPlugins/Tools`. Рабочие ресурсы лежат в `development/assets`, новые неразобранные файлы временно складываются в `development/inbox`. Локальные результаты и личные настройки находятся в `development/.build`, `development/.local` и `development/config`.

В корне остались `README.md`, команда `modpack.cmd` и служебные файлы Git и инструментов разработки. README отдельных игровых модов находятся рядом с их кодом в `scripts/ReloaderPlugins/Plugins`.

`modpack.cmd Check` проверяет код, `modpack.cmd Build` обновляет готовые файлы в `scripts`, `modpack.cmd Map` открывает локальную карту проекта. [Сборка релиза](development/build/package-release.ps1) создаёт проверенный ZIP в `development/.build/releases` из содержимого `scripts`. Menyoo и AddonPeds остаются внешними программами и в модпак не входят.
