using System;
using System.Windows.Forms;
using GTA;
using LemonUI;
using LemonUI.Menus;

public sealed class ModPackCatalogPlugin : IGtaPlugin
{
    private const string VisibleKey = "ModPack.Catalog.Visible";
    private readonly ObjectPool _pool = new ObjectPool();
    private NativeMenu _menu;
    private bool _f5WasDown;
    private bool _aborting;

    public void OnStart()
    {
        _menu = new NativeMenu("ModPack", "Моды в паке");
        AddEntry("ModdedCamera", "T", "Пролётки и следящая камера");
        AddEntry("Cinematic Moment", "Mouse4", "Замедление и камера при удержании");
        AddEntry("RainbowPaint", "I", "Покраска автомобилей");
        AddEntry("Weapon Manager", "L", "Оружие и удары автомобилем");
        AddEntry("Shark Rider", "O", "Акула и катание");
        AddEntry("MenyooStreamer", "U", "Стриминг педов");
        AddEntry("Spooner Prefabs", ";", "Стенка из мячей в Spooner");
        AddEntry("Frozen Dynamic", "K", "Управление NPC");
        AddEntry("Remove Dropped Peds", "H", "Очистка педов в воде");
        AddEntry("Development", "Ctrl+F10", "Диагностика");
        AddEntry("Crash Logger", "Фон", "Отчёты о сбоях");
        _menu.Closed += (s, e) =>
        {
            if (!_aborting) AppDomain.CurrentDomain.SetData(VisibleKey, false);
        };
        _pool.Add(_menu);

        _f5WasDown = Game.IsKeyPressed(Keys.F5);
        _menu.Visible = AppDomain.CurrentDomain.GetData(VisibleKey) is bool &&
            (bool)AppDomain.CurrentDomain.GetData(VisibleKey);
    }

    private void AddEntry(string name, string key, string description)
    {
        var item = new NativeItem(name, description) { AltTitle = key, Enabled = false };
        _menu.Add(item);
    }

    public void OnTick()
    {
        bool f5Down = Game.IsKeyPressed(Keys.F5);
        if (f5Down && !_f5WasDown && _menu != null)
        {
            _menu.Visible = !_menu.Visible;
            AppDomain.CurrentDomain.SetData(VisibleKey, _menu.Visible);
        }
        _f5WasDown = f5Down;
        _pool.Process();
    }

    public void OnKeyDown(Keys key) { }

    public void OnAbort()
    {
        _aborting = true;
        if (_menu != null) _menu.Visible = false;
    }
}
