using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;
using GTA.Math;
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
        private NativeCheckboxItem _vehiclePedKnockdownCheckbox;
        private bool _weaponsEnabled = false;
        private bool _vehiclePedKnockdownEnabled = false;
        private int _lastKeyTime = 0;
        private Vector3 _lastVehicleVelocity = Vector3.Zero;
        private Vector3 _preservedVehicleVelocity = Vector3.Zero;
        private int _keepVehicleMovingUntil = 0;
        private int _activeVehicleHandle = 0;
        private readonly Dictionary<int, int> _lastVehicleHitByPed = new Dictionary<int, int>();

        private const float MinVehicleSpeed = 4.0f;
        private const float NearbyPedSearchRadius = 10.0f;
        private const float PreservedVehicleSpeedFactor = 0.87f;
        private const float PedLaunchSpeed = 28.0f;
        private const float PedLaunchUpSpeed = 3.0f;
        private const int PedLaunchCooldownMs = 850;
        private const int KeepVehicleMovingMs = 220;

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

                _vehiclePedKnockdownCheckbox = new NativeCheckboxItem(
                    "Сбивать педов на транспорте",
                    "Включить — пешеходы легко отлетают от машины, а транспорт почти не тормозится об них.",
                    false);
                _vehiclePedKnockdownCheckbox.CheckboxChanged += OnToggleVehiclePedKnockdown;
                _menu.Add(_vehiclePedKnockdownCheckbox);

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
                UpdateVehiclePedKnockdown();
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
                    Function.Call(Hash.SET_PED_CAN_SWITCH_WEAPON, player.Handle, true);
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

        private void OnToggleVehiclePedKnockdown(object sender, EventArgs e)
        {
            try
            {
                _vehiclePedKnockdownEnabled = _vehiclePedKnockdownCheckbox.Checked;
                if (!_vehiclePedKnockdownEnabled)
                {
                    ResetVehicleKnockdownState();
                    GTA.UI.Notification.PostTicker("~r~Сбивание педов выключено", false);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~g~Сбивание педов включено", false);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("WeaponManager", "OnToggleVehiclePedKnockdown: " + ex.Message);
            }
        }

        private void UpdateVehiclePedKnockdown()
        {
            if (!_vehiclePedKnockdownEnabled)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || !player.IsInVehicle())
            {
                ResetVehicleKnockdownState();
                return;
            }

            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists())
            {
                ResetVehicleKnockdownState();
                return;
            }

            if (_activeVehicleHandle != vehicle.Handle)
            {
                ResetVehicleKnockdownState();
                _activeVehicleHandle = vehicle.Handle;
            }

            int now = Game.GameTime;
            Vector3 vehicleVelocity = GetEntityVelocity(vehicle);
            Vector3 velocityBeforeImpact = _lastVehicleVelocity.Length() > vehicleVelocity.Length()
                ? _lastVehicleVelocity
                : vehicleVelocity;

            vehicleVelocity = PreserveVehicleHorizontalSpeed(vehicle, vehicleVelocity, now);
            float speed = velocityBeforeImpact.Length();

            if (speed < MinVehicleSpeed)
            {
                _lastVehicleVelocity = vehicleVelocity;
                return;
            }

            Vector3 forward = velocityBeforeImpact;
            forward.Z = 0f;
            if (forward.Length() < 0.01f)
                forward = RotationToDirection(vehicle.Rotation);
            forward.Z = 0f;
            if (forward.Length() < 0.01f)
                return;
            forward.Normalize();

            Ped[] peds = World.GetNearbyPeds(vehicle.Position, NearbyPedSearchRadius);
            for (int i = 0; peds != null && i < peds.Length; i++)
            {
                Ped ped = peds[i];
                if (ped == null || !ped.Exists() || ped == player || ped.IsDead || ped.IsInVehicle())
                    continue;

                if (!AreEntitiesTouching(vehicle, ped))
                    continue;

                int lastHit;
                if (_lastVehicleHitByPed.TryGetValue(ped.Handle, out lastHit) && unchecked((uint)(now - lastHit)) < PedLaunchCooldownMs)
                    continue;

                LaunchPedFromVehicleHit(ped, forward, speed);
                _lastVehicleHitByPed[ped.Handle] = now;
                _keepVehicleMovingUntil = now + KeepVehicleMovingMs;
                _preservedVehicleVelocity = new Vector3(
                    velocityBeforeImpact.X * PreservedVehicleSpeedFactor,
                    velocityBeforeImpact.Y * PreservedVehicleSpeedFactor,
                    0f);
                vehicleVelocity = PreserveVehicleHorizontalSpeed(vehicle, GetEntityVelocity(vehicle), now);
            }

            _lastVehicleVelocity = vehicleVelocity;
        }

        private Vector3 PreserveVehicleHorizontalSpeed(Vehicle vehicle, Vector3 currentVelocity, int now)
        {
            if (now >= _keepVehicleMovingUntil)
                return currentVelocity;

            float preservedSpeed = (float)Math.Sqrt(
                _preservedVehicleVelocity.X * _preservedVehicleVelocity.X +
                _preservedVehicleVelocity.Y * _preservedVehicleVelocity.Y);
            float currentSpeed = (float)Math.Sqrt(
                currentVelocity.X * currentVelocity.X +
                currentVelocity.Y * currentVelocity.Y);
            if (preservedSpeed <= currentSpeed || preservedSpeed < 0.01f)
                return currentVelocity;

            Vector3 direction = new Vector3(currentVelocity.X, currentVelocity.Y, 0f);
            if (direction.Length() < 0.5f)
                direction = _preservedVehicleVelocity;
            direction.Normalize();

            Vector3 result = new Vector3(
                direction.X * preservedSpeed,
                direction.Y * preservedSpeed,
                currentVelocity.Z);
            Function.Call(Hash.SET_ENTITY_VELOCITY, vehicle.Handle, result.X, result.Y, result.Z);
            return result;
        }

        private static bool AreEntitiesTouching(Entity first, Entity second)
        {
            try
            {
                return Function.Call<bool>(Hash.IS_ENTITY_TOUCHING_ENTITY, first.Handle, second.Handle);
            }
            catch
            {
                return first.IsTouching(second);
            }
        }

        private void ResetVehicleKnockdownState()
        {
            _lastVehicleVelocity = Vector3.Zero;
            _preservedVehicleVelocity = Vector3.Zero;
            _keepVehicleMovingUntil = 0;
            _activeVehicleHandle = 0;
            _lastVehicleHitByPed.Clear();
        }

        private static void LaunchPedFromVehicleHit(Ped ped, Vector3 direction, float vehicleSpeed)
        {
            try
            {
                float launchSpeed = Math.Max(PedLaunchSpeed, vehicleSpeed * 1.45f);
                Function.Call(Hash.SET_PED_TO_RAGDOLL, ped.Handle, 2500, 3500, 0, true, true, false);
                Function.Call(Hash.SET_ENTITY_VELOCITY, ped.Handle,
                    direction.X * launchSpeed,
                    direction.Y * launchSpeed,
                    PedLaunchUpSpeed);
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, ped.Handle, 1,
                    direction.X * launchSpeed * 1.35f,
                    direction.Y * launchSpeed * 1.35f,
                    2.0f,
                    0f, 0f, 0f,
                    0, false, true, true, false, true);
            }
            catch (Exception ex)
            {
                PluginLog.Error("WeaponManager", "LaunchPedFromVehicleHit: " + ex.Message);
            }
        }

        private static Vector3 GetEntityVelocity(Entity entity)
        {
            try
            {
                return Function.Call<Vector3>(Hash.GET_ENTITY_VELOCITY, entity.Handle);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        private static Vector3 RotationToDirection(Vector3 rotation)
        {
            double z = rotation.Z * Math.PI / 180.0;
            return new Vector3((float)(-Math.Sin(z)), (float)Math.Cos(z), 0f);
        }

    }
}
