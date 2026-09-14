using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ModdedCamera.Services
{
    public class FollowCameraService
    {
        private const int DefaultFollowDurationMs = 7000;
        private const float SlowMotionScale = 0.75f;
        private const int DefaultGravityLevel = 2;
        private const int NormalGravityLevel = 0;
        private const float MaxFallbackDamageDistanceSq = 250f * 250f;

        private Camera _camera;
        private Ped _target;
        private long _followStartedMs;
        private long _lastTriggerMs;
        private long _lastAttackIntentMs;
        private long _attackIntentStartedMs;
        private Vector3 _cameraPosition;
        private Vector3 _cameraRotation;
        private bool _active;
        private bool _enabled;
        private bool _worldStateApplied;
        private readonly Dictionary<int, int> _lastVitalityByPed = new Dictionary<int, int>();
        private readonly Dictionary<int, bool> _lastFallingByPed = new Dictionary<int, bool>();

        public int FollowDurationMs { get; set; }
        public int GravityLevel { get; set; }

        public FollowCameraService()
        {
            FollowDurationMs = DefaultFollowDurationMs;
            GravityLevel = DefaultGravityLevel;
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (!_enabled)
                    Stop(true);
            }
        }

        public bool IsActive
        {
            get { return _active; }
        }

        public void Update(bool blockedByModCamera)
        {
            try
            {
                if (_active)
                {
                    UpdateActiveFollow();
                    return;
                }

                if (!_enabled || blockedByModCamera)
                    return;

                Ped target = FindFreshDamageTarget();
                if (target != null)
                    Start(target);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FollowCameraService: Update");
                Stop(true);
            }
        }

        public void Dispose()
        {
            Stop(true);
        }

        private Ped FindFreshDamageTarget()
        {
            Ped player = GetPlayerPed();
            if (player == null)
                return null;

            long now = Utils.NowMs();
            bool attackIntentPressed = IsAttackIntentPressed();
            if (attackIntentPressed)
            {
                if (_attackIntentStartedMs <= 0)
                    _attackIntentStartedMs = now;
                _lastAttackIntentMs = now;
            }
            else
            {
                _attackIntentStartedMs = 0;
            }

            if (now - _lastTriggerMs < 900)
            {
                RefreshHealthCache(null);
                return null;
            }

            bool recentAttackIntent = now - _lastAttackIntentMs < 1200;
            int selectedWeapon = GetSelectedWeaponHash(player);

            Ped[] peds = World.GetAllPeds();
            Ped best = null;
            int bestScore = -1;
            float bestDistSq = 9999f;
            Vector3 playerPos = player.Position;
            HashSet<int> seenHandles = new HashSet<int>();

            for (int i = 0; peds != null && i < peds.Length; i++)
            {
                Ped ped = peds[i];
                if (ped == null || !ped.Exists() || ped == player || ped.IsDead)
                    continue;

                seenHandles.Add(ped.Handle);

                Vector3 delta = ped.Position - playerPos;
                float distSq = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;

                bool damagedByPlayer = Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, ped.Handle, player.Handle, true);
                bool damagedByWeapon = selectedWeapon != 0 && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_WEAPON, ped.Handle, selectedWeapon, 0);
                bool freshVitalityDrop = HasFreshVitalityDrop(ped) && recentAttackIntent && distSq < MaxFallbackDamageDistanceSq;
                bool freshAttackFall = HasFreshAttackFall(ped) && recentAttackIntent && distSq < 16f;

                RememberVitality(ped);
                RememberFallingState(ped);

                if (!damagedByPlayer && !damagedByWeapon && !freshVitalityDrop && !freshAttackFall)
                    continue;

                Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, ped.Handle);
                int score = damagedByPlayer ? 4 : (damagedByWeapon ? 3 : (freshVitalityDrop ? 2 : 1));
                if (score > bestScore || (score == bestScore && distSq < bestDistSq))
                {
                    bestScore = score;
                    bestDistSq = distSq;
                    best = ped;
                }
            }

            RefreshHealthCache(seenHandles);
            return best;
        }

        private void Start(Ped target)
        {
            Ped player = GetPlayerPed();
            if (player == null || target == null || !target.Exists())
                return;

            _target = target;
            _followStartedMs = Utils.NowMs();
            _lastTriggerMs = _followStartedMs;
            RememberVitality(target);

            ApplyWorldState();
            LaunchTarget(player, target);

            Vector3 startPos = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_COORD);
            Vector3 startRot = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_ROT, 2);
            float startFov = Function.Call<float>(Hash.GET_GAMEPLAY_CAM_FOV);

            _cameraPosition = startPos;
            _cameraRotation = startRot;

            _camera = Camera.Create("DEFAULT_SCRIPTED_CAMERA", startPos, startRot, startFov);
            if (_camera == null || !_camera.Exists())
                throw new Exception("Follow camera creation failed");

            _camera.IsActive = true;
            ScriptCameraDirector.StartRendering();
            Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, true, true, 650, true, false);

            _active = true;
            Logger.Info("FollowCameraService: follow started for ped " + target.Handle);
        }

        private void UpdateActiveFollow()
        {
            long elapsed = Utils.NowMs() - _followStartedMs;
            if (elapsed >= FollowDurationMs || _target == null || !_target.Exists())
            {
                Stop(false);
                return;
            }

            ApplyWorldState();
            DisablePlayerControls();

            Vector3 targetPos = _target.Position + new Vector3(0f, 0f, 0.9f);
            Vector3 velocity = GetEntityVelocity(_target);
            Vector3 travelDir = velocity;
            if (travelDir.Length() < 0.1f)
                travelDir = Utils.RotationToDirection(_target.Rotation);
            travelDir.Normalize();

            Vector3 side = Vector3.Cross(travelDir, new Vector3(0f, 0f, 1f));
            if (side.Length() < 0.1f)
                side = new Vector3(1f, 0f, 0f);
            side.Normalize();

            Vector3 desiredPos = targetPos - travelDir * 5.5f + side * 2.2f + new Vector3(0f, 0f, 1.7f);
            Vector3 desiredRot = DirectionToRotation(targetPos - desiredPos, _cameraRotation.Z);

            float blend = elapsed < 550 ? 0.10f : 0.18f;
            _cameraPosition = Vector3.Lerp(_cameraPosition, desiredPos, blend);
            _cameraRotation = LerpRotation(_cameraRotation, desiredRot, blend);

            if (_camera != null && _camera.Exists())
            {
                _camera.Position = _cameraPosition;
                _camera.Rotation = _cameraRotation;
                _camera.FieldOfView = 55f;
                CameraRenderer.UpdateFocusArea(targetPos);
            }
        }

        private void Stop(bool immediate)
        {
            try
            {
                bool hadSomethingToStop = _active || _camera != null || _worldStateApplied;
                if (!hadSomethingToStop)
                    return;

                _active = false;
                _target = null;

                if (_camera != null && _camera.Exists())
                {
                    _camera.IsActive = false;
                    Function.Call(Hash.DESTROY_CAM, _camera.Handle);
                }
                _camera = null;

                ScriptCameraDirector.StopRendering(false);
                Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                CameraRenderer.ClearFocus();
                RestoreWorldState();
                if (hadSomethingToStop)
                    Logger.Info("FollowCameraService: follow stopped");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FollowCameraService: Stop");
                RestoreWorldState();
            }
        }

        private void ApplyWorldState()
        {
            Function.Call(Hash.SET_TIME_SCALE, SlowMotionScale);
            Function.Call(Hash.SET_GRAVITY_LEVEL, NormalizeGravityLevel(GravityLevel));
            _worldStateApplied = true;
        }

        private void RestoreWorldState()
        {
            if (!_worldStateApplied)
                return;

            try { Function.Call(Hash.SET_TIME_SCALE, 1f); } catch { }
            try { Function.Call(Hash.SET_GRAVITY_LEVEL, NormalGravityLevel); } catch { }
            _worldStateApplied = false;
        }

        private void DisablePlayerControls()
        {
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 2);
        }

        private void LaunchTarget(Ped player, Ped target)
        {
            try
            {
                Vector3 dir = target.Position - player.Position;
                if (dir.Length() < 0.1f)
                    dir = Utils.RotationToDirection(player.Rotation);
                dir.Normalize();

                Function.Call(Hash.SET_PED_TO_RAGDOLL, target.Handle, 7000, 7000, 0, true, true, false);
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, target.Handle, 1,
                    dir.X * 34f, dir.Y * 34f, 10.5f,
                    0f, 0f, 0f,
                    0, false, true, true, false, true);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FollowCameraService: LaunchTarget");
            }
        }

        private static Ped GetPlayerPed()
        {
            if (Game.Player == null || Game.Player.Character == null || !Game.Player.Character.Exists())
                return null;
            return Game.Player.Character;
        }

        private static bool IsAttackIntentPressed()
        {
            try
            {
                return Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 24) ||
                    Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 257) ||
                    Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 69) ||
                    Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 70) ||
                    Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 140) ||
                    Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 141) ||
                    Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 142) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 24) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 257) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 69) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 70) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 140) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 141) ||
                    Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, 142);
            }
            catch
            {
                return false;
            }
        }

        private bool HasFreshVitalityDrop(Ped ped)
        {
            int previous;
            if (!_lastVitalityByPed.TryGetValue(ped.Handle, out previous))
                return false;

            return GetPedVitality(ped) < previous;
        }

        private bool HasFreshAttackFall(Ped ped)
        {
            bool previous;
            if (!_lastFallingByPed.TryGetValue(ped.Handle, out previous))
                return false;

            return !previous && IsPedFallingOrRagdoll(ped);
        }

        private static int NormalizeGravityLevel(int level)
        {
            if (level < 0) return 0;
            if (level > 3) return 3;
            return level;
        }

        private void RememberVitality(Ped ped)
        {
            try
            {
                if (ped != null && ped.Exists())
                    _lastVitalityByPed[ped.Handle] = GetPedVitality(ped);
            }
            catch
            {
            }
        }

        private void RememberFallingState(Ped ped)
        {
            try
            {
                if (ped != null && ped.Exists())
                    _lastFallingByPed[ped.Handle] = IsPedFallingOrRagdoll(ped);
            }
            catch
            {
            }
        }

        private void RefreshHealthCache(HashSet<int> liveHandles)
        {
            if (liveHandles == null)
                return;

            List<int> remove = null;
            foreach (int handle in _lastVitalityByPed.Keys)
            {
                if (!liveHandles.Contains(handle))
                {
                    if (remove == null) remove = new List<int>();
                    remove.Add(handle);
                }
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
            {
                _lastVitalityByPed.Remove(remove[i]);
                _lastFallingByPed.Remove(remove[i]);
            }
        }

        private static int GetSelectedWeaponHash(Ped player)
        {
            try
            {
                return Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, player.Handle);
            }
            catch
            {
                return 0;
            }
        }

        private static int GetPedVitality(Ped ped)
        {
            int armor = 0;
            try { armor = Function.Call<int>(Hash.GET_PED_ARMOUR, ped.Handle); } catch { }
            return ped.Health + armor;
        }

        private static bool IsPedFallingOrRagdoll(Ped ped)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_PED_RAGDOLL, ped.Handle))
                    return true;
            }
            catch
            {
            }

            try
            {
                return Function.Call<bool>(Hash.IS_PED_FALLING, ped.Handle);
            }
            catch
            {
                return false;
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

        private static Vector3 DirectionToRotation(Vector3 direction, float fallbackYaw)
        {
            if (direction.Length() < 0.0001f)
                return new Vector3(0f, 0f, fallbackYaw);

            direction.Normalize();
            float pitch = (float)(Math.Asin(direction.Z) * 57.2957795f);
            float yaw = (float)(Math.Atan2(-direction.X, direction.Y) * 57.2957795f);
            return new Vector3(pitch, 0f, yaw);
        }

        private static Vector3 LerpRotation(Vector3 from, Vector3 to, float t)
        {
            return new Vector3(
                LerpAngle(from.X, to.X, t),
                LerpAngle(from.Y, to.Y, t),
                LerpAngle(from.Z, to.Z, t));
        }

        private static float LerpAngle(float a, float b, float t)
        {
            float delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return a + delta * t;
        }
    }
}
