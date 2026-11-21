using System;
using System.Windows.Forms;
using GTA;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using ModPack.Core;

namespace ModPack.Hub
{
    public sealed class HubPlugin : IGtaPlugin
    {
        private readonly ObjectPool _pool = new ObjectPool();
        private NativeMenu _modes, _optimization, _activeChild, _activeParent;
        private NativeMenu _pendingMenu;
        private bool _switching;

        public void OnStart()
        {
            _modes = new NativeMenu(ModuleMenus.Misc.Title, "Игровые функции");
            _optimization = new NativeMenu(ModuleMenus.Optimization.Title, "Педы в мире");
            Add(_modes, "Оружие и физика", "Weapons");
            Add(_modes, "Животные", "Wildlife");
            Add(_modes, "Следование камеры", "CameraFollow");
            Add(_modes, "Обмен моделей машин", "VehicleSwap");
            Add(_optimization, "Загрузка педов", "Streamer");
            Add(_optimization, "Очистка педов", "Cleanup");
            Add(_optimization, "Управление NPC", "PedControl");
            _pool.Add(_modes);
            _pool.Add(_optimization);
        }

        private void Add(NativeMenu parent, string title, string id)
        {
            var item = new NativeItem(title);
            item.Activated += (sender, args) => Open(parent, id);
            parent.Add(item);
        }

        private void Open(NativeMenu parent, string id)
        {
            NativeMenu child = MenuRegistry.Find(id);
            if (child == null)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Раздел пока недоступен: " + id, 2500);
                return;
            }
            _pendingMenu = null;
            _switching = true;
            parent.Visible = false;
            _switching = false;
            _activeChild = child;
            _activeParent = parent;
            child.Closed += ChildClosed;
            ScheduleShow(child);
        }

        private void ChildClosed(object sender, EventArgs args)
        {
            NativeMenu child = sender as NativeMenu;
            if (child != null) child.Closed -= ChildClosed;
            if (_activeChild != child) return;
            _activeChild = null;
            if (!_switching)
            {
                if (_activeParent != null) ScheduleShow(_activeParent);
            }
            _activeParent = null;
        }

        private void ScheduleShow(NativeMenu menu)
        {
            _pendingMenu = menu;
        }

        public void OnTick()
        {
            if (_pendingMenu != null)
            {
                NativeMenu menu = _pendingMenu;
                _pendingMenu = null;
                menu.Visible = true;
            }
            _pool.Process();
        }

        public void OnKeyDown(Keys key)
        {
            if (MenuTextInput.Active) return;
            if (key == Keys.I || key == Keys.T || key == Keys.K)
            {
                CloseNavigation();
                return;
            }
            if (key != Keys.L && key != Keys.O) return;
            NativeMenu target = key == Keys.L ? _modes : _optimization;
            bool wasOpen = target.Visible || _pendingMenu == target ||
                (_activeChild != null && _activeParent == target);
            CloseNavigation();
            if (!wasOpen) target.Visible = true;
        }

        private void CloseNavigation()
        {
            _pendingMenu = null;
            _switching = true;
            if (_activeChild != null)
            {
                NativeMenu child = _activeChild;
                _activeChild = null;
                child.Closed -= ChildClosed;
                child.Visible = false;
                _activeParent = null;
            }
            _modes.Visible = false;
            _optimization.Visible = false;
            _switching = false;
        }

        public void OnAbort()
        {
            _switching = true;
            _pendingMenu = null;
            if (_activeChild != null)
            {
                _activeChild.Closed -= ChildClosed;
                _activeChild.Visible = false;
                _activeChild = null;
                _activeParent = null;
            }
            _pool.HideAll();
            _switching = false;
        }
    }
}
