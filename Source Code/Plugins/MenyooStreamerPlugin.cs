using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;
using PluginLogging;

namespace MenyooStreamer
{
    public class MenyooStreamerPlugin : IGtaPlugin
    {
        private Config _config;
        private PedCaptureSystem _captureSystem;
        private PedStreamerSystem _streamerSystem;
        private ObjectPool _pool;
        private MenuManager _menu;
        private string _logPath;
        private List<PedRecord> _cachedPeds;
        private bool _startPending;
        private bool _rescanPending;
        private bool _working;

        public void OnStart()
        {
            _config = new Config();
            _config.Load();

            _captureSystem = new PedCaptureSystem((float)_config.ChunkSize);
            _streamerSystem = new PedStreamerSystem(_config);

            _logPath = Path.Combine(_config.DataDirectory, "MenyooStreamer.log");
            Log("Мод инициализирован (поток педов)");

            _pool = new ObjectPool();
            _menu = new MenuManager(_pool);
            _menu.SetValues((int)_config.ScanRadius, (int)_config.LoadRadius, (int)_config.ClearRadius, _config.MaxPeds);
            _menu.StartRequested += () => _startPending = true;
            _menu.RescanRequested += () => _rescanPending = true;
            _menu.StopRequested += ExecuteStop;
            _menu.ValuesChanged += () =>
            {
                _config.ScanRadius = _menu.ScanRadius;
                _config.LoadRadius = _menu.LoadRadius;
                _config.ClearRadius = _menu.UnloadRadius;
                _config.MaxPeds = _menu.MaxPeds;
                _config.Save();
            };

            GTA.UI.Notification.PostTicker("~b~MenyooStreamer~w~ мод загружен~n~Нажми ~y~U~w~ для меню", false, false);
        }

        public void OnTick()
        {
            try
            {
                _pool.Process();

                if (_working) return;

                if (_startPending)
                {
                    _startPending = false;
                    ExecuteRestart();
                    return;
                }

                if (_rescanPending)
                {
                    _rescanPending = false;
                    ExecuteRescan();
                    return;
                }

                if (!_streamerSystem.IsStreaming)
                    return;

                var player = Game.Player.Character;
                if (player == null || !player.Exists()) return;

                _streamerSystem.Update(player.Position);
            }
            catch (Exception ex)
            {
                Log("Ошибка тика: " + ex);
            }
        }

        public void OnKeyDown(Keys key)
        {
            try
            {
                if (key == Keys.U)
                {
                    Log("Переключение меню (стрим=" + _streamerSystem.IsStreaming + ")");
                    _menu.Toggle();
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка клавиши: " + ex);
            }
        }

        public void OnAbort()
        {
            try
            {
                Log("Завершение, остановка стрима...");
                if (_streamerSystem != null)
                    _streamerSystem.Stop();
            }
            catch (Exception ex)
            {
                Log("Ошибка завершения: " + ex);
            }
        }

        private void Log(string msg)
        {
            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n");
            }
            catch { }
        }

        private string DataDir()
        {
            if (_config != null && !string.IsNullOrEmpty(_config.DataDirectory))
                return _config.DataDirectory;
            return "scripts";
        }

