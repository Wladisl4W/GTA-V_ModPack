# Карта названий и совместимости

Исторический черновик решений от 20 ноября 2026 года, составленный до переименования. **Предложения ниже частично уже выполнены и не описывают текущее состояние.** Актуальные имена и пути: [`STRUCTURE_RU.md`](STRUCTURE_RU.md), [`development/registry/modules.json`](../development/registry/modules.json). Предыдущий снимок — в [`INVENTORY_RU.md`](INVENTORY_RU.md). Игровые клавиши сохранены.

| Сейчас: ID / заголовок | Предлагаемое имя для игрока | Программное имя | Совместимость и решение |
| --- | --- | --- | --- |
| `Core` / без меню | Ядро модпака | `Core` | Сохранить общий интерфейс и ключ `Logs` до проверки миграции. |
| `Loader` / без меню | Загрузчик | `Loader` | `Reloader.dll`, `Reloader.log`, формат загрузки затрагивают поставку; мигрировать отдельно. |
| `Catalog` / «ModPack» | Каталог модов | `Catalog` | Согласовать русские строки F5 с меню. |
| `Camera` / «ModdedCamera» | Камера | `Camera` | `ModdedCamera` остаётся ключом хранения до проверенной миграции путей и копий. |
| `SlowMo` / замедление | Замедление времени | `SlowMo` | Сохранить Mouse4 и общий контроль камеры. |
| `Paint` / «Радужная покраска» | Машины и покраска | `Paint` до решения о границе модуля | Меню уже включает обмен моделей, пресеты, пассажиров; `RainbowPaint*` — имена файлов данных и ключ хранения. Решить, оставить ли функции вместе. |
| `Animations` / «Анимации педов» | Анимации педов | `Animations` | Не переименовывать внешние файлы Menyoo. |
| `Weapons` / «Weapon Manager» | Оружие и удары | `Weapons` | UI можно локализовать без изменения класса. |
| `Wildlife` / «Shark Rider» | Акула и крокодил | `Wildlife` | `SharkRider*` — ключ хранения и имена настроек/журнала. Решить, остаются ли оба животных вместе. |
| `Streamer` / «MenyooStreamer» | Загрузка педов | `Streamer` | `MenyooStreamer.ini` и журнал сохранить либо мигрировать с тестом. Menyoo не менять. |
| `Prefabs` / «Заготовки» | Заготовки Spooner | `Prefabs` | Название UI уже понятно; внешний Object Spooner не менять. |
| `PedControl` / «Frozen Dynamic» | Управление педами | `PedControl` | `FrozenDynamic.log` оставить до явного решения о логах. |
| `Cleanup` / «Remove Dropped Peds» | Очистка упавших педов | `Cleanup` | `RemoveDroppedPeds.json` и лог требуют совместимой миграции при смене имени. |
| `Dev` / «ModPack» | Инструменты разработки | `Dev` | `DevelopmentSettings.json` и лог сохранить либо мигрировать. |
| `Crash` / без меню | Диагностика сбоев | `Crash` | Системные пути дампов и инструменты менять только отдельным проверяемым шагом. |
| `PedImporter` / «ModPack - Ped Importer» | Импорт педов | `PedImporter` | Два режима остаются раздельными; внешние файлы Menyoo и AddonPeds не переименовывать. |
| `PedToProp` / «ModPack - Ped to Prop» | Пед в объект | `PedToProp` | Путь `%LOCALAPPDATA%` и формат выходных файлов не менять без миграции. |

## Программные файлы

Реализованное правило этапа 4: имя файла соответствует основному типу; части класса получают вид `ClassName.Feature.cs`. Машинный список прежних и новых путей находится в [`development/registry/source-renames.json`](../development/registry/source-renames.json). Таблица ниже объясняет переход; исторические имена настроек остаются действующими.

