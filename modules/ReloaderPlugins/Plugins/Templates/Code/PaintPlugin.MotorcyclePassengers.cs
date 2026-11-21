using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

using ModPack.Core;

namespace ModPack.Paint
{
    public partial class PaintPlugin
    {
        private readonly string _motorcyclePassengersPath =
            PluginStorage.FilePath("RainbowPaint", "RainbowPaintPassengerModels.json");
        private readonly List<ModelExclusion> _motorcyclePassengerModels = new List<ModelExclusion>();
        private Vehicle[] _motorcyclePassengerVehicles;
        private List<int> _motorcycleLoadedPedModels;
        private readonly List<PassengerAssignment> _motorcycleAssignments = new List<PassengerAssignment>();
        private int _motorcyclePassengerIndex, _motorcyclePassengerSeated, _motorcyclePassengerSkipped;
        private int _motorcyclePassengerStartedAt;
        private bool _motorcyclePassengerLoading;
        private bool _motorcyclePassengerFromPreset;

        private struct PassengerAssignment
        {
            public Vector3 Position;
            public int ModelHash;
        }

        private ModelExclusion? GetAimedMotorcyclePassengerModel()
        {
            GTA.Camera camera = IsSpoonerModeActive() ? ScriptCameraDirector.RenderingCam : null;
            if (camera == null || !camera.Exists())
            {
                Screen.ShowSubtitle("~y~Открой Object Spooner и наведись на педа.", 3000);
                return null;
            }
            RaycastResult ray = World.Raycast(camera.Position,
                camera.Position + camera.Direction * 300f,
                IntersectFlags.Map | IntersectFlags.Objects | IntersectFlags.Vehicles | IntersectFlags.Peds,
                Game.Player.Character);
            Ped ped = ray.DidHit ? ray.HitEntity as Ped : null;
            if (ped == null || !ped.Exists() || ped.Handle == Game.Player.Character.Handle)
            {
                Screen.ShowSubtitle("~y~Наведи камеру на педа и нажми Enter.", 3000);
                return null;
            }
            int hash = ped.Model.Hash;
            if (!Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, hash) ||
                !Function.Call<bool>(Hash.IS_MODEL_A_PED, hash))
            {
                Screen.ShowSubtitle("~r~Эта модель педа недоступна.", 3000);
                return null;
            }
            string name = ped.Model.ToString();
            if (string.IsNullOrWhiteSpace(name)) name = hash.ToString("X8");
            return new ModelExclusion(name, hash);
        }

        private void LoadMotorcyclePassengerModels()
        {
            try
            {
                if (!SafeFiles.Exists(_motorcyclePassengersPath)) return;
                string json = SafeFiles.Read(_motorcyclePassengersPath, ValidateMotorcyclePassengerModels);
                var loaded = new JavaScriptSerializer().Deserialize<List<ModelExclusion>>(json);
                _motorcyclePassengerModels.Clear();
                var seen = new HashSet<int>();
                foreach (ModelExclusion model in loaded)
                    if (model.Hash != 0 && seen.Add(model.Hash))
                        _motorcyclePassengerModels.Add(new ModelExclusion(
                            string.IsNullOrWhiteSpace(model.Name) ? model.Hash.ToString("X8") : model.Name,
                            model.Hash));
            }
            catch (Exception ex) { PluginLog.Error("RainbowPaint: LoadMotorcyclePassengerModels", ex); }
        }

        private static void ValidateMotorcyclePassengerModels(string json)
        {
            if (new JavaScriptSerializer().Deserialize<List<ModelExclusion>>(json) == null)
                throw new InvalidDataException("Invalid RainbowPaint passenger models");
        }

