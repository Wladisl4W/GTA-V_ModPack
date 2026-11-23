using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
using ModPack.Prefabs;

namespace ModPack.Paint
{
    public partial class PaintPlugin
    {
        private const string DefaultPresetId = "modpack-default-preset";
        private static readonly string[] WeatherOptions = { "EXTRASUNNY", "CLEAR", "CLOUDS", "SMOG", "FOGGY",
            "OVERCAST", "RAIN", "THUNDER", "CLEARING", "NEUTRAL", "SNOW", "BLIZZARD",
            "SNOWLIGHT", "XMAS", "HALLOWEEN" };
        private static readonly string[] CloudOptions = { "altostratus", "Cirrus", "cirrocumulus",
            "Clear 01", "Cloudy 01", "Contrails", "Horizon", "horizonband1", "horizonband2",
            "horizonband3", "horsey", "Nimbus", "Puffs", "RAIN", "Snowy 01", "Stormy 01",
            "stratoscumulus", "Stripey", "shower", "Wispy" };
        public sealed class PaintPreset
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public List<string> PrimaryColors { get; set; }
            public List<string> SecondaryColors { get; set; }
            public List<ModelExclusion> Exclusions { get; set; }
            public List<ModelExclusion> DoorModels { get; set; }
            public List<ModelExclusion> PassengerModels { get; set; }
            public List<ModelExclusion> TextureModels { get; set; }
            public bool BothSame { get; set; }
            public int PaintTypeIndex { get; set; }
            public int SpeedIndex { get; set; }
            public bool SmokeMatches { get; set; }
            public string PlateText { get; set; }
            public bool RandomPaint { get; set; }
            public bool RandomTextures { get; set; }
            public bool RandomWeather { get; set; }
            public bool RandomClouds { get; set; }
            public List<string> WeatherChoices { get; set; }
            public List<string> CloudChoices { get; set; }
            public bool ApplyPlate { get; set; }
            public bool OpenDoors { get; set; }
            public bool SeatMotorcyclePassengers { get; set; }
            public bool SpawnPrefabs { get; set; }
            public List<PrefabPlacement> Prefabs { get; set; }
        }

