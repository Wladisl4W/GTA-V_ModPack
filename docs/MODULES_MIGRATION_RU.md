# Перестройка `modules`: карта переноса

Статус: исходники и готовые файлы перенесены в новую структуру; локальная GTA обновлена. На 21 ноября 2026 года 39 игровых `.cs` и `Reloader.dll` сверены по SHA-256. Все 59 файлов пролёток скопированы в новый путь с отдельной резервной копией; старые файлы оставлены. 22 ноября пользователь подтвердил запуск GTA и работу меню после переноса. Системные пути дампов переключены на `_System/Diagnostics/Data/Crash` и сверены в реестре; 22 файла дампов скопированы и проверены по хешу. Устаревшие каталоги из игрового `Plugins` перенесены в `development/.local/backups/legacy-game-plugins-20261122`. Пути в таблицах заданы относительно `scripts/` или `GTA V/scripts/`; некоторые разделы ниже сохраняют первоначальный план как историческое объяснение переноса.

## Принцип

`scripts/` одновременно содержит исходники игровых плагинов и готовые файлы для копирования в `GTA V/scripts/`. Исходники Reloader, проекты сборки, тесты и скрипты разработки находятся вне `scripts/`. Успешная сборка загрузчика кладёт `Reloader.dll` в `scripts/`; готовые Ped Importer и PedToProp также входят в `scripts/` и в Git. `Check` ничего не меняет в готовых файлах, `Build` их обновляет.

Верхний уровень `scripts/`: `README.md`, `Reloader.dll`, `ReloaderPlugins/`. Внутри `ReloaderPlugins/`: `Plugins/` и `Tools/`. У игрового меню своя папка в `Plugins`, у раздела — одинаковое имя подпапки в `Code` и `Data`. В `modules` нет пользовательских настроек, пролёток или журналов; их создаёт и хранит установленная игра. Релизный архив содержит содержимое `scripts/` без дополнительного корневого каталога. Игра получает обновление обычным копированием с заменой; удаляемые старые пути перечисляются в примечании к релизу.

## Целевая структура

```text
scripts/
  README.md
  Reloader.dll
  ReloaderPlugins/
    Plugins/
      Camera/Code/                 # пролётки K
      Camera/Data/                 # появляется в GTA; пролётки сохраняются
      Templates/Code/              # меню «Пресеты» T
      Templates/Data/
      Optimization/Code/{Streamer,Cleanup,PedControl}/
      Optimization/Data/{Streamer,Cleanup,PedControl}/
      VehiclePaint/Code/           # I
      VehiclePaint/Data/
      Misc/Code/{Weapons,Wildlife,FollowCamera,VehicleSwap}/
      Misc/Data/{Weapons,Wildlife,FollowCamera,VehicleSwap}/
      Animations/Code/             # Y
      Prefabs/Code/                # ;
      SlowMo/Code/                 # Mouse4
      Catalog/Code/                # F5
      _System/Shared/Code/
      _System/Shared/Libraries/Newtonsoft.Json.dll
      _System/Navigation/Code/
      _System/Diagnostics/Code/{Crash,Dev}/
      _System/Diagnostics/Data/{Crash,Dev}/
      _System/Diagnostics/Tools/CrashWatcher.exe
    Tools/
      PedImporter/
      PedToProp/
```

Пустые `Data` в Git не обязательны: мод создаёт их в игре при первом использовании. Если у раздела нет сохраняемых данных, подпапка `Data` не нужна. Подразделы в `Misc` и `Optimization` остаются независимыми плагинами; папка меню не обязывает создавать один общий плагин.

## Код: нынешний владелец → новый владелец

| Сейчас в `scripts/` | Цель в `scripts/ReloaderPlugins/Plugins/` | Условие |
| --- | --- | --- |
| `Core/src/Code/*` | `_System/Shared/Code/*` | Перенести общий контракт и реестр меню. |
| `Hub/src/Code/HubPlugin.cs` | `_System/Navigation/Code/*` | Разделить маршрутизацию L и O, сохранив возврат из подменю. |
| `Catalog/src/Code/*` | `Catalog/Code/*` | Прямой перенос. |
| `Camera/src/Code/*`, кроме `Follow/` | `Camera/Code/*` | Отделить команды следования, которые сейчас также есть в `MenuService.cs`. |
| `Camera/src/Code/Follow/*` | `Misc/Code/FollowCamera/*` | Не переносить отдельно до разделения общего состояния камеры. |
| `Paint/src/Code/VehiclePaint/*` | `VehiclePaint/Code/*` | Выделить из частичного класса общие поля, нужные другим разделам. |
| `Paint/src/Code/Presets/*` | `Templates/Code/*` | Разделить настройки покраски и пресетов. |
| `Paint/src/Code/VehicleSwap/*` | `Misc/Code/VehicleSwap/*` | Сохранить выбор машин и отмену незавершённого обмена. |
| `Weapons/src/Code/*` | `Misc/Code/Weapons/*` | Прямой перенос после поддержки вложенных `Code`. |
| `Wildlife/src/Code/*` | `Misc/Code/Wildlife/*` | Перенести акула/крокодил вместе; сохранить Mouse5. |
| `Streamer/src/Code/*` | `Optimization/Code/Streamer/*` | Сохранить пути внешних данных Menyoo как ссылки; сами файлы Menyoo не перемещать. |
| `Cleanup/src/Code/*` | `Optimization/Code/Cleanup/*` | Прямой перенос кода. |
| `PedControl/src/Code/*` | `Optimization/Code/PedControl/*` | Прямой перенос кода. |
| `Animations/src/Code/*` | `Animations/Code/*` | Прямой перенос. |
| `Prefabs/src/Code/*` | `Prefabs/Code/*` | Прямой перенос. |
| `SlowMo/src/Code/*` | `SlowMo/Code/*` | Прямой перенос. |
| `Dev/src/Code/*` | `_System/Diagnostics/Code/Dev/*` | Сохранить Ctrl+F10. |
| `Crash/src/Code/*` | `_System/Diagnostics/Code/Crash/*` | Перенастроить CrashWatcher и пути системных дампов отдельно. |

