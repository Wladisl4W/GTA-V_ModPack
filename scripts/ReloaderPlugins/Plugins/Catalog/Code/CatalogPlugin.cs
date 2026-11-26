using System;
using System.Windows.Forms;
using GTA;
using LemonUI;
using LemonUI.Menus;
using ModPack.Core;

namespace ModPack.Catalog
{
public sealed class CatalogPlugin : IGtaPlugin
{
    private const string VisibleKey = "ModPack.Catalog.Visible";
    private readonly ObjectPool _pool = new ObjectPool();
    private NativeMenu _menu;
    private bool _f5WasDown;
    private bool _aborting;

    public void OnStart()
    {
        _menu = new NativeMenu("Модпак", "Моды и клавиши");
        foreach (ModuleMenuInfo entry in ModuleMenus.InCatalogOrder)
            AddEntry(entry);
        _menu.Closed += (s, e) =>
        {
            if (!_aborting) AppDomain.CurrentDomain.SetData(VisibleKey, false);
        };
        _pool.Add(_menu);

        _f5WasDown = Game.IsKeyPressed(Keys.F5);
        _menu.Visible = AppDomain.CurrentDomain.GetData(VisibleKey) is bool &&
            (bool)AppDomain.CurrentDomain.GetData(VisibleKey);
    }

    private void AddEntry(ModuleMenuInfo entry)
    {
        var item = new NativeItem(entry.Title, entry.Description) { AltTitle = entry.KeyLabel, Enabled = false };
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
}
