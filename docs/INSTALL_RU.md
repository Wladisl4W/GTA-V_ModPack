# Установка и обновление

Для текущей структуры нужен GTA V с Script Hook V, ScriptHookVDotNet3 и LemonUI.SHVDN3. Содержимое [scripts](../scripts) или единого архива `ModPack-<версия>.zip` копируется прямо в `Grand Theft Auto V/scripts`. Закройте GTA перед заменой `Reloader.dll`.

## Новая установка

1. Скопируйте содержимое папки проекта `scripts` в `GTA V/scripts`. В результате `scripts/Reloader.dll` и `scripts/ReloaderPlugins/Plugins` должны существовать рядом.
2. Запустите GTA. F5 открывает каталог меню. В `scripts/ReloaderPlugins/Plugins/_System/Diagnostics/Data/Loader/Logs/Reloader.log` должна появиться свежая запись загрузки плагинов.
3. Ped Importer и PedToProp находятся в `scripts/ReloaderPlugins/Tools`; игра их автоматически не запускает.

## Обновление из предыдущей структуры

Обычное копирование не удаляет старые `.cs`. Перед обновлением сохраните копию `scripts/Reloader.dll`, всей папки `scripts/ReloaderPlugins/Plugins` и особенно `Camera/Data/Saves/Paths`. Затем при закрытой GTA удалите **только `.cs`** из прежних папок `Plugins/Core/Code`, `Plugins/Hub/Code`, `Plugins/Paint/Code`, `Plugins/Weapons/Code`, `Plugins/Wildlife/Code`, `Plugins/Streamer/Code`, `Plugins/Cleanup/Code`, `Plugins/PedControl/Code`, `Plugins/Dev/Code` и `Plugins/Crash/Code`. Удалите также старый `Plugins/Camera/Code/FollowCameraService.cs`, если он есть. Остальные старые `.cs` с прежними именами будут заменены копированием. Чужие файлы и любые папки `Data` не удаляйте.

Скопируйте содержимое нового архива в `GTA V/scripts` с заменой. Пролётки из `Plugins/Camera/Data/Saves/Paths` должны быть скопированы в `Plugins/Camera/Data/Paths`, включая `.bak`; до проверки в игре старую папку сохраняйте. Мод также умеет копировать старые пути при запуске, но отдельная копия важна для отката.

После запуска проверьте F5, меню камеры K, наличие сохранённых пролёток и свежий журнал загрузчика. Старые данные остальных модов могут остаться как резервная копия; новая версия пишет их в папки соответствующих меню. Системные пути CrashWatcher и дампов требуют отдельной перенастройки: до её проверки не удаляйте `Plugins/Crash/Data` и `Plugins/Crash/Tools`.

Исторический установщик и пять раздельных архивов версии 2.0 описаны в [старой инструкции](releases/v2.0.0.md). Переход на текущую структуру и удаление прежних файлов Ped Importer и PedToProp описаны в [примечаниях к 3.0.0](releases/v3.0.0.md). Menyoo, AddonPeds и игровые архивы GTA не входят в обновление.
