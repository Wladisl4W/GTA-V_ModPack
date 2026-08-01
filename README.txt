GTA V ModPack
=============

Набор модов для GTA V на базе собственного загрузчика Reloader.
Загрузчик один (Reloader.dll), а плагины — обычные C# файлы,
которые компилируются прямо при запуске игры.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ДЛЯ ПОЛЬЗОВАТЕЛЕЙ — папка "Ready To Use"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Требования:
  • Script Hook V            — http://www.dev-c.com/gtav/scripthookv/
  • Script Hook V .NET 3     — https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases

Установка:
  1. Установите Script Hook V и Script Hook V .NET в корень GTA V
  2. Скопируйте Reloader.dll в GTA V\scripts\
  3. Скопируйте папку ReloaderPlugins целиком в GTA V\scripts\
  4. Плагины компилируются автоматически при первом запуске игры

В GTA V\scripts\ должно быть:
  • Reloader.dll
  • ReloaderPlugins\Plugins\*.cs

Плагины и горячие клавиши:
  • Reloader (F5)            — перезагрузить все плагины без перезапуска игры
  • Rainbow Paint (I)        — радужная покраска машин
    — выделение машины работает в Object Spooner (Menyoo):
      включите его, наведите камеру на машину и нажмите I
    — 8 цветов + радужный перелив, типы краски, скорость перелива
    — "Зарандомить все машины" и список исключений (наведитесь на
      машину в подменю исключений и нажмите Enter)
  • Remove Dropped Peds (H)  — удаление упавших (под водой) педов
  • MenyooStreamer (U)       — стриминг педов из Menyoo
  • Modded Camera (T)        — пролёты камеры по точкам, Backspace — назад

Файлы модов (создаются автоматически):
  • scripts\ReloaderPlugins\Reloader.log       — лог загрузчика
  • scripts\ReloaderPlugins\compile_errors.txt — ошибки компиляции
  • scripts\ReloaderPlugins\Paths\             — сохранённые пролёты камер
  • scripts\ReloaderPlugins\Menyoostreamer.ini — настройки стримера

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ДЛЯ РАЗРАБОТЧИКОВ — папка "Source Code"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Структура:
  • Reloader\ — проект загрузчика (.NET Framework 4.8, Visual Studio)
  • Plugins\  — исходники плагинов (*.cs)
  • build.bat — сборка Reloader.dll

Сборка загрузчика:
  cd "Source Code"
  dotnet build -c Release

Плагины компилируются прямо в игре: положите *.cs в
scripts\ReloaderPlugins\Plugins\ — Reloader заметит изменения и
перекомпилирует их сам, без перезапуска игры (или нажмите F5).
