# Общее ядро (Core)

Core предоставляет общий контракт плагинов, владение управлением, безопасную работу с файлами и диагностику. Отдельного меню или клавиши у него нет.

## Как работает

Reloader компилирует файлы Core вместе с игровыми плагинами. Каждый плагин реализует глобальный `IGtaPlugin` с `OnStart`, `OnTick`, `OnKeyDown`, `OnAbort`. `ControlSession` не даёт двум модам одновременно захватить игрока или камеру. `MenuTextInput` блокирует горячие клавиши во время ввода текста. `PluginStorage` направляет файлы в папку владельца и переносит старые расположения. `ModuleMenus` хранит пользовательские названия, подписи клавиш, описания и порядок верхних меню; каталог F5 и заголовки читают их оттуда.

## Данные

Общий журнал — `scripts/ReloaderPlugins/Plugins/Core/Data/Logs/PluginErrors.log`; ошибки переноса данных — `StorageMigration.log`. Библиотека `Newtonsoft.Json.dll` остаётся в корне `Plugins` как общая зависимость. Core не содержит пользовательских настроек.

## Зависимости и ограничения

Нужны Reloader и ScriptHookVDotNet3. Исторические ключи `ModdedCamera`, `RainbowPaint`, `SharkRider` и другие остаются частью маршрутизации данных: смена имён файлов требует отдельной миграции. Глобальное имя `IGtaPlugin` сохраняется, поскольку загрузчик находит интерфейс через отражение.

## Исходники и проверка

Основные файлы: [IGtaPlugin.cs](../../modules/ReloaderPlugins/Plugins/_System/Shared/Code/IGtaPlugin.cs), [ControlSession.cs](../../modules/ReloaderPlugins/Plugins/_System/Shared/Code/ControlSession.cs), [PluginStorage.cs](../../modules/ReloaderPlugins/Plugins/_System/Shared/Code/PluginStorage.cs). Остальные перечислены в [реестре](../../catalog/modules.json). Проверки: `modpack.cmd Check`, `development/Core/tests/test-modpack.ps1` и `development/Core/tests/test-storage.ps1`.

## Обновление и откат

Core доставляется вместе с другими исходниками командой `modpack.cmd Update`. Перед изменением маршрутов хранения сохраните пользовательские данные; при откате верните совместимую проверенную версию всех связанных плагинов.
