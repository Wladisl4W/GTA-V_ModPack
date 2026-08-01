using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using PluginLogging;

namespace RainbowPaintMod
{
    public class RainbowPaintPlugin : IGtaPlugin
    {
        private struct RainbowColor
        {
            public string Name;
            public Color Paint;

            public RainbowColor(string name, int r, int g, int b)
            {
                Name = name;
                Paint = Color.FromArgb(r, g, b);
            }
        }

        // Радужный слот: какие цвета машины переливаются
        private class RainbowSlot
        {
            public Vehicle Vehicle;
            public bool Primary;
            public bool Secondary;
        }

        // Исключение рандомайзера: название модели + её хеш
        public struct ModelExclusion
        {
            public string Name;
            public int Hash;

            public ModelExclusion(string name, int hash)
            {
                Name = name;
                Hash = hash;
            }
        }

        private readonly ObjectPool _pool = new ObjectPool();
        private NativeMenu _menu;
        private NativeCheckboxItem _bothSameCheckbox;
        private NativeListItem<string> _colorSelector;
        private NativeListItem<string> _colorSelector2;
        private NativeListItem<string> _paintTypeList;
        private NativeListItem<string> _speedList;
        private NativeItem _resetItem;
        private NativeItem _randomizeAllItem;
        private NativeMenu _exceptionsMenu;
        private NativeItem _exceptionsDisplayItem;
        private readonly List<ModelExclusion> _excludedModels = new List<ModelExclusion>();
        private readonly string _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ReloaderPlugins",
            "RainbowPaintExceptions.json"
        );
        private readonly ExclusionsSerializer _serializer = new ExclusionsSerializer();
        private bool _menuEnabled;
        private readonly Random _rng = new Random();

        // Типы краски: название + стоковый индекс нужного типа (0-159)
        private static readonly string[] PaintTypeNames =
            { "Металлик", "Мат", "Метал", "Хром" };
        private static readonly int[] PaintTypeStockColors =
            { 0, 12, 117, 120 };

        private Vehicle _currentVehicle;
        private Blip _highlightBlip;

        // Все машины в радужном режиме (по слотам)
        private readonly List<RainbowSlot> _rainbowSlots = new List<RainbowSlot>();

        // Общий оттенок радуги (по времени)
        private float _rainbowHue;
        private int _lastHueStep = -1;
        private int _lastHueGameTime;
        private int _lastKeyGameTime;

        // Кэш маркера: рейкаст редкий, а рисуем каждый кадр — чтобы не мигал
        private Vehicle _markerVehicle;
        private Vector3 _markerPos;
        private int _markerMissCount;
        private int _lastMarkerRaycastTime;

        // Полный цикл перелива в секундах (по индексу _speedList): быстро/средне/медленно
        private static readonly float[] CycleSeconds = { 3f, 6f, 12f };

        // Квантование оттенка: машины перекрашиваются только при реальной смене
        // цвета. 256 шагов на цикл — плавно, но без нативных вызовов каждый кадр.
        private const int HueSteps = 256;

        // Индекс "Радужный (перелив)" в _rainbowColors — должен оставаться последним
        private const int RainbowIndex = 8;

        private readonly List<RainbowColor> _rainbowColors = new List<RainbowColor>
        {
            new RainbowColor("Красный", 255, 40, 40),
            new RainbowColor("Оранжевый", 255, 111, 0),
            new RainbowColor("Жёлтый", 255, 255, 0),
            new RainbowColor("Зелёный", 0, 230, 0),
            new RainbowColor("Голубой", 0, 220, 255),
            new RainbowColor("Синий", 30, 60, 255),
            new RainbowColor("Фиолетовый", 75, 0, 130),
            new RainbowColor("Розовый", 255, 20, 147),
            new RainbowColor("Радужный (перелив)", 0, 0, 0)
        };