| Текущая группа файлов | Предлагаемый стиль | Что проверить |
| --- | --- | --- |
| `_PluginInterface.cs`, `ModPack_Control.cs`, `ModPack_Core.cs`, `ModPack_Storage.cs`, `PluginLog.cs` | Имена по фактическому основному типу; для частичного класса `ModPack.Feature.cs` | Общие типы, порядок компиляции, интерфейс загрузчика. |
| `ModdedCameraPlugin.cs`, `ModdedCamera_*.cs` | `CameraPlugin.cs` и имена фактических сервисов/типов; части через точку | Сериализованные пути, отражение типов и все 13 исходников. |
| `CinematicMomentPlugin.cs` | `SlowMoPlugin.cs` | Регистрация `IGtaPlugin`, связанные сервисы. |
| `RainbowPaintPlugin.cs`, `RainbowPaintPlugin_MotorcyclePassengers.cs`, `RainbowPaintPlugin_Presets.cs` | `PaintPlugin.cs`, `PaintPlugin.MotorcyclePassengers.cs`, `PaintPlugin.Presets.cs` | Поля JSON и пресеты сохранены. |
| `PedAnimationsPlugin.cs`, `PedAnimations_Catalog.cs` | `AnimationsPlugin.cs`, `AnimationCatalog.cs` | Меню и список Menyoo не меняются. |
| `WeaponManagerPlugin.cs` | `WeaponsPlugin.cs` | Регистрация и ссылки в каталоге. |
| `SharkRiderPlugin.cs`, `SharkRider_BiteController.cs`, `SharkRider_CrocodileHunter.cs` | `WildlifePlugin.cs` и отдельные имена классов контроллеров | JSON и особенности состояний акулы/крокодила. |
| `MenyooStreamerPlugin.cs` | `StreamerPlugin.cs` | INI, внешние пути Menyoo. |
| `SpoonerPrefabsPlugin.cs` | `PrefabsPlugin.cs` | Интеграция Spooner. |
| `FrozenDynamicPlugin.cs` | `PedControlPlugin.cs` | Поведение NPC и лог. |
| `RemoveDroppedPedsPlugin.cs` | `CleanupPlugin.cs` | Настройки и лог. |
| `DevelopmentPlugin.cs` | `DevPlugin.cs` | Настройки, диагностика. |
| `CrashLoggerPlugin.cs` | `CrashPlugin.cs` | Связь с CrashWatcher и путями захвата. |
| `ModPackCatalogPlugin.cs` | `CatalogPlugin.cs` | F5 и записи каталога. |

## Внешние имена, которые не меняются автоматически

`PluginStorage` сопоставляет владельцев `ModdedCamera → Camera`, `RainbowPaint → Paint`, `SharkRider → Wildlife`, `RemoveDroppedPeds → Cleanup`, `FrozenDynamic → PedControl`, `MenyooStreamer → Streamer`, `Development → Dev`, `Logs → Core`. Это часть маршрутизации и переноса старых данных. Смена класса или заголовка меню не требует одновременно менять файл настроек; смена самого ключа или имени файла требует резервной копии, проверки совпадающих и конфликтующих файлов и повторного запуска.

`development/registry/modules.json`, `Modules`, документация, F5, `Source Code/Plugins`, `Ready To Use`, `development/build/modpack.ps1`, генератор карты, тесты и `.github/workflows/release.yml` ссылаются на ID или пути. Переход на `scripts/<ID>/src` нужно делать как согласованную миграцию с проверкой релизного ZIP и загрузки GTA.

## Решения перед следующим этапом

1. Сохранять ли `Paint` единым модулем «Машины и покраска», включая пассажиров и обмен моделей?
2. Сохранять ли `Wildlife` единым модулем «Акула и крокодил»?
3. Решено на этапе 3: канонический каталог README и кода — `scripts/<ID>`.
4. Сохранять ли старые имена файлов данных как совместимые идентификаторы надолго, а русский текст использовать только в интерфейсе?

Остальные имена и границы функций остаются предложениями для следующих этапов.
