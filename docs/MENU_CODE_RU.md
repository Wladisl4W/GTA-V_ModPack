# Меню и исходники

Игровые исходники находятся в `modules/ReloaderPlugins/Plugins`. Содержимое `modules` копируется в `GTA V/scripts`. Папки `Data` появляются в игре при первом использовании; пользовательские файлы не входят в релиз.

| Вход | Меню | Код | Данные в игре |
| --- | --- | --- | --- |
| F5 | Каталог | `Catalog/Code` | — |
| K | Камера | `Camera/Code` | `Camera/Data/Paths` |
| T | Пресеты | `Templates/Code` | `Templates/Data/Settings` |
| O | Оптимизация | `Optimization/Code/{Streamer,Cleanup,PedControl}` | Соответствующие папки `Optimization/Data` |
| I | Покраска машины | `VehiclePaint/Code` | `VehiclePaint/Data/Settings` |
| L | Разное | `Misc/Code/{Weapons,Wildlife,FollowCamera,VehicleSwap}` | Соответствующие папки `Misc/Data` |
| Y | Анимации педов | `Animations/Code` | — |
| ; | Заготовки | `Prefabs/Code` | — |
| Mouse4 | Замедление | `SlowMo/Code` | — |

`_System/Shared` содержит общий код и реестр названий меню; `_System/Navigation` переключает подменю; `_System/Diagnostics` хранит игровую диагностику и CrashWatcher. `Reloader.dll` собирается из `development/Loader`, а готовые Ped Importer и PedToProp лежат в `modules/ReloaderPlugins/Tools`.

`PaintPlugin` остаётся одним частичным классом в нескольких папках меню. Это сохраняет общие состояния при нынешнем поведении. При дальнейшей переработке разделяйте его настройки по владельцам, сохраняя совместимость сохранённых пресетов.