        private static Color ColorFromHSV(float hue)
        {
            float h = hue * 6f;
            int i = (int)Math.Floor(h);
            float f = h - i;
            float q = 1f - f;
            float r, g, b;
            switch (i % 6)
            {
                case 0: r = 1f; g = f; b = 0f; break;
                case 1: r = q; g = 1f; b = 0f; break;
                case 2: r = 0f; g = 1f; b = f; break;
                case 3: r = 0f; g = q; b = 1f; break;
                case 4: r = f; g = 0f; b = 1f; break;
                default: r = 1f; g = 0f; b = q; break;
            }
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        public void OnStart()
        {
            // Загрузка сохранённых исключений рандомайзера
            LoadExclusions();

            GTA.UI.Notification.PostTicker("~r~R~o~a~y~i~g~n~b~b~p~o~r~w~o~P~y~a~g~i~b~n~p~t~w~ мод загружен~n~Нажми ~b~I~w~ для меню покраски", false, false);

            _menu = new NativeMenu("Радужная Покраска", "Выбери цвет");
            _pool.Add(_menu);

            _bothSameCheckbox = new NativeCheckboxItem(
                "Красить всю машину",
                "Вкл: первый ползунок красит всю машину. Выкл: первый красит основной цвет, второй — вторичный.",
                true);
            _bothSameCheckbox.CheckboxChanged += (s, e) =>
            {
                if (_colorSelector2 == null) return;

                if (_bothSameCheckbox.Checked)
                {
                    _colorSelector2.Enabled = false;
                    _colorSelector2.Description = "Не используется — галочка включена, машина красится цветом основного";
                    _colorSelector2.SelectedIndex = _colorSelector.SelectedIndex;
                }
                else
                {
                    _colorSelector2.Enabled = _menuEnabled;
                    _colorSelector2.Description = "Красит только вторичный цвет машины";
                }
            };
            _menu.Add(_bothSameCheckbox);

            _colorSelector = new NativeListItem<string>("Основной цвет");
            foreach (var c in _rainbowColors)
                _colorSelector.Add(c.Name);
            _colorSelector.SelectedIndex = 0;
            _colorSelector.Activated += (s, e) => ApplyPrimaryColor();
            _colorSelector.ItemChanged += (s, e) =>
            {
                if (_bothSameCheckbox != null && _bothSameCheckbox.Checked && _colorSelector2 != null)
                    _colorSelector2.SelectedIndex = _colorSelector.SelectedIndex;
            };
            _menu.Add(_colorSelector);

            _colorSelector2 = new NativeListItem<string>("Вторичный цвет");
            foreach (var c in _rainbowColors)
                _colorSelector2.Add(c.Name);
            _colorSelector2.SelectedIndex = 0;
            _colorSelector2.Activated += (s, e) => ApplySecondaryColor();
            _colorSelector2.Description = "Не используется — галочка включена, машина красится цветом основного";
            _colorSelector2.Enabled = false;
            _menu.Add(_colorSelector2);

            _paintTypeList = new NativeListItem<string>(
                "Тип краски",
                "Стандарт / Металлик / Мат / Метал / Хром",
                PaintTypeNames);
            _paintTypeList.SelectedIndex = 0;
            _menu.Add(_paintTypeList);

            _speedList = new ClampedSpeedList(
                "Скорость перелива",
                "Быстро / Средне / Медленно",
                "Быстро", "Средне", "Медленно");
            _speedList.SelectedIndex = 1;
            _speedList.ItemChanged += (s, e) =>
            {
                _speedList.Description = "Текущая скорость: " + e.Object;
            };
            _menu.Add(_speedList);

            var resetItem = new NativeItem("Сбросить цвет (заводской)");
            resetItem.Activated += (s, e) => ResetColor();
            _resetItem = resetItem;
            _menu.Add(resetItem);

            var randomizeAllItem = new NativeItem("Зарандомить все машины");
            randomizeAllItem.Activated += (s, e) => RandomizeAllVehicles();
            _randomizeAllItem = randomizeAllItem;
            _menu.Add(randomizeAllItem);

            // Подменю исключений: модели, которые рандомайзер не красит
            _exceptionsMenu = new NativeMenu("Исключения", "Эти модели рандомайзер не красит");
            _pool.Add(_exceptionsMenu);

            var addModelItem = new NativeItem(
                "Добавить модель под прицелом",
                "Наведись на машину, модели которой не нужно красить, и нажми Enter. В список попадёт вся модель — такие машины рандомайзер будет пропускать.");
            addModelItem.Activated += (s, e) => AddAimedModel();
            _exceptionsMenu.Add(addModelItem);

            var clearModelsItem = new NativeItem("Очистить все");
            clearModelsItem.Activated += (s, e) =>
            {
                _excludedModels.Clear();
                UpdateExceptionsDisplay();
                SaveExclusions();
                GTA.UI.Screen.ShowSubtitle("~y~Все исключения удалены.", 3000);
            };
            _exceptionsMenu.Add(clearModelsItem);

            _exceptionsDisplayItem = new NativeItem("Исключения", "Нет");
            _exceptionsMenu.Add(_exceptionsDisplayItem);
            UpdateExceptionsDisplay();

            // Возврат в главное меню по Back
            _exceptionsMenu.Closed += (s, e) =>
            {
                _menu.Visible = true;
            };

            var exceptionsItem = new NativeItem("Настроить исключения рандомайзера");
            exceptionsItem.Activated += (s, e) =>
            {
                _menu.Visible = false;
                _exceptionsMenu.Visible = true;
            };
            _menu.Add(exceptionsItem);

            _lastHueGameTime = Game.GameTime;
            _lastHueStep = -1;
            _lastKeyGameTime = Game.GameTime;
            _lastMarkerRaycastTime = Game.GameTime;
        }

        public void OnTick()
        {
            _pool.Process();

            // Радуга для всех машин в списке — по времени, не по кадрам
            if (_rainbowSlots.Count > 0)
            {
                int now = Game.GameTime;
                uint elapsed = unchecked((uint)(now - _lastHueGameTime));
                _lastHueGameTime = now;

                float cycleSeconds = GetCycleSeconds();
                _rainbowHue += (float)elapsed / 1000f / cycleSeconds;
                if (_rainbowHue >= 1f)
                    _rainbowHue -= (float)Math.Floor(_rainbowHue);

                // Перекрашиваем только когда оттенок перешёл на новый шаг.
                // Без раннего return: маркер и остальное рисуются каждый кадр
                int step = (int)(_rainbowHue * HueSteps);
                if (step != _lastHueStep)
                {
                    _lastHueStep = step;

                    Color c = ColorFromHSV(_rainbowHue);

                    for (int i = _rainbowSlots.Count - 1; i >= 0; i--)
                    {
                        var slot = _rainbowSlots[i];
                        if (slot.Vehicle == null || !slot.Vehicle.Exists())
                        {
                            _rainbowSlots.RemoveAt(i);
                            continue;
                        }

                        if (slot.Primary)
                            slot.Vehicle.Mods.CustomPrimaryColor = c;
                        if (slot.Secondary)
                            slot.Vehicle.Mods.CustomSecondaryColor = c;
                    }
                }
            }

            // Индикатор над машиной, на которую смотришь
            DrawAimedMarker();

            // Метка выделения живёт, пока открыто меню
            if (!_menu.Visible && _highlightBlip != null && _highlightBlip.Exists())
            {
                _highlightBlip.Delete();
                _highlightBlip = null;
            }
        }

        public void OnKeyDown(Keys key)
        {
            if (key != Keys.I) return;

            // Защита от автоповтора при удержании
            int now = Game.GameTime;
            uint sinceLast = unchecked((uint)(now - _lastKeyGameTime));
            if (sinceLast < 300) return;
            _lastKeyGameTime = now;

            // Выделение машин работает только в Object Spooner (Menyoo)
            Vehicle v = IsSpoonerModeActive() ? GetVehiclePlayerIsLookingAt() : null;

            // Меню открыто: навёлся на машину — переключаемся на неё, иначе — закрываем
            bool menuOpen = _menu.Visible || (_exceptionsMenu != null && _exceptionsMenu.Visible);
            if (menuOpen)
            {
                if (v == null || !v.Exists())
                {
                    _menu.Visible = false;
                    if (_exceptionsMenu != null)
                        _exceptionsMenu.Visible = false;
                    return;
                }
                SelectVehicle(v);
                if (_exceptionsMenu != null)
                    _exceptionsMenu.Visible = false;
                GTA.UI.Screen.ShowSubtitle("~g~Машина выделена! Выбирай цвет в меню.", 4000);
                return;
            }

            if (v == null || !v.Exists())
            {
                // Без машины: меню открывается, но всё заблокировано
                _currentVehicle = null;
                if (_highlightBlip != null && _highlightBlip.Exists())
                {
                    _highlightBlip.Delete();
                    _highlightBlip = null;
                }
                _menu.Name = "Машина не выделена";
                SetMenuEnabled(false);
                _menu.Visible = true;
                GTA.UI.Screen.ShowSubtitle(IsSpoonerModeActive()
                    ? "~y~Машина не выделена. Наведись на машину и нажми ~b~I~w~."
                    : "~y~Выделение машин работает только в Object Spooner (Menyoo).", 4000);
                return;
            }

            SelectVehicle(v);
            _menu.Visible = true;
            GTA.UI.Screen.ShowSubtitle("~g~Машина выделена! Выбирай цвет в меню.", 4000);
        }

        private void SelectVehicle(Vehicle v)
        {
            _currentVehicle = v;

            if (_highlightBlip != null && _highlightBlip.Exists())
                _highlightBlip.Delete();

            _highlightBlip = v.AddBlip();
            _highlightBlip.Color = BlipColor.Yellow;
            _highlightBlip.Scale = 1.0f;
            _highlightBlip.Name = "Выделенная машина";

            _menu.Name = "Машина: " + GetVehicleName(v) + " (" + v.Model.Hash.ToString("X8") + ")";
            SetMenuEnabled(true);
        }

        // Серые (заблокированные) пункты, когда машина не выделена.
        // Галочка и тип краски остаются доступными — они настраивают рандомайзер.
        private void SetMenuEnabled(bool enabled)
        {
            _menuEnabled = enabled;
            _colorSelector.Enabled = enabled;
            _colorSelector2.Enabled = enabled && !_bothSameCheckbox.Checked;
            _resetItem.Enabled = enabled;
        }

        // Красит все машины на карте в случайные цвета по текущим настройкам
        private void RandomizeAllVehicles()
        {
            Vehicle[] all = World.GetAllVehicles();
            if (all == null || all.Length == 0)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Рядом нет машин.", 3000);
                return;
            }

            // Собираем цели, пропуская исключённые модели
            List<Vehicle> targets = new List<Vehicle>();
            int skippedCount = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Vehicle veh = all[i];
                if (veh == null || !veh.Exists())
                    continue;

                if (IsExcluded(veh))
                {
                    skippedCount++;
                    continue;
                }

                targets.Add(veh);
            }

