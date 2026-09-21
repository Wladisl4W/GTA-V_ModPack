using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using ModPack;

namespace ModdedCamera.Services
{
    public class FollowCameraService
    {
        private const int DefaultFollowDurationMs = 7000;
        private const float SlowMotionScale = 0.75f;
        private const int DefaultGravityLevel = 2;
        private const int NormalGravityLevel = 0;
        private const float MaxFallbackDamageDistanceSq = 250f * 250f;
        private const float FixedCameraDistance = 7f;
        private const float BaseLaunchForce = 224f;
        private const float BaseVelocityBoost = 84f;
        private const int MinFollowDurationMs = 900;
        private const int StillStopDelayMs = 650;
        private const int LaunchVelocityHoldMs = 260;
        private const float StillSpeedThreshold = 0.55f;
        private const float FreshLaunchSpeed = 6.0f;
        private const float FreshLaunchSpeedDelta = 4.0f;

        private readonly ControlSession _control = new ControlSession();
        private Camera _camera;
        private Ped _target;
        private long _followStartedMs;
        private long _lastTriggerMs;
        private long _lastAttackIntentMs;
        private long _attackIntentStartedMs;
        private long _stillSinceMs;
        private long _holdLaunchVelocityUntilMs;
        private Vector3 _cameraPosition;
        private Vector3 _cameraRotation;
        private Vector3 _fixedCameraForward;
        private Vector3 _lastTravelDirection;
        private Vector3 _heldLaunchVelocity;
        private bool _active;
        private bool _enabled;
        private bool _worldStateApplied;
        private readonly Dictionary<int, int> _lastVitalityByPed = new Dictionary<int, int>();
        private readonly Dictionary<int, bool> _lastFallingByPed = new Dictionary<int, bool>();
        private readonly Dictionary<int, Vector3> _lastVelocityByPed = new Dictionary<int, Vector3>();
        private readonly HashSet<int> _followedPedHandles = new HashSet<int>();

        public int FollowDurationMs { get; set; }
        public int GravityLevel { get; set; }
        public float HitForceMultiplier { get; set; }

