using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;

namespace ModdedCamera.Services
{
    public class MenuService
    {
        public NativeMenu MainMenu { get; private set; }
        public NativeMenu CameraOptionsMenu { get; private set; }
        public NativeMenu SavedPathsMenu { get; private set; }
        public NativeMenu NodeEditorMenu { get; private set; }

        private NativeItem _startItem;
        private NativeItem _stopItem;
        private NativeItem _setupNodesItem;
        private NativeItem _savePathItem;
        private NativeItem _loadPathItem;
        private NativeItem _cameraOptionsItem;
        private NativeItem _resetItem;
        private NativeItem _editNodesItem;
        private NativeItem _closeItem;

        private NativeListItem<string> _speedListItem;
        private NativeListItem<string> _fovListItem;
        private NativeCheckboxItem _usePlayerViewCheckbox;

        private readonly List<NativeMenu> _pathSubMenus = new List<NativeMenu>();
        private readonly List<NativeMenu> _nodeSubMenus = new List<NativeMenu>();
        private readonly List<NativeMenu> _pendingSubMenuRemovals = new List<NativeMenu>();

        private string _savedPathsSearch = "";
        private int _lastSelectedNodeIndex = -1;

        private enum KeyboardState { None, Search, Rename }
        private KeyboardState _keyboardState = KeyboardState.None;
        private string _renameTargetPath = "";

        private static readonly string[] NodeColorNames = new string[]
        {
            "Белый", "Жёлтый", "Красный", "Оранжевый", "Зелёный",
            "Голубой", "Синий", "Фиолетовый", "Розовый", "Серый",
            "Бирюзовый", "Коричневый"
        };
        private static readonly Color[] NodeColorValues = new Color[]
        {
            Color.White, Color.Yellow, Color.Red, Color.Orange, Color.Lime,
            Color.Cyan, Color.DodgerBlue, Color.Purple, Color.DeepPink, Color.Gray,
            Color.Turquoise, Color.SaddleBrown
        };

        public ObjectPool ActivePool { get; private set; }

        private readonly CameraService _cameraService;
        private readonly SaveService _saveService;
        private readonly InputService _inputService;

        public MenuService(CameraService cameraService, SaveService saveService, InputService inputService)
        {
            if (cameraService == null) throw new ArgumentNullException("cameraService");
            if (saveService == null) throw new ArgumentNullException("saveService");
            if (inputService == null) throw new ArgumentNullException("inputService");
            _cameraService = cameraService;
            _saveService = saveService;
            _inputService = inputService;
        }

        public void Initialize()
        {
            CreateCameraOptionsMenu();
            CreateSavedPathsMenu();
            CreateNodeEditorMenu();
            CreateMainMenu();

            ActivePool = new ObjectPool();
            ActivePool.Add(MainMenu);
            ActivePool.Add(CameraOptionsMenu);
            ActivePool.Add(SavedPathsMenu);
            ActivePool.Add(NodeEditorMenu);

            RefreshSavedPathsMenu();
        }

        public void Process()
        {
            UpdateKeyboardInput();
            FlushSubMenuRemovals();
            if (ActivePool != null) ActivePool.Process();
        }

        private void FlushSubMenuRemovals()
        {
            if (_pendingSubMenuRemovals.Count == 0) return;
            for (int i = _pendingSubMenuRemovals.Count - 1; i >= 0; i--)
            {
                var m = _pendingSubMenuRemovals[i];
                if (m.Visible) continue;
                if (ActivePool != null) ActivePool.Remove(m);
                _nodeSubMenus.Remove(m);
                _pathSubMenus.Remove(m);
                _pendingSubMenuRemovals.RemoveAt(i);
            }
        }

