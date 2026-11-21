using System;
using System.Windows.Forms;
using GTA;
using LemonUI;
using LemonUI.Menus;

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
        AddEntry("Камера", "T", "Пролётки и следящая камера");
        AddEntry("Замедление", "Mouse4", "Замедление и камера при удержании");
        AddEntry("Машины и покраска", "I", "Покраска и работа с машинами");
        AddEntry("Анимации педов", "Y", "Анимации выбранных педов в Object Spooner");
        AddEntry("Оружие и физика", "L", "Оружие, удары машиной и скользкое падение");
        AddEntry("Акула и крокодил", "O", "Катание, укусы и вызов крокодила");
        AddEntry("Загрузка педов", "U", "Потоковая загрузка и выгрузка педов");
        AddEntry("Заготовки", ";", "Стенка из мячей и горка в Object Spooner");
        AddEntry("Управление NPC", "K", "Заморозка и разморозка пешеходов");
        AddEntry("Очистка педов", "H", "Удаление педов, упавших в воду");
        AddEntry("Разработка", "Ctrl+F10", "Диагностика модпака");
        AddEntry("Отчёты о сбоях", "Фон", "Фоновая диагностика сбоев");
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
}