        public FollowCameraService()
        {
            FollowDurationMs = DefaultFollowDurationMs;
            GravityLevel = DefaultGravityLevel;
            HitForceMultiplier = 1f;
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                DevelopmentDiagnostics.State("Follow", value ? "Waiting for damage" : "Disabled");
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
                if (_control.Held && !_control.Valid) { Stop(true); return; }
                if (_active)
                {
                    UpdateActiveFollow();
                    return;
                }

                if (!_enabled) return;
                if (blockedByModCamera)
                {
                    DevelopmentDiagnostics.Event("Follow", "Blocked by camera/menu");
                    ConsumeBlockedDamage();
                    return;
                }

                Ped target = FindFreshDamageTarget();
                if (target != null) Start(target);
                DevelopmentDiagnostics.State("Follow", _active ? "Following" : "Waiting for fresh damage");
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

        private void ConsumeBlockedDamage()
        {
            if (ControlSession.Owner == null) return;
            Ped target = FindFreshDamageTarget();
            if (target != null) DevelopmentDiagnostics.Event("Follow", "Fresh damage ignored while controls occupied");
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
            Ped meleeTarget = GetMeleeTarget(player);
            bool recentPlayerWeaponImpact = recentAttackIntent && HasRecentWeaponImpact(player);

            Ped[] peds = World.GetAllPeds();
            Ped best = null;
            int bestScore = -1;
            float bestDistSq = 9999f;
            Vector3 playerPos = player.Position;
            HashSet<int> seenHandles = new HashSet<int>();

            for (int i = 0; peds != null && i < peds.Length; i++)
            {
                Ped ped = peds[i];
                if (ped == null || !ped.Exists() || ped == player)
                    continue;

                seenHandles.Add(ped.Handle);
                if (_followedPedHandles.Contains(ped.Handle))
                {
                    DevelopmentDiagnostics.Event("Follow", "Target already followed: " + ped.Handle);
                    RememberVitality(ped);
                    RememberFallingState(ped);
                    RememberVelocity(ped, GetEntityVelocity(ped));
                    continue;
                }

                Vector3 delta = ped.Position - playerPos;
                float distSq = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;

                bool damagedByPlayer = Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, ped.Handle, player.Handle, true);
                bool damagedByWeapon = selectedWeapon != 0 && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_WEAPON, ped.Handle, selectedWeapon, 0);
                bool meleeDamage = HasMeleeWeaponDamage(ped);
                bool freshVitalityDrop = HasFreshVitalityDrop(ped) && recentAttackIntent && distSq < MaxFallbackDamageDistanceSq;
                bool freshAttackFall = HasFreshAttackFall(ped) && recentAttackIntent && distSq < 36f;
                Vector3 velocity = GetEntityVelocity(ped);
                bool freshVelocityLaunch = HasFreshVelocityLaunch(ped, velocity) && recentAttackIntent && distSq < 100f;
                bool meleeTargetConfirmed = meleeTarget != null && meleeTarget.Exists() && meleeTarget.Handle == ped.Handle &&
                    recentAttackIntent && distSq < 49f && (meleeDamage || freshVelocityLaunch || recentPlayerWeaponImpact);

                RememberVitality(ped);
                RememberFallingState(ped);
                RememberVelocity(ped, velocity);

                if (!damagedByPlayer && !damagedByWeapon && !freshVitalityDrop && !freshAttackFall && !freshVelocityLaunch && !meleeTargetConfirmed)
                    continue;

                Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, ped.Handle);
                TryClearWeaponDamage(ped);
                DevelopmentDiagnostics.Event("Follow", "Damage ped " + ped.Handle + ": " +
                    (damagedByPlayer ? "player damage" : damagedByWeapon ? "weapon damage" :
                     meleeTargetConfirmed ? "melee contact" : freshVelocityLaunch ? "velocity" :
                     freshVitalityDrop ? "health/armor" : "fall"));
                int score = damagedByPlayer ? 6 : (damagedByWeapon ? 5 : (meleeTargetConfirmed ? 4 : (freshVelocityLaunch ? 3 : (freshVitalityDrop ? 2 : 1))));
                if (score > bestScore || (score == bestScore && distSq < bestDistSq))
                {
                    bestScore = score;
                    bestDistSq = distSq;
                    best = ped;
                }
            }

