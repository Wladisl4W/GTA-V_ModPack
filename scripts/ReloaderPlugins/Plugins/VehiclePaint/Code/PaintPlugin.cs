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
using ModPack.Core;

namespace ModPack.Paint
{
    public partial class PaintPlugin : IGtaPlugin
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
            public float Hue;
            public int LastStep = -1;
            public float CycleSeconds;
            public bool SmokeMatches;
            public bool FromPreset;
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

        private class PaintAssignment
        {
            public Vector3 Position;
            public int PrimaryIndex;
            public int SecondaryIndex;

            public PaintAssignment(Vector3 position, int primaryIndex, int secondaryIndex)
            {
                Position = position;
                PrimaryIndex = primaryIndex;
                SecondaryIndex = secondaryIndex;
            }
        }

        private struct TextureVariationTarget
        {
            public Prop Prop;
            public Vector3 Position;
            public int Variation;
        }

        private readonly ObjectPool _pool = new ObjectPool();
        private NativeMenu _paintMenu;
        private NativeCheckboxItem _bothSameCheckbox;
        private NativeListItem<string> _colorSelector;
        private NativeListItem<string> _colorSelector2;
        private NativeListItem<string> _paintTypeList;
        private NativeListItem<string> _speedList;
        private NativeItem _resetItem;
        private NativeCheckboxItem _smokeMatchesPrimaryCheckbox;
        private readonly List<ModelExclusion> _excludedModels = new List<ModelExclusion>();
        private readonly string _exclusionsPath = ModPack.Core.PluginStorage.FilePath("RainbowPaint", "RainbowPaintExceptions.json");
        private readonly string _settingsPath = ModPack.Core.PluginStorage.FilePath("RainbowPaint", "RainbowPaintSettings.json");
        private readonly ExclusionsSerializer _serializer = new ExclusionsSerializer();
        private readonly SettingsSerializer _settingsSerializer = new SettingsSerializer();
        private RainbowPaintSettings _settings = new RainbowPaintSettings();
        private bool _menuEnabled;
        private readonly Random _rng = new Random();
        private TextureVariationTarget[] _textureVariationProps;
        private int _textureVariationIndex;
        private int _textureVariationChanged;
        private static readonly string[] TextureVariationModels =
        {
            "bkr_prop_biker_bblock_cor", "bkr_prop_biker_bblock_cor_02", "bkr_prop_biker_bblock_cor_03",
            "bkr_prop_biker_bblock_huge_01", "bkr_prop_biker_bblock_huge_02", "bkr_prop_biker_bblock_huge_03",
            "bkr_prop_biker_bblock_huge_04", "bkr_prop_biker_bblock_huge_05",
            "bkr_prop_biker_bblock_hump_01", "bkr_prop_biker_bblock_hump_02",
            "bkr_prop_biker_bblock_lrg1", "bkr_prop_biker_bblock_lrg2", "bkr_prop_biker_bblock_lrg3",
            "bkr_prop_biker_bblock_mdm1", "bkr_prop_biker_bblock_mdm2", "bkr_prop_biker_bblock_mdm3",
            "bkr_prop_biker_bblock_qp", "bkr_prop_biker_bblock_qp2", "bkr_prop_biker_bblock_qp3",
            "bkr_prop_biker_bblock_sml1", "bkr_prop_biker_bblock_sml2", "bkr_prop_biker_bblock_sml3",
            "bkr_prop_biker_bblock_xl1", "bkr_prop_biker_bblock_xl2", "bkr_prop_biker_bblock_xl3",
            "stt_prop_ramp_jump_l", "stt_prop_ramp_jump_m", "stt_prop_ramp_jump_s",
            "stt_prop_ramp_jump_xl", "stt_prop_ramp_jump_xs", "stt_prop_ramp_jump_xxl"
        };
        private static readonly int[] TextureVariationWeights =
            { 0, 10, 80, 30, 2, 10, 2, 10, 80, 30, 80, 2, 2, 2, 2, 2 };
        private string _pendingPlateText;
        private string _applyingPlateText;
        private Vehicle[] _plateVehicles;
        private int _plateVehicleIndex;
        private int _plateAppliedCount;
        private bool _plateApplyWithoutSaving;
        private bool _closingMenus;
        private string _customPlateText = "";
        private Vehicle[] _doorVehicles;
        private int _doorVehicleIndex;
        private int _doorOpenedCount;
        private int _doorTargetHash;
        private HashSet<int> _doorTargetHashes;
        private string _doorModelName;

        // Штатные типы покрытия GTA V.
        private static readonly string[] PaintTypeNames =
            { "Металлик", "Мат", "Метал", "Хром", "Классика" };
        private static readonly int[] PaintTypeIds =
            { 0, 3, 4, 5, 1 };
        private static readonly int[] PaintTypeStockColors =
            { 0, 12, 117, 120, 0 };

