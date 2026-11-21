using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Elements;
using LemonUI.Menus;

using ModPack.Animations;
using ModPack.Core;

namespace ModPack.Animations
{
public sealed class AnimationsPlugin : IGtaPlugin
{
    private sealed class Selection
    {
        public Ped Ped;
        public int Model;
        public IntPtr Address;
        public AnimationEntry Assigned;
        public long LastStarted;
        public bool Valid()
        {
            return Ped != null && Ped.Exists() && Ped.Model.Hash == Model && Ped.MemoryAddress == Address;
        }
    }
    private readonly ObjectPool _pool = new ObjectPool();
    private readonly List<NativeMenu> _menus = new List<NativeMenu>();
    private readonly List<Selection> _peds = new List<Selection>();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private NativeMenu _main, _selected, _sections, _presets, _all, _favorites;
    private NativeItem _selectItem, _pedListItem, _stopItem;
    private bool _closing, _refreshSelected, _aborted, _legacyWarning;
    private string _menyooRoot;
    private AnimationEntry _active, _pending;
    private long _pendingStarted, _nextMaintenance, _nextRay;
    private Ped _aimed;
    private Task<List<AnimationEntry>> _catalogTask, _filterTask, _favoritesTask;
    private List<AnimationEntry> _catalog, _results = new List<AnimationEntry>();
    private string _catalogError, _query = "", _filterQuery;
    private int _page, _favoritePage;
    private List<AnimationEntry> _favoriteEntries = new List<AnimationEntry>();
    private DateTime _favoritesStamp = DateTime.MinValue;
    private string _favoritesError;
    private bool _allDirty, _favoritesDirty;
    private bool _searchActive, _searchReady, _searchRelease;
    private string _searchDraft;
    private ScaledRectangle _searchBackground;
    private ScaledText _searchTitle, _searchValue, _searchHint;

    public void OnStart()
    {
        string gameRoot = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        _menyooRoot = Path.Combine(gameRoot, "menyooStuff");
        string scriptsRoot = Path.Combine(gameRoot, "scripts");
        _legacyWarning = Directory.Exists(scriptsRoot) &&
            Directory.GetFiles(scriptsRoot, "DanceMode.dll", SearchOption.AllDirectories).Length > 0;
        if (_legacyWarning) Event("Legacy DanceMode.dll detected; disable it before using Y.");
        _main = Menu(ModPack.Core.ModuleMenus.Animations.Title, "Object Spooner", null);
        _selected = Menu("Выбранные педы", "Выберите педа", _main);
        _sections = Menu("Анимации", "Разделы", _main);
        _presets = Menu("Готовые действия", "Выберите действие", _sections);
        _all = Menu("Все анимации", "Каталог Menyoo", _sections);
        _favorites = Menu("Избранное", "Menyoo", _sections);
        _selectItem = Command(_main, "Выбрать педа", "Добавить педа под прицелом", SelectPed);
        _pedListItem = Command(_main, "Выбранные педы", "Убрать педа из списка", delegate {
            BuildSelected(); Show(_selected);
        });
        Command(_main, "Анимации", "Назначить действие выбранным педам", delegate { Show(_sections); });
        _stopItem = Command(_main, "Остановить анимации", "Список педов сохранится", StopAnimations);
        Command(_main, "Очистить список", "Остановить свои анимации и убрать выбор", delegate {
            StopAnimations(); _peds.Clear(); _refreshSelected = true; UpdateCount();
        });
        Command(_sections, "Готовые действия", "Именованные действия Menyoo", delegate { Show(_presets); });
        Command(_sections, "Все анимации", "Полный каталог с поиском", delegate {
            LoadCatalog(); BuildAll(); Show(_all);
        });
        Command(_sections, "Избранное", "Из FavouriteAnims.xml", delegate {
            LoadFavorites(); BuildFavorites(); Show(_favorites);
        });
        foreach (AnimationEntry entry in AnimationCatalog.Presets()) AddAnimation(_presets, entry);
        _searchBackground = new ScaledRectangle(new PointF(32, 180), new SizeF(540, 155)) {
            Color = Color.FromArgb(240, 20, 25, 30) };
        _searchTitle = new ScaledText(new PointF(48, 190), "Поиск анимаций", 0.5f);
        _searchValue = new ScaledText(new PointF(48, 240), "", 0.33f);
        _searchHint = new ScaledText(new PointF(48, 290), "Enter — поиск     Esc — отмена", 0.3f);
        UpdateCount();
        State("Idle");
    }

