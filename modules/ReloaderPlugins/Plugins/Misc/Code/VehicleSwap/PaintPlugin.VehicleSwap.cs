using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI.Menus;
using ModPack.Core;

namespace ModPack.Paint
{
    public partial class PaintPlugin
    {
        private sealed class VehicleSwapEntry
        {
            public Vehicle Original;
            public int OriginalHash;
            public int ReplacementHash;
            public Vector3 Position;
            public Vector3 Rotation;
            public bool Frozen;
        }

        private sealed class VehicleSwapJob
        {
            public readonly List<VehicleSwapEntry> Entries = new List<VehicleSwapEntry>();
            public readonly List<Vehicle> Staged = new List<Vehicle>();
            public int NextIndex;
            public int Committed;
            public int Skipped;
            public int StartedAt;
        }

        private NativeMenu _swapMenu;
        private NativeItem _swapFirstItem;
        private NativeItem _swapSecondItem;
        private NativeItem _swapApplyItem;
        private int _swapFirstHash;
        private int _swapSecondHash;
        private string _swapFirstName;
        private string _swapSecondName;
        private VehicleSwapJob _swapJob;
        private void InitializeVehicleSwapMenu()
        {
            _swapMenu = new NativeMenu("Обмен моделей", "Машины в мире");
            _pool.Add(_swapMenu);
            _swapFirstItem = new NativeItem("Выбрать первую модель");
            _swapFirstItem.Activated += (s, e) => SelectSwapModel(true);
            _swapMenu.Add(_swapFirstItem);
            _swapSecondItem = new NativeItem("Выбрать вторую модель");
            _swapSecondItem.Activated += (s, e) => SelectSwapModel(false);
            _swapMenu.Add(_swapSecondItem);
            _swapApplyItem = new NativeItem("Поменять модели");
            _swapApplyItem.Activated += (s, e) => StartVehicleSwap();
            _swapMenu.Add(_swapApplyItem);
            UpdateSwapMenu();
            _swapMenu.Closed += (s, e) =>
            {
                CancelVehicleSwap();
            };
            ModPack.Core.MenuRegistry.Register("VehicleSwap", _swapMenu);

        }

        private void UpdateSwapMenu()
        {
            if (_swapFirstItem == null) return;
            _swapFirstItem.AltTitle = _swapFirstHash == 0 ? "Не выбрана" : _swapFirstName;
            _swapSecondItem.AltTitle = _swapSecondHash == 0 ? "Не выбрана" : _swapSecondName;
            _swapApplyItem.Enabled = _swapJob == null && _swapFirstHash != 0 &&
                _swapSecondHash != 0 && _swapFirstHash != _swapSecondHash;
            _swapApplyItem.AltTitle = _swapJob == null ? "" :
                "Подготовка " + _swapJob.Staged.Count + "/" + _swapJob.Entries.Count;
        }

        private void SelectSwapModel(bool first)
        {
            if (_swapJob != null) return;
            Vehicle vehicle = IsSpoonerModeActive() ? GetVehiclePlayerIsLookingAt() : null;
            if (vehicle == null || !vehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle("~y~Наведись на машину в Object Spooner.", 3000);
                return;
            }
            if (first)
            {
                _swapFirstHash = vehicle.Model.Hash;
                _swapFirstName = GetVehicleName(vehicle);
            }
            else
            {
                _swapSecondHash = vehicle.Model.Hash;
                _swapSecondName = GetVehicleName(vehicle);
            }
            UpdateSwapMenu();
        }

