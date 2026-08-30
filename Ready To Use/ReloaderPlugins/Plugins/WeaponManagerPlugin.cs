using System;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;
using PluginLogging;

namespace WeaponManager
{
    public class WeaponManagerPlugin : IGtaPlugin
    {
        private ObjectPool _pool;
        private NativeMenu _menu;
        private NativeCheckboxItem _giveWeaponsCheckbox;
        private bool _weaponsEnabled = false;
        private int _lastKeyTime = 0;

        public void OnStart()
        {
            try
            {
                _pool = new ObjectPool();
                _menu = new NativeMenu("Weapon Manager", "Оружие");
                _pool.Add(_menu);

                _giveWeaponsCheckbox = new NativeCheckboxItem(
                    "Выдать оружие",
                    "Включить — всё оружие появится. Выключить — всё исчезнет.",
                    false);
                _giveWeaponsCheckbox.CheckboxChanged += OnToggleWeapons;
                _menu.Add(_giveWeaponsCheckbox);

                PluginLog.Error("WeaponManager", "Loaded");
            }
            catch (Exception ex)
            {
                PluginLog.Error("WeaponManager", "OnStart: " + ex.Message);
            }
        }

        public void OnTick()
        {
            try
            {
                if (_pool != null) _pool.Process();
            }
            catch { }
        }

        public void OnKeyDown(Keys key)
        {
            if (key != Keys.L) return;

            int now = Game.GameTime;
            uint sinceLast = unchecked((uint)(now - _lastKeyTime));
            if (sinceLast < 300) return;
            _lastKeyTime = now;

            if (_menu != null)
                _menu.Visible = !_menu.Visible;
        }

        public void OnAbort()
        {
            try
            {
                if (_menu != null)
                    _menu.Visible = false;
            }
            catch { }
        }

        private void OnToggleWeapons(object sender, EventArgs e)
        {
            try
            {
                _weaponsEnabled = _giveWeaponsCheckbox.Checked;
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists()) return;

                if (_weaponsEnabled)
                {
                    foreach (WeaponHash weapon in Enum.GetValues(typeof(WeaponHash)))
                    {
                        if (weapon == WeaponHash.Unarmed) continue;
                        try
                        {
                            Function.Call(Hash.GIVE_WEAPON_TO_PED, player.Handle, (int)weapon, 9999, false, true);
                        }
                        catch { }
                    }
                    GTA.UI.Notification.PostTicker("~g~Оружие выдано", false);
                }
                else
                {
                    Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, player.Handle, true);
                    GTA.UI.Notification.PostTicker("~r~Оружие убрано", false);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("WeaponManager", "OnToggleWeapons: " + ex.Message);
            }
        }
    }
}
