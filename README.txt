GTA V ModPack
=============

ModPack - набор скриптовых модов для GTA V на базе собственного загрузчика
Reloader. Загрузчик один (`Reloader.dll`), а сами моды лежат отдельными
файлами в папке `ReloaderPlugins\Plugins` и компилируются при запуске игры.

GitHub release 1.1:
  https://github.com/Wladisl4W/GTA-V_ModPack/releases/tag/v1.1

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  БЫСТРОЕ ОБНОВЛЕНИЕ ИЗ РЕЛИЗА
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Для обновления уже установленного ModPack скачайте файл:

  ModPack-Plugins-1.1.zip

Из архива нужно взять все файлы и скопировать их с заменой в папку:

  GTA V\scripts\ReloaderPlugins\Plugins\

Важно:
  • архив релиза содержит именно содержимое папки `Plugins`;
  • распаковывать его нужно в `ReloaderPlugins\Plugins`, не в корень GTA V;
  • `Reloader.dll` в этот архив не входит, потому что он нужен только для
    полной установки или отдельного обновления загрузчика.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ПОЛНАЯ УСТАНОВКА
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Требования:
  • Script Hook V        — http://www.dev-c.com/gtav/scripthookv/
  • Script Hook V .NET 3 — https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases
  • LemonUI              — https://github.com/LemonUIbyLemon/LemonUI

Установка:
  1. Установите Script Hook V и Script Hook V .NET в корень GTA V.
     LemonUI.SHVDN3.dll должен лежать в:

       GTA V\scripts\

  2. Из папки `Ready To Use` скопируйте `Reloader.dll` в:

       GTA V\scripts\

  3. Из папки `Ready To Use` скопируйте папку `ReloaderPlugins` целиком в:

       GTA V\scripts\

  4. Запустите игру. Плагины скомпилируются автоматически.

После установки структура должна выглядеть так:

  GTA V\scripts\Reloader.dll
  GTA V\scripts\ReloaderPlugins\Plugins\*.cs
  GTA V\scripts\ReloaderPlugins\Plugins\Newtonsoft.Json.dll

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  МОДЫ И ГОРЯЧИЕ КЛАВИШИ
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  • Reloader (F5)
    Перезагрузить все плагины без перезапуска игры.

  • Modded Camera (T)
    Кинематографическая камера и пролёты по точкам.
    Backspace - назад/выход из текущего меню.

    Особенности:
      - сохранение и загрузка путей камеры;
      - индивидуальная длительность, FOV, цвет и режим интерполяции для нод;
      - режимы интерполяции: Linear, SmoothStop, SmoothNoStop;
      - совместимость со старыми сохранёнными путями;
      - меню настроек камеры не меняет общий FOV, FOV настраивается через ноды;
      - зацикливание пролётки: последняя нода держится по своей длительности,
        затем происходит резкий переход к первой ноде.

  • Rainbow Paint (I)
    Радужная покраска машин.
    Выделение машины работает в Object Spooner (Menyoo): включите его,
    наведите камеру на машину и нажмите I.
    В рандомайзере можно выбрать участвующие цвета, включить дым шин в цвет
    основного цвета и задать кастомный номер для машин в мире.

  • Remove Dropped Peds (H)
    Удаление педов, упавших в воду, включая мёртвых.

  • MenyooStreamer (U)
    Стриминг педов из Menyoo.
    Радиусы стриминга считаются по горизонтали без учёта высоты, область
    работает как цилиндр.

  • Frozen Dynamic (K)
    Заморозка/разморозка всех NPC. Танцы сохраняются.

  • Shark Rider
    Автономный мод: в воде рядом спавнится акула и подплывает к игроку.
    WASD - плыть, Shift - всплыть, Ctrl - погрузиться. Выход на сушу
    отпускает акулу.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ФАЙЛЫ, КОТОРЫЕ СОЗДАЮТСЯ АВТОМАТИЧЕСКИ
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  • scripts\ReloaderPlugins\Reloader.log
    Лог загрузчика.

  • scripts\ReloaderPlugins\compile_errors.txt
    Ошибки компиляции плагинов.

  • scripts\ReloaderPlugins\Paths\
    Сохранённые пролёты Modded Camera.

  • scripts\ReloaderPlugins\Menyoostreamer.ini
    Настройки MenyooStreamer.

  • scripts\ReloaderPlugins\RainbowPaintSettings.json
    Настройки Rainbow Paint: цвета рандомайзера, дым шин и кастомный номер.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ДЛЯ РАЗРАБОТЧИКОВ
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Исходники лежат в папке:

  Source Code

Структура:
  • Source Code\Reloader\ — проект загрузчика (.NET Framework 4.8)
  • Source Code\Plugins\  — исходники плагинов (*.cs)
  • Source Code\build.bat — сборка Reloader.dll

Сборка:

  cd "Source Code"
  dotnet build -c Release

Во время разработки можно менять файлы плагинов в:

  GTA V\scripts\ReloaderPlugins\Plugins\

Reloader заметит изменения и перекомпилирует их при перезагрузке плагинов
через F5.
