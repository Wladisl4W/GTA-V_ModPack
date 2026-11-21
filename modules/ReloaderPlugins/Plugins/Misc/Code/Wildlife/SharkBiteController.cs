using System;
using ModPack.Core;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ModPack.Wildlife
{
    internal sealed class SharkBiteController
    {
        internal enum Result { Running, Completed, Cancelled }
        private enum BitePhase { Idle, IntroScene, Capture, Holding, Release, Completed, Cancelled }

        private const string AnimDict = "creatures@shark@move";
        private const string SharkIntroAnim = "attack";
        private const string SharkHoldAnim = "attack_onspot";
        private const string PlayerAnim = "attack_player";

        private const long IntroNoProgressTimeoutMs = 1000;
        private const long IntroMaxTimeoutMs = 2600;
        private const long CaptureDurationMs = 500;
        private const long HoldDurationMs = 3000;
        private const long ReleaseDurationMs = 500;
        private const long WholeBiteTimeoutMs = 9000;

        private readonly string[] _mouthBoneNames = new[]
        {
            "SKEL_Head", "SKEL_Jaw", "SKEL_JawRoot", "head", "jaw", "mouth"
        };

        private Ped _player;
        private Ped _shark;
        private ControlSession _control;
        private Action<string> _log;
        private BitePhase _phase = BitePhase.Idle;
        private long _startedMs;
        private long _phaseStartedMs;
        private long _lastSceneProgressMs;
        private float _lastScenePhase;
        private int _scene = -1;
        private int _mouthBoneIndex = -1;
        private bool _usingBone;
        private bool _originalCanRagdoll;
        private bool _snapshotTaken;
        private Vector3 _captureStartPosition;
        private Vector3 _captureStartRotation;
        private Vector3 _releaseStartPosition;
        private Vector3 _releaseTargetPosition;
        private float _holdHeadingOffset;
        private bool _attached;
        private bool _active;

        public bool Active
        {
            get { return _active; }
        }

        public bool Begin(Ped player, Ped shark, ControlSession control, long nowMs, Action<string> log)
        {
            Abort("перезапуск укуса");
            _player = player;
            _shark = shark;
            _control = control;
            _log = log ?? delegate { };
            _startedMs = nowMs;
            _phaseStartedMs = nowMs;
            _lastSceneProgressMs = nowMs;
            _lastScenePhase = 0f;
            _scene = -1;
            _mouthBoneIndex = -1;
            _usingBone = false;
            _snapshotTaken = false;
            _attached = false;
            _active = true;

            if (!IsUsable(_player) || !IsUsable(_shark) || _control == null || !_control.Held)
            {
                Abort("нет управления для укуса");
                return false;
            }

            try
            {
                _originalCanRagdoll = Function.Call<bool>(Hash.CAN_PED_RAGDOLL, _player.Handle);
                _snapshotTaken = true;
            }
            catch
            {
                _originalCanRagdoll = true;
                _snapshotTaken = true;
            }

            try
            {
                _player.IsPositionFrozen = false;
                _player.IsInvincible = true;
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, false);
                Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _player.Handle, _shark.Handle, true);
                Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _shark.Handle, _player.Handle, true);
                Function.Call(Hash.CLEAR_PED_TASKS, _player.Handle);
                Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
                _player.Velocity = Vector3.Zero;
                _shark.Velocity = Vector3.Zero;
            }
            catch (Exception ex)
            {
                _log("Bite begin safety: " + ex.Message);
            }

            ResolveMouthAttachment();
            if (TryStartIntroScene(nowMs))
            {
                SetPhase(BitePhase.IntroScene, nowMs, "старт локальной сцены укуса");
            }
            else
            {
                _captureStartPosition = SafePosition(_player);
                _captureStartRotation = SafeRotation(_player);
                SetPhase(BitePhase.Capture, nowMs, "локальная сцена недоступна, контролируемый захват");
            }

            return true;
        }

        public Result Update(long nowMs, float dt)
        {
            if (!_active) return Result.Cancelled;
            if (!IsUsable(_player) || !IsUsable(_shark) || _control == null || !_control.Held)
            {
                Abort("цель или управление потеряны");
                return Result.Cancelled;
            }
            if (nowMs - _startedMs > WholeBiteTimeoutMs)
            {
                Abort("тайм-аут укуса");
                return Result.Cancelled;
            }

            ApplySafety();

            switch (_phase)
            {
                case BitePhase.IntroScene:
                    return UpdateIntro(nowMs, dt);
                case BitePhase.Capture:
                    return UpdateCapture(nowMs, dt);
                case BitePhase.Holding:
                    return UpdateHolding(nowMs, dt);
                case BitePhase.Release:
                    return UpdateRelease(nowMs, dt);
                case BitePhase.Completed:
                    return Result.Completed;
                case BitePhase.Cancelled:
                    return Result.Cancelled;
                default:
                    return Result.Running;
            }
        }

        public void Abort(string reason)
        {
            if (!_active && _phase == BitePhase.Idle) return;

            try { StopSceneAndTasks(); } catch { }
            try
            {
                if (_player != null && _player.Exists())
                {
                    if (_attached || IsAttachedToShark())
                        _player.Detach();
                    if (_snapshotTaken)
                        Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, _originalCanRagdoll);
                    try { Function.Call(Hash.CLEAR_PED_TASKS, _player.Handle); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { _log("Bite abort player: " + ex.Message); } catch { }
            }

            try
            {
                if (_shark != null && _shark.Exists())
                {
                    try { Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle); } catch { }
                    _shark.Velocity = Vector3.Zero;
                }
            }
            catch (Exception ex)
            {
                try { _log("Bite abort shark: " + ex.Message); } catch { }
            }

            try
            {
                if (_control != null && _control.Held)
                    _control.Dispose();
            }
            catch { }

            if (_active)
                DevelopmentDiagnostics.Event("Shark", "Bite cleanup: " + reason);

            _active = false;
            _phase = BitePhase.Idle;
            _player = null;
            _shark = null;
            _control = null;
            _scene = -1;
            _mouthBoneIndex = -1;
            _usingBone = false;
            _snapshotTaken = false;
            _attached = false;
        }

        private Result UpdateIntro(long nowMs, float dt)
        {
            MoveSharkSlowly(dt);

            float phase = GetScenePhase();
            if (phase > _lastScenePhase + 0.002f)
            {
                _lastScenePhase = phase;
                _lastSceneProgressMs = nowMs;
            }

            if (nowMs - _lastSceneProgressMs >= IntroNoProgressTimeoutMs)
            {
                StopSceneAndTasks();
                _captureStartPosition = SafePosition(_player);
                _captureStartRotation = SafeRotation(_player);
                SetPhase(BitePhase.Capture, nowMs, "сцена укуса не продвигается");
                return Result.Running;
            }

            if (IsMouthCloseToPlayer() || phase >= 0.70f || nowMs - _phaseStartedMs >= IntroMaxTimeoutMs)
            {
                StopSceneAndTasks();
                _captureStartPosition = SafePosition(_player);
                _captureStartRotation = SafeRotation(_player);
                SetPhase(BitePhase.Capture, nowMs, "переход в удержание");
            }
            return Result.Running;
        }

        private Result UpdateCapture(long nowMs, float dt)
        {
            float t = Smooth01((nowMs - _phaseStartedMs) / (float)CaptureDurationMs);
            Vector3 target = GetMouthWorldPosition();
            Vector3 position = Lerp(_captureStartPosition, target, t);
            Vector3 rotation = GetBiteBodyRotation();
            Vector3 smoothRotation = LerpRotation(_captureStartRotation, rotation, t);

            SafeSetPositionAndRotation(_player, position, smoothRotation);
            MoveSharkSlowly(dt);

            if (nowMs - _phaseStartedMs >= CaptureDurationMs)
            {
                AttachPlayerToMouth();
                StartHoldAnimations();
                SetPhase(BitePhase.Holding, nowMs, "персонаж удерживается в пасти");
            }
            return Result.Running;
        }

        private Result UpdateHolding(long nowMs, float dt)
        {
            MoveSharkSlowly(dt);
            EnsureAttachedAndAnimated();

            if (nowMs - _phaseStartedMs >= HoldDurationMs)
            {
                StopSceneAndTasks();
                if (IsAttachedToShark())
                    _player.Detach();
                _attached = false;
                _releaseStartPosition = SafePosition(_player);
                _releaseTargetPosition = ComputeReleasePosition();
                SetPhase(BitePhase.Release, nowMs, "освобождение из пасти");
            }
            return Result.Running;
        }

        private Result UpdateRelease(long nowMs, float dt)
        {
            float t = Smooth01((nowMs - _phaseStartedMs) / (float)ReleaseDurationMs);
            Vector3 position = Lerp(_releaseStartPosition, _releaseTargetPosition, t);
            SafeSetPositionAndRotation(_player, position, GetReleaseRotation());
            MoveSharkSlowly(dt);

            if (nowMs - _phaseStartedMs >= ReleaseDurationMs)
            {
                Complete();
                return Result.Completed;
            }
            return Result.Running;
        }

        private void Complete()
        {
            try { StopSceneAndTasks(); } catch { }
            try
            {
                if (_player != null && _player.Exists())
                {
                    if (IsAttachedToShark())
                        _player.Detach();
                    if (_snapshotTaken)
                        Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, _originalCanRagdoll);
                    try { Function.Call(Hash.CLEAR_PED_TASKS, _player.Handle); } catch { }
                }
            }
            catch (Exception ex)
            {
                _log("Bite complete player: " + ex.Message);
            }

            try
            {
                if (_shark != null && _shark.Exists())
                {
                    try { Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle); } catch { }
                    _shark.Velocity = Vector3.Zero;
                }
            }
            catch { }

            try
            {
                if (_control != null && _control.Held)
                    _control.Dispose();
            }
            catch { }

            DevelopmentDiagnostics.Event("Shark", "Bite completed");
            _active = false;
            _phase = BitePhase.Completed;
            _attached = false;
        }

        private bool TryStartIntroScene(long nowMs)
        {
            try
            {
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, AnimDict))
                    return false;

                Vector3 playerPos = SafePosition(_player);
                Vector3 sharkPos = SafePosition(_shark);
                float heading = HeadingTo(playerPos - sharkPos);
                Vector3 origin = ComputeSceneOrigin(playerPos, heading);
                Vector3 sharkSceneStart = Function.Call<Vector3>(Hash.GET_ANIM_INITIAL_OFFSET_POSITION,
                    AnimDict, SharkIntroAnim,
                    origin.X, origin.Y, origin.Z,
                    0f, 0f, heading,
                    0f, 2);

                if (!IsInvalid(sharkSceneStart) && sharkSceneStart.DistanceTo(sharkPos) > 3.5f)
                {
                    _log("Bite scene skipped: shark offset too far");
                    return false;
                }

                _scene = Function.Call<int>(Hash.CREATE_SYNCHRONIZED_SCENE,
                    origin.X, origin.Y, origin.Z,
                    0f, 0f, heading,
                    2);
                if (_scene < 0) return false;

                Function.Call(Hash.TASK_SYNCHRONIZED_SCENE, _shark.Handle, _scene, AnimDict, SharkIntroAnim,
                    8f, -8f, -1, 0, 1000f, 0);
                Function.Call(Hash.TASK_SYNCHRONIZED_SCENE, _player.Handle, _scene, AnimDict, PlayerAnim,
                    8f, -8f, -1, 0, 1000f, 0);
                _lastSceneProgressMs = nowMs;
                _lastScenePhase = 0f;
                return true;
            }
            catch (Exception ex)
            {
                _log("Start local bite scene: " + ex.Message);
                _scene = -1;
                return false;
            }
        }

        private Vector3 ComputeSceneOrigin(Vector3 playerPos, float heading)
        {
            try
            {
                Vector3 offset = Function.Call<Vector3>(Hash.GET_ANIM_INITIAL_OFFSET_POSITION,
                    AnimDict, PlayerAnim,
                    0f, 0f, 0f,
                    0f, 0f, heading,
                    0f, 2);
                if (!IsInvalid(offset))
                    return playerPos - offset;
            }
            catch (Exception ex)
            {
                _log("Bite offset fallback: " + ex.Message);
            }
            Vector3 forward = DirectionFromHeading(heading);
            return playerPos - forward * 0.55f;
        }

        private void ResolveMouthAttachment()
        {
            _mouthBoneIndex = -1;
            _usingBone = false;
            for (int i = 0; i < _mouthBoneNames.Length; i++)
            {
                try
                {
                    int bone = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, _shark.Handle, _mouthBoneNames[i]);
                    if (bone < 0) continue;

                    Vector3 pos = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, _shark.Handle, bone);
                    if (IsInvalid(pos)) continue;

                    _mouthBoneIndex = bone;
                    _usingBone = true;
                    _log("Bite mouth bone: " + _mouthBoneNames[i] + " (" + bone + ")");
                    return;
                }
                catch { }
            }
            _log("Bite mouth bone: fallback model offset");
        }

        private void AttachPlayerToMouth()
        {
            try
            {
                if (_usingBone && _mouthBoneIndex >= 0)
                {
                    Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                        _player.Handle, _shark.Handle, _mouthBoneIndex,
                        -0.80f, 0.18f, -0.05f,
                        0f, 90f, 0f,
                        false, false, false, true, 2, true);
                    _attached = true;
                    return;
                }

                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY,
                    _player.Handle, _shark.Handle, -1,
                    -0.80f, 1.45f, 0.25f,
                    0f, 90f, 0f,
                    false, false, false, true, 2, true);
                _attached = true;
            }
            catch (Exception ex)
            {
                _log("Attach player to shark: " + ex.Message);
                _attached = false;
            }
        }

        private void StartHoldAnimations()
        {
            try
            {
                Function.Call(Hash.TASK_PLAY_ANIM, _shark.Handle, AnimDict, SharkHoldAnim,
                    8f, -8f, -1, 1, 0f, false, false, false);
            }
            catch (Exception ex)
            {
                _log("Shark hold anim: " + ex.Message);
            }

            try
            {
                Function.Call(Hash.TASK_PLAY_ANIM, _player.Handle, AnimDict, PlayerAnim,
                    8f, -8f, -1, 49, 0f, false, false, false);
            }
            catch (Exception ex)
            {
                _log("Player bite anim: " + ex.Message);
            }
        }

        private void EnsureAttachedAndAnimated()
        {
            if (!_attached && !IsAttachedToShark())
                AttachPlayerToMouth();

            try
            {
                if (!Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, _shark.Handle, AnimDict, SharkHoldAnim, 3))
                    Function.Call(Hash.TASK_PLAY_ANIM, _shark.Handle, AnimDict, SharkHoldAnim,
                        8f, -8f, -1, 1, 0f, false, false, false);
            }
            catch { }
        }

        private void ApplySafety()
        {
            try
            {
                _player.IsPositionFrozen = false;
                _player.IsInvincible = true;
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, false);
                Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _player.Handle, _shark.Handle, true);
                Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _shark.Handle, _player.Handle, true);

                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 21, true);  // sprint
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 22, true);  // jump
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 24, true);  // attack
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 25, true);  // aim
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 30, true);  // move left/right
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 31, true);  // move forward/back
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 44, true);  // cover
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 75, true);  // exit vehicle
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 140, true); // melee light
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 141, true); // melee heavy
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 142, true); // melee alternate
            }
            catch { }
        }

        private void MoveSharkSlowly(float dt)
        {
            try
            {
                Vector3 playerPos = SafePosition(_player);
                Vector3 sharkPos = SafePosition(_shark);
                Vector3 toPlayer = playerPos - sharkPos;
                toPlayer.Z = 0f;
                if (toPlayer.LengthSquared() > 0.01f)
                {
                    float targetHeading = HeadingTo(toPlayer);
                    _shark.Heading = LerpAngle(_shark.Heading, targetHeading, Clamp(dt * 2.0f, 0f, 0.08f));
                }

                Vector3 dir = DirectionFromHeading(_shark.Heading);
                float vertical = Clamp((playerPos.Z - 0.20f - sharkPos.Z) * 0.8f, -0.8f, 0.8f);
                _shark.Velocity = new Vector3(dir.X * 0.65f, dir.Y * 0.65f, vertical);
            }
            catch { }
        }

        private Vector3 GetMouthWorldPosition()
        {
            try
            {
                if (_usingBone && _mouthBoneIndex >= 0)
                {
                    Vector3 bone = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, _shark.Handle, _mouthBoneIndex);
                    if (!IsInvalid(bone))
                        return bone + DirectionFromHeading(_shark.Heading) * 0.18f;
                }

                Vector3 pos = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS,
                    _shark.Handle, -0.80f, 1.45f, 0.25f);
                if (!IsInvalid(pos)) return pos;
            }
            catch { }
            return SafePosition(_shark) + DirectionFromHeading(_shark.Heading) * 1.4f + new Vector3(0f, 0f, 0.2f);
        }

        private Vector3 ComputeReleasePosition()
        {
            Vector3 sharkPos = SafePosition(_shark);
            Vector3 playerPos = SafePosition(_player);
            Vector3 away = playerPos - sharkPos;
            away.Z = 0f;
            if (away.LengthSquared() < 0.01f)
            {
                Vector3 forward = DirectionFromHeading(_shark.Heading);
                away = new Vector3(-forward.Y, forward.X, 0f);
            }
            away.Normalize();
            Vector3 target = playerPos + away * 1.35f + new Vector3(0f, 0f, 0.10f);
            if (IsInvalid(target)) target = playerPos;
            return target;
        }

        private Vector3 GetBiteBodyRotation()
        {
            _holdHeadingOffset = _shark.Heading + 90f;
            return new Vector3(0f, 90f, _holdHeadingOffset);
        }

        private Vector3 GetReleaseRotation()
        {
            return new Vector3(0f, 0f, _holdHeadingOffset);
        }

        private bool IsMouthCloseToPlayer()
        {
            try
            {
                return GetMouthWorldPosition().DistanceTo(SafePosition(_player)) <= 0.75f;
            }
            catch
            {
                return false;
            }
        }

        private void StopSceneAndTasks()
        {
            try
            {
                if (_scene >= 0)
                    Function.Call(Hash.SET_SYNCHRONIZED_SCENE_RATE, _scene, 0f);
            }
            catch { }
            _scene = -1;
            try
            {
                if (_player != null && _player.Exists())
                    Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, _player.Handle);
            }
            catch { }
            try
            {
                if (_shark != null && _shark.Exists())
                    Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
            }
            catch { }
        }

        private float GetScenePhase()
        {
            if (_scene < 0) return 0f;
            try
            {
                return Function.Call<float>(Hash.GET_SYNCHRONIZED_SCENE_PHASE, _scene);
            }
            catch
            {
                return 0f;
            }
        }

        private bool IsAttachedToShark()
        {
            try
            {
                return _player != null && _player.Exists() && _shark != null && _shark.Exists() &&
                    Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY, _player.Handle, _shark.Handle);
            }
            catch
            {
                return false;
            }
        }

        private void SetPhase(BitePhase phase, long nowMs, string message)
        {
            _phase = phase;
            _phaseStartedMs = nowMs;
            DevelopmentDiagnostics.Event("Shark", "Bite phase: " + message);
        }

        private static bool IsUsable(Ped ped)
        {
            return ped != null && ped.Exists() && !ped.IsDead;
        }

        private static Vector3 SafePosition(Entity entity)
        {
            try
            {
                return entity != null && entity.Exists() ? entity.Position : Vector3.Zero;
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        private static Vector3 SafeRotation(Entity entity)
        {
            try
            {
                return entity != null && entity.Exists() ? entity.Rotation : Vector3.Zero;
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        private static void SafeSetPositionAndRotation(Entity entity, Vector3 position, Vector3 rotation)
        {
            if (entity == null || !entity.Exists() || IsInvalid(position)) return;
            try
            {
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, entity.Handle,
                    position.X, position.Y, position.Z, false, false, false);
                entity.Rotation = rotation;
                entity.Velocity = Vector3.Zero;
            }
            catch { }
        }

        private static float Smooth01(float value)
        {
            float t = Clamp(value, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private static Vector3 Lerp(Vector3 from, Vector3 to, float amount)
        {
            return new Vector3(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount);
        }

        private static Vector3 LerpRotation(Vector3 from, Vector3 to, float amount)
        {
            return new Vector3(
                LerpAngle(from.X, to.X, amount),
                LerpAngle(from.Y, to.Y, amount),
                LerpAngle(from.Z, to.Z, amount));
        }

        private static float LerpAngle(float from, float to, float amount)
        {
            float delta = to - from;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return from + delta * amount;
        }

        private static float HeadingTo(Vector3 direction)
        {
            if (direction.LengthSquared() < 0.01f) return 0f;
            return (float)(Math.Atan2(-direction.X, direction.Y) * 180.0 / Math.PI);
        }

        private static Vector3 DirectionFromHeading(float heading)
        {
            double radians = heading * Math.PI / 180.0;
            return new Vector3((float)(-Math.Sin(radians)), (float)Math.Cos(radians), 0f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool IsInvalid(Vector3 v)
        {
            return float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) ||
                   float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z);
        }
    }
}