        private void ExecuteRestart()
        {
            if (_working) return;

            if (_cachedPeds == null || _cachedPeds.Count == 0)
            {
                ExecuteRescan();
                return;
            }

            _working = true;
            try
            {
                _menu.Close();
                Script.Yield();

                if (_streamerSystem.IsStreaming)
                {
                    _streamerSystem.Stop();
                    Script.Yield();
                }

                _config.LoadRadius = _menu.LoadRadius;
                _config.ClearRadius = _menu.UnloadRadius;
                _config.MaxPeds = _menu.MaxPeds;
                _config.Save();

                Log("Перезапуск стрима с " + _cachedPeds.Count + " сохранёнными педами");
                _streamerSystem.Start(_cachedPeds);
                Script.Yield();

                int chunks = _streamerSystem.TotalChunkCount;
                GTA.UI.Notification.PostTicker(
                    "~g~Перезапуск~w~: " + _cachedPeds.Count + " педов, ~g~" + chunks + " чанков.", false, false);
            }
            catch (Exception ex)
            {
                Log("Ошибка перезапуска: " + ex);
                GTA.UI.Notification.PostTicker("~r~Ошибка: ~w~" + ex.Message, false, false);
                try { File.AppendAllText(Path.Combine(DataDir(), "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\n"); } catch { }
            }
            finally
            {
                _working = false;
            }
        }

        private void ExecuteRescan()
        {
            if (_working) return;
            _working = true;

            try
            {
                _menu.Close();
                Script.Yield();

                if (_streamerSystem.IsStreaming)
                {
                    _streamerSystem.Stop();
                    Script.Yield();
                }

                _config.ScanRadius = _menu.ScanRadius;
                _config.LoadRadius = _menu.LoadRadius;
                _config.ClearRadius = _menu.UnloadRadius;
                _config.MaxPeds = _menu.MaxPeds;
                _config.Save();

                Log("Сканирование педов: скан=" + _config.ScanRadius + "м загрузка=" + _config.LoadRadius + "м выгрузка=" + _config.ClearRadius + "м");
                GTA.UI.Screen.ShowSubtitle("~b~Сканирование педов...", 2000);
                Script.Yield();

                var peds = _captureSystem.CaptureAllPeds(_config.ScanRadius);
                Log("Захвачено педов: " + peds.Count);

                if (peds.Count == 0)
                {
                    GTA.UI.Notification.PostTicker("~r~В радиусе сканирования педы не найдены.", false, false);
                    return;
                }

                _cachedPeds = peds;
                _streamerSystem.Start(_cachedPeds);
                Script.Yield();

                int chunks = _streamerSystem.TotalChunkCount;
                Log("Стрим запущен: " + peds.Count + " педов, " + chunks + " чанков");

                GTA.UI.Notification.PostTicker(
                    "~g~Захвачено~w~: " + peds.Count + " педов, ~g~" + chunks + " чанков. ~b~Стрим запущен.", false, false);
            }
            catch (Exception ex)
            {
                Log("Ошибка сканирования: " + ex);
                GTA.UI.Notification.PostTicker("~r~Ошибка: ~w~" + ex.Message, false, false);
                try { File.AppendAllText(Path.Combine(DataDir(), "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\n"); } catch { }
            }
            finally
            {
                _working = false;
            }
        }

        private void ExecuteStop()
        {
            try
            {
                Log("Остановка стрима...");
                _streamerSystem.Stop();
                GTA.UI.Notification.PostTicker("~y~Стрим педов остановлен.", false, false);
            }
            catch (Exception ex)
            {
                Log("Ошибка остановки: " + ex);
                try { File.AppendAllText(Path.Combine(DataDir(), "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\n"); } catch { }
            }
        }
    }

    public class Config
    {
        public float LoadRadius { get; set; }
        public float ClearRadius { get; set; }
        public float ScanRadius { get; set; }
        public int CheckInterval { get; set; }
        public int BatchSize { get; set; }
        public int MaxPeds { get; set; }
        public float ChunkSize { get; set; }
        public string DataDirectory { get; set; }

        private readonly string _configPath;
        private const int CurrentVersion = 3;

        public Config()
        {
            LoadRadius = 80f;
            ClearRadius = 100f;
            ScanRadius = 3000f;
            CheckInterval = 1000;
            BatchSize = 20;
            MaxPeds = 150;
            ChunkSize = 50f;

            try
            {
                string pluginsRoot = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins");
                DataDirectory = Path.Combine(pluginsRoot, "MenyooStreamer");
                _configPath = Path.Combine(pluginsRoot, "MenyooStreamer.ini");
            }
            catch
            {
                DataDirectory = "ReloaderPlugins/MenyooStreamer";
                _configPath = "ReloaderPlugins/MenyooStreamer.ini";
            }
        }

        public void Load()
        {
            try
            {
                bool needsSave = false;
                int iniVersion = 0;

                if (File.Exists(_configPath))
                {
                    var lines = File.ReadAllLines(_configPath);

                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                            continue;

                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;

                        var key = line.Substring(0, eq).Trim().ToLower();
                        var val = line.Substring(eq + 1).Trim();

                        int v;
                        float lr, cr, sr, cs;
                        int ci, bs;

                        if (key == "version" && int.TryParse(val, out v))
                            iniVersion = v;
                        else if (key == "loadradius" && float.TryParse(val, out lr))
                            LoadRadius = lr;
                        else if (key == "clearradius" && float.TryParse(val, out cr))
                            ClearRadius = cr;
                        else if (key == "scanradius" && float.TryParse(val, out sr))
                            ScanRadius = sr;
                        else if (key == "checkinterval" && int.TryParse(val, out ci))
                            CheckInterval = ci;
                        else if (key == "batchsize" && int.TryParse(val, out bs))
                            BatchSize = bs;
                        else if (key == "maxpeds" && int.TryParse(val, out v))
                            MaxPeds = v;
                        else if (key == "chunksize" && float.TryParse(val, out cs))
                            ChunkSize = cs;
                    }
                }
                else
                {
                    needsSave = true;
                }

                if (iniVersion < CurrentVersion)
                {
                    if (iniVersion < 2)
                    {
                        LoadRadius = 80f;
                        ClearRadius = 100f;
                        ScanRadius = 3000f;
                        ChunkSize = 50f;
                    }
                    needsSave = true;
                }

                Validate();

                if (needsSave)
                    Save();
            }
            catch (Exception ex)
            {
                PluginLog.Error("MenyooStreamer: Config.Load", ex);
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var lines = new List<string>
                {
                    "; MenyooStreamer v" + CurrentVersion,
                    "Version=" + CurrentVersion,
                    "",
                    "[Streaming]",
                    "; Загружать педов в этом радиусе от игрока (метры)",
                    "LoadRadius=" + LoadRadius,
                    "; Выгружать педов за этим радиусом (метры)",
                    "ClearRadius=" + ClearRadius,
                    "; Радиус сканирования при захвате (метры)",
                    "ScanRadius=" + ScanRadius,
                    "; Как часто проверять позицию игрока (миллисекунды)",
                    "CheckInterval=" + CheckInterval,
                    "; Максимум чанков за тик (больше = быстрее, но лаги)",
                    "BatchSize=" + BatchSize,
                    "; Максимум одновременно загруженных педов",
                    "MaxPeds=" + MaxPeds,
                    "; Размер ячейки сетки (метры)",
                    "ChunkSize=" + ChunkSize,
                };

                File.WriteAllLines(_configPath, lines);
            }
            catch (Exception ex)
            {
                PluginLog.Error("MenyooStreamer: Config.Save", ex);
            }
        }

        private void Validate()
        {
            try
            {
                LoadRadius = Math.Max(5f, Math.Min(LoadRadius, 2000f));
                ClearRadius = Math.Max(LoadRadius + 5f, ClearRadius);
                ScanRadius = Math.Max(100f, Math.Min(ScanRadius, 5000f));
                CheckInterval = Math.Max(100, Math.Min(CheckInterval, 10000));
                BatchSize = Math.Max(1, Math.Min(BatchSize, 100));
                MaxPeds = Math.Max(50, Math.Min(MaxPeds, 1000));
                ChunkSize = Math.Max(10f, Math.Min(ChunkSize, 1000f));
            }
            catch (Exception ex)
            {
                PluginLog.Error("MenyooStreamer: Config.Validate", ex);
            }
        }
    }

    public class PedRecord
    {
        public int ModelHash { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public float Pitch { get; set; }
        public float Roll { get; set; }
        public float Yaw { get; set; }
        public float Heading { get; set; }
        public int ChunkX { get; set; }
        public int ChunkY { get; set; }
        public bool DeletedByMod { get; set; }

        public PedRecord()
        {
            DeletedByMod = true;
        }
    }

    public class MenuManager
    {
        private NativeMenu _menu;
        private NativeListItem<int> _scanList;
        private NativeListItem<int> _loadList;
        private NativeListItem<int> _unloadList;
        private NativeListItem<int> _maxPedsList;
        private NativeItem _startItem;
        private NativeItem _rescanItem;
        private NativeItem _stopItem;
        private NativeItem _debugItem;

        public bool IsOpen { get; private set; }
        public int ScanRadius
        {
            get { try { return _scanList.SelectedItem; } catch { return 3000; } }
        }
        public int LoadRadius
        {
            get { try { return _loadList.SelectedItem; } catch { return 80; } }
        }
        public int UnloadRadius
        {
            get { try { return _unloadList.SelectedItem; } catch { return 100; } }
        }
        public int MaxPeds
        {
            get { try { return _maxPedsList.SelectedItem; } catch { return 150; } }
        }

        public event Action StartRequested;
        public event Action RescanRequested;
        public event Action StopRequested;
        public event Action ValuesChanged;

        public MenuManager(ObjectPool pool)
        {
            try
            {
                _menu = new NativeMenu("MenyooStreamer", "Потоковая загрузка педов");

                _scanList = CreateNumberList("Радиус сканирования", 100, 5000, 100);
                _loadList = CreateNumberList("Радиус загрузки", 5, 2000, 5);
                _unloadList = CreateNumberList("Радиус выгрузки", 10, 2500, 5);
                _maxPedsList = CreateNumberList("Лимит педов", 50, 1000, 50);

                _startItem = new NativeItem(
                    "~g~Перезапустить стрим",
                    "Перезапустить поток с сохранёнными педами (без повторного сканирования)");
                _rescanItem = new NativeItem(
                    "~y~Сканировать заново",
                    "Сканировать мир заново и перезапустить поток");
                _stopItem = new NativeItem(
                    "~r~Остановить стрим",
                    "Остановить поток и удалить всех созданных педов");
                _debugItem = new NativeItem(
                    "Отладка",
                    "Показать текущий статус потока");

                _menu.Add(_scanList);
                _menu.Add(_loadList);
                _menu.Add(_unloadList);
                _menu.Add(_maxPedsList);
                _menu.Add(_startItem);
                _menu.Add(_rescanItem);
                _menu.Add(_stopItem);
                _menu.Add(_debugItem);

                _startItem.Activated += (s, e) =>
                {
                    try { if (StartRequested != null) StartRequested(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: StartRequested", ex); }
                };
                _rescanItem.Activated += (s, e) =>
                {
                    try { if (RescanRequested != null) RescanRequested(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: RescanRequested", ex); }
                };
                _stopItem.Activated += (s, e) =>
                {
                    try { if (StopRequested != null) StopRequested(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: StopRequested", ex); }
                };
                _debugItem.Activated += (s, e) =>
                {
                    try { UpdateDebugInfo(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: UpdateDebugInfo", ex); }
                };

                _scanList.ItemChanged += (s, e) => { try { if (ValuesChanged != null) ValuesChanged(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: ValuesChanged", ex); } };
                _loadList.ItemChanged += (s, e) => { try { if (ValuesChanged != null) ValuesChanged(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: ValuesChanged", ex); } };
                _unloadList.ItemChanged += (s, e) => { try { if (ValuesChanged != null) ValuesChanged(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: ValuesChanged", ex); } };
                _maxPedsList.ItemChanged += (s, e) => { try { if (ValuesChanged != null) ValuesChanged(); } catch (Exception ex) { PluginLog.Error("MenyooStreamer: ValuesChanged", ex); } };

                _menu.Closing += (s, e) => IsOpen = false;
                _menu.Closed += (s, e) => IsOpen = false;
                _menu.Shown += (s, e) => IsOpen = true;

                pool.Add(_menu);
            }
            catch
            {
            }
        }

        private NativeListItem<int> CreateNumberList(string title, int min, int max, int step)
        {
            try
            {
                int count = ((max - min) / step) + 1;
                var values = new int[count];
                for (int i = 0; i < count; i++)
                    values[i] = min + i * step;

                return new NativeListItem<int>(title, values);
            }
            catch
            {
                return null;
            }
        }

        public void SetValues(int scanRadius, int loadRadius, int unloadRadius, int maxPeds)
        {
            try
            {
                SetListValue(_scanList, scanRadius);
                SetListValue(_loadList, loadRadius);
                SetListValue(_unloadList, unloadRadius);
                SetListValue(_maxPedsList, maxPeds);
            }
            catch
            {
            }
        }

        private void SetListValue(NativeListItem<int> list, int value)
        {
            try
            {
                if (list == null) return;
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.Items[i] == value)
                    {
                        list.SelectedIndex = i;
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        public void Open()
        {
            try
            {
                IsOpen = true;
                if (_menu != null)
                    _menu.Visible = true;
            }
            catch
            {
            }
        }

        public void Close()
        {
            try
            {
                IsOpen = false;
                if (_menu != null)
                    _menu.Visible = false;
            }
            catch
            {
            }
        }

        public void Toggle()
        {
            try
            {
                if (IsOpen)
                    Close();
                else
                    Open();
            }
            catch
            {
            }
        }

        private void UpdateDebugInfo()
        {
            try
            {
                int worldPeds = 0;
                try
                {
                    Ped[] allPeds = GTA.World.GetAllPeds();
                    if (allPeds != null)
                        worldPeds = allPeds.Length;
                }
                catch (Exception ex) { PluginLog.Error("MenyooStreamer: GetAllPeds", ex); }
                string msg = "Педы в мире: " + worldPeds + "\n" +
                             "Скан: " + ScanRadius + "м | Загрузка: " + LoadRadius + "м | Выгрузка: " + UnloadRadius + "м";

                if (_menu != null)
                    _menu.Name = msg;
            }
            catch
            {
            }
        }
    }

    public class PedCaptureSystem
    {
        private readonly float _chunkSize;

        private struct AnimPair
        {
            public string Dict;
            public string Anim;

            public AnimPair(string dict, string anim)
            {
                Dict = dict;
                Anim = anim;
            }
        }

        private static readonly AnimPair[] KnownAnims = new[]
        {
            new AnimPair("anim@mp_player_intcelebrationmale", "idle_a"),
            new AnimPair("anim@mp_player_intcelebrationmale", "idle_b"),
            new AnimPair("anim@mp_player_intcelebrationmale", "idle_c"),
            new AnimPair("anim@mp_player_intcelebrationmale", "idle_d"),
            new AnimPair("anim@mp_player_intcelebrationmale", "idle_e"),
            new AnimPair("anim@mp_player_intcelebrationmale", "idle_f"),
            new AnimPair("anim@mp_player_intcelebrationfemale", "idle_a"),
            new AnimPair("anim@mp_player_intcelebrationfemale", "idle_b"),
            new AnimPair("anim@mp_player_intcelebrationfemale", "idle_c"),
            new AnimPair("anim@mp_player_intcelebrationfemale", "idle_d"),
            new AnimPair("anim@mp_player_intcelebrationfemale", "idle_e"),
            new AnimPair("anim@mp_player_intcelebrationfemale", "idle_f"),
            new AnimPair("move_clown@p_m_zero_idles@", "fidget_short_dance"),
            new AnimPair("move_clown@p_m_one_idles@", "fidget_short_dance"),
            new AnimPair("move_clown@p_m_zero_idles@", "fidget_dance_enter"),
            new AnimPair("move_clown@p_m_one_idles@", "fidget_dance_enter"),
            new AnimPair("amb@world_human_dancing@male@base", "base"),
            new AnimPair("amb@world_human_dancing@female@base", "base"),
            new AnimPair("mini@strip_club@pole_dance@pole_a_2_stage", "pd_a2_stage"),
            new AnimPair("mini@strip_club@pole_dance@pole_a_1_stage", "pd_a1_stage"),
            new AnimPair("mini@strip_club@pole_dance@pole_b_2_stage", "pd_b2_stage"),
            new AnimPair("mini@strip_club@pole_dance@pole_b_1_stage", "pd_b1_stage"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_01@dance@", "solo"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_02@dance@", "solo"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_03@dance@", "solo"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_04@dance@", "solo"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_05@dance@", "solo"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_06@dance@", "solo"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_01@", "couple_dance"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_02@", "couple_dance"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_03@", "couple_dance"),
            new AnimPair("anim@mp_player_intupperair_shagging", "idle_a"),
            new AnimPair("anim@mp_player_intupperuncle_disco", "idle_a"),
            new AnimPair("anim@mp_player_intupperfind_the_tensor", "idle_a"),
            new AnimPair("anim@mp_player_intupperpeace", "idle_a"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_groups_transitions@from_low_intensity", "trans_dance_crowd_li_to_mi_09_v1_male^4"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_single_props_transitions@from_med_intensity", "trans_dance_prop_mi_to_hi_11_v1_male^1"),
            new AnimPair("anim@amb@nightclub@dancers@club_ambientpeds@med-hi_intensity", "mi-hi_amb_club_12_v1_male^4"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_facedj_transitions@", "trans_dance_facedj_mi_to_hi_09_v1_female^3"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_single_props@med_intensity", "mi_dance_prop_17_v1_female^3"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_groups_transitions@from_med_intensity", "trans_dance_crowd_mi_to_li_12_v1_female^5"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_facedj@hi_intensity", "hi_dance_facedj_15_v1_male^6"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_facedj_transitions@from_hi_intensity", "trans_dance_facedj_hi_to_li_09_v1_female^6"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_groups@hi_intensity", "hi_dance_crowd_13_v2_male^4"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_facedj@hi_intensity", "hi_dance_facedj_15_v2_male^4"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_groups@hi_intensity", "hi_dance_crowd_17_v2_male^5"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_facedj@hi_intensity", "hi_dance_facedj_17_v2_female^3"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_groups@hi_intensity", "hi_dance_crowd_13_v2_male^6"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_single_props@hi_intensity", "hi_dance_prop_17_v1_female^5"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_single_props@hi_intensity", "hi_dance_prop_15_v1_male^6"),
            new AnimPair("anim@amb@nightclub@dancers@crowddance_single_props@hi_intensity", "hi_dance_prop_11_v1_female^6"),
            new AnimPair("anim@amb@nightclub@dancers@tale_of_us_entourage@", "mi_dance_prop_13_v2_male^4"),
            new AnimPair("anim@amb@nightclub@lazlow@hi_dancefloor@", "dancecrowd_hi_05_dlg_havingit_laz"),
            new AnimPair("anim@amb@nightclub@lazlow@hi_dancefloor@", "dancecrowd_trans_07_hi_to_mi_laz"),
            new AnimPair("anim@amb@nightclub@lazlow@hi_podium@", "danceidle_hi_11_buttwiggle_b_laz"),
            new AnimPair("anim@amb@nightclub@lazlow@hi_podium@", "danceidle_hi_15_crazyrobot_laz"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_b@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_d@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_f@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_h@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_j@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@female@var_b@", "med_center_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@shuffle@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@shuffle@", "med_center"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_karate@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "med_center"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@male@var_b@", "med_center"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@jumper@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@female@var_b@", "low_left_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@female@var_b@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_k@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_k@", "ped_a_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_l@", "ped_b_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_l@", "ped_a_dance_idle"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_paired@dance_m@", "ped_a_dance_idle"),
            new AnimPair("amb@world_human_paparazzi@male@idle_a", "idle_a"),
            new AnimPair("anim@arena@celeb@podium@no_prop@", "hands_air_b_1st"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_karate@", "med_right_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "high_right"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "high_center_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "high_center"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@techno_monkey@", "med_center_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@shuffle@", "high_center"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@male@var_b@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@male@var_b@", "high_center_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@male@var_a@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@male@var_a@", "high_center_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@male@var_a@", "high_center_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@jumper@", "high_left_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@jumper@", "med_center"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@jumper@", "high_center_down"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@jumper@", "med_center_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@female@var_a@", "high_center_up"),
            new AnimPair("anim@amb@nightclub@mini@dance@dance_solo@beach_boxing@", "high_left_up"),
            new AnimPair("anim@amb@nightclub@lazlow@hi_railing@", "ambclub_12_mi_hi_bootyshake_laz"),
        };

        private static readonly HashSet<string> KnownAnimsSet = new HashSet<string>(
            KnownAnims.Select(a => a.Dict + "@" + a.Anim));

        public PedCaptureSystem(float chunkSize)
        {
            _chunkSize = chunkSize;
        }

        public List<PedRecord> CaptureAllPeds(float scanRadius)
        {
            var records = new List<PedRecord>();

            Ped[] peds;
            try
            {
                peds = World.GetAllPeds();
            }
            catch
            {
                return records;
            }

            var player = Game.Player.Character;
            Vector3 playerPos = Vector3.Zero;
            if (player != null && player.Exists())
                playerPos = player.Position;

            float scanSq = scanRadius * scanRadius;

            foreach (var ped in peds)
            {
                try
                {
                    if (ped == null || !ped.Exists()) continue;
                    if (ped == player) continue;
                    if (ped.IsInVehicle()) continue;
                    if (ped.IsDead) continue;

                    var pos = ped.Position;
                    float dx = pos.X - playerPos.X;
                    float dy = pos.Y - playerPos.Y;
                    float dz = pos.Z - playerPos.Z;

                    if (scanRadius > 0 && (dx * dx + dy * dy + dz * dz) > scanSq)
                        continue;

                    var rot = ped.Rotation;

                    int cx = (int)Math.Floor(pos.X / _chunkSize);
                    int cy = (int)Math.Floor(pos.Y / _chunkSize);

                    if (IsPedAnimated(ped))
                        continue;

                    records.Add(new PedRecord
                    {
                        ModelHash = ped.Model.Hash,
                        PosX = pos.X,
                        PosY = pos.Y,
                        PosZ = pos.Z,
                        Pitch = rot.X,
                        Roll = rot.Y,
                        Yaw = rot.Z,
                        Heading = ped.Heading,
                        ChunkX = cx,
                        ChunkY = cy
                    });

                    ped.Delete();
                }
                catch
                {
                }
            }

            return records;
        }

        private bool IsPedAnimated(Ped ped)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_PED_USING_ANY_SCENARIO, ped.Handle))
                    return true;

                string dict = Function.Call<string>((Hash)0x840F03E904F4E5C1, ped.Handle);
                string name = Function.Call<string>((Hash)0x4E4A5A9F21BB650E, ped.Handle);
                if (!string.IsNullOrEmpty(dict) && !string.IsNullOrEmpty(name) &&
                    KnownAnimsSet.Contains(dict + "@" + name))
                    return true;
            }
            catch
            {
            }

            return false;
        }
    }

    public class PedStreamerSystem
    {
        private readonly Config _config;
        private Dictionary<string, List<PedRecord>> _chunks;
        private Dictionary<string, List<Ped>> _loadedChunks;
        private Dictionary<int, PedRecord> _handleMap;
        private Dictionary<string, int> _failedLoads;
        private DateTime _lastCheck;
        private bool _isStreaming;

        private const int PedsPerChunkTick = 10;
        private const int MaxLoadAttempts = 20;

        public bool IsStreaming { get { return _isStreaming; } }
        public int LoadedPedCount
        {
            get
            {
                try
                {
                    if (_loadedChunks == null) return 0;
                    int total = 0;
                    foreach (var kvp in _loadedChunks)
                    {
                        kvp.Value.RemoveAll(p => !p.Exists());
                        total += kvp.Value.Count;
                    }
                    return total;
                }
                catch
                {
                    return 0;
                }
            }
        }
        public int LoadedChunkCount
        {
            get { return _loadedChunks == null ? 0 : _loadedChunks.Count; }
        }
        public int TotalChunkCount
        {
            get { return _chunks == null ? 0 : _chunks.Count; }
        }

        public PedStreamerSystem(Config config)
        {
            _config = config;
            _chunks = new Dictionary<string, List<PedRecord>>();
            _loadedChunks = new Dictionary<string, List<Ped>>();
            _handleMap = new Dictionary<int, PedRecord>();
            _failedLoads = new Dictionary<string, int>();
            _lastCheck = DateTime.MinValue;
            _isStreaming = false;
        }

        public void Start(List<PedRecord> peds)
        {
            try
            {
                Stop();

                _chunks.Clear();
                _handleMap.Clear();
                _failedLoads.Clear();

                foreach (var ped in peds)
                {
                    ped.DeletedByMod = true;
                    string key = ped.ChunkX + "_" + ped.ChunkY;
                    if (!_chunks.ContainsKey(key))
                        _chunks[key] = new List<PedRecord>();
                    _chunks[key].Add(ped);
                }

                _loadedChunks.Clear();
                _isStreaming = true;
                _lastCheck = DateTime.MinValue;
            }
            catch
            {
                _isStreaming = false;
            }
        }

        public void Stop()
        {
            _isStreaming = false;

            try
            {
                foreach (var kvp in _loadedChunks)
                {
                    foreach (var ped in kvp.Value)
                    {
                        try
                        {
                            if (ped.Exists())
                            {
                                ped.MarkAsNoLongerNeeded();
                                ped.Delete();
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            _loadedChunks.Clear();
            _handleMap.Clear();
            _failedLoads.Clear();
        }

        public void Update(Vector3 playerPosition)
        {
            try
            {
                if (!_isStreaming) return;

                var now = DateTime.UtcNow;
                if ((now - _lastCheck).TotalMilliseconds < _config.CheckInterval)
                    return;
                _lastCheck = now;

                float loadSq = _config.LoadRadius * _config.LoadRadius;
                float clearSq = _config.ClearRadius * _config.ClearRadius;

                var toUnload = new List<string>();
                var toLoad = new List<string>();

                foreach (var kvp in _chunks)
                {
                    float minDistSq = float.MaxValue;

                    foreach (var record in kvp.Value)
                    {
                        float dx = record.PosX - playerPosition.X;
                        float dy = record.PosY - playerPosition.Y;
                        float dz = record.PosZ - playerPosition.Z;
                        float distSq = dx * dx + dy * dy + dz * dz;
                        if (distSq < minDistSq)
                            minDistSq = distSq;
                    }

                    bool isLoaded = _loadedChunks.ContainsKey(kvp.Key);

                    if (isLoaded && minDistSq > clearSq)
                        toUnload.Add(kvp.Key);
                    else if (!isLoaded && minDistSq < loadSq)
                        toLoad.Add(kvp.Key);
                }

                foreach (var key in toUnload)
                    UnloadChunk(key);

                int processed = 0;
                foreach (var key in toLoad)
                {
                    if (processed >= _config.BatchSize) break;
                    if (_handleMap.Count >= _config.MaxPeds) break;
                    LoadChunk(key);
                    processed++;
                }

                foreach (var kvp in _loadedChunks)
                {
                    if (processed >= _config.BatchSize) break;
                    if (_handleMap.Count >= _config.MaxPeds) break;

                    List<PedRecord> records;
                    if (!_chunks.TryGetValue(kvp.Key, out records)) continue;

                    bool hasPending = false;
                    foreach (var record in records)
                    {
                        if (record.DeletedByMod)
                        {
                            hasPending = true;
                            break;
                        }
                    }

                    if (hasPending)
                    {
                        LoadChunk(kvp.Key);
                        processed++;
                    }
                }
            }
            catch
            {
            }
        }

        private bool LoadChunk(string key)
        {
            try
            {
                List<PedRecord> records;
                if (!_chunks.TryGetValue(key, out records)) return false;

                var recordsToSpawn = records
                    .Where(r => r.DeletedByMod)
                    .Take(PedsPerChunkTick)
                    .ToList();

                if (recordsToSpawn.Count == 0) return false;

                var modelHashes = recordsToSpawn
                    .Select(r => r.ModelHash)
                    .Distinct()
                    .ToList();

                var models = new List<Model>();
                foreach (var hash in modelHashes)
                {
                    var model = new Model(hash);
                    if (model.IsValid && model.IsInCdImage)
                    {
                        model.Request();
                        models.Add(model);
                    }
                }

                if (models.Count == 0)
                {
                    FailChunk(key);
                    return false;
                }
                if (models.Any(m => !m.IsLoaded))
                {
                    FailChunk(key);
                    return false;
                }

                var peds = new List<Ped>();
                foreach (var entry in recordsToSpawn)
                {
                    if (_handleMap.Count >= _config.MaxPeds) break;
                    try
                    {
                        var record = entry;
                        var model = new Model(record.ModelHash);
                        if (!model.IsValid || !model.IsInCdImage) continue;

                        var pos = new Vector3(record.PosX, record.PosY, record.PosZ);
                        var ped = World.CreatePed(model, pos, record.Heading);

                        if (ped != null && ped.Exists())
                        {
                            ped.Rotation = new Vector3(record.Pitch, record.Roll, record.Yaw);
                            ped.BlockPermanentEvents = true;
                            _handleMap[ped.Handle] = record;
                            peds.Add(ped);
                        }
                    }
                    catch
                    {
                    }
                }

                foreach (var model in models)
                    model.MarkAsNoLongerNeeded();

                if (peds.Count > 0)
                {
                    List<Ped> existing;
                    if (!_loadedChunks.TryGetValue(key, out existing))
                    {
                        existing = new List<Ped>();
                        _loadedChunks[key] = existing;
                    }
                    existing.AddRange(peds);
                    _failedLoads.Remove(key);
                    return true;
                }

                FailChunk(key);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void FailChunk(string key)
        {
            int fails;
            if (!_failedLoads.TryGetValue(key, out fails))
                fails = 0;
            fails++;

            if (fails >= MaxLoadAttempts)
            {
                _chunks.Remove(key);
                _failedLoads.Remove(key);
            }
            else
            {
                _failedLoads[key] = fails;
            }
        }

        private void UnloadChunk(string key)
        {
            try
            {
                List<Ped> peds;
                if (!_loadedChunks.TryGetValue(key, out peds)) return;

                foreach (var ped in peds)
                {
                    try
                    {
                        PedRecord record;
                        if (_handleMap.TryGetValue(ped.Handle, out record))
                        {
                            if (ped.Exists() && !ped.IsDead)
                            {
                                record.DeletedByMod = true;
                                ped.MarkAsNoLongerNeeded();
                                ped.Delete();
                            }
                            else
                            {
                                record.DeletedByMod = false;
                            }

                            _handleMap.Remove(ped.Handle);
                        }
                    }
                    catch
                    {
                    }
                }

                _loadedChunks.Remove(key);
            }
            catch
            {
            }
        }
    }
}