        private Vehicle _currentVehicle;
        private Blip _highlightBlip;

        // Все машины в радужном режиме (по слотам)
        private readonly List<RainbowSlot> _rainbowSlots = new List<RainbowSlot>();

        // Общий оттенок радуги (по времени)
        private float _rainbowHue;
        private int _lastHueGameTime;

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
        private int RainbowIndex { get { return _rainbowColors.Count - 1; } }

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
            new RainbowColor("Белый", 255, 255, 255),
            new RainbowColor("Серый", 128, 128, 128),
            new RainbowColor("Чёрный", 5, 5, 5),
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
            LoadSettings();


            _paintMenu = new NativeMenu(ModuleMenus.VehiclePaint.Title, "Выбранная машина");
            _pool.Add(_paintMenu);
            InitializePresetMenus();
            LoadMotorcyclePassengerModels();

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
            _paintMenu.Add(_bothSameCheckbox);

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
            _paintMenu.Add(_colorSelector);

            _colorSelector2 = new NativeListItem<string>("Вторичный цвет");
            foreach (var c in _rainbowColors)
                _colorSelector2.Add(c.Name);
            _colorSelector2.SelectedIndex = 0;
            _colorSelector2.Activated += (s, e) => ApplySecondaryColor();
            _colorSelector2.Description = "Не используется — галочка включена, машина красится цветом основного";
            _colorSelector2.Enabled = false;
            _paintMenu.Add(_colorSelector2);

            _paintTypeList = new NativeListItem<string>(
                "Тип краски",
                "Покрытие для следующей покраски",
                PaintTypeNames);
            _paintTypeList.SelectedIndex = 0;
            _paintMenu.Add(_paintTypeList);

            _speedList = new ClampedSpeedList(
                "Скорость перелива",
                "Быстро / Средне / Медленно",
                "Быстро", "Средне", "Медленно");
            _speedList.SelectedIndex = 1;
            _speedList.ItemChanged += (s, e) =>
            {
                _speedList.Description = "Текущая скорость: " + e.Object;
            };
            _paintMenu.Add(_speedList);

            var resetItem = new NativeItem("Сбросить цвет (заводской)");
            resetItem.Activated += (s, e) => ResetColor();
            _resetItem = resetItem;
            _paintMenu.Add(resetItem);

            _smokeMatchesPrimaryCheckbox = new NativeCheckboxItem(
                "Дым шин в цвет основного",
                "Вкл: дым из-под колёс получает цвет основного цвета машины.",
                false);
            _smokeMatchesPrimaryCheckbox.Checked = _settings.SmokeMatchesPrimaryColor;
            _smokeMatchesPrimaryCheckbox.CheckboxChanged += (s, e) =>
            {
                _settings.SmokeMatchesPrimaryColor = _smokeMatchesPrimaryCheckbox.Checked;
                SaveSettings();
            };
            _paintMenu.Add(_smokeMatchesPrimaryCheckbox);

            InitializeVehicleSwapMenu();