    private NativeMenu Menu(string title, string subtitle, NativeMenu parent)
    {
        var menu = new NativeMenu(title, subtitle);
        _menus.Add(menu);
        _pool.Add(menu);
        menu.Closed += delegate { if (!_closing && parent != null && IsSpooner()) Show(parent); };
        return menu;
    }
    private static NativeItem Command(NativeMenu menu, string title, string description, Action action)
    {
        var item = new NativeItem(title, description);
        item.Activated += delegate { action(); };
        menu.Add(item);
        return item;
    }
    private void Show(NativeMenu menu)
    {
        CloseMenus();
        menu.Visible = true;
    }
    private void CloseMenus()
    {
        _closing = true;
        try { foreach (NativeMenu menu in _menus) menu.Visible = false; }
        finally { _closing = false; }
    }
    private static bool IsSpooner()
    {
        return !Game.IsPaused && ControlSession.Owner == null && ScriptCameraDirector.RenderingCam != null;
    }

    public void OnKeyDown(Keys key)
    {
        if (_searchActive) { SearchKey(key); return; }
        if (MenuTextInput.Active || _searchRelease || key != Keys.Y) return;
        try
        {
            if (_menus.Any(m => m.Visible)) { CloseMenus(); return; }
            if (!IsSpooner()) { Notice("~y~Открой Object Spooner (F9)."); return; }
            if (_legacyWarning) Notice("~y~Отключи старый DanceMode.dll: он тоже использует Y.");
            Show(_main);
        }
        catch (Exception ex) { Error("KeyDown", ex); CloseMenus(); }
    }

    public void OnTick()
    {
        if (_aborted) return;
        try
        {
            CompleteReads();
            UpdatePending();
            if (_clock.ElapsedMilliseconds >= _nextMaintenance)
            {
                _nextMaintenance = _clock.ElapsedMilliseconds + 500;
                Maintain();
            }
            bool spooner = IsSpooner();
            if (!spooner)
            {
                CloseMenus();
                if (_searchActive) FinishSearch(false, false);
            }
            if (_refreshSelected) { BuildSelected(); _refreshSelected = false; }
            if (_allDirty) { RenderAll(); _allDirty = false; }
            if (_favoritesDirty) { RenderFavorites(); _favoritesDirty = false; }
            if (_searchActive)
            {
                Game.DisableAllControlsThisFrame();
                _searchBackground.Draw(); _searchTitle.Draw(); _searchValue.Draw(); _searchHint.Draw();
                if (!Game.IsKeyPressed(Keys.Enter)) _searchReady = true;
            }
            else if (_searchRelease)
            {
                if (!Game.IsKeyPressed(Keys.Enter) && !Game.IsKeyPressed(Keys.Escape) && !Game.IsKeyPressed(Keys.Back))
                { _searchRelease = false; MenuTextInput.End(this); }
            }
            else if (!MenuTextInput.Active) _pool.Process();
            if (spooner && !_searchActive && _menus.Any(m => m.Visible)) DrawMarkers();
        }
        catch (Exception ex)
        {
            Error("Tick", ex);
            CloseMenus();
            FinishSearch(false, false);
            StopAnimations();
        }
    }