            RefreshHealthCache(seenHandles);
            if (best == null && recentAttackIntent)
                DevelopmentDiagnostics.Event("Follow", "No fresh eligible damage target");
            return best;
        }

        private void Start(Ped target)
        {
            Ped player = GetPlayerPed();
            if (player == null || target == null || !target.Exists())
                return;

            if (!_control.Acquire("Следование камеры", false)) return;
            _target = target;
            _followedPedHandles.Add(target.Handle);
            _followStartedMs = Utils.NowMs();
            _lastTriggerMs = _followStartedMs;
            _stillSinceMs = 0;
            RememberVitality(target);

            ApplyWorldState();
            _heldLaunchVelocity = LaunchTarget(player, target);
            _holdLaunchVelocityUntilMs = _followStartedMs + LaunchVelocityHoldMs;

            Vector3 startPos = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_COORD);
            Vector3 startRot = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_ROT, 2);
            float startFov = Function.Call<float>(Hash.GET_GAMEPLAY_CAM_FOV);

            _cameraPosition = startPos;
            _cameraRotation = startRot;
            _fixedCameraForward = Utils.RotationToDirection(_cameraRotation);
            if (_fixedCameraForward.Length() < 0.01f)
                _fixedCameraForward = new Vector3(0f, 1f, 0f);
            _fixedCameraForward.Normalize();
            _lastTravelDirection = GetLaunchDirection(player, target);

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
            long now = Utils.NowMs();
            long elapsed = now - _followStartedMs;
            if (_target == null || !_target.Exists())
            {
                DevelopmentDiagnostics.Event("Follow", "Stopped: target entity missing");
                Stop(false);
                return;
            }

            ApplyWorldState();
            DisablePlayerControls();

            Vector3 targetPos = _target.Position + new Vector3(0f, 0f, 0.9f);
            Vector3 velocity = GetEntityVelocity(_target);
            if (now <= _holdLaunchVelocityUntilMs)
            {
                Function.Call(Hash.SET_ENTITY_VELOCITY, _target.Handle, _heldLaunchVelocity.X, _heldLaunchVelocity.Y, _heldLaunchVelocity.Z);
                velocity = _heldLaunchVelocity;
            }

            if (ShouldStopFollow(now, elapsed, velocity))
            {
                DevelopmentDiagnostics.Event("Follow", "Stopped: duration/settled target");
                Stop(false);
                return;
            }

            Vector3 travelDir = velocity;
            if (travelDir.Length() > 0.45f)
            {
                travelDir.Normalize();
                _lastTravelDirection = travelDir;
            }
            else
            {
                travelDir = _lastTravelDirection;
            }

            _cameraPosition = targetPos - _fixedCameraForward * FixedCameraDistance;

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
                bool hadSomethingToStop = _active || _camera != null || _worldStateApplied || _control.Held;
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

                if (_control.Held) ScriptCameraDirector.StopRendering(false);
                if (_control.Held) Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                if (_control.Held) CameraRenderer.ClearFocus();
                RestoreWorldState();
                if (hadSomethingToStop)
                    Logger.Info("FollowCameraService: follow stopped");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FollowCameraService: Stop");
                RestoreWorldState();
            }
            finally
            {
                if (_control.Held)
                {
                    try { if (_camera != null && _camera.Exists()) _camera.Delete(); } catch { }
                    try { ScriptCameraDirector.StopRendering(false); } catch { }
                    try { Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false); } catch { }
                    try { CameraRenderer.ClearFocus(); } catch { }
                }
                _active = false;
                _target = null;
                _camera = null;
                _worldStateApplied = false;
                _control.Dispose();
                DevelopmentDiagnostics.State("Follow", "Idle");
            }
        }

        private bool ShouldStopFollow(long now, long elapsed, Vector3 velocity)
        {
            if (elapsed >= FollowDurationMs)
                return true;

            if (elapsed < MinFollowDurationMs)
            {
                _stillSinceMs = 0;
                return false;
            }

            bool settled = velocity.Length() <= StillSpeedThreshold && !IsEntityInAir(_target);
            if (!settled)
            {
                _stillSinceMs = 0;
                return false;
            }

            if (_stillSinceMs <= 0)
            {
                _stillSinceMs = now;
                return false;
            }

            return now - _stillSinceMs >= StillStopDelayMs;
        }

        private void ApplyWorldState()
        {
            _control.TimeScale(SlowMotionScale);
            _control.Gravity(NormalizeGravityLevel(GravityLevel));
            _worldStateApplied = true;
        }

        private void RestoreWorldState()
        {
            if (!_worldStateApplied)
                return;

            _worldStateApplied = false;
        }

        private void DisablePlayerControls()
        {
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 2);
        }

        private Vector3 LaunchTarget(Ped player, Ped target)
        {
            try
            {
                Vector3 dir = GetLaunchDirection(player, target, IsPlayerUsingFirearm(player));
                float forceMultiplier = NormalizeHitForceMultiplier(HitForceMultiplier);
                Vector3 currentVelocity = GetEntityVelocity(target);
                Vector3 launchVelocity = new Vector3(
                    dir.X * BaseVelocityBoost * forceMultiplier,
                    dir.Y * BaseVelocityBoost * forceMultiplier,
                    Math.Max(currentVelocity.Z + 2.5f, 2.5f));

                Function.Call(Hash.SET_PED_TO_RAGDOLL, target.Handle, 7000, 7000, 0, true, true, false);
                Function.Call(Hash.SET_ENTITY_VELOCITY, target.Handle, launchVelocity.X, launchVelocity.Y, launchVelocity.Z);
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, target.Handle, 1,
                    dir.X * BaseLaunchForce * forceMultiplier, dir.Y * BaseLaunchForce * forceMultiplier, 5.5f,
                    0f, 0f, 0f,
                    0, false, true, true, false, true);
                return launchVelocity;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FollowCameraService: LaunchTarget");
                return GetEntityVelocity(target);
            }
        }

        private static Vector3 GetLaunchDirection(Ped player, Ped target)
        {
            return GetLaunchDirection(player, target, false);
        }

        private static Vector3 GetLaunchDirection(Ped player, Ped target, bool preferAimDirection)
        {
            Vector3 dir = preferAimDirection ? GetGameplayCameraDirection() : Vector3.Zero;
            if (dir.Length() < 0.1f)
                dir = target.Position - player.Position;
            dir.Z = 0f;
            if (dir.Length() < 0.1f)
            {
                dir = Utils.RotationToDirection(player.Rotation);
                dir.Z = 0f;
            }
            if (dir.Length() < 0.1f)
                dir = new Vector3(0f, 1f, 0f);
            dir.Normalize();
            return dir;
        }

        private static Vector3 GetGameplayCameraDirection()
        {
            try
            {
                Vector3 rotation = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_ROT, 2);
                return Utils.RotationToDirection(rotation);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        private static bool IsPlayerUsingFirearm(Ped player)
        {
            try
            {
                return Function.Call<bool>(Hash.IS_PED_ARMED, player.Handle, 4);
            }
            catch
            {
                return false;
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

        private bool HasFreshVelocityLaunch(Ped ped, Vector3 velocity)
        {
            Vector3 previous;
            if (!_lastVelocityByPed.TryGetValue(ped.Handle, out previous))
                return false;

            float speed = velocity.Length();
            float previousSpeed = previous.Length();
            return speed >= FreshLaunchSpeed && speed - previousSpeed >= FreshLaunchSpeedDelta;
        }

        private static int NormalizeGravityLevel(int level)
        {
            if (level < 0) return 0;
            if (level > 3) return 3;
            return level;
        }

        private static float NormalizeHitForceMultiplier(float value)
        {
            if (value < 0.25f) return 0.25f;
            if (value > 3f) return 3f;
            return value;
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

        private void RememberVelocity(Ped ped, Vector3 velocity)
        {
            try
            {
                if (ped != null && ped.Exists())
                    _lastVelocityByPed[ped.Handle] = velocity;
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
                _lastVelocityByPed.Remove(remove[i]);
            }
        }

        private static Ped GetMeleeTarget(Ped player)
        {
            try
            {
                return Function.Call<Ped>(Hash.GET_MELEE_TARGET_FOR_PED, player.Handle);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasMeleeWeaponDamage(Ped ped)
        {
            try
            {
                return Function.Call<bool>(Hash.HAS_PED_BEEN_DAMAGED_BY_WEAPON, ped.Handle, 0, 1);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasRecentWeaponImpact(Ped player)
        {
            try
            {
                OutputArgument impactArg = new OutputArgument();
                if (!Function.Call<bool>(Hash.GET_PED_LAST_WEAPON_IMPACT_COORD, player.Handle, impactArg))
                    return false;

                Vector3 impact = impactArg.GetResult<Vector3>();
                return Math.Abs(impact.X) > 0.001f || Math.Abs(impact.Y) > 0.001f || Math.Abs(impact.Z) > 0.001f;
            }
            catch
            {
                return false;
            }
        }

        private static void TryClearWeaponDamage(Ped ped)
        {
            try { Function.Call(Hash.CLEAR_ENTITY_LAST_WEAPON_DAMAGE, ped.Handle); } catch { }
            try { Function.Call(Hash.CLEAR_PED_LAST_WEAPON_DAMAGE, ped.Handle); } catch { }
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

        private static bool IsEntityInAir(Entity entity)
        {
            try
            {
                return entity != null && entity.Exists() && Function.Call<bool>(Hash.IS_ENTITY_IN_AIR, entity.Handle);
            }
            catch
            {
                return false;
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