            _lastHueGameTime = Game.GameTime;
            _lastMarkerRaycastTime = Game.GameTime;
        }

        public void OnTick()
        {
            if (_presetTyping)
            {
                Game.DisableAllControlsThisFrame();
                DrawPresetTyping();
                if (!Game.IsKeyPressed(Keys.Enter)) _presetTypingReady = true;
            }
            else if (_presetTextReturnPending)
            {
                if (!Game.IsKeyPressed(Keys.Enter) && !Game.IsKeyPressed(Keys.Escape))
                {
                    _presetTextReturnPending = false;
                    MenuTextInput.End(this);
                }
            }
            else _pool.Process();
            UpdatePlateApplication();
            UpdateOpeningModelDoors();
            UpdateObjectTextureVariations();
            UpdateVehicleSwap();
            UpdatePresetRun();
            UpdateMotorcyclePassengers();

            // Радуга для всех машин в списке — по времени, не по кадрам
            if (_rainbowSlots.Count > 0)
            {
                int now = Game.GameTime;
                uint elapsed = unchecked((uint)(now - _lastHueGameTime));
                _lastHueGameTime = now;

                _rainbowHue += (float)elapsed / 1000f / GetCycleSeconds();
                if (_rainbowHue >= 1f) _rainbowHue -= (float)Math.Floor(_rainbowHue);
                for (int i = _rainbowSlots.Count - 1; i >= 0; i--)
                {
                    var slot = _rainbowSlots[i];
                    if (slot.Vehicle == null || !slot.Vehicle.Exists())
                    {
                        _rainbowSlots.RemoveAt(i);
                        continue;
                    }
                    float seconds = slot.FromPreset ? slot.CycleSeconds : GetCycleSeconds();
                    slot.Hue += (float)elapsed / 1000f / seconds;
                    if (slot.Hue >= 1f) slot.Hue -= (float)Math.Floor(slot.Hue);
                    int step = (int)(slot.Hue * HueSteps);
                    if (step == slot.LastStep) continue;
                    slot.LastStep = step;
                    Color c = ColorFromHSV(slot.Hue);
                    if (slot.Primary)
                    {
                        slot.Vehicle.Mods.CustomPrimaryColor = c;
                        bool smoke = slot.FromPreset ? slot.SmokeMatches :
                            _smokeMatchesPrimaryCheckbox != null && _smokeMatchesPrimaryCheckbox.Checked;
                        if (smoke) SetTyreSmokeColor(slot.Vehicle, c);
                    }
                    if (slot.Secondary) slot.Vehicle.Mods.CustomSecondaryColor = c;
                }
            }

            // Индикатор над машиной, на которую смотришь
            DrawAimedMarker();

            // Метка выделения живёт, пока открыто меню
            if (!IsAnyMenuVisible() && _highlightBlip != null && _highlightBlip.Exists())
            {
                _highlightBlip.Delete();
                _highlightBlip = null;
            }
        }

        public void OnKeyDown(Keys key)
        {
            if (_presetTyping) { HandlePresetTyping(key); return; }
            if (_presetTextReturnPending) return;
            if (_presetRun != null) return;
            if (ModPack.Core.MenuTextInput.Active || (key != Keys.I && key != Keys.T)) return;

            if (key == Keys.T)
            {
                bool alreadyOpen = IsPresetMenuVisible();
                CloseMenus();
                if (!alreadyOpen)
                {
                    _presetMenuDismissed = false;
                    _presetsMenu.Visible = true;
                }
                return;
            }

            // Выделение машин работает только в Object Spooner (Menyoo)
            Vehicle v = IsSpoonerModeActive() ? GetVehiclePlayerIsLookingAt() : null;

            // Меню открыто: навёлся на машину — переключаемся на неё, иначе — закрываем
            bool menuOpen = _paintMenu.Visible ||
                (_swapMenu != null && _swapMenu.Visible) || IsPresetMenuVisible();
            if (menuOpen)
            {
                if (v == null || !v.Exists())
                {
                    CloseMenus();
                    return;
                }
                CloseMenus();
                SelectVehicle(v);
                _paintMenu.Visible = true;
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
                _paintMenu.Name = "Машина не выделена";
                SetMenuEnabled(false);
                _paintMenu.Visible = true;
                GTA.UI.Screen.ShowSubtitle(IsSpoonerModeActive()
                    ? "~y~Машина не выделена. Наведись на машину и нажми ~b~I~w~."
                    : "~y~Выделение машин работает только в Object Spooner (Menyoo).", 4000);
                return;
            }

            SelectVehicle(v);
            _paintMenu.Visible = true;
            GTA.UI.Screen.ShowSubtitle("~g~Машина выделена! Выбирай цвет в меню.", 4000);
        }

        private void CloseMenus()
        {
            CancelVehicleSwap();
            _closingMenus = true;
            try
            {
                if (_paintMenu != null) _paintMenu.Visible = false;
                if (_swapMenu != null) _swapMenu.Visible = false;
                ClosePresetMenus();
            }
            finally
            {
                _closingMenus = false;
            }
        }

        private void ShowMenu(NativeMenu current, NativeMenu next)
        {
            _closingMenus = true;
            try
            {
                current.Visible = false;
            }
            finally
            {
                _closingMenus = false;
            }
            next.Visible = true;
        }

        private bool IsAnyMenuVisible()
        {
            return _paintMenu.Visible || _swapMenu.Visible || IsPresetMenuVisible();
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

            _paintMenu.Name = "Машина: " + GetVehicleName(v) + " (" + v.Model.Hash.ToString("X8") + ")";
            SetMenuEnabled(true);
        }

        // Серые пункты, когда машина не выделена.
        private void SetMenuEnabled(bool enabled)
        {
            _menuEnabled = enabled;
            _colorSelector.Enabled = enabled;
            _colorSelector2.Enabled = enabled && !_bothSameCheckbox.Checked;
            _resetItem.Enabled = enabled;
        }

        private bool IsRandomColorEnabled(string name, bool primary)
        {
            List<string> colors = _presetPaintContext != null ?
                (primary ? _presetPaintContext.PrimaryColors : _presetPaintContext.SecondaryColors) :
                (primary ? _settings.EnabledRandomColors : _settings.EnabledSecondaryRandomColors);
            if (colors == null)
                return true;

            for (int i = 0; i < colors.Count; i++)
            {
                if (string.Equals(colors[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private List<int> GetEnabledRandomColorIndices(bool includeRainbow, bool primary)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < _rainbowColors.Count; i++)
            {
                if (!includeRainbow && IsRainbowIndex(i))
                    continue;

                if (IsRandomColorEnabled(_rainbowColors[i].Name, primary))
                    result.Add(i);
            }
            return result;
        }

        private static string NormalizePlateText(string value)
        {
            var text = new System.Text.StringBuilder(8);
            foreach (char character in (value ?? "").Trim().ToUpperInvariant())
            {
                if ((character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9') ||
                    character == ' ' || character == '-') text.Append(character);
                if (text.Length == 8) break;
            }
            return text.ToString();
        }

        private void UpdatePlateApplication()
        {
            try
            {
                if (_pendingPlateText != null)
                {
                    _applyingPlateText = _pendingPlateText;
                    _pendingPlateText = null;
                    if (!_plateApplyWithoutSaving)
                    {
                        _customPlateText = _applyingPlateText;
                        _settings.CustomPlateText = _customPlateText;
                        SaveSettings();
                    }
                    _plateVehicles = World.GetAllVehicles() ?? new Vehicle[0];
                    _plateVehicleIndex = _plateAppliedCount = 0;
                    CrashDiagnostics.Event("RainbowPaint", "Applying custom plate; vehicles=" + (_plateVehicles == null ? 0 : _plateVehicles.Length));
                }
                if (_plateVehicles == null) return;
                int end = Math.Min(_plateVehicleIndex + 16, _plateVehicles.Length);
                while (_plateVehicleIndex < end)
                {
                    Vehicle vehicle = _plateVehicles[_plateVehicleIndex++];
                    if (vehicle == null || !vehicle.Exists() || !Function.Call<bool>(Hash.IS_ENTITY_A_VEHICLE, vehicle.Handle)) continue;
                    Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, vehicle.Handle, _applyingPlateText);
                    _plateAppliedCount++;
                }
                if (_plateVehicleIndex < _plateVehicles.Length) return;
                _plateVehicles = null;
                _plateApplyWithoutSaving = false;
                CrashDiagnostics.Event("RainbowPaint", "Custom plate applied; vehicles=" + _plateAppliedCount);
                GTA.UI.Screen.ShowSubtitle("~g~Номер применён к машинам: " + _plateAppliedCount + ".", 4000);
            }
            catch (Exception ex)
            {
                _plateVehicles = null;
                _plateApplyWithoutSaving = false;
                CrashDiagnostics.Exception("RainbowPaint", "Custom plate application failed", ex);
                PluginLog.Error("RainbowPaint: UpdatePlateApplication", ex);
            }
        }

        private void UpdateOpeningModelDoors()
        {
            if (_doorVehicles == null) return;
            int end = Math.Min(_doorVehicleIndex + 12, _doorVehicles.Length);
            while (_doorVehicleIndex < end)
            {
                Vehicle vehicle = _doorVehicles[_doorVehicleIndex++];
                try
                {
                    if (vehicle == null || !vehicle.Exists() || _doorTargetHashes == null ||
                        !_doorTargetHashes.Contains(vehicle.Model.Hash))
                        continue;
                    bool opened = false;
                    for (int door = 0; door <= 7; door++)
                    {
                        try
                        {
                            if (!Function.Call<bool>(Hash.GET_IS_DOOR_VALID, vehicle.Handle, door))
                                continue;
                            Function.Call(Hash.SET_VEHICLE_DOOR_OPEN, vehicle.Handle, door, false, false);
                            opened = true;
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Error("RainbowPaint: OpenDoor " + door, ex);
                        }
                    }
                    if (opened) _doorOpenedCount++;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("RainbowPaint: UpdateOpeningModelDoors", ex);
                }
            }
            if (_doorVehicleIndex < _doorVehicles.Length) return;
            _doorVehicles = null;
            _doorTargetHashes = null;
            CrashDiagnostics.Event("RainbowPaint", "Model doors opened; model=" +
                _doorTargetHash.ToString("X8") + "; vehicles=" + _doorOpenedCount);
            GTA.UI.Screen.ShowSubtitle("~g~Открыты доступные двери: " + _doorOpenedCount +
                (_doorTargetHash == 0 ? " машин из пресета." : " машин модели " + _doorModelName + "."), 4000);
        }

        private static List<ModelExclusion> LegacyTextureModels()
        {
            var models = new List<ModelExclusion>(TextureVariationModels.Length);
            foreach (string name in TextureVariationModels)
                models.Add(new ModelExclusion(name, new Model(name).Hash));
            return models;
        }

        private void RandomizeObjectTextureVariations(List<ModelExclusion> selectedModels)
        {
            try
            {
                if (selectedModels == null || selectedModels.Count == 0)
                {
                    _textureVariationProps = null;
                    GTA.UI.Screen.ShowSubtitle("~y~Список моделей для текстур пуст.", 3000);
                    return;
                }
                Model[] models = new Model[selectedModels.Count];
                for (int i = 0; i < models.Length; i++) models[i] = new Model(selectedModels[i].Hash);
                Prop[] props = World.GetAllProps(models);
                if (props == null || props.Length == 0)
                {
                    _textureVariationProps = null;
                    GTA.UI.Screen.ShowSubtitle("~y~Подходящих загруженных объектов нет.", 3000);
                    return;
                }
                List<TextureVariationTarget> targets = new List<TextureVariationTarget>(props.Length);
                foreach (Prop prop in props)
                {
                    if (prop != null && prop.Exists())
                        targets.Add(new TextureVariationTarget { Prop = prop, Position = prop.Position });
                }
                targets.Sort((a, b) =>
                {
                    int x = a.Position.X.CompareTo(b.Position.X);
                    if (x != 0) return x;
                    int y = a.Position.Y.CompareTo(b.Position.Y);
                    return y != 0 ? y : a.Position.Z.CompareTo(b.Position.Z);
                });
                _textureVariationProps = targets.ToArray();
                _textureVariationIndex = 0;
                _textureVariationChanged = 0;
                if (_textureVariationProps.Length == 0)
                {
                    _textureVariationProps = null;
                    GTA.UI.Screen.ShowSubtitle("~y~Подходящих загруженных объектов нет.", 3000);
                }
            }
            catch (Exception ex)
            {
                _textureVariationProps = null;
                PluginLog.Error("RainbowPaint: RandomizeObjectTextureVariations", ex);
                GTA.UI.Screen.ShowSubtitle("~r~Не найдены объекты для смены текстур.", 3000);
            }
        }

        private void UpdateObjectTextureVariations()
        {
            if (_textureVariationProps == null) return;
            int end = Math.Min(_textureVariationIndex + 40, _textureVariationProps.Length);
            for (; _textureVariationIndex < end; _textureVariationIndex++)
            {
                TextureVariationTarget target = _textureVariationProps[_textureVariationIndex];
                Prop prop = target.Prop;
                if (prop == null || !prop.Exists()) continue;
                try
                {
                    int variation = PickObjectTextureVariation(target.Position);
                    Function.Call(Hash.SET_OBJECT_TINT_INDEX, prop.Handle, variation);
                    target.Variation = variation;
                    _textureVariationProps[_textureVariationIndex] = target;
                    _textureVariationChanged++;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("RainbowPaint: Object texture variation", ex);
                }
            }
            if (_textureVariationIndex >= _textureVariationProps.Length)
            {
                _textureVariationProps = null;
                GTA.UI.Screen.ShowSubtitle("~g~Вариант текстуры изменён у объектов: " + _textureVariationChanged, 3000);
            }
        }

        private int PickObjectTextureVariation(Vector3 position)
        {
            const float nearbyDistance = 15f;
            const float nearbyDistanceSquared = nearbyDistance * nearbyDistance;
            float nearestDistanceSquared = nearbyDistanceSquared;
            int nearestVariation = 0;
            for (int i = _textureVariationIndex - 1; i >= 0; i--)
            {
                TextureVariationTarget other = _textureVariationProps[i];
                float dx = position.X - other.Position.X;
                if (dx > nearbyDistance) break;
                if (other.Variation < 1 || other.Variation > 15) continue;
                float dy = position.Y - other.Position.Y;
                float dz = position.Z - other.Position.Z;
                float distanceSquared = dx * dx + dy * dy + dz * dz;
                if (distanceSquared <= nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestVariation = other.Variation;
                }
            }

            int totalWeight = 0;
            for (int variation = 1; variation <= 15; variation++)
                if (variation != nearestVariation)
                    totalWeight += TextureVariationWeights[variation];

            int ticket = _rng.Next(totalWeight);
            for (int variation = 1; variation <= 15; variation++)
            {
                if (variation == nearestVariation) continue;
                ticket -= TextureVariationWeights[variation];
                if (ticket < 0) return variation;
            }
            return 1;
        }

        private HashSet<int> PickRainbowTargets(int count, bool allowed, bool onlyRainbow)
        {
            HashSet<int> result = new HashSet<int>();
            if (!allowed || count == 0) return result;
            if (onlyRainbow)
            {
                for (int i = 0; i < count; i++) result.Add(i);
                return result;
            }
            if (_rng.NextDouble() >= 0.8) return result;
            int first = _rng.Next(0, count);
            result.Add(first);
            if (count > 1 && _rng.NextDouble() < 0.2)
            {
                int second = _rng.Next(0, count - 1);
                result.Add(second >= first ? second + 1 : second);
            }
            return result;
        }

        private int PickColorForVehicle(Vehicle vehicle, List<PaintAssignment> assignments, List<int> allowedColors, int avoidIndex, bool primary)
        {
            if (allowedColors.Count == 1)
                return allowedColors[0];

            int bestScore = int.MaxValue;
            List<int> best = new List<int>();

            for (int i = 0; i < allowedColors.Count; i++)
            {
                int colorIndex = allowedColors[i];
                if (allowedColors.Count > 1 && colorIndex == avoidIndex)
                    continue;

                int score = GetNearbyColorScore(vehicle.Position, assignments, colorIndex, primary);
                if (score < bestScore)
                {
                    bestScore = score;
                    best.Clear();
                    best.Add(colorIndex);
                }
                else if (score == bestScore)
                {
                    best.Add(colorIndex);
                }
            }

            if (best.Count == 0)
                return allowedColors[_rng.Next(0, allowedColors.Count)];

            return best[_rng.Next(0, best.Count)];
        }

        private int GetNearbyColorScore(Vector3 position, List<PaintAssignment> assignments, int colorIndex, bool primary)
        {
            const float closeDistance = 12f;
            const float rowDistance = 24f;
            int score = 0;

            for (int i = assignments.Count - 1; i >= 0; i--)
            {
                float dist = (position - assignments[i].Position).Length();
                if (dist > rowDistance)
                    continue;

                int assignedIndex = primary ? assignments[i].PrimaryIndex : assignments[i].SecondaryIndex;
                if (assignedIndex != colorIndex)
                    continue;

                if (dist <= closeDistance)
                    score += 8;
                else
                    score += 3;

                int recentDistance = assignments.Count - i;
                if (recentDistance <= 3)
                    score += 2;
            }

            return score;
        }

        private void ApplyTyreSmokeColor(Vehicle vehicle, Color primaryColor)
        {
            bool enabled = _presetPaintContext != null ? _presetPaintContext.SmokeMatches :
                _smokeMatchesPrimaryCheckbox != null && _smokeMatchesPrimaryCheckbox.Checked;
            if (!enabled)
                return;

            SetTyreSmokeColor(vehicle, primaryColor);
        }

        private void SetTyreSmokeColor(Vehicle vehicle, Color primaryColor)
        {
            try
            {
                Function.Call(Hash.TOGGLE_VEHICLE_MOD, vehicle.Handle, 20, true);
                Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, vehicle.Handle, (int)primaryColor.R, (int)primaryColor.G, (int)primaryColor.B);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: ApplyTyreSmokeColor", ex);
            }
        }

        // Модель машины в списке исключений?
        private bool IsExcluded(Vehicle v)
        {
            List<ModelExclusion> exclusions = _presetPaintContext != null ?
                _presetPaintContext.Exclusions : _excludedModels;
            for (int i = 0; i < exclusions.Count; i++)
            {
                if (exclusions[i].Hash == v.Model.Hash)
                    return true;
            }
            return false;
        }

        public void OnAbort()
        {
            if (_swapMenu != null) ModPack.Core.MenuRegistry.Unregister("VehicleSwap", _swapMenu);
            CancelMotorcyclePassengers();
            _presetTyping = _presetTextReturnPending = false;
            _presetRun = null;
            _presetPaintContext = null;
            _presetPaintTargets = null;
            MenuTextInput.End(this);
            _pendingPlateText = null;
            _plateVehicles = null;
            _plateApplyWithoutSaving = false;
            _doorVehicles = null;
            _doorTargetHashes = null;
            _textureVariationProps = null;
            try
            {
                if (_highlightBlip != null && _highlightBlip.Exists())
                    _highlightBlip.Delete();
                _highlightBlip = null;

                _rainbowSlots.Clear();

                CloseMenus();
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: OnAbort", ex);
            }

            SaveExclusions();
            SaveSettings();
        }

        // Загрузка исключений из файла
        private void LoadExclusions()
        {
            try
            {
                if (!SafeFiles.Exists(_exclusionsPath))
                    return;

                string json = SafeFiles.Read(_exclusionsPath, ValidateExclusions);
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
                Directory.CreateDirectory(Path.GetDirectoryName(_exclusionsPath));
                SafeFiles.Write(_exclusionsPath, _serializer.Serialize(_excludedModels), ValidateExclusions);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: SaveExclusions", ex);
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!SafeFiles.Exists(_settingsPath))
                {
                    _settings = CreateDefaultSettings();
                    return;
                }

                string json = SafeFiles.Read(_settingsPath, ValidateSettings);
                RainbowPaintSettings settings = _settingsSerializer.Deserialize(json);
                _settings = NormalizeSettings(settings);
                _customPlateText = _settings.CustomPlateText ?? "";
            }
            catch (Exception ex)
            {
                _settings = CreateDefaultSettings();
                PluginLog.Error("RainbowPaint: LoadSettings", ex);
            }
        }

        private void ValidateSettings(string text)
        {
            if (new JavaScriptSerializer().Deserialize<RainbowPaintSettings>(text) == null)
                throw new InvalidDataException("Invalid paint settings");
        }
        private void ValidateExclusions(string text)
        {
            var entries = new JavaScriptSerializer().Deserialize<List<ModelExclusion>>(text);
            if (entries == null)
                throw new InvalidDataException("Invalid paint exclusions");
        }
        private void SaveSettings()
        {
            try
            {
                _settings = NormalizeSettings(_settings);
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                SafeFiles.Write(_settingsPath, _settingsSerializer.Serialize(_settings), ValidateSettings);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: SaveSettings", ex);
            }
        }

        private RainbowPaintSettings CreateDefaultSettings()
        {
            RainbowPaintSettings settings = new RainbowPaintSettings();
            settings.EnabledRandomColors = new List<string>();
            for (int i = 0; i < _rainbowColors.Count; i++)
                settings.EnabledRandomColors.Add(_rainbowColors[i].Name);
            settings.EnabledSecondaryRandomColors = new List<string>(settings.EnabledRandomColors);
            settings.SmokeMatchesPrimaryColor = false;
            settings.CustomPlateText = "";
            return settings;
        }

        private RainbowPaintSettings NormalizeSettings(RainbowPaintSettings settings)
        {
            if (settings == null)
                settings = CreateDefaultSettings();

            if (settings.EnabledRandomColors == null)
            {
                settings.EnabledRandomColors = new List<string>();
                for (int i = 0; i < _rainbowColors.Count; i++)
                    settings.EnabledRandomColors.Add(_rainbowColors[i].Name);
            }
            else
            {
                List<string> normalized = new List<string>();
                for (int i = 0; i < _rainbowColors.Count; i++)
                {
                    if (ContainsColorName(settings.EnabledRandomColors, _rainbowColors[i].Name))
                        normalized.Add(_rainbowColors[i].Name);
                }
                settings.EnabledRandomColors = normalized;
            }

            if (settings.EnabledSecondaryRandomColors == null)
                settings.EnabledSecondaryRandomColors = new List<string>(settings.EnabledRandomColors);
            else
            {
                List<string> normalized = new List<string>();
                for (int i = 0; i < _rainbowColors.Count; i++)
                {
                    if (ContainsColorName(settings.EnabledSecondaryRandomColors, _rainbowColors[i].Name))
                        normalized.Add(_rainbowColors[i].Name);
                }
                settings.EnabledSecondaryRandomColors = normalized;
            }

            settings.CustomPlateText = NormalizePlateText(settings.CustomPlateText);

            return settings;
        }

        private bool ContainsColorName(List<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public class RainbowPaintSettings
        {
            public List<string> EnabledRandomColors { get; set; }
            public List<string> EnabledSecondaryRandomColors { get; set; }
            public bool SmokeMatchesPrimaryColor { get; set; }
            public string CustomPlateText { get; set; }
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

        private sealed class SettingsSerializer
        {
            private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

            public RainbowPaintSettings Deserialize(string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                    return null;
                return _serializer.Deserialize<RainbowPaintSettings>(json);
            }

            public string Serialize(RainbowPaintSettings settings)
            {
                return _serializer.Serialize(settings);
            }
        }

        private float GetCycleSeconds()
        {
            if (_presetPaintContext != null)
                return CycleSeconds[Math.Max(0, Math.Min(CycleSeconds.Length - 1, _presetPaintContext.SpeedIndex))];
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
            if (_presetPaintContext != null)
                return Math.Max(0, Math.Min(PaintTypeIds.Length - 1, _presetPaintContext.PaintTypeIndex));
            if (_paintTypeList == null) return 0;

            int idx = _paintTypeList.SelectedIndex;
            if (idx < 0 || idx >= PaintTypeIds.Length)
                idx = 0;
            return idx;
        }

        private void ApplyPaintTypeToVehicle(Vehicle v, bool primary, bool secondary)
        {
            try
            {
                int typeIdx = GetPaintTypeIndex();
                int type = PaintTypeIds[typeIdx];
                int stock = PaintTypeStockColors[typeIdx];
                if (typeIdx >= 1 && typeIdx <= 3)
                {
                    Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, stock, stock);
                    return;
                }
                if (primary) Function.Call(Hash.SET_VEHICLE_MOD_COLOR_1, v.Handle, type, stock, 0);
                if (secondary) Function.Call(Hash.SET_VEHICLE_MOD_COLOR_2, v.Handle, type, stock);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: ApplyPaintTypeToVehicle", ex);
            }
        }

        private Color ApplyColorToChannel(Vehicle vehicle, int colorIndex, bool primary)
        {
            if (colorIndex == RainbowIndex)
            {
                Color hue = ColorFromHSV(_rainbowHue);
                if (primary) vehicle.Mods.CustomPrimaryColor = hue;
                else vehicle.Mods.CustomSecondaryColor = hue;
                return hue;
            }

            Color color = _rainbowColors[colorIndex].Paint;
            if (primary) vehicle.Mods.CustomPrimaryColor = color;
            else vehicle.Mods.CustomSecondaryColor = color;
            return color;
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
                if ((_swapMenu != null && _swapMenu.Visible) ||
                    (_presetModelsMenu != null && _presetModelsMenu.Visible))
                {
                    if (!IsSpoonerModeActive()) { _markerVehicle = null; return; }
                    int tick = Game.GameTime;
                    if ((uint)(tick - _lastMarkerRaycastTime) >= 50u)
                    {
                        _lastMarkerRaycastTime = tick;
                        float distance;
                        _markerVehicle = RaycastVehicle(out distance);
                        if (_markerVehicle != null)
                        {
                            var size = _markerVehicle.Model.Dimensions;
                            float height = size.Item2.Z - size.Item1.Z;
                            _markerPos = _markerVehicle.GetOffsetPosition(
                                new Vector3(0f, 0f, height / 2f + 0.8f));
                        }
                    }
                    if (_markerVehicle != null && _markerVehicle.Exists())
                        World.DrawMarker(MarkerType.Arrow, _markerPos, Vector3.Zero, Vector3.Zero,
                            new Vector3(0.35f, 0.35f, 0.35f), Color.FromArgb(220, 0, 255, 100));
                    return;
                }
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
                GTA.Camera cam = ScriptCameraDirector.RenderingCam;

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
            slot.Hue = _rainbowHue;
            slot.LastStep = -1;
            slot.CycleSeconds = GetCycleSeconds();
            slot.SmokeMatches = _presetPaintContext != null ? _presetPaintContext.SmokeMatches :
                _smokeMatchesPrimaryCheckbox != null && _smokeMatchesPrimaryCheckbox.Checked;
            slot.FromPreset = _presetPaintContext != null;
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
                ApplyPaintTypeToVehicle(_currentVehicle, true, false);

                var slot = GetOrCreateSlot(_currentVehicle);
                slot.Primary = true;

                ApplyTyreSmokeColor(_currentVehicle, ApplyColorToChannel(_currentVehicle, index, true));
                _currentVehicle.DirtLevel = 0f;
                GTA.UI.Screen.ShowSubtitle("~p~Радуга на основной цвет!", 3000);
            }
            else
            {
                var slot = FindSlot(_currentVehicle);
                if (slot != null)
                    slot.Primary = false;
                RemoveSlotIfEmpty(_currentVehicle);

                ApplyPaintTypeToVehicle(_currentVehicle, true, false);

                var color = _rainbowColors[index];
                ApplyTyreSmokeColor(_currentVehicle, ApplyColorToChannel(_currentVehicle, index, true));
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
                ApplyPaintTypeToVehicle(_currentVehicle, false, true);

                var slot = GetOrCreateSlot(_currentVehicle);
                slot.Secondary = true;

                ApplyColorToChannel(_currentVehicle, index, false);
                GTA.UI.Screen.ShowSubtitle("~p~Радуга на вторичный цвет!", 3000);
            }
            else
            {
                var slot = FindSlot(_currentVehicle);
                if (slot != null)
                    slot.Secondary = false;
                RemoveSlotIfEmpty(_currentVehicle);

                ApplyPaintTypeToVehicle(_currentVehicle, false, true);

                var color = _rainbowColors[index];
                ApplyColorToChannel(_currentVehicle, index, false);

                GTA.UI.Screen.ShowSubtitle("~g~Вторичный цвет: " + color.Name + "!", 3000);
            }
        }

        // Галочка включена — первый ползунок красит всю машину
        private void ApplyColorToBoth(int index)
        {
            if (IsRainbowIndex(index))
            {
                ApplyPaintTypeToVehicle(_currentVehicle, true, true);

                var slot = GetOrCreateSlot(_currentVehicle);
                slot.Primary = true;
                slot.Secondary = true;

                Color hueColor = ApplyColorToChannel(_currentVehicle, index, true);
                ApplyColorToChannel(_currentVehicle, index, false);
                ApplyTyreSmokeColor(_currentVehicle, hueColor);
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

                ApplyPaintTypeToVehicle(_currentVehicle, true, true);

                var color = _rainbowColors[index];
                Color primary = ApplyColorToChannel(_currentVehicle, index, true);
                ApplyColorToChannel(_currentVehicle, index, false);
                ApplyTyreSmokeColor(_currentVehicle, primary);
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