        private readonly string _presetsPath = ModPack.Core.PluginStorage.FilePath("RainbowPaint", "RainbowPaintPresets.json");
        private readonly List<PaintPreset> _presets = new List<PaintPreset>();
        private NativeMenu _presetsMenu, _presetDetailMenu, _presetEditMenu, _presetPaintMenu, _presetColorsMenu;
        private NativeMenu _presetModelsMenu, _presetPassengerModelsMenu, _presetTextureModelsMenu, _presetDeleteMenu;
        private NativeMenu _presetClearListMenu, _clearListParent;
        private NativeMenu _presetWeatherMenu, _presetCloudMenu;
        private NativeMenu _presetPrefabsMenu, _presetPrefabDeleteMenu;
        private string _previewCloudName;
        private string _cloudBeforePreview;
        private string _lastPresetCloudName;
        private bool _cloudPreviewActive;
        private PaintPreset _selectedPreset, _draftPreset, _presetRun;
        private bool _editingDoorModels, _editingPrimaryColors, _editingName;
        private bool _presetMenuDismissed;
        private bool _presetTyping, _presetTypingReady, _presetTextReturnPending;
        private bool _typingReplaceOnInput;
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
            _presetsMenu = new NativeMenu(ModuleMenus.Presets.Title, "Сохранённые сценарии");
            _presetDetailMenu = new NativeMenu("Пресет", "Действия");
            _presetEditMenu = new NativeMenu("Редактор пресета", "Настройки и действия");
            _presetPaintMenu = new NativeMenu("Покраска", "Параметры случайной покраски");
            _presetColorsMenu = new NativeMenu("Цвета пресета", "Палитра случайной покраски");
            _presetModelsMenu = new NativeMenu("Модели пресета", "Список моделей");
            _presetPassengerModelsMenu = new NativeMenu("Пассажиры мотоциклов", "Модели педов пресета");
            _presetTextureModelsMenu = new NativeMenu("Текстуры объектов", "Модели объектов пресета");
            _presetWeatherMenu = new NativeMenu("Погода", "Варианты для случайного выбора");
            _presetCloudMenu = new NativeMenu("Облака", "Варианты для случайного выбора");
            _presetPrefabsMenu = new NativeMenu("Префабы пресета", "В порядке добавления");
            _presetPrefabDeleteMenu = new NativeMenu("Удалить префаб?", "Подтверждение");
            _presetDeleteMenu = new NativeMenu("Удалить пресет?", "Подтверждение");
            _presetClearListMenu = new NativeMenu("Очистить список?", "Подтверждение");
            _typingBackdrop = new ScaledRectangle(new System.Drawing.PointF(0, 0), new System.Drawing.SizeF(1280, 720));
            _typingBackdrop.Color = System.Drawing.Color.FromArgb(145, 0, 0, 0);
            _typingPanel = new ScaledRectangle(new System.Drawing.PointF(365, 235), new System.Drawing.SizeF(550, 245));
            _typingPanel.Color = System.Drawing.Color.FromArgb(245, 20, 34, 52);
            _typingTitle = new ScaledText(new System.Drawing.PointF(395, 255), "", 0.55f);
            _typingValue = new ScaledText(new System.Drawing.PointF(395, 323), "", 0.52f);
            _typingHint = new ScaledText(new System.Drawing.PointF(395, 417),
                "Enter — сохранить   Esc — отмена   Delete — очистить", 0.29f);
            foreach (NativeMenu menu in new[] { _presetsMenu, _presetDetailMenu, _presetEditMenu,
                _presetPaintMenu, _presetColorsMenu, _presetModelsMenu, _presetPassengerModelsMenu,
                _presetTextureModelsMenu, _presetWeatherMenu, _presetCloudMenu, _presetDeleteMenu,
                _presetClearListMenu, _presetPrefabsMenu, _presetPrefabDeleteMenu }) _pool.Add(menu);
            _presetDetailMenu.Closed += (s, e) => ReturnToPresetMenu(_presetsMenu);
            _presetEditMenu.Closed += (s, e) => ReturnToPresetMenu(_presetDetailMenu);
            _presetPaintMenu.Closed += (s, e) => ReturnToPresetMenu(_presetEditMenu);
            _presetColorsMenu.Closed += (s, e) => ReturnToPresetMenu(_presetPaintMenu);
            _presetModelsMenu.Closed += (s, e) =>
            {
                ReturnToPresetMenu(_editingDoorModels ? _presetEditMenu : _presetPaintMenu);
            };
            _presetPassengerModelsMenu.Closed += (s, e) => ReturnToPresetMenu(_presetEditMenu);
            _presetTextureModelsMenu.Closed += (s, e) => ReturnToPresetMenu(_presetEditMenu);
            _presetWeatherMenu.Closed += (s, e) => ReturnToPresetMenu(_presetEditMenu);
            _presetCloudMenu.SelectedIndexChanged += (s, e) => PreviewSelectedCloud();
            _presetCloudMenu.Closed += (s, e) =>
            {
                if (!_closingMenus) RestoreCloudPreview();
                ReturnToPresetMenu(_presetEditMenu);
            };
            _presetDeleteMenu.Closed += (s, e) => ReturnToPresetMenu(_presetDetailMenu);
            _presetPrefabsMenu.Closed += (s, e) => ReturnToPresetMenu(_presetEditMenu);
            _presetPrefabDeleteMenu.Closed += (s, e) => ReturnToPresetMenu(_presetPrefabsMenu);
            _presetClearListMenu.Closed += (s, e) =>
            {
                if (_clearListParent != null) ReturnToPresetMenu(_clearListParent);
            };
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
                EnsureDefaultPreset();
            }
            catch (Exception ex) { PluginLog.Error("RainbowPaint: LoadPresets", ex); }
        }

        private void ValidatePresetJson(string json)
        {
            if (new JavaScriptSerializer().Deserialize<List<PaintPreset>>(json) == null)
                throw new InvalidDataException("Invalid RainbowPaint presets");
        }

        private bool SavePresets()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_presetsPath));
                string json = new JavaScriptSerializer().Serialize(_presets);
                SafeFiles.Write(_presetsPath, json, ValidatePresetJson);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: SavePresets", ex);
                GTA.UI.Screen.ShowSubtitle("~r~Не удалось сохранить пресеты.", 3000);
                return false;
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
            preset.TextureModels = NormalizeModels(preset.TextureModels ?? LegacyTextureModels());
            preset.WeatherChoices = NormalizeChoices(preset.WeatherChoices, WeatherOptions);
            preset.CloudChoices = NormalizeChoices(preset.CloudChoices, CloudOptions);
            preset.PaintTypeIndex = Math.Max(0, Math.Min(PaintTypeNames.Length - 1, preset.PaintTypeIndex));
            preset.SpeedIndex = Math.Max(0, Math.Min(CycleSeconds.Length - 1, preset.SpeedIndex));
            preset.PlateText = NormalizePlateText(preset.PlateText);
            preset.Prefabs = NormalizePrefabPlacements(preset.Prefabs);
            return preset;
        }

        private static List<PrefabPlacement> NormalizePrefabPlacements(List<PrefabPlacement> placements)
        {
            var result = new List<PrefabPlacement>();
            if (placements == null) return result;
            foreach (PrefabPlacement placement in placements)
            {
                if (placement == null || result.Count >= 50 ||
                    (placement.Kind != "Balls" && placement.Kind != "Ramp")) continue;
                if (float.IsNaN(placement.X) || float.IsNaN(placement.Y) || float.IsNaN(placement.Z) ||
                    float.IsInfinity(placement.X) || float.IsInfinity(placement.Y) || float.IsInfinity(placement.Z)) continue;
                if (placement.Kind == "Balls" && (placement.Spacing <= 0f || placement.Radius <= 0f)) continue;
                if (float.IsNaN(placement.DirectionX) || float.IsNaN(placement.DirectionY) ||
                    float.IsInfinity(placement.DirectionX) || float.IsInfinity(placement.DirectionY) ||
                    float.IsNaN(placement.Spacing) || float.IsInfinity(placement.Spacing) ||
                    float.IsNaN(placement.Radius) || float.IsInfinity(placement.Radius)) continue;
                result.Add(placement.Copy());
            }
            return result;
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

        private static List<string> NormalizeChoices(List<string> choices, string[] available)
        {
            if (choices == null) return new List<string>(available);
            var result = new List<string>();
            foreach (string option in available)
                if (choices.Exists(value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase)))
                    result.Add(option);
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
                TextureModels = new List<ModelExclusion>(source.TextureModels),
                WeatherChoices = new List<string>(source.WeatherChoices),
                CloudChoices = new List<string>(source.CloudChoices),
                BothSame = source.BothSame, PaintTypeIndex = source.PaintTypeIndex,
                SpeedIndex = source.SpeedIndex, SmokeMatches = source.SmokeMatches,
                PlateText = source.PlateText, RandomPaint = source.RandomPaint,
                RandomTextures = source.RandomTextures, RandomWeather = source.RandomWeather,
                RandomClouds = source.RandomClouds, ApplyPlate = source.ApplyPlate,
                OpenDoors = source.OpenDoors,
                SeatMotorcyclePassengers = source.SeatMotorcyclePassengers,
                SpawnPrefabs = source.SpawnPrefabs,
                Prefabs = source.Prefabs.ConvertAll(item => item.Copy())
            };
        }

        private void EnsureDefaultPreset()
        {
            int index = _presets.FindIndex(p => p.Id == DefaultPresetId);
            if (index >= 0)
            {
                PaintPreset existing = _presets[index];
                existing.Name = "Дефолтный";
                if (index > 0) { _presets.RemoveAt(index); _presets.Insert(0, existing); }
                return;
            }
            _presets.Insert(0, NormalizePreset(new PaintPreset
            {
                Id = DefaultPresetId, Name = "Дефолтный",
                PrimaryColors = new List<string>(_settings.EnabledRandomColors),
                SecondaryColors = new List<string>(_settings.EnabledSecondaryRandomColors),
                Exclusions = new List<ModelExclusion>(_excludedModels),
                DoorModels = new List<ModelExclusion>(),
                PassengerModels = new List<ModelExclusion>(),
                TextureModels = LegacyTextureModels(),
                WeatherChoices = new List<string>(WeatherOptions),
                CloudChoices = new List<string>(CloudOptions),
                BothSame = true, PaintTypeIndex = 0, SpeedIndex = 1,
                SmokeMatches = _settings.SmokeMatchesPrimaryColor,
                PlateText = "", RandomPaint = true, RandomTextures = true,
                ApplyPlate = false, OpenDoors = false, SeatMotorcyclePassengers = false
            }));
        }

        private string NextPresetName()
        {
            for (int n = 1; ; n++)
            {
                string name = "Пресет " + n;
                if (!PresetNameExists(name, null)) return name;
            }
        }

        private string NextPresetCopyName(string sourceName)
        {
            for (int number = 1; ; number++)
            {
                string suffix = number == 1 ? " копия" : " копия " + number;
                string prefix = sourceName.Substring(0, Math.Min(sourceName.Length, Math.Max(0, 24 - suffix.Length))).TrimEnd();
                string name = prefix + suffix;
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
            EnsureDefaultPreset();
            _presetsMenu.Clear();
            PaintPreset baseline = _presets[0];
            var defaultItem = new NativeItem("~b~Дефолтный", "Параметры для новых пресетов");
            defaultItem.Activated += (s, e) => OpenPreset(baseline);
            _presetsMenu.Add(defaultItem);
            var create = new NativeItem("~g~Создать из дефолтного");
            create.Activated += (s, e) =>
            {
                PaintPreset preset = ClonePreset(_presets[0]);
                preset.Id = Guid.NewGuid().ToString("N");
                preset.Name = NextPresetName();
                _presets.Add(preset);
                SavePresets();
                RefreshPresetsMenu();
                OpenPreset(preset);
            };
            _presetsMenu.Add(create);
            var sortedPresets = new List<PaintPreset>(_presets);
            var russianNames = StringComparer.Create(new CultureInfo("ru-RU"), true);
            sortedPresets.Sort((left, right) =>
            {
                int order = russianNames.Compare(left.Name, right.Name);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            foreach (PaintPreset preset in sortedPresets)
            {
                if (preset.Id == DefaultPresetId) continue;
                PaintPreset selected = preset;
                var item = new NativeItem(preset.Name, "Открыть и применить или изменить пресет");
                item.Activated += (s, e) => OpenPreset(selected);
                _presetsMenu.Add(item);
            }
        }

        private void OpenPreset(PaintPreset preset)
        {
            // Menu entries can outlive the preset object replaced by CommitPresetEdit.
            // Always open the current saved instance instead of the captured old one.
            _selectedPreset = _presets.Find(value => value.Id == preset.Id);
            if (_selectedPreset == null)
            {
                RefreshPresetsMenu();
                return;
            }
            RefreshPresetDetail();
            ShowMenu(_presetsMenu, _presetDetailMenu);
        }

        private void RefreshPresetDetail()
        {
            _presetDetailMenu.Name = _selectedPreset.Name;
            _presetDetailMenu.Clear();
            bool isDefault = _selectedPreset.Id == DefaultPresetId;
            if (!isDefault)
            {
                var apply = new NativeItem("~g~Применить пресет", "Запустить массовые действия один раз");
                apply.Activated += (s, e) => StartPresetRun(_selectedPreset);
                _presetDetailMenu.Add(apply);
            }
            var edit = new NativeItem("~b~Редактировать", "Изменить действия и их настройки");
            edit.Activated += (s, e) =>
            {
                _draftPreset = ClonePreset(_selectedPreset);
                RefreshPresetEditor();
                ShowMenu(_presetDetailMenu, _presetEditMenu);
            };
            _presetDetailMenu.Add(edit);
            if (isDefault) return;
            var duplicate = new NativeItem("~b~Дублировать пресет", "Создать независимую копию со всеми настройками");
            duplicate.Activated += (s, e) =>
            {
                PaintPreset copy = ClonePreset(_selectedPreset);
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Name = NextPresetCopyName(_selectedPreset.Name);
                _presets.Add(copy);
                if (!SavePresets())
                {
                    _presets.Remove(copy);
                    return;
                }
                RefreshPresetsMenu();
                _selectedPreset = copy;
                RefreshPresetDetail();
                GTA.UI.Screen.ShowSubtitle("~g~Создан пресет: " + copy.Name, 2500);
            };
            _presetDetailMenu.Add(duplicate);
            var delete = new NativeItem("~r~Удалить пресет");
            delete.Activated += (s, e) =>
            {
                _presetDeleteMenu.Clear();
                var yes = new NativeItem("~r~Да, удалить " + _selectedPreset.Name);
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
            AddEditorLink("~b~  Настроить покраску", () =>
            {
                RefreshPresetPaintMenu();
                ShowMenu(_presetEditMenu, _presetPaintMenu);
            });
            AddPresetToggle("Случайные текстуры объектов", _draftPreset.RandomTextures,
                value => _draftPreset.RandomTextures = value);
            AddEditorLink("~b~  Модели объектов для текстур", OpenPresetTextureModels);
            AddPresetToggle("Случайная погода", _draftPreset.RandomWeather,
                value => _draftPreset.RandomWeather = value);
            AddEditorLink("~b~  Варианты погоды", () => OpenPresetChoices(true));
            AddPresetToggle("Случайные облака", _draftPreset.RandomClouds,
                value => _draftPreset.RandomClouds = value);
            AddEditorLink("~b~  Варианты облаков", () => OpenPresetChoices(false));
            AddPresetToggle("Номер всем машинам", _draftPreset.ApplyPlate,
                value => _draftPreset.ApplyPlate = value);
            AddEditorLink("~b~  Текст номера: " + (_draftPreset.PlateText.Length == 0 ? "нет" : _draftPreset.PlateText),
                () => OpenPresetText(false));
            AddPresetToggle("Открыть двери выбранных моделей", _draftPreset.OpenDoors,
                value => _draftPreset.OpenDoors = value);
            AddEditorLink("~b~  Модели для открытия дверей", () => OpenPresetModels(true));
            AddPresetToggle("Посадить пассажиров на мотоциклы", _draftPreset.SeatMotorcyclePassengers,
                value => _draftPreset.SeatMotorcyclePassengers = value);
            AddEditorLink("~b~  Модели пассажиров мотоциклов", OpenPresetPassengerModels);
            AddPresetToggle("Спавнить префабы", _draftPreset.SpawnPrefabs,
                value => _draftPreset.SpawnPrefabs = value);
            AddEditorLink("~b~  Задать префабы", OpenPresetPrefabs);
            if (_draftPreset.Id != DefaultPresetId)
                AddEditorLink("~b~Имя: " + _draftPreset.Name, () => OpenPresetText(true));
        }

        private void RefreshPresetPaintMenu()
        {
            _presetPaintMenu.Clear();
            AddPresetToggle(_presetPaintMenu, "Одинаковый цвет кузова", _draftPreset.BothSame,
                value => _draftPreset.BothSame = value);
            AddPresetToggle(_presetPaintMenu, "Дым шин в цвет кузова", _draftPreset.SmokeMatches,
                value => _draftPreset.SmokeMatches = value);
            var paintType = new NativeListItem<string>("Тип краски", PaintTypeNames);
            paintType.SelectedIndex = _draftPreset.PaintTypeIndex;
            paintType.ItemChanged += (s, e) =>
            {
                _draftPreset.PaintTypeIndex = paintType.SelectedIndex;
                CommitPresetEdit();
            };
            _presetPaintMenu.Add(paintType);
            var speed = new ClampedSpeedList("Скорость перелива", "Для радужных машин пресета",
                "Быстро", "Средне", "Медленно");
            speed.SelectedIndex = _draftPreset.SpeedIndex;
            speed.ItemChanged += (s, e) =>
            {
                _draftPreset.SpeedIndex = speed.SelectedIndex;
                CommitPresetEdit();
            };
            _presetPaintMenu.Add(speed);
            AddMenuLink(_presetPaintMenu, "~b~Основные цвета", () => OpenPresetColors(true));
            AddMenuLink(_presetPaintMenu, "~b~Вторичные цвета", () => OpenPresetColors(false));
            AddMenuLink(_presetPaintMenu, "~b~Исключения рандомайзера", () => OpenPresetModels(false));
        }

        private void OpenPresetPrefabs()
        {
            RefreshPresetPrefabs();
            ShowMenu(_presetEditMenu, _presetPrefabsMenu);
        }

        private void RefreshPresetPrefabs()
        {
            _presetPrefabsMenu.Clear();
            var add = new NativeItem("~g~Поставить новый", "Запомнить место через меню заготовок; объект пока не появится");
            add.Activated += (s, e) =>
            {
                PrefabsPlugin prefabs = PrefabsPlugin.Current;
                if (prefabs == null)
                {
                    GTA.UI.Screen.ShowSubtitle("~r~Мод заготовок не загружен.", 3000);
                    return;
                }
                string presetId = _draftPreset.Id;
                if (!prefabs.BeginPresetPlacement(placement =>
                {
                    if (_draftPreset == null || _draftPreset.Id != presetId)
                    {
                        _presetMenuDismissed = false;
                        return;
                    }
                    _draftPreset.Prefabs.Add(placement);
                    CommitPresetEdit();
                    RefreshPresetPrefabs();
                    _presetMenuDismissed = false;
                    _presetPrefabsMenu.Visible = true;
                }, () =>
                {
                    _presetMenuDismissed = false;
                    _presetPrefabsMenu.Visible = true;
                })) return;
                _presetMenuDismissed = true;
                _presetPrefabsMenu.Visible = false;
            };
            _presetPrefabsMenu.Add(add);
            for (int i = 0; i < _draftPreset.Prefabs.Count; i++)
            {
                int index = i;
                PrefabPlacement placement = _draftPreset.Prefabs[i];
                string name = placement.Kind == "Ramp" ? "Горка" : "Стенка мячиков";
                var item = new NativeItem((i + 1) + ". " + name,
                    "X: " + placement.X.ToString("F1") + "  Y: " + placement.Y.ToString("F1") +
                    "  Z: " + placement.Z.ToString("F1"));
                item.Activated += (s, e) =>
                {
                    _presetPrefabDeleteMenu.Name = "Удалить префаб?";
                    _presetPrefabDeleteMenu.Clear();
                    var no = new NativeItem("Нет, оставить");
                    no.Activated += (sender, args) => ShowMenu(_presetPrefabDeleteMenu, _presetPrefabsMenu);
                    _presetPrefabDeleteMenu.Add(no);
                    var yes = new NativeItem("~r~Да, удалить " + name);
                    yes.Activated += (sender, args) =>
                    {
                        _draftPreset.Prefabs.RemoveAt(index);
                        CommitPresetEdit();
                        RefreshPresetPrefabs();
                        ShowMenu(_presetPrefabDeleteMenu, _presetPrefabsMenu);
                    };
                    _presetPrefabDeleteMenu.Add(yes);
                    ShowMenu(_presetPrefabsMenu, _presetPrefabDeleteMenu);
                };
                _presetPrefabsMenu.Add(item);
            }
            if (_draftPreset.Prefabs.Count > 0)
            {
                var clear = new NativeItem("~r~Очистить список", "Удалить все префабы этого пресета");
                clear.Activated += (s, e) =>
                {
                    _presetPrefabDeleteMenu.Name = "Очистить префабы?";
                    _presetPrefabDeleteMenu.Clear();
                    var no = new NativeItem("Нет, оставить");
                    no.Activated += (sender, args) => ShowMenu(_presetPrefabDeleteMenu, _presetPrefabsMenu);
                    _presetPrefabDeleteMenu.Add(no);
                    var yes = new NativeItem("~r~Да, очистить список");
                    yes.Activated += (sender, args) =>
                    {
                        _draftPreset.Prefabs.Clear();
                        CommitPresetEdit();
                        RefreshPresetPrefabs();
                        ShowMenu(_presetPrefabDeleteMenu, _presetPrefabsMenu);
                    };
                    _presetPrefabDeleteMenu.Add(yes);
                    ShowMenu(_presetPrefabsMenu, _presetPrefabDeleteMenu);
                };
                _presetPrefabsMenu.Add(clear);
            }
        }

        private void AddPresetToggle(string title, bool initial, Action<bool> changed)
        {
            AddPresetToggle(_presetEditMenu, title, initial, changed);
        }

        private void AddPresetToggle(NativeMenu menu, string title, bool initial, Action<bool> changed)
        {
            var item = new NativeCheckboxItem(title, "Выполнять при применении пресета", initial);
            item.CheckboxChanged += (s, e) =>
            {
                changed(item.Checked);
                CommitPresetEdit();
            };
            menu.Add(item);
        }

        private void AddEditorLink(string title, Action action)
        {
            AddMenuLink(_presetEditMenu, title, action);
        }

        private static void AddMenuLink(NativeMenu menu, string title, Action action)
        {
            var item = new NativeItem(title);
            item.Activated += (s, e) => action();
            menu.Add(item);
        }

        private bool CommitPresetEdit()
        {
            PaintPreset updated = NormalizePreset(ClonePreset(_draftPreset));
            if (PresetNameExists(updated.Name, updated.Id))
            {
                GTA.UI.Screen.ShowSubtitle("~y~Пресет с таким именем уже есть.", 3000);
                return false;
            }
            int index = _presets.FindIndex(value => value.Id == updated.Id);
            if (index < 0) return false;
            PaintPreset previous = _presets[index];
            _presets[index] = updated;
            if (!SavePresets())
            {
                _presets[index] = previous;
                return false;
            }
            _selectedPreset = updated;
            if (!string.Equals(previous.Name, updated.Name, StringComparison.Ordinal))
            {
                RefreshPresetsMenu();
                RefreshPresetDetail();
            }
            return true;
        }

        private void OpenPresetColors(bool primary)
        {
            _editingPrimaryColors = primary;
            RefreshPresetColors();
            ShowMenu(_presetPaintMenu, _presetColorsMenu);
        }

        private void RefreshPresetColors()
        {
            bool primary = _editingPrimaryColors;
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
                    CommitPresetEdit();
                };
                _presetColorsMenu.Add(item);
            }
            AddClearPresetList(_presetColorsMenu, list, RefreshPresetColors);
        }

        private void OpenPresetModels(bool doors)
        {
            _editingDoorModels = doors;
            _presetModelsMenu.Name = doors ? "Модели дверей" : "Исключения";
            RefreshPresetModels();
            ShowMenu(doors ? _presetEditMenu : _presetPaintMenu, _presetModelsMenu);
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
                {
                    list.Add(new ModelExclusion(GetVehicleName(vehicle), hash));
                    CommitPresetEdit();
                }
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
                    CommitPresetEdit();
                    RefreshPresetModels();
                };
                _presetModelsMenu.Add(remove);
            }
            AddClearPresetList(_presetModelsMenu, list, RefreshPresetModels);
        }

        private void OpenPresetPassengerModels()
        {
            RefreshPresetPassengerModels();
            ShowMenu(_presetEditMenu, _presetPassengerModelsMenu);
        }

        private void OpenPresetTextureModels()
        {
            RefreshPresetTextureModels();
            ShowMenu(_presetEditMenu, _presetTextureModelsMenu);
        }

        private void OpenPresetChoices(bool weather)
        {
            NativeMenu menu = weather ? _presetWeatherMenu : _presetCloudMenu;
            RefreshPresetChoices(weather);
            ShowMenu(_presetEditMenu, menu);
            if (!weather)
            {
                _cloudBeforePreview = _lastPresetCloudName;
                _cloudPreviewActive = true;
                PreviewSelectedCloud();
            }
        }

        private void RefreshPresetChoices(bool weather)
        {
            NativeMenu menu = weather ? _presetWeatherMenu : _presetCloudMenu;
            menu.Clear();
            List<string> selected = weather ? _draftPreset.WeatherChoices : _draftPreset.CloudChoices;
            foreach (string option in weather ? WeatherOptions : CloudOptions)
            {
                string choice = option;
                var item = new NativeCheckboxItem(choice, "Разрешить этот вариант в пресете", selected.Contains(choice));
                item.CheckboxChanged += (s, e) =>
                {
                    if (item.Checked)
                    {
                        if (!selected.Contains(choice)) selected.Add(choice);
                    }
                    else selected.Remove(choice);
                    CommitPresetEdit();
                };
                menu.Add(item);
            }
            AddClearPresetList(menu, selected, () => RefreshPresetChoices(weather));
        }

        private void PreviewSelectedCloud()
        {
            if (!_cloudPreviewActive || !_presetCloudMenu.Visible) return;
            int index = _presetCloudMenu.SelectedIndex;
            if (index < 0 || index >= CloudOptions.Length) return;
            string cloud = CloudOptions[index];
            if (string.Equals(_previewCloudName, cloud, StringComparison.Ordinal)) return;
            try
            {
                RestorePreviewCloudHat();
                Function.Call(Hash.LOAD_CLOUD_HAT, cloud, 0.35f);
                _previewCloudName = cloud;
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: Cloud preview " + cloud, ex);
            }
        }

        private void RestorePreviewCloudHat()
        {
            if (string.IsNullOrEmpty(_previewCloudName)) return;
            string cloud = _previewCloudName;
            _previewCloudName = null;
            try { Function.Call(Hash.UNLOAD_CLOUD_HAT, cloud, 0.35f); }
            catch (Exception ex) { PluginLog.Error("RainbowPaint: Restore cloud preview " + cloud, ex); }
        }

        private void RestoreCloudPreview()
        {
            _cloudPreviewActive = false;
            RestorePreviewCloudHat();
            string previous = _cloudBeforePreview;
            _cloudBeforePreview = null;
            if (string.IsNullOrEmpty(previous)) return;
            try { Function.Call(Hash.LOAD_CLOUD_HAT, previous, 0.35f); }
            catch (Exception ex) { PluginLog.Error("RainbowPaint: Restore preset cloud " + previous, ex); }
        }

        private void RefreshPresetTextureModels()
        {
            _presetTextureModelsMenu.Clear();
            var add = new NativeItem("~g~Добавить объект под прицелом",
                "В Object Spooner наведи камеру на объект и нажми Enter");
            add.Activated += (s, e) =>
            {
                Prop prop = GetAimedTextureProp();
                if (prop == null)
                {
                    GTA.UI.Screen.ShowSubtitle("~y~Наведи камеру Object Spooner на объект.", 3000);
                    return;
                }
                int hash = prop.Model.Hash;
                if (!_draftPreset.TextureModels.Exists(value => value.Hash == hash))
                {
                    _draftPreset.TextureModels.Add(new ModelExclusion("0x" + ((uint)hash).ToString("X8"), hash));
                    CommitPresetEdit();
                }
                RefreshPresetTextureModels();
            };
            _presetTextureModelsMenu.Add(add);
            var removeAimed = new NativeItem("~r~Удалить объект под прицелом из списка",
                "В Object Spooner наведи камеру на объект из списка и нажми Enter");
            removeAimed.Activated += (s, e) =>
            {
                Prop prop = GetAimedTextureProp();
                if (prop == null)
                {
                    GTA.UI.Screen.ShowSubtitle("~y~Наведи камеру Object Spooner на объект.", 3000);
                    return;
                }
                int hash = prop.Model.Hash;
                var previous = new List<ModelExclusion>(_draftPreset.TextureModels);
                int removed = _draftPreset.TextureModels.RemoveAll(value => value.Hash == hash);
                if (removed == 0)
                {
                    GTA.UI.Screen.ShowSubtitle("~y~Этой модели нет в списке.", 3000);
                    return;
                }
                if (!CommitPresetEdit())
                {
                    _draftPreset.TextureModels.Clear();
                    _draftPreset.TextureModels.AddRange(previous);
                }
                RefreshPresetTextureModels();
            };
            _presetTextureModelsMenu.Add(removeAimed);
            foreach (ModelExclusion model in new List<ModelExclusion>(_draftPreset.TextureModels))
            {
                ModelExclusion selected = model;
                var remove = new NativeItem("~r~Убрать: " + model.Name,
                    "Исключить эту модель из рандома текстур пресета");
                remove.Activated += (s, e) =>
                {
                    _draftPreset.TextureModels.RemoveAll(value => value.Hash == selected.Hash);
                    CommitPresetEdit();
                    RefreshPresetTextureModels();
                };
                _presetTextureModelsMenu.Add(remove);
            }
            AddClearPresetList(_presetTextureModelsMenu, _draftPreset.TextureModels, RefreshPresetTextureModels);
        }

        private Prop GetAimedTextureProp()
        {
            if (!IsSpoonerModeActive()) return null;
            GTA.Camera camera = ScriptCameraDirector.RenderingCam;
            if (camera == null) return null;
            RaycastResult ray = World.Raycast(camera.Position, camera.Position + camera.Direction * 300f,
                IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Vehicles | IntersectFlags.Peds,
                Game.Player.Character);
            Prop prop = ray.DidHit ? ray.HitEntity as Prop : null;
            return prop != null && prop.Exists() ? prop : null;
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
                    CommitPresetEdit();
                    RefreshPresetPassengerModels();
                }
                catch (Exception ex) { PluginLog.Error("RainbowPaint: AddPresetPassengerModel", ex); }
            };
            _presetPassengerModelsMenu.Add(add);

            var copy = new NativeItem("Добавить из старого списка",
                "Скопировать сохранённые модели пассажиров в этот пресет");
            copy.Activated += (s, e) =>
            {
                foreach (ModelExclusion model in _motorcyclePassengerModels)
                    if (!_draftPreset.PassengerModels.Exists(item => item.Hash == model.Hash))
                        _draftPreset.PassengerModels.Add(model);
                CommitPresetEdit();
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
                    CommitPresetEdit();
                    RefreshPresetPassengerModels();
                };
                _presetPassengerModelsMenu.Add(remove);
            }
            AddClearPresetList(_presetPassengerModelsMenu, _draftPreset.PassengerModels, RefreshPresetPassengerModels);
        }

        private void AddClearPresetList<T>(NativeMenu menu, List<T> list, Action refresh)
        {
            var clear = new NativeItem("~r~Очистить весь список", "Откроется подтверждение");
            clear.Activated += (s, e) =>
            {
                _clearListParent = menu;
                _presetClearListMenu.Name = "Очистить " + menu.Name + "?";
                _presetClearListMenu.Clear();
                var no = new NativeItem("Нет", "Вернуться без изменений");
                no.Activated += (sender, args) => ShowMenu(_presetClearListMenu, menu);
                _presetClearListMenu.Add(no);
                var yes = new NativeItem("~r~Да, очистить", "Удалить все элементы этого списка");
                yes.Activated += (sender, args) =>
                {
                    var previous = new List<T>(list);
                    list.Clear();
                    if (!CommitPresetEdit()) list.AddRange(previous);
                    refresh();
                    ShowMenu(_presetClearListMenu, menu);
                };
                _presetClearListMenu.Add(yes);
                _presetClearListMenu.SelectedIndex = 0;
                ShowMenu(menu, _presetClearListMenu);
            };
            menu.Add(clear);
        }

        private void OpenPresetText(bool name)
        {
            if (_presetTyping || _presetTextReturnPending || !MenuTextInput.Begin(this)) return;
            try
            {
                _editingName = name;
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
            if (save) CommitPresetEdit();
            _presetTyping = false;
            _presetTextReturnPending = true;
            RefreshPresetEditor();
            _presetMenuDismissed = false;
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
                _presetEditMenu.Visible || _presetPaintMenu.Visible || _presetColorsMenu.Visible || _presetModelsMenu.Visible ||
                _presetPassengerModelsMenu.Visible || _presetTextureModelsMenu.Visible ||
                _presetWeatherMenu.Visible || _presetCloudMenu.Visible || _presetDeleteMenu.Visible ||
                _presetClearListMenu.Visible || _presetPrefabsMenu.Visible || _presetPrefabDeleteMenu.Visible);
        }

        private void ReturnToPresetMenu(NativeMenu parent)
        {
            if (!_closingMenus && !_presetMenuDismissed && !IsPresetMenuVisible())
                parent.Visible = true;
        }

        private void ClosePresetMenus()
        {
            if (_presetsMenu == null) return;
            _presetMenuDismissed = true;
            RestoreCloudPreview();
            _presetClearListMenu.Visible = false;
            _presetPrefabDeleteMenu.Visible = false;
            _presetPrefabsMenu.Visible = false;
            _clearListParent = null;
            _presetDeleteMenu.Visible = false;
            _presetModelsMenu.Visible = false;
            _presetPassengerModelsMenu.Visible = false;
            _presetTextureModelsMenu.Visible = false;
            _presetWeatherMenu.Visible = false;
            _presetCloudMenu.Visible = false;
            _presetColorsMenu.Visible = false;
            _presetPaintMenu.Visible = false;
            _presetEditMenu.Visible = false;
            _presetDetailMenu.Visible = false;
            _presetsMenu.Visible = false;
        }

        private enum PresetStage { Paint, TexturesStart, TexturesWait, Environment,
            PlateStart, PlateWait,
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
                _motorcyclePassengerVehicles != null)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Дождись завершения текущего действия.", 3000);
                return;
            }
            _presetRun = ClonePreset(selected);
            PrefabsPlugin prefabPlugin = PrefabsPlugin.Current;
            if (prefabPlugin != null)
                prefabPlugin.ApplyPresetPlacements(_presetRun.SpawnPrefabs ? _presetRun.Prefabs : null);
            else if (_presetRun.SpawnPrefabs)
                GTA.UI.Screen.ShowSubtitle("~r~Заготовки не загружены: стриминг не запущен.", 3500);
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
                        if (_presetRun.RandomTextures) RandomizeObjectTextureVariations(_presetRun.TextureModels);
                        _presetStage = PresetStage.TexturesWait;
                        break;
                    case PresetStage.TexturesWait:
                        if (_textureVariationProps == null) _presetStage = PresetStage.Environment;
                        break;
                    case PresetStage.Environment:
                        ApplyPresetEnvironment();
                        _presetStage = PresetStage.PlateStart;
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

        private void ApplyPresetEnvironment()
        {
            if (_presetRun.RandomWeather)
            {
                if (_presetRun.WeatherChoices.Count == 0)
                    GTA.UI.Screen.ShowSubtitle("~y~В пресете не выбраны варианты погоды.", 3000);
                else
                {
                    string weather = _presetRun.WeatherChoices[_rng.Next(_presetRun.WeatherChoices.Count)];
                    try
                    {
                        Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, weather);
                        CrashDiagnostics.Event("RainbowPaint", "Preset weather: " + weather);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("RainbowPaint: Preset weather " + weather, ex);
                        GTA.UI.Screen.ShowSubtitle("~r~Не удалось сменить погоду.", 3000);
                    }
                }
            }
            if (_presetRun.RandomClouds)
            {
                if (_presetRun.CloudChoices.Count == 0)
                    GTA.UI.Screen.ShowSubtitle("~y~В пресете не выбраны варианты облаков.", 3000);
                else
                {
                    string cloud = _presetRun.CloudChoices[_rng.Next(_presetRun.CloudChoices.Count)];
                    try
                    {
                        Function.Call(Hash.UNLOAD_ALL_CLOUD_HATS);
                        Function.Call(Hash.LOAD_CLOUD_HAT, cloud, 3f);
                        _lastPresetCloudName = cloud;
                        CrashDiagnostics.Event("RainbowPaint", "Preset clouds: " + cloud);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("RainbowPaint: Preset clouds " + cloud, ex);
                        GTA.UI.Screen.ShowSubtitle("~r~Не удалось сменить облака.", 3000);
                    }
                }
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
        }
    }
}
