using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using LemonUI;
using LemonUI.Menus;

namespace RemoveDroppedPedsMod
{
    public class RemoveDroppedPedsPlugin : IGtaPlugin
    {
        // Настройки
        private bool _modEnabled = true;
        private float _scanRadius = 500f;
        private int _scanIntervalMs = 5000;

        // LemonUI
        private readonly ObjectPool _pool = new ObjectPool();
        private NativeMenu _mainMenu;
        private NativeCheckboxItem _enableCheckbox;
        private NativeListItem<int> _radiusList;
        private NativeListItem<string> _intervalList;

        // Варианты частоты сканирования: мс и названия пунктов (по индексу _intervalList)
        private static readonly int[] IntervalOptionsMs = { 1000, 2000, 3000, 5000, 10000 };
        private static readonly string[] IntervalNames = { "1с", "2с", "3с", "5с", "10с" };

        // Таймеры (GameTime - быстрее чем DateTime.Now)
        private int _lastScanGameTime = 0;
        private int _lastSaveGameTime = 0;
        private bool _settingsDirty = false;

        // Константы
        private const float MinScanRadius = 50f;
        private const float MaxScanRadius = 5000f;

        /// <summary>
        /// Проверяет, прошло ли достаточно времени с учётом переполнения Game.GameTime.
        /// </summary>
        private static bool HasElapsed(int lastTime, int intervalMs)
        {
            int currentTime = Game.GameTime;
            uint elapsed = unchecked((uint)(currentTime - lastTime));
            return elapsed >= (uint)intervalMs;
        }

        // Сохранение настроек
        private class ModSettings
        {
            public bool ModEnabled { get; set; }
            public float ScanRadius { get; set; }
            public int ScanIntervalMs { get; set; }

            public ModSettings()
            {
                ModEnabled = true;
                ScanRadius = 500f;
                ScanIntervalMs = 5000;
            }
        }

        private readonly string _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ReloaderPlugins",
            "RemoveDroppedPeds.json"
        );
        private readonly SettingsSerializer _serializer = new SettingsSerializer();
        private ModSettings _settings;

        public void OnStart()
        {
            // Загрузка настроек
            _settings = LoadSettings();

            // Применение загруженных настроек
            _modEnabled = _settings.ModEnabled;
            _scanRadius = Math.Max(MinScanRadius, Math.Min(MaxScanRadius, _settings.ScanRadius));
            _scanIntervalMs = Array.IndexOf(IntervalOptionsMs, _settings.ScanIntervalMs) >= 0
                ? _settings.ScanIntervalMs
                : 5000;

            // Инициализация таймеров
            _lastScanGameTime = Game.GameTime;
            _lastSaveGameTime = Game.GameTime;

            // Создание меню
            _mainMenu = new NativeMenu("Remove Dropped Peds", "Удаление педов, упавших в воду");

            // Checkbox — вкл/выкл мод
            _enableCheckbox = new NativeCheckboxItem(
                "Включить мод",
                "Удалять педов в воде, включая мёртвых",
                _modEnabled);

            // Список — радиус сканирования (конкретные числа)
            int[] radiusOptions = { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1500, 2000 };
            _radiusList = new NativeListItem<int>(
                "Радиус сканирования",
                "Текущий: " + ((int)_scanRadius).ToString() + "м",
                radiusOptions);

            if (_radiusList.Items.Contains((int)_scanRadius))
                _radiusList.SelectedItem = (int)_scanRadius;
            else
                _radiusList.SelectedItem = 500;

            // Список — частота сканирования
            int intervalIdx = Array.IndexOf(IntervalOptionsMs, _scanIntervalMs);
            if (intervalIdx < 0) intervalIdx = 3;
            _intervalList = new NativeListItem<string>(
                "Частота сканирования",
                "Текущий: " + IntervalNames[intervalIdx],
                IntervalNames);
            _intervalList.SelectedIndex = intervalIdx;

            // Добавление элементов в меню
            _mainMenu.Add(_enableCheckbox);
            _mainMenu.Add(_radiusList);
            _mainMenu.Add(_intervalList);

            // Добавление меню в пул
            _pool.Add(_mainMenu);

            // Обработчики изменений (только помечаем dirty, сохраняем отложенно)
            _enableCheckbox.CheckboxChanged += (s, e) =>
            {
                _modEnabled = _enableCheckbox.Checked;
                _settings.ModEnabled = _modEnabled;
                MarkSettingsDirty();
            };

            _radiusList.ItemChanged += (s, e) =>
            {
                _scanRadius = _radiusList.SelectedItem;
                _radiusList.Description = "Текущий: " + ((int)_scanRadius).ToString() + "м";
                _settings.ScanRadius = _scanRadius;
                MarkSettingsDirty();
            };

            _intervalList.ItemChanged += (s, e) =>
            {
                _scanIntervalMs = IntervalOptionsMs[e.Index];
                _intervalList.Description = "Текущий: " + IntervalNames[e.Index];
                _settings.ScanIntervalMs = _scanIntervalMs;
                MarkSettingsDirty();
            };

            GTA.UI.Notification.PostTicker("~r~R~o~e~y~m~g~o~b~v~p~e~r~D~o~r~y~o~g~p~b~p~p~e~r~d~o~P~y~e~g~d~b~s~w~ мод загружен~n~Нажми ~y~H~w~ для меню", false, false);
        }