Исходники `Loader/src` переходят за пределы `modules` в каталог разработки; `Reloader.dll` собирается в корень `modules`. Исходники `PedImporter/src` и `PedToProp/src` также переходят за пределы `modules`; собранные программы и нужные им ресурсы переходят в `ReloaderPlugins/Tools/PedImporter` и `.../PedToProp`. Точное имя каталога разработки следует выбрать при переносе вместе с обновлением всех скриптов сборки.

Текущая общая зависимость `Plugins/Newtonsoft.Json.dll` переходит в `_System/Shared/Libraries/`. Файлы `Plugins/Crash/Tools/CrashWatcher.exe`, его конфигурация и управляющие скрипты переходят в `_System/Diagnostics/Tools/`; эти пути зашиты в коде и установщике системного захвата, поэтому простого перемещения недостаточно.

## Данные в существующей игре

| Текущий путь под `scripts/ReloaderPlugins/Plugins/` | Будущий путь | Проверка/решение |
| --- | --- | --- |
| `Camera/Data/Saves/Paths/*` | `Camera/Data/Paths/*` | **Сохранить все файлы, включая `.bak`**, проверить число, относительные имена и SHA-256 до и после. На момент инвентаризации: 59 файлов (51 JSON и 8 `.bak`). Старый путь не удалять до проверки в игре. |
| `Camera/Data/Logs/ModdedCamera.log` | `Camera/Data/Logs/ModdedCamera.log` | Журнал может остаться на месте; это не пользовательское сохранение. |
| `Paint/Data/Settings/RainbowPaintPresets.json*` | `Templates/Data/Presets/RainbowPaintPresets.json*` | Новый формат хранения отделить от настроек покраски; текущие настройки пользователю сохранять необязательно. |
| `Paint/Data/Settings/RainbowPaintPassengerModels.json*` | `Templates/Data/Presets/RainbowPaintPassengerModels.json*` | То же. |
| `Paint/Data/Settings/RainbowPaintSettings.json*`, `RainbowPaintExceptions.json*` | `VehiclePaint/Data/Settings/*` и `Templates/Data/Presets/*` по владельцу поля | Файлы смешанного назначения нельзя перенести целиком без разбора полей. Сделать явную миграцию либо начать новые настройки, сохранив архив старых. |
| `Wildlife/Data/*` | `Misc/Data/Wildlife/*` | В новой версии пути не должны ссылаться на старую папку. |
| `Streamer/Data/*`, `Cleanup/Data/*`, `PedControl/Data/*` | `Optimization/Data/{Streamer,Cleanup,PedControl}/*` | Разные подразделы хранят данные раздельно. |
| `Core/Data/Logs/*` | `_System/Shared/Data/Logs/*` | Общий журнал. |
| `Loader/Data/Logs/*` | `_System/Diagnostics/Data/Loader/Logs/*` | Журнал Reloader, включая путь в коде и проверках. |
| `Dev/Data/*`, `Crash/Data/*` | `_System/Diagnostics/Data/{Dev,Crash}/*` | Для Crash отдельно сверить исполняемый файл, записи реестра и системные пути дампов. |

Иные пользовательские настройки на момент планирования не обязательны к сохранению, но старые файлы перед миграцией всё равно архивируются. Существующие файлы Menyoo и AddonPeds вне модпака никогда не переносить и не изменять.

## Что удалить из старой установки после перехода

После успешной сборки, копирования, сверки и проверки запуска в примечании к релизу перечислить точные старые каталоги `Plugins/{Core,Hub,Paint,Weapons,Wildlife,Streamer,Cleanup,PedControl,Dev,Crash,Loader}` и остальные переименованные каталоги. Удалять только старые `.cs` и поставляемые бинарные файлы **после** отдельного сохранения их `Data`; не удалять целые папки с данными командой общего удаления. Проверить также старые `scripts/ReloaderPlugins/Plugins/*.cs` и отдельные пути инструментов в старом релизе. Конкретный список файлов формировать сравнением прежнего релизного архива с новым, без предположения, что каждый установленный пользователь имеет одинаковую версию.

Обычное копирование папки не удаляет старые `.cs`. Пока прежние исходники лежат в игре, загрузчик может скомпилировать обе версии. Поэтому инструкция обновления должна содержать точные пути удаления и порядок: закрыть GTA → резервная копия `Data` → убрать указанные старые файлы → скопировать новый архив → запустить GTA → проверить журнал и пролётки.

## Условия начала переноса

1. Добавить загрузчику и `modpack.cmd Check` одинаковый рекурсивный поиск `.cs` в `Plugins/**/Code/**`; не сканировать `Data` и `Tools`.
2. Переделать реестр модулей, сборку, упаковку, тесты и README на единую переносимую структуру. Проверка не должна менять `modules`; сборка должна обновлять DLL/EXE только после успешного результата.
3. На копии данных проверить перенос путей камеры с резервной копией и SHA-256; затем проверить настоящую игру. Не выполнять групповое удаление старых файлов до этого.
4. Обновить системные пути CrashWatcher и дампов отдельным шагом при закрытой GTA.
5. Сформировать релизный архив из содержимого `modules`, проверить его распаковкой во временную папку и совпадением хешей. Текущие игровые данные и журналы в архив не включать.
