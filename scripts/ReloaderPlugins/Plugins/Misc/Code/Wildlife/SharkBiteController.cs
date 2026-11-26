using System;
using System.IO;
using System.Media;
using ModPack.Core;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ModPack.Wildlife
{
    // Maneater's living-victim sequence, adapted to an autonomous, non-lethal attacker.
    internal sealed class SharkBiteController
    {
        internal enum Result { Running, Completed, Cancelled }
        private enum Phase { Idle, Grab, Hold, ChewStart, Chew, FinishChew, Throw, Release }
        private enum Grip { Middle, Upper, Lower }
        private const string SharkDict = "creatures@shark@melee@streamed_core@";
        private const string Attack = "attack";
        private const string VictimDict = "dead@fall";
        private const string VictimAnim = "dead_fall_up";
        private const string ChewDict = "missexile3_rpg";
        private const string ChewAnim = "aim_breathe_additive";
        // Maneater's helper uses FORCE_START for its unattached shark in water.
        private const int SharkFlags = 131072;
        private const float GrabStart = 0.07605013f;
        private const float ChewStart = 0.16408630f;
        private const float ChewLoopStart = 0.34948018f;
        private const float ChewLoopEnd = 0.53748029f;
        private const float ThrowStart = 0.12981175f;
        private const float ThrowRelease = 0.22181161f;
        private const float ThrowEnd = 0.28581163f;
        private static readonly Random Random = new Random();

        private Ped _player, _shark;
        private ControlSession _control;
        private Action<string> _log = delegate { };
        private Phase _phase;
        private Grip _grip;
        private long _started, _phaseStarted, _animationStarted, _nextVictimRetry;
        private bool _active, _snapshot, _invincible, _canRagdoll, _attached;
        private bool _seekPending, _chewing, _victimSeekPending;
        private int _headBone, _chews, _targetChews, _victimRetries;
        private float _seekTime, _victimSpeed, _gripYaw;
        private float _attackBlend;
        private bool _attackLockY;
        private long _nextSharkRetry;
        private int _sharkRetries;
        private SoundPlayer _sound;
        private bool _soundErrorLogged;

        public bool Active { get { return _active; } }

        public bool Begin(Ped player, Ped shark, ControlSession control, long now, Action<string> log)
        {
            Abort("перезапуск");
            _player = player;
            _shark = shark;
            _control = control;
            _log = log ?? delegate { };
            _active = true;
            _snapshot = _attached = _chewing = _victimSeekPending = false;
            _chews = _victimRetries = 0;
            _nextVictimRetry = now;
            _started = now;
            _soundErrorLogged = false;
            _grip = (Grip)Random.Next(3);
            _targetChews = Random.Next(2, 5);
            _gripYaw = Random.Next(170, 186) * (Random.Next(2) == 0 ? -1f : 1f);
            _victimSpeed = _grip == Grip.Upper ? 1f : 3f;
            try
            {
                if (!Usable(_player) || !Usable(_shark) || control == null || !control.Valid)
                    throw new InvalidOperationException("цель или управление недоступны");
                _headBone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, _shark.Handle, 31086);
                if (_headBone <= 0) throw new InvalidOperationException("нет кости головы акулы");
                _invincible = _player.IsInvincible;
                _canRagdoll = Function.Call<bool>(Hash.CAN_PED_RAGDOLL, _player.Handle);
                _snapshot = true;
                _player.IsPositionFrozen = false;
                _player.IsInvincible = true;
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, false);
                NoCollision();
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, _player.Handle);
                Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
                _shark.BlockPermanentEvents = true;
                _shark.Velocity = Vector3.Zero;
                StartAttack(GrabStart, 8f, true, now);
                SetPhase(Phase.Grab, now);
                _log("Maneater bite: grip=" + _grip + ", chews=" + _targetChews + ", head=" + _headBone);
                return true;
            }
            catch (Exception ex)
            {
                Abort("начало укуса: " + ex.Message);
                return false;
            }
        }

        public Result Update(long now, float dt)
        {
            if (!_active) return Result.Cancelled;
            try
            {
                if (!Usable(_player) || !Usable(_shark) || _control == null || !_control.Valid)
                    throw new InvalidOperationException("цель или управление потеряны");
                if (now - _started > 20000) throw new InvalidOperationException("тайм-аут укуса");
                _player.IsInvincible = true;
                NoCollision();
                DisableInput();
                if (_phase != Phase.Release)
                    Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, false);
                if (_attached) UpdateVictim(now, dt);

                float t;
                switch (_phase)
                {
                    case Phase.Grab:
                        if (!ReadAttack(now, out t)) break;
                        AttachVictim();
                        _attached = IsAttached();
                        if (!_attached) throw new InvalidOperationException("захват не удался");
                        PlayVictim(now);
                        PlaySound("bite");
                        SetPhase(Phase.Hold, now);
                        break;
                    case Phase.Hold:
                        if (_seekPending) break;
                        t = AttackTime();
                        if (t < 0f || t >= 0.30f)
                        {
                            if (_animationStarted >= 0)
                            {
                                StopAttack();
                                _animationStarted = -1;
                                _phaseStarted = now;
                            }
                            // Maneater waits here for another attack command. Supply it automatically.
                            if (now - _phaseStarted >= 650)
                            {
                                StartAttack(ChewStart, 8f, false, now);
                                SetPhase(Phase.ChewStart, now);
                            }
                        }
                        break;
                    case Phase.ChewStart:
                        if (!ReadAttack(now, out t)) break;
                        if (t >= ChewLoopStart)
                        {
                            _chewing = true;
                            PlayVictim(now);
                            SetPhase(Phase.Chew, now);
                        }
                        break;
                    case Phase.Chew:
                        if (!ReadAttack(now, out t)) break;
                        if (t >= ChewLoopEnd)
                        {
                            if (_chews < _targetChews)
                            {
                                _chews++;
                                PlaySound("bite");
                                SeekAttack(ChewLoopStart, 1.25f);
                                _log("Maneater chew " + _chews + "/" + _targetChews);
                            }
                            else
                            {
                                SeekAttack(ChewLoopStart, 1f);
                                SetPhase(Phase.FinishChew, now);
                            }
                        }
                        break;
                    case Phase.FinishChew:
                        if (!ReadAttack(now, out t)) break;
                        if (t >= 0.70f)
                        {
                            // Living victims in Maneater go through its separate spit/throw stage.
                            StopAttack();
                            StartAttack(ThrowStart, 4f, true, now);
                            SetPhase(Phase.Throw, now);
                        }
                        break;
                    case Phase.Throw:
                        if (!ReadAttack(now, out t)) break;
                        if (t >= ThrowRelease)
                        {
                            ReleaseVictim();
                            SetPhase(Phase.Release, now);
                        }
                        break;
                    case Phase.Release:
                        t = AttackTime();
                        if (t < 0f || t >= ThrowEnd) StopAttack();
                        if (now - _phaseStarted >= 1300)
                        {
                            Cleanup(false);
                            _log("Maneater bite completed");
                            return Result.Completed;
                        }
                        break;
                }
                return Result.Running;
            }
            catch (Exception ex)
            {
                Abort("обновление укуса: " + ex.Message);
                return Result.Cancelled;
            }
        }

        private void StartAttack(float start, float blend, bool lockY, long now)
        {
            _attackBlend = blend;
            _attackLockY = lockY;
            _sharkRetries = 0;
            _nextSharkRetry = now + 250;
            _seekTime = start;
            PlayAttack();
            // Supply the original start phase to the task itself. Seeking on the next
            // tick cannot recover a non-looping task already started at its end (1.0).
            _seekPending = true;
            _animationStarted = now;
            PlaySound("move");
        }

        private void PlayAttack()
        {
            Function.Call(Hash.TASK_PLAY_ANIM, _shark.Handle, SharkDict, Attack,
                _attackBlend, -1.5f, -1, SharkFlags, _seekTime, false, _attackLockY, false);
        }

        private bool ReadAttack(long now, out float t)
        {
            t = AttackTime();
            if (_seekPending)
            {
                if (t >= 0f && now > _animationStarted)
                {
                    SeekAttack(_seekTime, 1f);
                    _seekPending = false;
                    t = _seekTime;
                    return true;
                }
                if (now - _animationStarted > 3500)
                    throw new InvalidOperationException("анимация акулы не запустилась: " + _phase);
                if (now >= _nextSharkRetry)
                {
                    _nextSharkRetry = now + 250;
                    if (++_sharkRetries == 4)
                        Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, _shark.Handle);
                    PlayAttack();
                }
                return false;
            }
            if (t < 0f) throw new InvalidOperationException("анимация акулы прервана: " + _phase);
            return true;
        }

        private float AttackTime()
        {
            if (!Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, _shark.Handle, SharkDict, Attack, 3)) return -1f;
            return Function.Call<float>(Hash.GET_ENTITY_ANIM_CURRENT_TIME, _shark.Handle, SharkDict, Attack);
        }

        private void SeekAttack(float time, float speed)
        {
            Function.Call(Hash.SET_ENTITY_ANIM_CURRENT_TIME, _shark.Handle, SharkDict, Attack, time);
            Function.Call(Hash.SET_ENTITY_ANIM_SPEED, _shark.Handle, SharkDict, Attack, speed);
        }

        private void StopAttack()
        {
            Function.Call(Hash.STOP_ANIM_TASK, _shark.Handle, SharkDict, Attack, 2f);
        }

        private void AttachVictim()
        {
            Vector3 offset = new Vector3(0.39f, 0.05f, -0.07f);
            Vector3 rotation = new Vector3(90f, 0f, _gripYaw);
            if (_chewing)
            {
                // Original ingest grip: move the body through the mouth with each completed chew.
                float progress = (float)_chews / _targetChews;
                offset = new Vector3(-0.7f * progress - 0.08f, -0.04f, 0.07f * progress - 0.09f);
                rotation = new Vector3(90f, 10f, 0f);
            }
            else if (_grip == Grip.Upper)
            {
                offset += new Vector3(0.4f, -0.1f, -0.28f);
                rotation = new Vector3(110f, 180f, -90f);
            }
            else if (_grip == Grip.Lower) rotation = new Vector3(110f, 0f, 90f);
            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, _player.Handle, _shark.Handle, _headBone,
                offset.X, offset.Y, offset.Z, rotation.X, rotation.Y, rotation.Z,
                false, false, false, true, 0, true);
        }

        private void PlayVictim(long now)
        {
            Function.Call(Hash.TASK_PLAY_ANIM, _player.Handle,
                _chewing ? ChewDict : VictimDict, _chewing ? ChewAnim : VictimAnim,
                _chewing ? 8f : 3f, -1.5f, -1, 9,
                _chewing ? 0.66144305f : 0f, false, false, false);
            _victimSeekPending = _chewing;
            _nextVictimRetry = now + 250;
        }

        private void UpdateVictim(long now, float dt)
        {
            string dict = _chewing ? ChewDict : VictimDict;
            string anim = _chewing ? ChewAnim : VictimAnim;
            if (Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, _player.Handle, dict, anim, 3))
            {
                _victimRetries = 0;
                if (_victimSeekPending)
                {
                    Function.Call(Hash.SET_ENTITY_ANIM_CURRENT_TIME, _player.Handle, dict, anim, 0.66144305f);
                    _victimSeekPending = false;
                }
                if (!_chewing)
                {
                    float target = _grip == Grip.Upper ? 0.5f : 1.2f;
                    _victimSpeed += (target - _victimSpeed) * (1f - (float)Math.Pow(0.995, dt * 60f));
                    Function.Call(Hash.SET_ENTITY_ANIM_SPEED, _player.Handle, dict, anim, _victimSpeed);
                }
                AttachVictim();
            }
            else if (now >= _nextVictimRetry)
            {
                if (++_victimRetries > 10) throw new InvalidOperationException("анимация жертвы не запустилась");
                // A swimming/get-up task may prevent the full-body animation while attached.
                if (_victimRetries == 4)
                {
                    _player.Detach();
                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, _player.Handle);
                }
                AttachVictim();
                PlayVictim(now);
            }
            if (!IsAttached()) AttachVictim();
        }

        private void ReleaseVictim()
        {
            PlaySound("split");
            StopVictim();
            _player.Detach();
            _attached = false;
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, true);
            Function.Call(Hash.SET_PED_TO_RAGDOLL, _player.Handle, 1200, 1200, 0, false, false, false);
            // Same rightward throw as Maneater's living-victim path; no damage/deletion.
            double heading = _shark.Heading * Math.PI / 180.0;
            _player.Velocity = new Vector3((float)Math.Cos(heading), (float)Math.Sin(heading), 0f) * 10f;
        }

        private void StopVictim()
        {
            Function.Call(Hash.STOP_ANIM_TASK, _player.Handle, VictimDict, VictimAnim, 1.25f);
            Function.Call(Hash.STOP_ANIM_TASK, _player.Handle, ChewDict, ChewAnim, 2f);
        }

        private void NoCollision()
        {
            Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _player.Handle, _shark.Handle, true);
            Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _shark.Handle, _player.Handle, true);
        }

        private static readonly int[] DisabledControls = { 21, 22, 24, 25, 30, 31, 44, 75, 140, 141, 142 };
        private static void DisableInput()
        {
            foreach (int control in DisabledControls) Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, control, true);
        }

        public void Abort(string reason)
        {
            if (!_active) return;
            Cleanup(true);
            _log("Maneater bite cancelled: " + reason);
        }

        private void Cleanup(bool cancelled)
        {
            if (_sound != null)
            {
                Try(delegate { _sound.Stop(); _sound.Dispose(); });
                _sound = null;
            }
            // Restore each setting independently: one failed native must not leave control locked.
            if (_player != null && _player.Exists())
            {
                Try(delegate { StopVictim(); });
                Try(delegate { if (IsAttached()) _player.Detach(); });
                if (_snapshot)
                {
                    Try(delegate { Function.Call(Hash.SET_PED_CAN_RAGDOLL, _player.Handle, _canRagdoll); });
                    Try(delegate { _player.IsInvincible = _invincible; });
                }
                if (cancelled) Try(delegate { Function.Call(Hash.CLEAR_PED_TASKS, _player.Handle); });
            }
            if (_shark != null && _shark.Exists())
            {
                Try(delegate { StopAttack(); });
                Try(delegate { _shark.Velocity = Vector3.Zero; });
            }
            Try(delegate { if (_control != null) _control.Dispose(); });
            _active = _attached = _snapshot = false;
            _phase = Phase.Idle;
            _player = _shark = null;
            _control = null;
        }

        private void Try(Action action)
        {
            try { action(); }
            catch (Exception ex) { _log("Bite cleanup: " + ex.Message); }
        }

        private void PlaySound(string kind)
        {
            try
            {
                bool underwater = Function.Call<bool>(Hash.IS_PED_SWIMMING_UNDER_WATER, _player.Handle);
                int variant = Random.Next(1, kind == "split" ? 3 : 4);
                string name = kind == "bite"
                    ? (underwater ? "bite_" + variant + "_u_b.wav" : "bite_1.wav")
                    : kind + "_" + variant + (underwater ? "_u.wav" : ".wav");
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "ReloaderPlugins", "Plugins", "Misc", "Assets", "Wildlife", "Sounds", name);
                if (!File.Exists(path)) throw new FileNotFoundException("Нет звука акулы", path);
                if (_sound == null) _sound = new SoundPlayer();
                _sound.SoundLocation = path;
                // Load first so Play cannot fall back to the Windows error beep.
                _sound.Load();
                _sound.Play();
            }
            catch (Exception ex)
            {
                if (!_soundErrorLogged) _log("Shark sound: " + ex.Message);
                _soundErrorLogged = true;
            }
        }

        private bool IsAttached()
        {
            return _player != null && _player.Exists() && _shark != null && _shark.Exists() &&
                Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY, _player.Handle, _shark.Handle);
        }

        private void SetPhase(Phase phase, long now)
        {
            _phase = phase;
            _phaseStarted = now;
            _log("Maneater bite phase: " + phase);
        }

        private static bool Usable(Ped ped) { return ped != null && ped.Exists() && !ped.IsDead; }
    }
}