        private void UpdateKeyboardInput()
        {
            if (_keyboardState == KeyboardState.None) return;
            try
            {
                int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
                if (status == 0) return;
                KeyboardState state = _keyboardState;
                _keyboardState = KeyboardState.None;
                if (status == 2)
                {
                    SavedPathsMenu.Visible = true;
                    return;
                }
                string input = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
                if (string.IsNullOrEmpty(input))
                {
                    SavedPathsMenu.Visible = true;
                    return;
                }
                input = input.Trim();
                if (state == KeyboardState.Search)
                {
                    _savedPathsSearch = input;
                    RefreshSavedPathsMenu();
                    SavedPathsMenu.Visible = true;
                }
                else if (state == KeyboardState.Rename)
                {
                    if (_saveService.RenamePath(_renameTargetPath, input))
                        RefreshSavedPathsMenu();
                    SavedPathsMenu.Visible = true;
                }
            }
            catch (Exception ex)
            {
                _keyboardState = KeyboardState.None;
                Logger.Error(ex, "MenuService: Error in UpdateKeyboardInput");
            }
        }

        public bool AreAnyVisible
        {
            get { return (ActivePool != null) ? ActivePool.AreAnyVisible : false; }
        }

        public void ToggleMenu()
        {
            if (AreAnyVisible)
                ActivePool.HideAll();
            else
                MainMenu.Visible = true;
        }

        public void HideAll()
        {
            if (ActivePool != null) ActivePool.HideAll();
        }

        public void ShowMainMenu()
        {
            SavedPathsMenu.Visible = false;
            CameraOptionsMenu.Visible = false;
            MainMenu.Visible = true;
        }

        public void SyncCameraOptionsWithMenu()
        {
            try
            {
                _fovListItem.SelectedItem = _cameraService.CurrentFov.ToString();
                _speedListItem.SelectedItem = SnapSpeedToNearest(_cameraService.CurrentSpeed);
                _usePlayerViewCheckbox.Checked = _cameraService.UsePlayerView;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in SyncCameraOptionsWithMenu");
            }
        }