        public void OnTick()
        {
            // LemonUI требует вызова Process() каждый кадр regardless of Visible
            _pool.Process();

            // Отложенное сохранение настроек (раз в 3 секунды)
            if (_settingsDirty && HasElapsed(_lastSaveGameTime, 3000))
            {
                SaveSettings();
                _settingsDirty = false;
                _lastSaveGameTime = Game.GameTime;
            }

            // Автосканирование если мод включен. В меню и на паузе не сканируем —
            // незачем тратить нативы, когда игрок не в мире
            if (_modEnabled && !_mainMenu.Visible && !Game.IsPaused &&
                HasElapsed(_lastScanGameTime, _scanIntervalMs))
            {
                ScanAndRemoveUnderwaterPeds();
                _lastScanGameTime = Game.GameTime;
            }
        }

        public void OnKeyDown(Keys key)
        {
            // Открытие/закрытие меню — клавиша H
            if (key == Keys.H)
            {
                _mainMenu.Visible = !_mainMenu.Visible;
            }
        }

        public void OnAbort()
        {
            // Сохраняем настройки при завершении мода
            if (_settingsDirty)
            {
                SaveSettings();
            }
        }

        private void MarkSettingsDirty()
        {
            _settingsDirty = true;
        }

        private int ScanAndRemoveUnderwaterPeds()
        {
            var playerChar = Game.Player.Character;
            if (playerChar == null || !playerChar.Exists())
                return 0;

            int removedCount = 0;
            Vector3 playerPos = playerChar.Position;

            try
            {
                Ped[] peds = World.GetNearbyPeds(playerPos, _scanRadius);

                if (peds == null || peds.Length == 0)
                    return 0;

                foreach (Ped ped in peds)
                {
                    try
                    {
                        if (ped == null || !ped.Exists())
                            continue;

                        if (ped.IsPlayer || ped.IsInVehicle())
                            continue;

                        // Пед упал в воду (в том числе мёртвый, утонувший) — удаляем
                        if (!ped.IsInWater)
                            continue;

                        ped.MarkAsNoLongerNeeded();
                        ped.Delete();
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error("Ошибка удаления педа: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("Ошибка сканирования: " + ex.Message);
            }

            return removedCount;
        }

        private ModSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = _serializer.Deserialize(json);

                    if (settings != null)
                    {
                        settings.ScanRadius = Math.Max(MinScanRadius, Math.Min(MaxScanRadius, settings.ScanRadius));
                        ModLogger.Info(string.Format("Настройки загружены: Enabled={0}, Radius={1}, Interval={2}мс", settings.ModEnabled, settings.ScanRadius, settings.ScanIntervalMs));
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("Ошибка загрузки настроек: " + ex.Message);
            }
            ModLogger.Info("Используются настройки по умолчанию");
            return new ModSettings();
        }

        private void SaveSettings()
        {
            try
            {
                // Создаём директорию если отсутствует
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = _serializer.Serialize(_settings);
                File.WriteAllText(_settingsPath, json);
                ModLogger.Info("Настройки сохранены в файл");
            }
            catch (Exception ex)
            {
                ModLogger.Error("Ошибка сохранения настроек: " + ex.Message);
            }
        }

        /// <summary>
        /// Обёртка для сериализации/десериализации настроек.
        /// Изолирует использование устаревшего JavaScriptSerializer.
        /// </summary>
        private sealed class SettingsSerializer
        {
            private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

            public ModSettings Deserialize(string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                    return new ModSettings();

                var settings = _serializer.Deserialize<ModSettings>(json);
                return settings ?? new ModSettings();
            }

            public string Serialize(ModSettings settings)
            {
                return _serializer.Serialize(settings);
            }
        }
    }

    /// <summary>
    /// Логгер мода — записывает логи в файл и в Debug консоль.
    /// Файл: scripts/RemoveDroppedPeds.log
    /// </summary>
    internal static class ModLogger
    {
        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ReloaderPlugins",
            "RemoveDroppedPeds.log"
        );

        private static readonly object _lock = new object();

        public static void Info(string message)
        {
            Log("INFO", message);
        }

        public static void Warn(string message)
        {
            Log("WARN", message);
        }

        public static void Error(string message)
        {
            Log("ERROR", message);
        }

        private static void Log(string level, string message)
        {
            string formatted = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}", DateTime.Now, level, message);
            Debug.WriteLine("[RemoveDroppedPeds] " + message);

            try
            {
                lock (_lock)
                {
                    var directory = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.AppendAllText(LogPath, formatted + Environment.NewLine);
                }
            }
            catch
            {
                // Игнорируем ошибки логирования — не должны ломать мод
            }
        }
    }
}