    private Ped AimedPed()
    {
        GTA.Camera camera = ScriptCameraDirector.RenderingCam;
        if (camera == null || !camera.Exists()) return null;
        RaycastResult hit = World.Raycast(camera.Position, camera.Position + camera.Direction * 300f,
            IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Vehicles | IntersectFlags.Peds,
            Game.Player.Character);
        Ped ped = hit.DidHit ? hit.HitEntity as Ped : null;
        return ped != null && ped.Exists() && !ped.IsDead && ped.Handle != Game.Player.Character.Handle ? ped : null;
    }
    private void SelectPed()
    {
        if (!IsSpooner()) return;
        Ped ped = AimedPed();
        if (ped == null) { Notice("~y~Наведись на живого педа."); return; }
        if (_peds.Any(p => p.Valid() && p.Ped.Handle == ped.Handle)) { Notice("~y~Пед уже выбран."); return; }
        var selection = new Selection { Ped = ped, Model = ped.Model.Hash, Address = ped.MemoryAddress };
        _peds.Add(selection);
        if (_active != null) Play(selection, _active);
        _refreshSelected = true;
        UpdateCount();
        Event("Selected ped " + ped.Handle);
    }
    private void UpdateCount()
    {
        string count = _peds.Count.ToString();
        if (_pedListItem != null && _pedListItem.AltTitle != count) _pedListItem.AltTitle = count;
        if (_stopItem != null) _stopItem.Enabled = _active != null || _pending != null;
    }
    private void BuildSelected()
    {
        int selectedIndex = _selected.SelectedIndex;
        _selected.Clear();
        foreach (Selection selection in _peds)
        {
            Selection captured = selection;
            Command(_selected, "Пед " + selection.Ped.Handle, "Убрать из списка и остановить свою анимацию", delegate {
                StopPed(captured); _peds.Remove(captured); _refreshSelected = true; UpdateCount();
            });
        }
        if (_peds.Count == 0) Empty(_selected, "Никто не выбран", "Добавь педа через прицел");
        _selected.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, _selected.Items.Count - 1));
    }
    private void DrawMarkers()
    {
        if (_clock.ElapsedMilliseconds >= _nextRay)
        {
            _nextRay = _clock.ElapsedMilliseconds + 50;
            _aimed = AimedPed();
            string label = _aimed == null ? "" : "Под прицелом";
            if (_selectItem.AltTitle != label) _selectItem.AltTitle = label;
        }
        foreach (Selection selection in _peds)
            if (selection.Valid()) Marker(selection.Ped, Color.FromArgb(160, 60, 220, 130), 0.25f);
        if (_aimed != null && _aimed.Exists() && !_aimed.IsDead)
            Marker(_aimed, Color.FromArgb(220, 60, 190, 255), 0.35f);
    }
    private static void Marker(Ped ped, Color color, float size)
    {
        World.DrawMarker(MarkerType.Sphere, ped.Position + new Vector3(0, 0, 1.25f),
            Vector3.Zero, Vector3.Zero, new Vector3(size, size, size), color);
    }

    private void AddAnimation(NativeMenu menu, AnimationEntry entry)
    {
        Command(menu, AnimationCatalog.Label(entry.Title),
            "Словарь: " + entry.Dictionary + "\nКлип: " + entry.Clip, delegate { Request(entry); });
    }
    private static void Empty(NativeMenu menu, string title, string description)
    {
        menu.Add(new NativeItem(title, description) { Enabled = false });
    }
    private void LoadCatalog()
    {
        if (_catalog != null || _catalogTask != null) return;
        _catalogError = null;
        string path = Path.Combine(_menyooRoot, "PedAnimList.txt");
        _catalogTask = Task.Run(delegate {
            using (var reader = new StreamReader(path)) return AnimationCatalog.ReadCatalog(reader);
        });
        Event("Reading PedAnimList.txt");
    }
    private void FilterCatalog()
    {
        if (_catalog == null || _filterTask != null) return;
        _filterQuery = _query;
        string query = _query;
        List<AnimationEntry> catalog = _catalog;
        _filterTask = Task.Run(delegate { return AnimationCatalog.Search(catalog, query); });
    }
    private void LoadFavorites()
    {
        if (_favoritesTask != null) return;
        string path = Path.Combine(_menyooRoot, "FavouriteAnims.xml");
        DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_favoritesError == null && stamp != DateTime.MinValue && stamp == _favoritesStamp) return;
        _favoritesStamp = stamp;
        _favoritesError = null;
        _favoritesTask = Task.Run(delegate {
            using (var reader = new StreamReader(path)) return AnimationCatalog.ReadFavorites(reader);
        });
    }
    private void CompleteReads()
    {
        if (_catalogTask != null && _catalogTask.IsCompleted)
        {
            try { _catalog = _catalogTask.GetAwaiter().GetResult(); Event("Catalog loaded: " + _catalog.Count); }
            catch (Exception ex) { _catalogError = "PedAnimList.txt недоступен"; Error("Catalog", ex); }
            _catalogTask = null;
            FilterCatalog(); BuildAll();
        }
        if (_filterTask != null && _filterTask.IsCompleted)
        {
            try
            {
                List<AnimationEntry> results = _filterTask.GetAwaiter().GetResult();
                if (_filterQuery == _query) { _results = results; _page = 0; }
            }
            catch (Exception ex) { Error("Search", ex); }
            _filterTask = null;
            if (_filterQuery != _query) FilterCatalog();
            BuildAll();
        }
        if (_favoritesTask != null && _favoritesTask.IsCompleted)
        {
            try { _favoriteEntries = _favoritesTask.GetAwaiter().GetResult(); _favoritePage = 0; Event("Favorites loaded: " + _favoriteEntries.Count); }
            catch (Exception ex) { _favoriteEntries.Clear(); _favoritesError = "Избранное недоступно"; Error("Favorites", ex); }
            _favoritesTask = null; BuildFavorites();
        }
    }
    private void BuildAll()
    {
        _allDirty = true;
    }
    private void RenderAll()
    {
        _all.Clear();
        Command(_all, "Поиск", "По словарю или названию клипа", StartSearch).AltTitle = AnimationCatalog.Label(_query);
        if (_query.Length > 0) Command(_all, "Сбросить поиск", "Показать весь каталог", delegate {
            _query = ""; FilterCatalog(); BuildAll();
        });
        if (_catalogError != null)
        {
            Empty(_all, _catalogError, "Проверь файл в menyooStuff");
            Command(_all, "Повторить загрузку", "", delegate { LoadCatalog(); BuildAll(); });
        }
        else if (_catalogTask != null || _filterTask != null) Empty(_all, "Загрузка...", "Каталог обрабатывается в фоне");
        else AddPage(_all, _results, _page, delegate(int page) { _page = page; BuildAll(); });
    }
    private void BuildFavorites()
    {
        _favoritesDirty = true;
    }
    private void RenderFavorites()
    {
        _favorites.Clear();
        if (_favoritesTask != null) Empty(_favorites, "Загрузка...", "Чтение FavouriteAnims.xml");
        else if (_favoritesError != null) Empty(_favorites, _favoritesError, "Проверь FavouriteAnims.xml в menyooStuff");
        else AddPage(_favorites, _favoriteEntries, _favoritePage, delegate(int page) { _favoritePage = page; BuildFavorites(); });
    }
    private void AddPage(NativeMenu menu, IList<AnimationEntry> entries, int page, Action<int> change)
    {
        if (entries.Count == 0) { Empty(menu, "Нет анимаций", "Измени запрос или добавь избранное в Menyoo"); return; }
        int pages = (entries.Count + AnimationCatalog.PageSize - 1) / AnimationCatalog.PageSize;
        foreach (AnimationEntry entry in AnimationCatalog.Page(entries, page)) AddAnimation(menu, entry);
        if (page > 0) Command(menu, "Предыдущая страница", "", delegate { change(page - 1); });
        if (page + 1 < pages) Command(menu, "Следующая страница", "", delegate { change(page + 1); });
        Empty(menu, "Страница " + (page + 1) + " / " + pages, "Найдено анимаций: " + entries.Count);
    }

    private void Request(AnimationEntry entry)
    {
        if (_peds.Count == 0) { Notice("~y~Сначала выбери педов."); return; }
        CancelPending();
        if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, entry.Dictionary))
        { Notice("~y~Словарь анимации недоступен."); Event("Rejected dictionary: " + entry.Dictionary); return; }
        _pending = entry;
        _pendingStarted = _clock.ElapsedMilliseconds;
        Function.Call(Hash.REQUEST_ANIM_DICT, entry.Dictionary);
        UpdateCount(); State("Loading"); Event("Requested " + entry.Dictionary + " / " + entry.Clip);
    }
    private void UpdatePending()
    {
        if (_pending == null) return;
        if (_clock.ElapsedMilliseconds - _pendingStarted >= 5000)
        { Event("Animation loading timed out"); CancelPending(); Notice("~y~Анимация не загрузилась."); return; }
        if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _pending.Dictionary)) return;
        if (Function.Call<float>(Hash.GET_ANIM_DURATION, _pending.Dictionary, _pending.Clip) <= 0f)
        { Event("Rejected clip: " + _pending.Clip); CancelPending(); Notice("~y~Клип анимации недоступен."); return; }
        AnimationEntry next = _pending;
        _pending = null;
        AnimationEntry previous = _active;
        foreach (Selection selection in _peds) StopPed(selection);
        _active = next;
        foreach (Selection selection in _peds) Play(selection, next);
        if (previous != null && previous.Dictionary != next.Dictionary) Release(previous.Dictionary);
        UpdateCount(); State("Playing"); Event("Started " + next.Dictionary + " / " + next.Clip + " on " + _peds.Count + " peds");
    }
    private void Play(Selection selection, AnimationEntry entry)
    {
        if (!selection.Valid()) return;
        Ped ped = selection.Ped;
        if (ped.IsDead || ped.IsRagdoll || ped.IsFalling || ped.Handle == Game.Player.Character.Handle) return;
        Function.Call(Hash.TASK_PLAY_ANIM, ped.Handle, entry.Dictionary, entry.Clip,
            8f, -8f, -1, 1, 1f, false, false, false);
        selection.Assigned = entry;
        selection.LastStarted = _clock.ElapsedMilliseconds;
    }
    private void StopPed(Selection selection)
    {
        AnimationEntry assigned = selection.Assigned;
        selection.Assigned = null;
        if (assigned == null) return;
        try
        {
            if (selection.Valid())
                Function.Call(Hash.STOP_ANIM_TASK, selection.Ped.Handle, assigned.Dictionary, assigned.Clip, -4f);
        }
        catch (Exception ex) { Error("Stop ped " + selection.Ped.Handle, ex); }
    }
    private void Maintain()
    {
        for (int i = _peds.Count - 1; i >= 0; i--)
        {
            Selection selection = _peds[i];
            if (!selection.Valid() || selection.Ped.IsDead || selection.Ped.Handle == Game.Player.Character.Handle)
            { StopPed(selection); _peds.RemoveAt(i); _refreshSelected = true; continue; }
            if (_active != null && _clock.ElapsedMilliseconds - selection.LastStarted >= 500 &&
                !selection.Ped.IsRagdoll && !selection.Ped.IsFalling &&
                !Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, selection.Ped.Handle, _active.Dictionary, _active.Clip, 3))
                Play(selection, _active);
        }
        UpdateCount();
    }
    private void CancelPending()
    {
        if (_pending == null) return;
        string dictionary = _pending.Dictionary;
        _pending = null;
        if (_active == null || dictionary != _active.Dictionary) Release(dictionary);
        UpdateCount(); State(_active == null ? "Idle" : "Playing");
    }
    private static void Release(string dictionary)
    {
        try { Function.Call(Hash.REMOVE_ANIM_DICT, dictionary); }
        catch (Exception ex) { Error("Release dictionary " + dictionary, ex); }
    }
    private void StopAnimations()
    {
        CancelPending();
        foreach (Selection selection in _peds) StopPed(selection);
        if (_active != null) { string dictionary = _active.Dictionary; _active = null; Release(dictionary); }
        UpdateCount(); State("Idle");
    }

    private void StartSearch()
    {
        if (!MenuTextInput.Begin(this)) return;
        CloseMenus();
        _searchDraft = _query;
        _searchValue.Text = AnimationCatalog.Label(_searchDraft) + "_";
        _searchActive = true; _searchReady = false;
    }
    private void SearchKey(Keys key)
    {
        if (!_searchReady) return;
        if (key == Keys.Enter) { FinishSearch(true, true); return; }
        if (key == Keys.Escape) { FinishSearch(false, true); return; }
        if (key == Keys.Back)
        {
            if (_searchDraft.Length > 0) _searchDraft = _searchDraft.Substring(0, _searchDraft.Length - 1);
        }
        else
        {
            string value = "";
            if (Game.IsKeyPressed(Keys.ControlKey) || Game.IsKeyPressed(Keys.Menu)) return;
            else if (key >= Keys.A && key <= Keys.Z) value = ((char)('a' + (int)key - (int)Keys.A)).ToString();
            else if (key >= Keys.D0 && key <= Keys.D9)
            {
                int digit = (int)key - (int)Keys.D0;
                value = Game.IsKeyPressed(Keys.ShiftKey) && digit == 2 ? "@" :
                    Game.IsKeyPressed(Keys.ShiftKey) && digit == 6 ? "^" : digit.ToString();
            }
            else if (key >= Keys.NumPad0 && key <= Keys.NumPad9) value = ((int)key - (int)Keys.NumPad0).ToString();
            else if (key == Keys.Space) value = " ";
            else if (key == Keys.OemMinus) value = Game.IsKeyPressed(Keys.ShiftKey) ? "_" : "-";
            else if (key == Keys.OemPeriod) value = ".";
            foreach (char character in value)
                if (character >= 32 && character < 127 && character != '~' && _searchDraft.Length < 126) _searchDraft += character;
        }
        _searchValue.Text = AnimationCatalog.Label(_searchDraft) + "_";
    }
    private void FinishSearch(bool apply, bool returnToMenu)
    {
        if (!_searchActive) return;
        _searchActive = false;
        if (apply) { _query = _searchDraft.Trim(); _page = 0; FilterCatalog(); BuildAll(); }
        if (returnToMenu) { _searchRelease = true; Show(_all); }
        else { _searchRelease = false; MenuTextInput.End(this); }
    }

    private static void Notice(string text) { GTA.UI.Screen.ShowSubtitle(text, 2500); }
    private static void Event(string message) { DevelopmentDiagnostics.Event("PedAnimations", message); }
    private static void State(string state) { DevelopmentDiagnostics.State("PedAnimations", state); }
    private static void Error(string operation, Exception ex)
    {
        PluginLog.Error("PedAnimations: " + operation, ex);
        CrashDiagnostics.Exception("PedAnimations", operation, ex);
    }
    public void OnAbort()
    {
        _aborted = true;
        try { CloseMenus(); StopAnimations(); }
        finally
        {
            _searchActive = _searchRelease = false;
            MenuTextInput.End(this);
            _peds.Clear(); _aimed = null;
            _catalogTask = _filterTask = _favoritesTask = null;
        }
    }
}
}