        public void RefreshSavedPathsMenu()
        {
            try
            {
                foreach (var m in _pathSubMenus)
                    _pendingSubMenuRemovals.Add(m);
                _pathSubMenus.Clear();
                SavedPathsMenu.Clear();

                List<string> allPaths = PathManager.GetAllSavedPaths();

                NativeItem searchItem = new NativeItem("~b~Поиск", string.IsNullOrEmpty(_savedPathsSearch) ? "Нажмите, чтобы ввести текст" : "Фильтр: \"" + _savedPathsSearch + "\"");
                searchItem.Activated += delegate
                {
                    try
                    {
                        Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, true, "R*", "", "", "", "", "", 64);
                        _keyboardState = KeyboardState.Search;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Search input error");
                    }
                };
                SavedPathsMenu.Add(searchItem);

                if (!string.IsNullOrEmpty(_savedPathsSearch))
                {
                    NativeItem clearSearch = new NativeItem("~y~Сбросить поиск", "Показать все сохранённые пути");
                    clearSearch.Activated += delegate { _savedPathsSearch = ""; RefreshSavedPathsMenu(); };
                    SavedPathsMenu.Add(clearSearch);
                }

                List<string> filteredPaths;
                if (string.IsNullOrEmpty(_savedPathsSearch))
                    filteredPaths = allPaths;
                else
                    filteredPaths = allPaths.FindAll(p => p.IndexOf(_savedPathsSearch, StringComparison.OrdinalIgnoreCase) >= 0);

                if (allPaths.Count == 0)
                {
                    SavedPathsMenu.Add(new NativeItem("~y~Нет сохранённых путей", "Сначала сохраните путь!"));
                    return;
                }

                if (filteredPaths.Count == 0)
                {
                    SavedPathsMenu.Add(new NativeItem("~r~Совпадений нет", "Нет путей по запросу \"" + _savedPathsSearch + "\""));
                    return;
                }

                foreach (string pathName in filteredPaths)
                {
                    NativeMenu pathSubMenu = new NativeMenu(pathName, "Действия");
                    ActivePool.Add(pathSubMenu);
                    _pathSubMenus.Add(pathSubMenu);

                    NativeItem backBtn = new NativeItem("< Назад", "Вернуться назад");
                    string currentPathName = pathName;
                    backBtn.Activated += delegate { pathSubMenu.Visible = false; SavedPathsMenu.Visible = true; };
                    pathSubMenu.Add(backBtn);

                    NativeItem loadBtn = new NativeItem("~g~Загрузить", "Загрузить и воспроизвести");
                    string pn1 = pathName;
                    loadBtn.Activated += delegate
                    {
                        try
                        {
                            _saveService.LoadPath(pn1);
                            ActivePool.HideAll();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error loading: " + pn1);
                            GTA.UI.Notification.PostTicker("~r~Не удалось загрузить: " + ex.Message, false, false);
                        }
                    };
                    pathSubMenu.Add(loadBtn);

                    NativeItem renameBtn = new NativeItem("~y~Переименовать", "Переименовать этот путь");
                    string renamePn = pathName;
                    renameBtn.Activated += delegate
                    {
                        try
                        {
                            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, true, "R*", "", "", "", "", "", 64);
                            _keyboardState = KeyboardState.Rename;
                            _renameTargetPath = renamePn;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error renaming path");
                        }
                    };
                    pathSubMenu.Add(renameBtn);

                    NativeMenu delMenu = new NativeMenu("Удалить: " + pathName, "Вы уверены?");
                    ActivePool.Add(delMenu);
                    _pathSubMenus.Add(delMenu);

                    NativeItem delBackBtn = new NativeItem("< Назад", "Отмена");
                    delBackBtn.Activated += delegate { delMenu.Visible = false; pathSubMenu.Visible = true; };
                    delMenu.Add(delBackBtn);

                    NativeItem delYesBtn = new NativeItem("~r~Да, удалить", "Подтвердить");
                    string pn2 = pathName;
                    delYesBtn.Activated += delegate
                    {
                        try
                        {
                            _saveService.DeletePath(pn2);
                            RefreshSavedPathsMenu();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error deleting: " + pn2);
                            GTA.UI.Notification.PostTicker("~r~Не удалось удалить: " + ex.Message, false, false);
                        }
                        delMenu.Visible = false;
                        pathSubMenu.Visible = true;
                    };
                    delMenu.Add(delYesBtn);

                    NativeItem deleteBtn = new NativeItem("~r~Удалить", "Удалить этот путь");
                    deleteBtn.Activated += delegate { pathSubMenu.Visible = false; delMenu.Visible = true; };
                    pathSubMenu.Add(deleteBtn);

                    NativeItem pathItem = new NativeItem(pathName, "Нажмите, чтобы открыть действия");
                    pathItem.Activated += delegate { SavedPathsMenu.Visible = false; pathSubMenu.Visible = true; };
                    SavedPathsMenu.Add(pathItem);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in RefreshSavedPathsMenu");
            }
        }

        public bool HandleBackNavigation()
        {
            try
            {
                foreach (var m in _pathSubMenus)
                {
                    if (m.Visible && m.BannerText.Text.StartsWith("Удалить: "))
                    {
                        m.Visible = false;
                        string pathName = m.BannerText.Text.Substring("Удалить: ".Length);
                        foreach (var pm in _pathSubMenus)
                        {
                            if (pm.BannerText.Text == pathName && !pm.Visible)
                            {
                                pm.Visible = true;
                                break;
                            }
                        }
                        return true;
                    }
                }

                foreach (var m in _pathSubMenus)
                {
                    if (m.Visible)
                    {
                        m.Visible = false;
                        SavedPathsMenu.Visible = true;
                        return true;
                    }
                }

                if (SavedPathsMenu.Visible)
                {
                    SavedPathsMenu.Visible = false;
                    MainMenu.Visible = true;
                    return true;
                }

                foreach (var m in _nodeSubMenus)
                {
                    if (m.Visible)
                    {
                        m.Visible = false;
                        NodeEditorMenu.Visible = true;
                        return true;
                    }
                }

                if (NodeEditorMenu.Visible)
                {
                    NodeEditorMenu.Visible = false;
                    MainMenu.Visible = true;
                    return true;
                }

                if (CameraOptionsMenu.Visible)
                {
                    CameraOptionsMenu.Visible = false;
                    MainMenu.Visible = true;
                    return true;
                }

                if (_cameraService.IsSelectorActive)
                {
                    _cameraService.ExitPointSelector();
                    return true;
                }

                if (MainMenu.Visible) return true;
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in HandleBackNavigation");
                return false;
            }
        }