        private static bool VehicleHasOccupants(Vehicle vehicle)
        {
            int seats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, vehicle.Handle);
            for (int seat = -1; seat < seats; seat++)
                if (!Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, vehicle.Handle, seat, false))
                    return true;
            return false;
        }

        private void StartVehicleSwap()
        {
            if (_swapJob != null || _swapFirstHash == 0 || _swapSecondHash == 0 ||
                _swapFirstHash == _swapSecondHash || !IsSpoonerModeActive()) return;
            try
            {
                var job = new VehicleSwapJob { StartedAt = Game.GameTime };
                Vehicle[] vehicles = World.GetAllVehicles() ?? new Vehicle[0];
                foreach (Vehicle vehicle in vehicles)
                {
                    if (vehicle == null || !vehicle.Exists()) continue;
                    int hash = vehicle.Model.Hash;
                    if (hash != _swapFirstHash && hash != _swapSecondHash) continue;
                    if (Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, vehicle.Handle) ||
                        VehicleHasOccupants(vehicle))
                    {
                        job.Skipped++;
                        continue;
                    }
                    job.Entries.Add(new VehicleSwapEntry
                    {
                        Original = vehicle,
                        OriginalHash = hash,
                        ReplacementHash = hash == _swapFirstHash ? _swapSecondHash : _swapFirstHash,
                        Position = vehicle.Position,
                        Rotation = vehicle.Rotation,
                        Frozen = vehicle.IsPositionFrozen
                    });
                }
                if (job.Entries.Count == 0)
                {
                    GTA.UI.Screen.ShowSubtitle("~y~Нет свободных машин выбранных моделей. Пропущено: " + job.Skipped, 4000);
                    return;
                }
                _swapJob = job;
                Function.Call(Hash.REQUEST_MODEL, _swapFirstHash);
                Function.Call(Hash.REQUEST_MODEL, _swapSecondHash);
                UpdateSwapMenu();
                CrashDiagnostics.Event("RainbowPaint", "Vehicle swap preparing " + job.Entries.Count + " vehicles");
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: StartVehicleSwap", ex);
                CancelVehicleSwap();
                GTA.UI.Screen.ShowSubtitle("~r~Не удалось подготовить обмен моделей.", 4000);
            }
        }

        private void UpdateVehicleSwap()
        {
            VehicleSwapJob job = _swapJob;
            if (job == null) return;
            try
            {
                if (!_swapMenu.Visible || !IsSpoonerModeActive())
                {
                    CancelVehicleSwap();
                    return;
                }
                if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, _swapFirstHash) ||
                    !Function.Call<bool>(Hash.HAS_MODEL_LOADED, _swapSecondHash))
                {
                    if ((uint)(Game.GameTime - job.StartedAt) > 5000u)
                        FailVehicleSwap("Модели не загрузились за 5 секунд.");
                    return;
                }
                int end = Math.Min(job.NextIndex + 6, job.Entries.Count);
                while (job.NextIndex < end)
                {
                    VehicleSwapEntry entry = job.Entries[job.NextIndex];
                    if (entry.Original == null || !entry.Original.Exists() ||
                        entry.Original.Model.Hash != entry.OriginalHash ||
                        VehicleHasOccupants(entry.Original) ||
                        Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, entry.Original.Handle))
                    {
                        FailVehicleSwap("Исходная машина изменилась во время подготовки.");
                        return;
                    }
                    Vehicle replacement = World.CreateVehicle(new Model(entry.ReplacementHash),
                        entry.Position, entry.Rotation.Z);
                    if (replacement == null || !replacement.Exists())
                    {
                        FailVehicleSwap("Не удалось создать заменяющую машину.");
                        return;
                    }
                    job.Staged.Add(replacement);
                    replacement.IsPersistent = true;
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, replacement.Handle, true);
                    Function.Call(Hash.SET_ENTITY_COLLISION, replacement.Handle, false, false);
                    replacement.Rotation = entry.Rotation;
                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, replacement.Handle,
                        entry.Position.X, entry.Position.Y, entry.Position.Z,
                        false, false, true);
                    job.NextIndex++;
                }
                UpdateSwapMenu();
                if (job.NextIndex != job.Entries.Count) return;
                foreach (VehicleSwapEntry entry in job.Entries)
                    if (!entry.Original.Exists() ||
                        entry.Original.Model.Hash != entry.OriginalHash ||
                        VehicleHasOccupants(entry.Original) ||
                        Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, entry.Original.Handle))
                    {
                        FailVehicleSwap("Исходная машина исчезла или занята.");
                        return;
                    }
                int changed = 0;
                for (int i = 0; i < job.Entries.Count; i++)
                {
                    Vehicle original = job.Entries[i].Original;
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, original.Handle, true, true);
                    original.Delete();
                    if (original.Exists())
                    {
                        PluginLog.Error("RainbowPaint: VehicleSwap", new InvalidOperationException(
                            "Cannot delete original vehicle " + original.Handle));
                        break;
                    }
                    job.Committed = i + 1;
                    Vehicle replacement = job.Staged[i];
                    Function.Call(Hash.SET_ENTITY_COLLISION, replacement.Handle, true, true);
                    Vector3 position = job.Entries[i].Position;
                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, replacement.Handle,
                        position.X, position.Y, position.Z, false, false, true);
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, replacement.Handle,
                        job.Entries[i].Frozen);
                    changed++;
                }
                for (int i = changed; i < job.Staged.Count; i++)
                    if (job.Staged[i] != null && job.Staged[i].Exists()) job.Staged[i].Delete();
                FinishVehicleSwap(job, changed);
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: UpdateVehicleSwap", ex);
                if (job.Committed > 0)
                {
                    for (int i = job.Committed; i < job.Staged.Count; i++)
                        try
                        {
                            if (job.Staged[i] != null && job.Staged[i].Exists()) job.Staged[i].Delete();
                        }
                        catch (Exception cleanupError)
                        {
                            PluginLog.Error("RainbowPaint: VehicleSwapCleanup", cleanupError);
                        }
                    FinishVehicleSwap(job, job.Committed);
                }
                else FailVehicleSwap("Обмен прерван ошибкой; исходные машины сохранены.");
            }
        }

        private void FinishVehicleSwap(VehicleSwapJob job, int changed)
        {
            _swapJob = null;
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, _swapFirstHash);
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, _swapSecondHash);
            UpdateSwapMenu();
            if (_highlightBlip != null && _highlightBlip.Exists()) _highlightBlip.Delete();
            _highlightBlip = null;
            _currentVehicle = null;
            SetMenuEnabled(false);
            CrashDiagnostics.Event("RainbowPaint", "Vehicle swap changed=" + changed +
                ", skipped=" + job.Skipped);
            GTA.UI.Screen.ShowSubtitle((changed == job.Entries.Count ? "~g~" : "~y~") +
                "Заменено: " + changed + ". Пропущено: " + job.Skipped +
                (changed == job.Entries.Count ? "." : ". Не все машины удалось заменить."), 5000);
        }

        private void FailVehicleSwap(string reason)
        {
            PluginLog.Error("RainbowPaint: VehicleSwap", new InvalidOperationException(reason));
            CancelVehicleSwap();
            GTA.UI.Screen.ShowSubtitle("~r~" + reason, 5000);
        }

        private void CancelVehicleSwap()
        {
            VehicleSwapJob job = _swapJob;
            if (job == null) return;
            _swapJob = null;
            for (int i = job.Committed; i < job.Staged.Count; i++)
                try
                {
                    if (job.Staged[i] != null && job.Staged[i].Exists()) job.Staged[i].Delete();
                }
                catch (Exception ex)
                {
                    PluginLog.Error("RainbowPaint: VehicleSwapCleanup", ex);
                }
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, _swapFirstHash);
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, _swapSecondHash);
            UpdateSwapMenu();
        }

    }
}