            if (targets.Count == 0)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Нет машин для покраски (все в исключениях).", 3000);
                return;
            }

            // Сортируем по позиции: рядом стоящие машины соседствуют в списке,
            // чтобы им не доставались одинаковые цвета
            targets.Sort(delegate(Vehicle a, Vehicle b)
            {
                float dx = a.Position.X - b.Position.X;
                if (dx > 0.01f) return 1;
                if (dx < -0.01f) return -1;
                float dy = a.Position.Y - b.Position.Y;
                if (dy > 0f) return 1;
                if (dy < 0f) return -1;
                return 0;
            });

            // Радужные машины: один бросок — 80% на первую (случайную),
            // и если она выпала — 20% на вторую (другую случайную). Максимум 2.
            int rainbowFirst = -1;
            int rainbowSecond = -1;
            if (_rng.NextDouble() < 0.8)
            {
                rainbowFirst = _rng.Next(0, targets.Count);
                if (targets.Count > 1 && _rng.NextDouble() < 0.2)
                {
                    rainbowSecond = _rng.Next(0, targets.Count - 1);
                    if (rainbowSecond >= rainbowFirst)
                        rainbowSecond++;
                }
            }

            bool bothSame = _bothSameCheckbox.Checked;
            int lastSolidIndex = RainbowIndex - 1;
            int lastColorIdx = -1;

            int count = 0;
            int rainbowCount = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Vehicle veh = targets[i];
                try
                {
                    var slot = FindSlot(veh);
                    if (slot != null)
                        _rainbowSlots.Remove(slot);

                    ApplyPaintTypeToVehicle(veh);

                    if (i == rainbowFirst || i == rainbowSecond)
                    {
                        rainbowCount++;
                        slot = GetOrCreateSlot(veh);
                        slot.Primary = true;
                        slot.Secondary = true;

                        Color hue = ColorFromHSV(_rainbowHue);
                        veh.Mods.CustomPrimaryColor = hue;
                        veh.Mods.CustomSecondaryColor = hue;
                        veh.DirtLevel = 0f;
                        count++;
                        continue;
                    }

                    // Цвет без повтора цвета предыдущей (соседней) машины
                    int idx = _rng.Next(0, lastSolidIndex);
                    if (lastColorIdx >= 0 && idx >= lastColorIdx)
                        idx++;
                    lastColorIdx = idx;

                    var color = _rainbowColors[idx];
                    Color c = color.Paint;

                    if (bothSame)
                    {
                        veh.Mods.CustomPrimaryColor = c;
                        veh.Mods.CustomSecondaryColor = c;
                    }
                    else
                    {
                        int idx2 = _rng.Next(0, lastSolidIndex);
                        if (lastColorIdx >= 0 && idx2 >= lastColorIdx)
                            idx2++;
                        veh.Mods.CustomPrimaryColor = c;
                        veh.Mods.CustomSecondaryColor = _rainbowColors[idx2].Paint;
                    }

                    veh.DirtLevel = 0f;
                    count++;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("RainbowPaint: PaintVehicles", ex);
                }
            }

            GTA.UI.Screen.ShowSubtitle("~g~Покрашено машин: " + count + " (~p~радужных: " + rainbowCount + "~g~)~n~~w~Пропущено (исключения): " + skippedCount + ".", 5000);
        }

        // Модель машины в списке исключений?
        private bool IsExcluded(Vehicle v)
        {
            for (int i = 0; i < _excludedModels.Count; i++)
            {
                if (_excludedModels[i].Hash == v.Model.Hash)
                    return true;
            }
            return false;
        }

        // Добавляет в исключения модель машины, на которую смотришь
        private void AddAimedModel()
        {
            try
            {
                // Рейкаст работает только в Object Spooner (Menyoo)
                Vehicle v = IsSpoonerModeActive() ? GetVehiclePlayerIsLookingAt() : null;
                if (v == null || !v.Exists())
                {
                    GTA.UI.Screen.ShowSubtitle("~r~Наведись на машину в Object Spooner и попробуй снова.", 3000);
                    return;
                }

                int hash = v.Model.Hash;
                for (int i = 0; i < _excludedModels.Count; i++)
                {
                    if (_excludedModels[i].Hash == hash)
                    {
                        GTA.UI.Screen.ShowSubtitle("~y~Модель '" + GetVehicleName(v) + "' уже в исключениях.", 3000);
                        return;
                    }
                }

                string displayName = GetVehicleName(v);
                _excludedModels.Add(new ModelExclusion(displayName, hash));
                UpdateExceptionsDisplay();
                SaveExclusions();
                GTA.UI.Screen.ShowSubtitle("~g~Модель '" + displayName + "' добавлена в исключения.~n~~w~Все такие машины рандомайзер не красит.", 4000);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: AddAimedModel", ex);
            }
        }

        // Обновление строки-списка исключений в подменю
        private void UpdateExceptionsDisplay()
        {
            if (_exceptionsDisplayItem == null) return;

            if (_excludedModels.Count == 0)
            {
                _exceptionsDisplayItem.Title = "Исключения: нет";
                _exceptionsDisplayItem.AltTitle = "Рандомайзер красит все машины";
                return;
            }

            string names = "";
            for (int i = 0; i < _excludedModels.Count; i++)
            {
                if (i > 0) names += ", ";
                names += _excludedModels[i].Name;
            }
            if (names.Length > 50)
                names = names.Substring(0, 47) + "...";

            _exceptionsDisplayItem.Title = "Исключения (" + _excludedModels.Count + ")";
            _exceptionsDisplayItem.AltTitle = names;
        }

        public void OnAbort()
        {
            try
            {
                if (_highlightBlip != null && _highlightBlip.Exists())
                    _highlightBlip.Delete();
                _highlightBlip = null;

                _rainbowSlots.Clear();

                if (_menu != null)
                    _menu.Visible = false;
                if (_exceptionsMenu != null)
                    _exceptionsMenu.Visible = false;
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: OnAbort", ex);
            }

            SaveExclusions();
        }

        // Загрузка исключений из файла
        private void LoadExclusions()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                    return;

                string json = File.ReadAllText(_settingsPath);
                List<ModelExclusion> list = _serializer.Deserialize(json);
                if (list == null)
                    return;

                _excludedModels.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(list[i].Name))
                        continue;
                    _excludedModels.Add(new ModelExclusion(list[i].Name, list[i].Hash));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: LoadExclusions", ex);
            }
        }

        // Сохранение исключений в файл
        private void SaveExclusions()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                File.WriteAllText(_settingsPath, _serializer.Serialize(_excludedModels));
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: SaveExclusions", ex);
            }
        }

        // Изолирует использование устаревшего JavaScriptSerializer
        private sealed class ExclusionsSerializer
        {
            private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

            public List<ModelExclusion> Deserialize(string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                    return null;
                return _serializer.Deserialize<List<ModelExclusion>>(json);
            }

            public string Serialize(List<ModelExclusion> exclusions)
            {
                return _serializer.Serialize(exclusions);
            }
        }

        private float GetCycleSeconds()
        {
            if (_speedList == null) return 6f;

            int idx = _speedList.SelectedIndex;
            if (idx < 0 || idx >= CycleSeconds.Length)
                idx = 1;
            return CycleSeconds[idx];
        }

        // Ползунок без зацикливания: на краях списка стрелки не переносят
        // на противоположный конец, как в стандартном LemonUI
        private class ClampedSpeedList : NativeListItem<string>
        {
            public ClampedSpeedList(string title, string description, params string[] items)
                : base(title, description, items)
            {
            }

            public override void GoLeft()
            {
                if (SelectedIndex <= 0) return;
                SelectedIndex--;
            }

            public override void GoRight()
            {
                if (SelectedIndex >= Items.Count - 1) return;
                SelectedIndex++;
            }
        }

        private int GetPaintTypeIndex()
        {
            if (_paintTypeList == null) return 0;

            int idx = _paintTypeList.SelectedIndex;
            if (idx < 0 || idx >= PaintTypeStockColors.Length)
                idx = 0;
            return idx;
        }

        // Ставим стоковый цвет нужного типа краски, чтобы кастомный RGB
        // накладывался с этим типом (мат/хром/метал видны явно)
        private void ApplyPaintTypeToVehicle(Vehicle v)
        {
            try
            {
                int typeIdx = GetPaintTypeIndex();
                int stock = PaintTypeStockColors[typeIdx];

                Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, stock, stock);

                // Металлик в игре = классический цвет + перламутр того же цвета
                if (typeIdx == 0)
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v.Handle, stock, stock);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: ApplyPaintTypeToVehicle", ex);
            }
        }

        private string GetVehicleName(Vehicle v)
        {
            try
            {
                string localized = Game.GetLocalizedString(v.DisplayName);
                if (!string.IsNullOrEmpty(localized) && localized != v.DisplayName)
                    return localized;
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: GetVehicleName", ex);
            }
            return v.DisplayName;
        }

        // Зелёный шеврон над выделенной машиной, когда смотришь на неё
        private void DrawAimedMarker()
        {
            try
            {
                if (_currentVehicle == null || !_currentVehicle.Exists())
                {
                    _markerVehicle = null;
                    return;
                }

                // Выделение машин работает только в Object Spooner (Menyoo)
                if (!IsSpoonerModeActive())
                {
                    _markerVehicle = null;
                    return;
                }

                // Рейкаст дорогой — обновляем прицел ~20 раз в секунду
                int now = Game.GameTime;
                if ((uint)(now - _lastMarkerRaycastTime) >= 50u)
                {
                    _lastMarkerRaycastTime = now;

                    float dist;
                    Vehicle veh = RaycastVehicle(out dist);
                    if (veh != null && veh.Handle == _currentVehicle.Handle)
                    {
                        _markerVehicle = veh;
                        _markerMissCount = 0;
                        var dims = veh.Model.Dimensions;
                        float vehicleHeight = dims.Item2.Z - dims.Item1.Z;
                        _markerPos = veh.GetOffsetPosition(new Vector3(0f, 0f, vehicleHeight / 2f + 0.8f));
                    }
                    else
                    {
                        // Прячем маркер только после нескольких промахов подряд,
                        // иначе он мигает на границах кузова
                        _markerMissCount++;
                        if (_markerMissCount >= 3)
                            _markerVehicle = null;
                    }
                }

                if (_markerVehicle == null || _markerVehicle.Handle != _currentVehicle.Handle)
                    return;

                // Рисуем каждый кадр из кэша — маркер не мигает
                World.DrawMarker(MarkerType.Arrow, _markerPos, Vector3.Zero, Vector3.Zero,
                    new Vector3(0.35f, 0.35f, 0.35f), Color.FromArgb(220, 0, 255, 100));
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: DrawAimedMarker", ex);
            }
        }

        // Луч из камеры по машинам: машина + дистанция до точки попадания
        private Vehicle RaycastVehicle(out float hitDistance)
        {
            hitDistance = 0f;
            try
            {
                Camera cam = ScriptCameraDirector.RenderingCam;

                Vector3 source = cam.Position;
                Vector3 target = source + cam.Direction * 300f;

                RaycastResult ray = World.Raycast(source, target, IntersectFlags.Vehicles);
                if (ray.DidHit)
                {
                    Vehicle veh = ray.HitEntity as Vehicle;
                    if (veh != null && veh.Exists())
                    {
                        hitDistance = (ray.HitPosition - source).Length();
                        return veh;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: RaycastVehicle", ex);
            }
            return null;
        }

        private Vehicle GetVehiclePlayerIsLookingAt()
        {
            float dist;
            return RaycastVehicle(out dist);
        }

        // Menyoo Object Spooner: рендерится скриптовая камера, игра не на паузе.
        // Внешних флагов у Menyoo нет, поэтому определяем режим по камере.
        private bool IsSpoonerModeActive()
        {
            try
            {
                if (Game.IsPaused) return false;
                return ScriptCameraDirector.RenderingCam != null;
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: IsSpoonerModeActive", ex);
                return false;
            }
        }

        private RainbowSlot FindSlot(Vehicle v)
        {
            for (int i = 0; i < _rainbowSlots.Count; i++)
            {
                if (_rainbowSlots[i].Vehicle == v)
                    return _rainbowSlots[i];
            }
            return null;
        }

        private RainbowSlot GetOrCreateSlot(Vehicle v)
        {
            var slot = FindSlot(v);
            if (slot == null)
            {
                slot = new RainbowSlot { Vehicle = v };
                _rainbowSlots.Add(slot);
            }
            return slot;
        }

        private void RemoveSlotIfEmpty(Vehicle v)
        {
            var slot = FindSlot(v);
            if (slot != null && !slot.Primary && !slot.Secondary)
                _rainbowSlots.Remove(slot);
        }

        private bool IsRainbowIndex(int index)
        {
            return index == RainbowIndex;
        }

        // Enter на "Основной цвет" (или оба, если галочка включена)
        private void ApplyPrimaryColor()
        {
            if (_currentVehicle == null || !_currentVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle("~r~Машина исчезла!", 3000);
                return;
            }

            if (_bothSameCheckbox.Checked)
            {
                ApplyColorToBoth(_colorSelector.SelectedIndex);
                return;
            }

            int index = _colorSelector.SelectedIndex;

            if (IsRainbowIndex(index))
            {
                ApplyPaintTypeToVehicle(_currentVehicle);

                var slot = GetOrCreateSlot(_currentVehicle);
                slot.Primary = true;

                _currentVehicle.Mods.CustomPrimaryColor = ColorFromHSV(_rainbowHue);
                _currentVehicle.DirtLevel = 0f;
                GTA.UI.Screen.ShowSubtitle("~p~Радуга на основной цвет!", 3000);
            }
            else
            {
                var slot = FindSlot(_currentVehicle);
                if (slot != null)
                    slot.Primary = false;
                RemoveSlotIfEmpty(_currentVehicle);

                ApplyPaintTypeToVehicle(_currentVehicle);

                var color = _rainbowColors[index];
                _currentVehicle.Mods.CustomPrimaryColor = color.Paint;
                _currentVehicle.DirtLevel = 0f;

                GTA.UI.Screen.ShowSubtitle("~g~Основной цвет: " + color.Name + "!", 3000);
            }
        }

        // Enter на "Вторичный цвет" (при выключенной галочке)
        private void ApplySecondaryColor()
        {
            if (_currentVehicle == null || !_currentVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle("~r~Машина исчезла!", 3000);
                return;
            }

            int index = _colorSelector2.SelectedIndex;

            if (IsRainbowIndex(index))
            {
                ApplyPaintTypeToVehicle(_currentVehicle);

                var slot = GetOrCreateSlot(_currentVehicle);
                slot.Secondary = true;

                _currentVehicle.Mods.CustomSecondaryColor = ColorFromHSV(_rainbowHue);
                GTA.UI.Screen.ShowSubtitle("~p~Радуга на вторичный цвет!", 3000);
            }
            else
            {
                var slot = FindSlot(_currentVehicle);
                if (slot != null)
                    slot.Secondary = false;
                RemoveSlotIfEmpty(_currentVehicle);

                ApplyPaintTypeToVehicle(_currentVehicle);

                var color = _rainbowColors[index];
                _currentVehicle.Mods.CustomSecondaryColor = color.Paint;

                GTA.UI.Screen.ShowSubtitle("~g~Вторичный цвет: " + color.Name + "!", 3000);
            }
        }

        // Галочка включена — первый ползунок красит всю машину
        private void ApplyColorToBoth(int index)
        {
            if (IsRainbowIndex(index))
            {
                ApplyPaintTypeToVehicle(_currentVehicle);

                var slot = GetOrCreateSlot(_currentVehicle);
                slot.Primary = true;
                slot.Secondary = true;

                Color hueColor = ColorFromHSV(_rainbowHue);
                _currentVehicle.Mods.CustomPrimaryColor = hueColor;
                _currentVehicle.Mods.CustomSecondaryColor = hueColor;
                _currentVehicle.DirtLevel = 0f;
                GTA.UI.Screen.ShowSubtitle("~p~Радужный режим активирован! Машин в переливе: " + _rainbowSlots.Count + ".", 4000);
            }
            else
            {
                var slot = FindSlot(_currentVehicle);
                if (slot != null)
                {
                    slot.Primary = false;
                    slot.Secondary = false;
                }
                RemoveSlotIfEmpty(_currentVehicle);

                ApplyPaintTypeToVehicle(_currentVehicle);

                var color = _rainbowColors[index];
                _currentVehicle.Mods.CustomPrimaryColor = color.Paint;
                _currentVehicle.Mods.CustomSecondaryColor = color.Paint;
                _currentVehicle.DirtLevel = 0f;

                GTA.UI.Screen.ShowSubtitle("~g~Покрашено в " + color.Name + "!", 4000);
            }
        }

        private void ResetColor()
        {
            if (_currentVehicle == null || !_currentVehicle.Exists()) return;

            var slot = FindSlot(_currentVehicle);
            if (slot != null)
                _rainbowSlots.Remove(slot);

            _currentVehicle.Mods.CustomPrimaryColor = Color.Empty;
            _currentVehicle.Mods.CustomSecondaryColor = Color.Empty;
            _currentVehicle.DirtLevel = 0f;

            GTA.UI.Screen.ShowSubtitle("~y~Цвет сброшен на заводской.", 4000);
        }
    }
}