        public int GetNodeDuration() { return _cameraService.NodeDuration; }

        public void IncreaseNodeDuration()
        {
            _cameraService.NodeDuration += 250;
        }

        public void DecreaseNodeDuration()
        {
            _cameraService.NodeDuration = Math.Max(250, _cameraService.NodeDuration - 250);
        }

        public void Dispose()
        {
            if (ActivePool != null)
            {
                ActivePool.HideAll();
                ActivePool = null;
            }
        }

        private void CreateMainMenu()
        {
            MainMenu = new NativeMenu("ModdedCamera", "Кинематографическая камера");

            _startItem = new NativeItem("~g~Воспроизвести путь", "");
            _startItem.Activated += (s, e) =>
            {
                ActivePool.HideAll();
                _cameraService.StartPlayback();
            };
            MainMenu.Add(_startItem);

            _stopItem = new NativeItem("~r~Остановить воспроизведение", "");
            _stopItem.Activated += (s, e) => _cameraService.StopPlayback();
            MainMenu.Add(_stopItem);

            _setupNodesItem = new NativeItem("~y~Настроить узлы", "");
            _setupNodesItem.Activated += (s, e) =>
            {
                ActivePool.HideAll();
                _cameraService.EnterPointSelector();
            };
            MainMenu.Add(_setupNodesItem);

            _savePathItem = new NativeItem("Сохранить текущий путь", "");
            _savePathItem.Activated += (s, e) =>
            {
                ActivePool.HideAll();
                _saveService.StartSave();
            };
            MainMenu.Add(_savePathItem);

            _loadPathItem = new NativeItem("Загрузить путь", "Выберите сохранённый путь для загрузки");
            _loadPathItem.Activated += (s, e) => { MainMenu.Visible = false; SavedPathsMenu.Visible = true; };
            MainMenu.Add(_loadPathItem);

            _cameraOptionsItem = new NativeItem("Настройки камеры", "Настроить параметры камеры");
            _cameraOptionsItem.Activated += (s, e) => { MainMenu.Visible = false; CameraOptionsMenu.Visible = true; };
            MainMenu.Add(_cameraOptionsItem);

            _editNodesItem = new NativeItem("~y~Редактор узлов", "Изменить длительность и интерполяцию каждого узла");
            _editNodesItem.Activated += (s, e) =>
            {
                _lastSelectedNodeIndex = -1;
                RefreshNodeEditorMenu();
                MainMenu.Visible = false;
                NodeEditorMenu.Visible = true;
            };
            MainMenu.Add(_editNodesItem);

            _resetItem = new NativeItem("Сбросить все камеры", "");
            _resetItem.Activated += (s, e) => _cameraService.ResetAll();
            MainMenu.Add(_resetItem);

            _closeItem = new NativeItem("Закрыть", "");
            _closeItem.Activated += (s, e) => ActivePool.HideAll();
            MainMenu.Add(_closeItem);
        }

        private void CreateCameraOptionsMenu()
        {
            CameraOptionsMenu = new NativeMenu("Настройки камеры", "");

            _speedListItem = new NativeListItem<string>("Скорость", "Множитель скорости воспроизведения");
            foreach (string sv in Utils.SpeedLabels)
                _speedListItem.Items.Add(sv);
            _speedListItem.SelectedItem = "x1.00";
            CameraOptionsMenu.Add(_speedListItem);

            _fovListItem = new NativeListItem<string>("Поле зрения (FOV)", "");
            for (int i = 1; i <= 100; i++)
                _fovListItem.Items.Add(i.ToString());
            _fovListItem.SelectedItem = "50";
            CameraOptionsMenu.Add(_fovListItem);

            _usePlayerViewCheckbox = new NativeCheckboxItem("Вид от игрока", "(Плавнее рендеринг местности, но ограничено движение)");
            CameraOptionsMenu.Add(_usePlayerViewCheckbox);

            _speedListItem.ItemChanged += OnSpeedChanged;
            _fovListItem.ItemChanged += OnFovChanged;
            _usePlayerViewCheckbox.CheckboxChanged += OnCheckboxChanged;
        }