        private bool StartMotorcyclePassengers(List<ModelExclusion> models, bool fromPreset)
        {
            if (_motorcyclePassengerVehicles != null || models == null || models.Count == 0) return false;
            try
            {
                _motorcycleLoadedPedModels = new List<int>();
                foreach (ModelExclusion model in models)
                {
                    if (!Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, model.Hash) ||
                        !Function.Call<bool>(Hash.IS_MODEL_A_PED, model.Hash)) continue;
                    Function.Call(Hash.REQUEST_MODEL, model.Hash);
                    _motorcycleLoadedPedModels.Add(model.Hash);
                }
                if (_motorcycleLoadedPedModels.Count == 0)
                {
                    Screen.ShowSubtitle("~y~В списке нет доступных моделей педов.", 3000);
                    CancelMotorcyclePassengers();
                    return false;
                }
                var targets = new List<Vehicle>();
                foreach (Vehicle vehicle in World.GetAllVehicles() ?? new Vehicle[0])
                {
                    try
                    {
                        if (vehicle != null && vehicle.Exists() && !vehicle.IsDead &&
                            Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, vehicle.Model.Hash) &&
                            !Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, vehicle.Model.Hash) &&
                            Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, vehicle.Handle) > 0)
                            targets.Add(vehicle);
                    }
                    catch (Exception ex) { PluginLog.Error("RainbowPaint: InspectMotorcyclePassengerTarget", ex); }
                }
                targets.Sort((a, b) =>
                {
                    int x = a.Position.X.CompareTo(b.Position.X);
                    return x != 0 ? x : a.Position.Y.CompareTo(b.Position.Y);
                });
                _motorcyclePassengerVehicles = targets.ToArray();
                _motorcyclePassengerIndex = _motorcyclePassengerSeated = _motorcyclePassengerSkipped = 0;
                _motorcyclePassengerStartedAt = Game.GameTime;
                _motorcyclePassengerLoading = true;
                _motorcyclePassengerFromPreset = fromPreset;
                _motorcycleAssignments.Clear();
                CrashDiagnostics.Event("RainbowPaint", "Motorcycle passengers started; bikes=" + targets.Count);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("RainbowPaint: StartMotorcyclePassengers", ex);
                CancelMotorcyclePassengers();
                Screen.ShowSubtitle("~r~Не удалось начать посадку пассажиров.", 3000);
                return false;
            }
        }

        private void UpdateMotorcyclePassengers()
        {
            if (_motorcyclePassengerVehicles == null) return;
            if (_motorcyclePassengerLoading)
            {
                try
                {
                bool allReady = true;
                for (int i = _motorcycleLoadedPedModels.Count - 1; i >= 0; i--)
                {
                    int hash = _motorcycleLoadedPedModels[i];
                    if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash)) allReady = false;
                }
                if (!allReady && unchecked((uint)(Game.GameTime - _motorcyclePassengerStartedAt)) < 5000u)
                    return;
                _motorcycleLoadedPedModels.RemoveAll(hash =>
                    !Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash));
                _motorcyclePassengerLoading = false;
                if (_motorcycleLoadedPedModels.Count == 0)
                {
                    PluginLog.Error("RainbowPaint: MotorcyclePassengerModels", new InvalidOperationException("No ped models loaded"));
                    Screen.ShowSubtitle("~r~Модели педов не загрузились.", 3000);
                    FinishMotorcyclePassengers();
                    return;
                }
                }
                catch (Exception ex)
                {
                    PluginLog.Error("RainbowPaint: LoadMotorcyclePassengerModels", ex);
                    CancelMotorcyclePassengers();
                    Screen.ShowSubtitle("~r~Не удалось загрузить модели пассажиров.", 3000);
                    return;
                }
            }
            int end = Math.Min(_motorcyclePassengerIndex + 4, _motorcyclePassengerVehicles.Length);
            while (_motorcyclePassengerIndex < end)
            {
                Vehicle vehicle = _motorcyclePassengerVehicles[_motorcyclePassengerIndex++];
                Ped created = null;
                try
                {
                    if (vehicle == null || !vehicle.Exists() || vehicle.IsDead ||
                        !Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, vehicle.Handle, 0, false))
                    {
                        _motorcyclePassengerSkipped++;
                        continue;
                    }
                    int hash = PickMotorcyclePassengerModel(vehicle.Position);
                    created = World.CreatePed(new Model(hash), vehicle.Position);
                    if (created == null || !created.Exists())
                        throw new InvalidOperationException("Ped creation failed: " + hash.ToString("X8"));
                    Function.Call(Hash.SET_PED_INTO_VEHICLE, created.Handle, vehicle.Handle, 0);
                    if (Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle.Handle, 0, false) != created.Handle)
                        throw new InvalidOperationException("Ped did not occupy motorcycle passenger seat");
                    _motorcycleAssignments.Add(new PassengerAssignment
                        { Position = vehicle.Position, ModelHash = hash });
                    _motorcyclePassengerSeated++;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("RainbowPaint: SeatMotorcyclePassenger", ex);
                    _motorcyclePassengerSkipped++;
                    try { if (created != null && created.Exists()) created.Delete(); }
                    catch (Exception cleanup) { PluginLog.Error("RainbowPaint: RemoveFailedPassenger", cleanup); }
                }
            }
            if (_motorcyclePassengerIndex >= _motorcyclePassengerVehicles.Length)
                FinishMotorcyclePassengers();
        }

        private int PickMotorcyclePassengerModel(Vector3 position)
        {
            int bestScore = int.MaxValue;
            var best = new List<int>();
            foreach (int hash in _motorcycleLoadedPedModels)
            {
                int score = 0;
                foreach (PassengerAssignment assignment in _motorcycleAssignments)
                {
                    if (assignment.ModelHash != hash) continue;
                    float distance = (position - assignment.Position).Length();
                    if (distance <= 12f) score += 8;
                    else if (distance <= 24f) score += 3;
                }
                if (score < bestScore) { bestScore = score; best.Clear(); best.Add(hash); }
                else if (score == bestScore) best.Add(hash);
            }
            return best[_rng.Next(best.Count)];
        }

        private void FinishMotorcyclePassengers()
        {
            if (_motorcyclePassengerVehicles == null) return;
            int total = _motorcyclePassengerVehicles.Length;
            ReleaseMotorcyclePassengerModels();
            _motorcyclePassengerVehicles = null;
            _motorcycleLoadedPedModels = null;
            _motorcycleAssignments.Clear();
            _motorcyclePassengerLoading = false;
            CrashDiagnostics.Event("RainbowPaint", "Motorcycle passengers finished; seated=" +
                _motorcyclePassengerSeated + "; skipped=" + _motorcyclePassengerSkipped + "; bikes=" + total);
            if (!_motorcyclePassengerFromPreset)
                Screen.ShowSubtitle("~g~Пассажиры посажены: " + _motorcyclePassengerSeated +
                    "~w~; пропущено: " + _motorcyclePassengerSkipped + ".", 4000);
            _motorcyclePassengerFromPreset = false;
        }

        private void CancelMotorcyclePassengers()
        {
            ReleaseMotorcyclePassengerModels();
            _motorcyclePassengerVehicles = null;
            _motorcycleLoadedPedModels = null;
            _motorcycleAssignments.Clear();
            _motorcyclePassengerLoading = false;
            _motorcyclePassengerFromPreset = false;
        }

        private void ReleaseMotorcyclePassengerModels()
        {
            if (_motorcycleLoadedPedModels == null) return;
            foreach (int hash in _motorcycleLoadedPedModels)
            {
                try { Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, hash); }
                catch (Exception ex) { PluginLog.Error("RainbowPaint: ReleasePassengerModel", ex); }
            }
        }

    }
}
