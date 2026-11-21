using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI.Elements;
using LemonUI.Menus;

using ModPack.Core;

namespace ModPack.Paint
{
    public partial class PaintPlugin
    {
        public sealed class PaintPreset
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public List<string> PrimaryColors { get; set; }
            public List<string> SecondaryColors { get; set; }
            public List<ModelExclusion> Exclusions { get; set; }
            public List<ModelExclusion> DoorModels { get; set; }
            public List<ModelExclusion> PassengerModels { get; set; }
            public bool BothSame { get; set; }
            public int PaintTypeIndex { get; set; }
            public int SpeedIndex { get; set; }
            public bool SmokeMatches { get; set; }
            public string PlateText { get; set; }
            public bool RandomPaint { get; set; }
            public bool RandomTextures { get; set; }
            public bool ApplyPlate { get; set; }
            public bool OpenDoors { get; set; }
            public bool SeatMotorcyclePassengers { get; set; }
        }

        private readonly string _presetsPath = ModPack.Core.PluginStorage.FilePath("RainbowPaint", "RainbowPaintPresets.json");
        private readonly List<PaintPreset> _presets = new List<PaintPreset>();
        private NativeMenu _presetsMenu, _presetDetailMenu, _presetEditMenu, _presetColorsMenu;
        private NativeMenu _presetModelsMenu, _presetPassengerModelsMenu, _presetDeleteMenu;
        private PaintPreset _selectedPreset, _draftPreset, _presetRun;
        private bool _editingDoorModels, _editingPrimaryColors, _editingName;
        private bool _presetTyping, _presetTypingReady, _presetTextReturnPending;
        private bool _typingReplaceOnInput;
        private bool _typingStandalonePlate;
        private string _typingDraft;
        private ScaledRectangle _typingBackdrop, _typingPanel;
        private ScaledText _typingTitle, _typingValue, _typingHint;

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint key, uint scanCode, byte[] state,
            StringBuilder buffer, int capacity, uint flags, IntPtr layout);

        private void InitializePresetMenus()
        {
            LoadPresets();
            _presetsMenu = new NativeMenu("Пресеты", "Сохранённые сценарии");
            _presetDetailMenu = new NativeMenu("Пресет", "Действия");
            _presetEditMenu = new NativeMenu("Редактор пресета", "Настройки и действия");
            _presetColorsMenu = new NativeMenu("Цвета пресета", "Палитра случайной покраски");
            _presetModelsMenu = new NativeMenu("Модели пресета", "Список моделей");
            _presetPassengerModelsMenu = new NativeMenu("Пассажиры мотоциклов", "Модели педов пресета");
            _presetDeleteMenu = new NativeMenu("Удалить пресет?", "Подтверждение");
            _typingBackdrop = new ScaledRectangle(new System.Drawing.PointF(0, 0), new System.Drawing.SizeF(1280, 720));
            _typingBackdrop.Color = System.Drawing.Color.FromArgb(145, 0, 0, 0);
            _typingPanel = new ScaledRectangle(new System.Drawing.PointF(365, 235), new System.Drawing.SizeF(550, 245));
            _typingPanel.Color = System.Drawing.Color.FromArgb(245, 20, 34, 52);
            _typingTitle = new ScaledText(new System.Drawing.PointF(395, 255), "", 0.55f);
            _typingValue = new ScaledText(new System.Drawing.PointF(395, 323), "", 0.52f);
            _typingHint = new ScaledText(new System.Drawing.PointF(395, 417),
                "Enter — сохранить   Esc — отмена   Delete — очистить", 0.29f);
            foreach (NativeMenu menu in new[] { _presetsMenu, _presetDetailMenu, _presetEditMenu,
                _presetColorsMenu, _presetModelsMenu, _presetPassengerModelsMenu, _presetDeleteMenu }) _pool.Add(menu);
            AddSection("Пресеты", "Сохранить и запустить массовые действия", _presetsMenu);
            _presetDetailMenu.Closed += (s, e) => { if (!_closingMenus) _presetsMenu.Visible = true; };
            _presetEditMenu.Closed += (s, e) => { if (!_closingMenus) _presetDetailMenu.Visible = true; };
            _presetColorsMenu.Closed += (s, e) => { if (!_closingMenus) _presetEditMenu.Visible = true; };
            _presetModelsMenu.Closed += (s, e) => { if (!_closingMenus) _presetEditMenu.Visible = true; };
            _presetPassengerModelsMenu.Closed += (s, e) => { if (!_closingMenus) _presetEditMenu.Visible = true; };
            _presetDeleteMenu.Closed += (s, e) => { if (!_closingMenus) _presetDetailMenu.Visible = true; };
            RefreshPresetsMenu();
        }

        private void LoadPresets()
        {
            try
            {
                if (!SafeFiles.Exists(_presetsPath)) return;
                string json = SafeFiles.Read(_presetsPath, ValidatePresetJson);
                var loaded = new JavaScriptSerializer().Deserialize<List<PaintPreset>>(json);
                if (loaded == null) return;
                _presets.Clear();
                foreach (PaintPreset preset in loaded)
                    if (preset != null) _presets.Add(NormalizePreset(preset));
            }
            catch (Exception ex) { PluginLog.Error("RainbowPaint: LoadPresets", ex); }
        }

        private void ValidatePresetJson(string json)
        {
            if (new JavaScriptSerializer().Deserialize<List<PaintPreset>>(json) == null)
                throw new InvalidDataException("Invalid RainbowPaint presets");
        }

        private void SavePresets()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_presetsPath));
                string json = new JavaScriptSerializer().Serialize(_presets);
                SafeFiles.Write(_presetsPath, json, ValidatePresetJson);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: SavePresets", ex);
                GTA.UI.Screen.ShowSubtitle("~r~Не удалось сохранить пресеты.", 3000);
            }
        }

        private PaintPreset NormalizePreset(PaintPreset preset)
        {
            if (string.IsNullOrEmpty(preset.Id)) preset.Id = Guid.NewGuid().ToString("N");
            preset.Name = (preset.Name ?? "Пресет").Trim();
            if (preset.Name.Length > 24) preset.Name = preset.Name.Substring(0, 24);
            if (preset.Name.Length == 0) preset.Name = "Пресет";
            preset.PrimaryColors = NormalizePresetColors(preset.PrimaryColors);
            preset.SecondaryColors = NormalizePresetColors(preset.SecondaryColors);
            preset.Exclusions = NormalizeModels(preset.Exclusions);
            preset.DoorModels = NormalizeModels(preset.DoorModels);
            preset.PassengerModels = NormalizeModels(preset.PassengerModels);
            preset.PaintTypeIndex = Math.Max(0, Math.Min(PaintTypeNames.Length - 1, preset.PaintTypeIndex));
            preset.SpeedIndex = Math.Max(0, Math.Min(CycleSeconds.Length - 1, preset.SpeedIndex));
            preset.PlateText = NormalizePlateText(preset.PlateText);
            return preset;
        }

        private List<string> NormalizePresetColors(List<string> names)
        {
            var result = new List<string>();
            if (names == null) return result;
            foreach (RainbowColor color in _rainbowColors)
                if (ContainsColorName(names, color.Name)) result.Add(color.Name);
            return result;
        }

        private static List<ModelExclusion> NormalizeModels(List<ModelExclusion> models)
        {
            var result = new List<ModelExclusion>();
            var seen = new HashSet<int>();
            if (models == null) return result;
            foreach (ModelExclusion model in models)
                if (model.Hash != 0 && seen.Add(model.Hash))
                    result.Add(new ModelExclusion(model.Name ?? model.Hash.ToString("X8"), model.Hash));
            return result;
        }

        private PaintPreset ClonePreset(PaintPreset source)
        {
            return new PaintPreset
            {
                Id = source.Id, Name = source.Name,
                PrimaryColors = new List<string>(source.PrimaryColors),
                SecondaryColors = new List<string>(source.SecondaryColors),
                Exclusions = new List<ModelExclusion>(source.Exclusions),
                DoorModels = new List<ModelExclusion>(source.DoorModels),
                PassengerModels = new List<ModelExclusion>(source.PassengerModels),
                BothSame = source.BothSame, PaintTypeIndex = source.PaintTypeIndex,
                SpeedIndex = source.SpeedIndex, SmokeMatches = source.SmokeMatches,
                PlateText = source.PlateText, RandomPaint = source.RandomPaint,
                RandomTextures = source.RandomTextures, ApplyPlate = source.ApplyPlate,
                OpenDoors = source.OpenDoors,
                SeatMotorcyclePassengers = source.SeatMotorcyclePassengers
            };
        }

        private PaintPreset CaptureCurrentPreset()
        {
            return NormalizePreset(new PaintPreset
            {
                Id = Guid.NewGuid().ToString("N"), Name = NextPresetName(),
                PrimaryColors = new List<string>(_settings.EnabledRandomColors),
                SecondaryColors = new List<string>(_settings.EnabledSecondaryRandomColors),
                Exclusions = new List<ModelExclusion>(_excludedModels),
                DoorModels = new List<ModelExclusion>(),
                PassengerModels = new List<ModelExclusion>(_motorcyclePassengerModels),
                BothSame = _bothSameCheckbox.Checked, PaintTypeIndex = GetPaintTypeIndex(),
                SpeedIndex = _speedList.SelectedIndex,
                SmokeMatches = _smokeMatchesPrimaryCheckbox.Checked,
                PlateText = _customPlateText, RandomPaint = true, RandomTextures = true,
                ApplyPlate = !string.IsNullOrEmpty(_customPlateText), OpenDoors = false,
                SeatMotorcyclePassengers = _motorcyclePassengerModels.Count > 0
            });
        }

        private string NextPresetName()
        {
            for (int n = 1; ; n++)
            {
                string name = "Пресет " + n;
                if (!PresetNameExists(name, null)) return name;
            }
        }

        private bool PresetNameExists(string name, string exceptId)
        {
            foreach (PaintPreset preset in _presets)
                if (preset.Id != exceptId && string.Equals(preset.Name, name,
                    StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void RefreshPresetsMenu()
        {
            _presetsMenu.Clear();
            var create = new NativeItem("Создать из текущих настроек");
            create.Activated += (s, e) =>
            {
                PaintPreset preset = CaptureCurrentPreset();
                _presets.Add(preset);
                SavePresets();
                RefreshPresetsMenu();
                OpenPreset(preset);
            };
            _presetsMenu.Add(create);
            foreach (PaintPreset preset in _presets)
            {
                PaintPreset selected = preset;
                var item = new NativeItem(preset.Name, "Открыть и применить или изменить пресет");
                item.Activated += (s, e) => OpenPreset(selected);
                _presetsMenu.Add(item);
            }
        }

        private void OpenPreset(PaintPreset preset)
        {
            _selectedPreset = preset;
            RefreshPresetDetail();
            ShowMenu(_presetsMenu, _presetDetailMenu);
        }

        private void RefreshPresetDetail()
        {
            _presetDetailMenu.Name = _selectedPreset.Name;
            _presetDetailMenu.Clear();
            var apply = new NativeItem("Применить пресет", "Запустить массовые действия один раз");
            apply.Activated += (s, e) => StartPresetRun(_selectedPreset);
            _presetDetailMenu.Add(apply);
            var edit = new NativeItem("Редактировать", "Изменить действия и их настройки");
            edit.Activated += (s, e) =>
            {
                if (_draftPreset == null || _draftPreset.Id != _selectedPreset.Id)
                    _draftPreset = ClonePreset(_selectedPreset);
                RefreshPresetEditor();
                ShowMenu(_presetDetailMenu, _presetEditMenu);
            };
            _presetDetailMenu.Add(edit);
            var delete = new NativeItem("Удалить пресет");
            delete.Activated += (s, e) =>
            {
                _presetDeleteMenu.Clear();
                var yes = new NativeItem("Да, удалить " + _selectedPreset.Name);
                yes.Activated += (sender, args) =>
                {
                    _presets.Remove(_selectedPreset);
                    _draftPreset = null;
                    SavePresets();
                    RefreshPresetsMenu();
                    ShowMenu(_presetDeleteMenu, _presetsMenu);
                };
                _presetDeleteMenu.Add(yes);
                var no = new NativeItem("Отмена");
                no.Activated += (sender, args) => ShowMenu(_presetDeleteMenu, _presetDetailMenu);
                _presetDeleteMenu.Add(no);
                ShowMenu(_presetDetailMenu, _presetDeleteMenu);
            };
            _presetDetailMenu.Add(delete);
        }

        private void RefreshPresetEditor()
        {
            _presetEditMenu.Name = _draftPreset.Name;
            _presetEditMenu.Clear();
            AddPresetToggle("Случайная покраска машин", _draftPreset.RandomPaint,
                value => _draftPreset.RandomPaint = value);
            AddPresetToggle("Случайные текстуры объектов", _draftPreset.RandomTextures,
                value => _draftPreset.RandomTextures = value);
            AddPresetToggle("Номер всем машинам", _draftPreset.ApplyPlate,
                value => _draftPreset.ApplyPlate = value);
            AddPresetToggle("Открыть двери выбранных моделей", _draftPreset.OpenDoors,
                value => _draftPreset.OpenDoors = value);
            AddPresetToggle("Посадить пассажиров на мотоциклы", _draftPreset.SeatMotorcyclePassengers,
                value => _draftPreset.SeatMotorcyclePassengers = value);
            AddPresetToggle("Одинаковый цвет кузова", _draftPreset.BothSame,
                value => _draftPreset.BothSame = value);
            AddPresetToggle("Дым шин в цвет кузова", _draftPreset.SmokeMatches,
                value => _draftPreset.SmokeMatches = value);
            var paintType = new NativeListItem<string>("Тип краски", PaintTypeNames);
            paintType.SelectedIndex = _draftPreset.PaintTypeIndex;
            paintType.ItemChanged += (s, e) => _draftPreset.PaintTypeIndex = paintType.SelectedIndex;
            _presetEditMenu.Add(paintType);
            var speed = new ClampedSpeedList("Скорость перелива", "Для радужных машин пресета",
                "Быстро", "Средне", "Медленно");
            speed.SelectedIndex = _draftPreset.SpeedIndex;
            speed.ItemChanged += (s, e) => _draftPreset.SpeedIndex = speed.SelectedIndex;
            _presetEditMenu.Add(speed);
            AddEditorLink("Основные цвета", () => OpenPresetColors(true));
            AddEditorLink("Вторичные цвета", () => OpenPresetColors(false));
            AddEditorLink("Исключения рандомайзера", () => OpenPresetModels(false));
            AddEditorLink("Модели для открытия дверей", () => OpenPresetModels(true));
            AddEditorLink("Модели пассажиров мотоциклов", OpenPresetPassengerModels);
            AddEditorLink("Текст номера: " + (_draftPreset.PlateText.Length == 0 ? "нет" : _draftPreset.PlateText),
                () => OpenPresetText(false));
            AddEditorLink("Имя: " + _draftPreset.Name, () => OpenPresetText(true));
            AddEditorLink("Сохранить изменения", SaveEditedPreset);
        }

        private void AddPresetToggle(string title, bool initial, Action<bool> changed)
        {
            var item = new NativeCheckboxItem(title, "Выполнять при применении пресета", initial);
            item.CheckboxChanged += (s, e) => changed(item.Checked);
            _presetEditMenu.Add(item);
        }

        private void AddEditorLink(string title, Action action)
        {
            var item = new NativeItem(title);
            item.Activated += (s, e) => action();
            _presetEditMenu.Add(item);
        }

        private void SaveEditedPreset()
        {
            PaintPreset updated = NormalizePreset(ClonePreset(_draftPreset));
            if (PresetNameExists(updated.Name, updated.Id))
            {
                GTA.UI.Screen.ShowSubtitle("~y~Пресет с таким именем уже есть.", 3000);
                return;
            }
            int index = _presets.IndexOf(_selectedPreset);
            if (index < 0) return;
            _presets[index] = updated;
            _selectedPreset = updated;
            _draftPreset = null;
            SavePresets();
            RefreshPresetsMenu();
            RefreshPresetDetail();
            ShowMenu(_presetEditMenu, _presetDetailMenu);
        }

        private void OpenPresetColors(bool primary)
        {
            _editingPrimaryColors = primary;
            _presetColorsMenu.Name = primary ? "Основные цвета" : "Вторичные цвета";
            _presetColorsMenu.Clear();
            List<string> list = primary ? _draftPreset.PrimaryColors : _draftPreset.SecondaryColors;
            foreach (RainbowColor color in _rainbowColors)
            {
                string name = color.Name;
                var item = new NativeCheckboxItem(name, "Участвует в случайной покраске",
                    ContainsColorName(list, name));
                item.CheckboxChanged += (s, e) =>
                {
                    list.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
                    if (item.Checked) list.Add(name);
                };
                _presetColorsMenu.Add(item);
            }
            ShowMenu(_presetEditMenu, _presetColorsMenu);
        }

        private void OpenPresetModels(bool doors)
        {
            _editingDoorModels = doors;
            _presetModelsMenu.Name = doors ? "Модели дверей" : "Исключения";
            RefreshPresetModels();
            ShowMenu(_presetEditMenu, _presetModelsMenu);
        }

        private void RefreshPresetModels()
        {
            _presetModelsMenu.Clear();
            List<ModelExclusion> list = _editingDoorModels ? _draftPreset.DoorModels : _draftPreset.Exclusions;
            var add = new NativeItem("Добавить модель под прицелом",
                "Наведи камеру Spooner на машину и нажми Enter прямо в этом меню");
            add.Activated += (s, e) =>
            {
                Vehicle vehicle = IsSpoonerModeActive() ? GetVehiclePlayerIsLookingAt() : null;
                if (vehicle == null || !vehicle.Exists())
                {
                    GTA.UI.Screen.ShowSubtitle("~y~Наведи камеру Spooner на машину.", 3000);
                    return;
                }
                int hash = vehicle.Model.Hash;
                if (!list.Exists(model => model.Hash == hash))
                    list.Add(new ModelExclusion(GetVehicleName(vehicle), hash));
                RefreshPresetModels();
            };
            _presetModelsMenu.Add(add);
            foreach (ModelExclusion model in new List<ModelExclusion>(list))
            {
                ModelExclusion selected = model;
                var remove = new NativeItem("Убрать: " + model.Name);
                remove.Activated += (s, e) =>
                {
                    list.RemoveAll(value => value.Hash == selected.Hash);
                    RefreshPresetModels();
                };
                _presetModelsMenu.Add(remove);
            }
        }

        private void OpenPresetPassengerModels()
        {
            RefreshPresetPassengerModels();
            ShowMenu(_presetEditMenu, _presetPassengerModelsMenu);
        }

        private void RefreshPresetPassengerModels()
        {
            _presetPassengerModelsMenu.Clear();
            var add = new NativeItem("Добавить модель под прицелом",
                "Наведи камеру Object Spooner на педа и нажми Enter");
            add.Activated += (s, e) =>
            {
                try
                {
                    ModelExclusion? aimed = GetAimedMotorcyclePassengerModel();
                    if (!aimed.HasValue) return;
                    ModelExclusion model = aimed.Value;
                    if (_draftPreset.PassengerModels.Exists(item => item.Hash == model.Hash))
                    {
                        GTA.UI.Screen.ShowSubtitle("~y~Эта модель уже в пресете.", 3000);
                        return;
                    }
                    _draftPreset.PassengerModels.Add(model);
                    RefreshPresetPassengerModels();
                }
                catch (Exception ex) { PluginLog.Error("RainbowPaint: AddPresetPassengerModel", ex); }
            };
            _presetPassengerModelsMenu.Add(add);

            var copy = new NativeItem("Добавить из общего списка",
                "Скопировать модели из раздела пассажиров мотоциклов в этот пресет");
            copy.Activated += (s, e) =>
            {
                foreach (ModelExclusion model in _motorcyclePassengerModels)
                    if (!_draftPreset.PassengerModels.Exists(item => item.Hash == model.Hash))
                        _draftPreset.PassengerModels.Add(model);
                RefreshPresetPassengerModels();
            };
            _presetPassengerModelsMenu.Add(copy);

            foreach (ModelExclusion model in new List<ModelExclusion>(_draftPreset.PassengerModels))
            {
                int hash = model.Hash;
                var remove = new NativeItem("Убрать: " + model.Name);
                remove.Activated += (s, e) =>
                {
                    _draftPreset.PassengerModels.RemoveAll(item => item.Hash == hash);
                    RefreshPresetPassengerModels();
                };
                _presetPassengerModelsMenu.Add(remove);
            }
        }

        private void OpenPresetText(bool name)
        {
            if (_presetTyping || _presetTextReturnPending || _keyboardPlateActive ||
                _plateReturnPending || !MenuTextInput.Begin(this)) return;
            try
            {
                _editingName = name;
                _typingStandalonePlate = false;
                _typingDraft = name ? "" : _draftPreset.PlateText;
                _typingReplaceOnInput = !name && _typingDraft.Length > 0;
                _typingTitle.Text = name ? "Новое имя пресета" : "Номерной знак пресета";
                _presetTypingReady = false;
                _presetTyping = true;
                CloseMenus();
                UpdatePresetTypingDisplay();
            }
            catch (Exception ex)
            {
                _presetTyping = false;
                MenuTextInput.End(this);
                PluginLog.Error("RainbowPaint: OpenPresetText", ex);
            }
        }

        private void HandlePresetTyping(Keys key)
        {
            if (!_presetTypingReady) return;
            try
            {
                if (key == Keys.Escape) { FinishPresetTyping(false); return; }
                if (key == Keys.Enter) { FinishPresetTyping(true); return; }
                if (key == Keys.Back)
                {
                    _typingReplaceOnInput = false;
                    if (_typingDraft.Length > 0)
                        _typingDraft = _typingDraft.Substring(0, _typingDraft.Length - 1);
                }
                else if (key == Keys.Delete) { _typingDraft = ""; _typingReplaceOnInput = false; }
                else
                {
                    if (Game.IsKeyPressed(Keys.ControlKey) || Game.IsKeyPressed(Keys.Menu)) return;
                    string characters = TranslatePresetKey(key);
                    if (characters.Length > 0 && _typingReplaceOnInput)
                    {
                        _typingDraft = "";
                        _typingReplaceOnInput = false;
                    }
                    foreach (char character in characters)
                    {
                        char value = _editingName ? character : char.ToUpperInvariant(character);
                        bool allowed = _editingName ? char.IsLetterOrDigit(value) || value == ' ' ||
                            value == '-' || value == '_' :
                            value >= 'A' && value <= 'Z' || value >= '0' && value <= '9' ||
                            value == ' ' || value == '-';
                        if (allowed && _typingDraft.Length < (_editingName ? 24 : 8))
                            _typingDraft += value;
                    }
                }
                UpdatePresetTypingDisplay();
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: HandlePresetTyping", ex);
                FinishPresetTyping(false);
            }
        }

        private string TranslatePresetKey(Keys key)
        {
            // Номер использует только ASCII. Прямой перевод KeyCode не зависит от
            // состояния клавиатуры скриптового потока и работает в Object Spooner.
            if (!_editingName) return TranslatePlateKey(key);
            var state = new byte[256];
            if (!GetKeyboardState(state)) return TranslatePlateKey(key);
            var buffer = new StringBuilder(8);
            int length = ToUnicodeEx((uint)key, MapVirtualKey((uint)key, 0), state,
                buffer, buffer.Capacity, 0, GetKeyboardLayout(0));
            return length > 0 ? buffer.ToString().Substring(0, Math.Min(length, buffer.Length)) :
                TranslatePlateKey(key);
        }

        private static string TranslatePlateKey(Keys key)
        {
            if (key >= Keys.A && key <= Keys.Z) return ((char)('A' + (int)key - (int)Keys.A)).ToString();
            if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (int)key - (int)Keys.D0)).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
                return ((char)('0' + (int)key - (int)Keys.NumPad0)).ToString();
            if (key == Keys.Space) return " ";
            if (key == Keys.OemMinus || key == Keys.Subtract) return "-";
            return "";
        }

        private void FinishPresetTyping(bool save)
        {
            if (_typingStandalonePlate)
            {
                if (save)
                {
                    string plate = NormalizePlateText(_typingDraft);
                    if (string.IsNullOrWhiteSpace(plate))
                        GTA.UI.Screen.ShowSubtitle("~y~Номер пустой — изменения не применены.", 3000);
                    else
                        _pendingPlateText = plate;
                }
                _presetTyping = false;
                _presetTextReturnPending = true;
                _typingStandalonePlate = false;
                _worldMenu.Visible = true;
                CrashDiagnostics.Event("RainbowPaint", save ? "Custom plate confirmed" : "Custom plate cancelled");
                return;
            }
            if (save && _editingName)
            {
                string name = _typingDraft.Trim();
                if (name.Length == 0 || PresetNameExists(name, _draftPreset.Id))
                {
                    GTA.UI.Screen.ShowSubtitle("~y~Нужно уникальное непустое имя.", 3000);
                    return;
                }
                _draftPreset.Name = name;
            }
            else if (save) _draftPreset.PlateText = NormalizePlateText(_typingDraft);
            _presetTyping = false;
            _presetTextReturnPending = true;
            RefreshPresetEditor();
            _presetEditMenu.Visible = true;
        }

        private void UpdatePresetTypingDisplay()
        {
            _typingValue.Text = "[ " + _typingDraft + "_ ]   " + _typingDraft.Length +
                "/" + (_editingName ? 24 : 8);
        }

        private void DrawPresetTyping()
        {
            try
            {
                _typingBackdrop.Draw();
                _typingPanel.Draw();
                _typingTitle.Draw();
                _typingValue.Draw();
                _typingHint.Draw();
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: DrawPresetTyping", ex);
                FinishPresetTyping(false);
            }
        }
        private bool IsPresetMenuVisible()
        {
            return _presetsMenu != null && (_presetsMenu.Visible || _presetDetailMenu.Visible ||
                _presetEditMenu.Visible || _presetColorsMenu.Visible || _presetModelsMenu.Visible ||
                _presetPassengerModelsMenu.Visible || _presetDeleteMenu.Visible);
        }

        private void ClosePresetMenus()
        {
            if (_presetsMenu == null) return;
            _presetDeleteMenu.Visible = false;
            _presetModelsMenu.Visible = false;
            _presetPassengerModelsMenu.Visible = false;
            _presetColorsMenu.Visible = false;
            _presetEditMenu.Visible = false;
            _presetDetailMenu.Visible = false;
            _presetsMenu.Visible = false;
        }

        private enum PresetStage { Paint, TexturesStart, TexturesWait, PlateStart, PlateWait,
            DoorsStart, DoorsWait, PassengersStart, PassengersWait, Done }
        private PresetStage _presetStage;
        private PaintPreset _presetPaintContext;
        private List<Vehicle> _presetPaintTargets;
        private List<PaintAssignment> _presetAssignments;
        private List<int> _presetPrimaryColors, _presetSecondaryColors;
        private HashSet<int> _presetPrimaryRainbow, _presetSecondaryRainbow;
        private int _presetPaintIndex, _presetPaintedCount, _presetSkippedCount;
        private int _presetPassengerSeatedCount;
        private bool _presetPassengerJobStarted;

        private void StartPresetRun(PaintPreset selected)
        {
            if (_presetRun != null || _swapJob != null || _textureVariationProps != null ||
                _plateVehicles != null || _pendingPlateText != null || _doorVehicles != null ||
                _keyboardPlateActive || _plateReturnPending || _motorcyclePassengerVehicles != null)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Дождись завершения текущего действия.", 3000);
                return;
            }
            _presetRun = ClonePreset(selected);
            _presetStage = PresetStage.Paint;
            _presetPaintTargets = null;
            _presetPaintIndex = _presetPaintedCount = _presetSkippedCount = 0;
            _presetPassengerSeatedCount = 0;
            _presetPassengerJobStarted = false;
            CloseMenus();
            CrashDiagnostics.Event("RainbowPaint", "Preset started: " + _presetRun.Name);
            GTA.UI.Screen.ShowSubtitle("~b~Применяется пресет: " + _presetRun.Name, 3000);
        }

        private void UpdatePresetRun()
        {
            if (_presetRun == null) return;
            try
            {
                switch (_presetStage)
                {
                    case PresetStage.Paint:
                        if (!_presetRun.RandomPaint) { _presetStage = PresetStage.TexturesStart; break; }
                        if (_presetPaintTargets == null) BeginPresetPaint();
                        if (_presetPaintTargets != null) UpdatePresetPaint();
                        break;
                    case PresetStage.TexturesStart:
                        if (_presetRun.RandomTextures) RandomizeObjectTextureVariations();
                        _presetStage = PresetStage.TexturesWait;
                        break;
                    case PresetStage.TexturesWait:
                        if (_textureVariationProps == null) _presetStage = PresetStage.PlateStart;
                        break;
                    case PresetStage.PlateStart:
                        if (_presetRun.ApplyPlate && !string.IsNullOrEmpty(_presetRun.PlateText))
                        {
                            _plateApplyWithoutSaving = true;
                            _pendingPlateText = _presetRun.PlateText;
                        }
                        _presetStage = PresetStage.PlateWait;
                        break;
                    case PresetStage.PlateWait:
                        if (_pendingPlateText == null && _plateVehicles == null)
                            _presetStage = PresetStage.DoorsStart;
                        break;
                    case PresetStage.DoorsStart:
                        if (_presetRun.OpenDoors && _presetRun.DoorModels.Count > 0)
                            StartOpeningPresetDoors();
                        _presetStage = PresetStage.DoorsWait;
                        break;
                    case PresetStage.DoorsWait:
                        if (_doorVehicles == null) _presetStage = PresetStage.PassengersStart;
                        break;
                    case PresetStage.PassengersStart:
                        if (_presetRun.SeatMotorcyclePassengers && _presetRun.PassengerModels.Count > 0)
                            _presetPassengerJobStarted = StartMotorcyclePassengers(_presetRun.PassengerModels, true);
                        _presetStage = PresetStage.PassengersWait;
                        break;
                    case PresetStage.PassengersWait:
                        if (_motorcyclePassengerVehicles == null)
                        {
                            if (_presetPassengerJobStarted)
                                _presetPassengerSeatedCount = _motorcyclePassengerSeated;
                            _presetStage = PresetStage.Done;
                        }
                        break;
                    case PresetStage.Done:
                        CrashDiagnostics.Event("RainbowPaint", "Preset finished: " + _presetRun.Name +
                            "; painted=" + _presetPaintedCount + "; passengers=" +
                            _presetPassengerSeatedCount);
                        GTA.UI.Screen.ShowSubtitle("~g~Пресет применён: " + _presetRun.Name +
                            ". Покрашено машин: " + _presetPaintedCount +
                            "; пассажиров посажено: " +
                            _presetPassengerSeatedCount + ".", 5000);
                        _presetRun = null;
                        _presetPaintTargets = null;
                        break;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: UpdatePresetRun", ex);
                _presetPaintContext = null;
                _presetRun = null;
                _presetPaintTargets = null;
                GTA.UI.Screen.ShowSubtitle("~r~Пресет прерван ошибкой. Проверь журнал.", 4000);
            }
        }

        private void BeginPresetPaint()
        {
            _presetPaintTargets = new List<Vehicle>();
            _presetPaintContext = _presetRun;
            try
            {
                foreach (Vehicle vehicle in World.GetAllVehicles() ?? new Vehicle[0])
                {
                    if (vehicle == null || !vehicle.Exists()) continue;
                    if (IsExcluded(vehicle)) { _presetSkippedCount++; continue; }
                    _presetPaintTargets.Add(vehicle);
                }
                _presetPaintTargets.Sort((a, b) =>
                {
                    int x = a.Position.X.CompareTo(b.Position.X);
                    return x != 0 ? x : a.Position.Y.CompareTo(b.Position.Y);
                });
                _presetPrimaryColors = GetEnabledRandomColorIndices(false, true);
                _presetSecondaryColors = _presetRun.BothSame ? _presetPrimaryColors :
                    GetEnabledRandomColorIndices(false, false);
                bool firstRainbow = IsRandomColorEnabled(_rainbowColors[RainbowIndex].Name, true);
                bool secondRainbow = _presetRun.BothSame ? firstRainbow :
                    IsRandomColorEnabled(_rainbowColors[RainbowIndex].Name, false);
                if ((_presetPrimaryColors.Count == 0 && !firstRainbow) ||
                    (_presetSecondaryColors.Count == 0 && !secondRainbow))
                {
                    PluginLog.Error("RainbowPaint: Preset palette is empty", new InvalidDataException(_presetRun.Name));
                    _presetPaintTargets.Clear();
                }
                _presetPrimaryRainbow = PickRainbowTargets(_presetPaintTargets.Count, firstRainbow,
                    _presetPrimaryColors.Count == 0);
                _presetSecondaryRainbow = _presetRun.BothSame ? _presetPrimaryRainbow :
                    PickRainbowTargets(_presetPaintTargets.Count, secondRainbow, _presetSecondaryColors.Count == 0);
                _presetAssignments = new List<PaintAssignment>();
            }
            finally { _presetPaintContext = null; }
        }

        private void UpdatePresetPaint()
        {
            int end = Math.Min(_presetPaintIndex + 12, _presetPaintTargets.Count);
            _presetPaintContext = _presetRun;
            try
            {
                while (_presetPaintIndex < end)
                {
                    int index = _presetPaintIndex++;
                    Vehicle vehicle = _presetPaintTargets[index];
                    try
                    {
                        if (vehicle == null || !vehicle.Exists()) continue;
                        RainbowSlot slot = FindSlot(vehicle);
                        if (slot != null) _rainbowSlots.Remove(slot);
                        ApplyPaintTypeToVehicle(vehicle, true, true);
                        int primary = _presetPrimaryRainbow.Contains(index) ? RainbowIndex :
                            PickColorForVehicle(vehicle, _presetAssignments, _presetPrimaryColors, -1, true);
                        int secondary = _presetRun.BothSame ? primary :
                            (_presetSecondaryRainbow.Contains(index) ? RainbowIndex :
                            PickColorForVehicle(vehicle, _presetAssignments, _presetSecondaryColors, primary, false));
                        Color color = ApplyColorToChannel(vehicle, primary, true);
                        ApplyColorToChannel(vehicle, secondary, false);
                        if (primary == RainbowIndex || secondary == RainbowIndex)
                        {
                            slot = GetOrCreateSlot(vehicle);
                            slot.Primary = primary == RainbowIndex;
                            slot.Secondary = secondary == RainbowIndex;
                        }
                        ApplyTyreSmokeColor(vehicle, color);
                        vehicle.DirtLevel = 0f;
                        _presetAssignments.Add(new PaintAssignment(vehicle.Position, primary, secondary));
                        _presetPaintedCount++;
                    }
                    catch (Exception ex) { PluginLog.Error("RainbowPaint: PresetPaintVehicle", ex); }
                }
            }
            finally { _presetPaintContext = null; }
            if (_presetPaintIndex >= _presetPaintTargets.Count)
            {
                CrashDiagnostics.Event("RainbowPaint", "Preset paint complete; painted=" +
                    _presetPaintedCount + "; excluded=" + _presetSkippedCount);
                _presetPaintTargets = null;
                _presetStage = PresetStage.TexturesStart;
            }
        }

        private void StartOpeningPresetDoors()
        {
            _doorTargetHashes = new HashSet<int>();
            foreach (ModelExclusion model in _presetRun.DoorModels)
                _doorTargetHashes.Add(model.Hash);
            _doorTargetHash = 0;
            _doorModelName = "из пресета";
            _doorVehicles = World.GetAllVehicles() ?? new Vehicle[0];
            _doorVehicleIndex = _doorOpenedCount = 0;
            _openModelDoorsItem.Enabled = false;
        }
    }
}