        private void CreateSavedPathsMenu()
        {
            SavedPathsMenu = new NativeMenu("Сохранённые пути", "");
        }

        private void CreateNodeEditorMenu()
        {
            NodeEditorMenu = new NativeMenu("Редактор узлов", "Выберите узел для редактирования");
        }

        public void RefreshNodeEditorMenu()
        {
            try
            {
                foreach (var m in _nodeSubMenus)
                    _pendingSubMenuRemovals.Add(m);
                _nodeSubMenus.Clear();
                NodeEditorMenu.Clear();

                var spline = _cameraService.SplineCamera;
                if (spline == null || spline.Nodes.Count == 0)
                {
                    NodeEditorMenu.Add(new NativeItem("~y~Нет узлов", "Сначала добавьте узлы (Настроить узлы)"));
                    return;
                }
                int nodeCount = spline.Nodes.Count;

                NativeItem backMain = new NativeItem("< Назад", "Вернуться в главное меню");
                backMain.Activated += delegate
                {
                    NodeEditorMenu.Visible = false;
                    MainMenu.Visible = true;
                };
                NodeEditorMenu.Add(backMain);

                float totalSec = 0f;
                for (int i = 0; i < nodeCount; i++)
                {
                    try
                    {
                        int nodeIndex = i;
                        Vector3 pos = spline.Nodes[i].Item1;
                        int duration = spline.GetDurations()[i];
                        int nodeMode = (i < spline.GetNodeInterpolationModes().Count) ? spline.GetNodeInterpolationModes()[i] : 2;

                        string modeLabel = (nodeMode == 0) ? "Линейно" : (nodeMode == 1) ? "Плавно (без остановки)" : "Плавно";
                        float durSec = (float)duration / 1000f;
                        totalSec += durSec;
                        string label = "Узел " + (i + 1) + "  (" + durSec.ToString("F2") + "с, " + modeLabel + ") | всего: " + totalSec.ToString("F2") + "с";
                        string desc = "Поз: " + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1");

                        NativeMenu nodeMenu = new NativeMenu("Узел " + (i + 1), "Длительность и интерполяция");
                        ActivePool.Add(nodeMenu);
                        _nodeSubMenus.Add(nodeMenu);

                        NativeItem nodeBack = new NativeItem("< Назад", "К списку узлов");
                        nodeBack.Activated += delegate { nodeMenu.Visible = false; NodeEditorMenu.Visible = true; if (_lastSelectedNodeIndex >= 0) NodeEditorMenu.SelectedIndex = _lastSelectedNodeIndex + 1; };
                        nodeMenu.Add(nodeBack);

                        // Duration list item: 0.00..30.00 in 0.25s steps
                        NativeListItem<string> durItem = new NativeListItem<string>("Длительность", "Длительность узла в секундах");
                        for (int d = 250; d <= 30000; d += 250)
                            durItem.Items.Add(((float)d / 1000f).ToString("F2"));
                        string foundDur = durSec.ToString("F2");
                        if (durItem.Items.Contains(foundDur))
                            durItem.SelectedItem = foundDur;
                        int capturedIndex = nodeIndex;
                        durItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            float newDurSec;
                            if (float.TryParse(args.Object, out newDurSec) && newDurSec >= 0f)
                            {
                                int newDurMs = (int)(newDurSec * 1000f);
                                var sp = _cameraService.SplineCamera;
                                if (sp != null)
                                {
                                    sp.SetNodeDuration(capturedIndex, newDurMs);
                                    sp.SetStartNodeIndex(capturedIndex);
                                    _cameraService.RestartPlaybackIfActive();
                                    RefreshNodeEditorMenu();
                                }
                            }
                        };
                        nodeMenu.Add(durItem);

                        // Interpolation mode
                        NativeListItem<string> modeItem = new NativeListItem<string>("Интерполяция", "Режим движения камеры для узла");
                        modeItem.Items.Add("Линейно");
                        modeItem.Items.Add("Плавно (без остановки)");
                        modeItem.Items.Add("Плавно");
                        modeItem.SelectedItem = (nodeMode == 0) ? "Линейно" : (nodeMode == 1) ? "Плавно (без остановки)" : "Плавно";
                        int capturedIndex2 = nodeIndex;
                        modeItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            int newMode = (args.Object == "Линейно") ? 0 : (args.Object == "Плавно (без остановки)") ? 1 : 2;
                            var sp = _cameraService.SplineCamera;
                            if (sp != null)
                            {
                                sp.SetNodeInterpolationMode(capturedIndex2, newMode);
                                sp.SetStartNodeIndex(capturedIndex2);
                                _cameraService.RestartPlaybackIfActive();
                                RefreshNodeEditorMenu();
                            }
                        };
                        nodeMenu.Add(modeItem);

                        // Node color
                        int curArgb = _cameraService.SplineCamera.GetNodeColor(nodeIndex);
                        NativeListItem<string> colorItem = new NativeListItem<string>("Цвет", "Цвет маркера узла для ориентации при редактировании");
                        for (int ci = 0; ci < NodeColorNames.Length; ci++)
                            colorItem.Items.Add(NodeColorNames[ci]);
                        string foundColor = "Белый";
                        for (int ci = 0; ci < NodeColorValues.Length; ci++)
                        {
                            if (NodeColorValues[ci].ToArgb() == curArgb)
                            {
                                foundColor = NodeColorNames[ci];
                                break;
                            }
                        }
                        colorItem.SelectedItem = foundColor;
                        int capturedColorIndex = nodeIndex;
                        colorItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            int newArgb = Color.White.ToArgb();
                            for (int ci = 0; ci < NodeColorNames.Length; ci++)
                            {
                                if (NodeColorNames[ci] == args.Object)
                                {
                                    newArgb = NodeColorValues[ci].ToArgb();
                                    break;
                                }
                            }
                            var sp = _cameraService.SplineCamera;
                            if (sp != null)
                            {
                                sp.SetNodeColor(capturedColorIndex, newArgb);
                                RefreshNodeEditorMenu();
                            }
                        };
                        nodeMenu.Add(colorItem);

                        // Per-node FOV
                        int curFov = _cameraService.SplineCamera.GetNodeFov(nodeIndex);
                        NativeListItem<string> fovItem = new NativeListItem<string>("Поле зрения (FOV)", "Индивидуальное поле зрения узла");
                        for (int f = 1; f <= 100; f++)
                            fovItem.Items.Add(f.ToString());
                        string foundFov = curFov.ToString();
                        if (fovItem.Items.Contains(foundFov))
                            fovItem.SelectedItem = foundFov;
                        int capturedFovIndex = nodeIndex;
                        fovItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            int newFov;
                            if (int.TryParse(args.Object, out newFov) && newFov > 0)
                            {
                                var sp = _cameraService.SplineCamera;
                                if (sp != null)
                                {
                                    sp.SetNodeFov(capturedFovIndex, newFov);
                                    _cameraService.RestartPlaybackIfActive();
                                    RefreshNodeEditorMenu();
                                }
                            }
                        };
                        nodeMenu.Add(fovItem);

                        // Edit camera of the node
                        NativeItem editCamItem = new NativeItem("~y~Изменить камеру узла", "Свободная камера в позиции узла. Поменяйте ракурс/позицию, ЛКМ — применить и вернуться");
                        int editCamIndex = nodeIndex;
                        editCamItem.Activated += delegate
                        {
                            nodeMenu.Visible = false;
                            NodeEditorMenu.Visible = false;
                            _cameraService.EnterPointSelectorForNode(editCamIndex);
                        };
                        nodeMenu.Add(editCamItem);

                        // Duplicate node
                        NativeItem dupItem = new NativeItem("~g~Дублировать узел", "Вставить копию этого узла сразу после него");
                        int dupIndex = nodeIndex;
                        dupItem.Activated += delegate
                        {
                            var sp = _cameraService.SplineCamera;
                            if (sp != null && sp.DuplicateNode(dupIndex))
                            {
                                _lastSelectedNodeIndex = dupIndex + 1;
                                _cameraService.RestartPlaybackIfActive();
                                RefreshNodeEditorMenu();
                            }
                        };
                        nodeMenu.Add(dupItem);

                        // Delete node
                        NativeItem delItem = new NativeItem("~r~Удалить узел", "Удалить этот узел (должно остаться минимум 2)");
                        int delIndex = nodeIndex;
                        delItem.Activated += delegate
                        {
                            var sp = _cameraService.SplineCamera;
                            if (sp != null)
                            {
                                if (sp.RemoveNode(delIndex))
                                {
                                    _lastSelectedNodeIndex = Math.Min(delIndex, sp.Nodes.Count - 1);
                                    _cameraService.RestartPlaybackIfActive();
                                    RefreshNodeEditorMenu();
                                }
                                else
                                {
                                    GTA.UI.Notification.PostTicker("~r~Нужно минимум 2 узла!", false, false);
                                }
                            }
                        };
                        nodeMenu.Add(delItem);

                        NativeItem nodeItem = new NativeItem(label, desc);
                        if (curArgb != Color.White.ToArgb())
                        {
                            Color nodeTextColor = Color.FromArgb(curArgb);
                            nodeItem.Colors.TitleNormal = nodeTextColor;
                            nodeItem.Colors.TitleHovered = nodeTextColor;
                        }
                        int capturedNodeIndex = nodeIndex;
                        nodeItem.Activated += delegate { _lastSelectedNodeIndex = capturedNodeIndex; NodeEditorMenu.Visible = false; nodeMenu.Visible = true; };
                        NodeEditorMenu.Add(nodeItem);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "MenuService: Error creating node item " + i);
                        NodeEditorMenu.Add(new NativeItem("~r~Узел " + (i + 1) + " (ошибка)", ex.Message));
                    }
                }
                int restoreIdx = _lastSelectedNodeIndex + 1;
                if (_lastSelectedNodeIndex >= 0 && restoreIdx < NodeEditorMenu.Items.Count)
                    NodeEditorMenu.SelectedIndex = restoreIdx;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in RefreshNodeEditorMenu");
            }
        }

        private void OnSpeedChanged(object sender, ItemChangedEventArgs<string> e)
        {
            string sel = e.Object;
            if (sel != null && sel.StartsWith("x"))
            {
                float v;
                if (float.TryParse(sel.Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0f)
                {
                    _cameraService.CurrentSpeed = v;
                    var sc = _cameraService.SplineCamera;
                    if (sc != null && sc.Nodes.Count >= 2)
                    {
                        sc.Speed = v;
                        sc.RestartInterpolator();
                    }
                    Logger.Info("MenuService: Speed changed to x" + v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        private void OnFovChanged(object sender, ItemChangedEventArgs<string> e)
        {
            int v;
            if (int.TryParse(_fovListItem.SelectedItem, out v) && v > 0)
            {
                _cameraService.CurrentFov = v;
                _cameraService.ApplyCameraSettings();
                Logger.Info("MenuService: FOV changed to: " + v);
            }
        }

        private void OnCheckboxChanged(object sender, EventArgs e)
        {
            try
            {
                if (sender == _usePlayerViewCheckbox)
                    _cameraService.UsePlayerView = _usePlayerViewCheckbox.Checked;
                _cameraService.ApplyCameraSettings();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in OnCheckboxChanged");
            }
        }

        private static string SnapSpeedToNearest(float speed)
        {
            float nearest = Utils.ValidSpeeds[0];
            float minDiff = Math.Abs(speed - nearest);
            for (int i = 1; i < Utils.ValidSpeeds.Length; i++)
            {
                float diff = Math.Abs(speed - Utils.ValidSpeeds[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearest = Utils.ValidSpeeds[i];
                }
            }
            return "x" + nearest.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
