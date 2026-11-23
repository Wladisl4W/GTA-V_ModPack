using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GTA;
using GTA.Math;
using GTA.Native;
using ModPack.Core;

namespace ModPack.Wildlife
{
    internal sealed class CrocodileHunter
    {
        private const string ModelName = "a_c_crocodile";
        private const string AudioBank = "DLC_CROCODILE/CROCODILE";
        private const string SoundSet = "dlc_crocodile_soundset";
        private const string MoveAnimDict = "creatures@crocodile@move";
        private const int MaxCrocodiles = 8;
        private const int Health = 400;
        private const float SpawnRadius = 5f;
        private const float MinSpawnDistance = 2f;
        private const float DespawnDistance = 320f;
        private const float WanderRadius = 10f;
        private const float AttackRange = 5f;
        private const float BiteRange = 2.1f;
        private const int HeadBoneId = 31086;
        private const float MouthRadius = 1.75f;
        private const float CarryForward = 0.68f;
        private const float CarryRight = 0.25f;
        private const float CarryUp = 0.19f;
        private const int BiteDamage = 20;
        private const long BiteIntervalMs = 2000;
        private readonly Model _model = new Model(ModelName);
        private readonly Random _random = new Random();
        private readonly List<Crocodile> _crocs = new List<Crocodile>();
        private RelationshipGroup _group;
        private bool _groupCreated;
        private bool _modelRequested;
        private bool _modelWarned;
        private bool _soundBankLoaded;
        private bool _soundBankWarned;
        private bool _biteAnimChecked;
        private string _biteClip;
        private long _animRequestedAt;
        private long _soundBankFirstTry;
        private long _soundBankLastNeeded;
        private long _nextSoundBankTry;
        private long _modelRequestedAt;
        private Vector3 _pendingPoint;
        private int _playerHandle;
        private readonly ControlSession _biteControl = new ControlSession();
        private Ped _heldPlayer;
        private Crocodile _holdingCrocodile;
        private Vector3 _pinPoint;
        private long _carryStartsAt;
        private bool _carryStarted;
        private long _nextCarryLift;
        private long _nextRagdollRefresh;
        private bool _originalCanRagdoll;
        private bool _invincibleAtHold;
        private bool _ragdollWarned;

        private sealed class Crocodile
        {
            public Ped Ped;
            public Blip Blip;
            public Vector3 Home;
            public bool Basking;
            public int State; // 0: home, 1: pursuing, 2: returning, 3: turning, 4: lunging, 5: recovering, 6: holding
            public long NextThink;
            public bool Dead;
            public int SoundId = -1;
            public long NextBellow;
            public long NextGrowl;
            public long NextSnarl;
            public int BellowLeft;
            public int LastHealth;
            public long PainCooldown;
            public long BiteCooldown;
            public long HissCooldown;
            public bool Warned;
            public bool DeathPlayed;
            public int LastState = -1;
            public long StateUntil;
            public long NextPursuitTask;
            public bool BiteLanded;
            public int HeadBoneIndex;
            public float ClosestMouthDistance;
            public string MissReason;
            public Vector3 CarryDestination;
            public long NextCarryTask;
            public long CarryUntil;
        }

        public bool Enabled { get; set; }

        public void Update(Ped player)
        {
            try
            {
                if (!Enabled || player == null || !player.Exists())
                {
                    Stop();
                    return;
                }
                if (_playerHandle != 0 && _playerHandle != player.Handle) Stop();
                _playerHandle = player.Handle;
                long now = NowMs();
                UpdateHold(now);
                if (!_model.IsValid || !_model.IsInCdImage)
                {
                    if (!_modelWarned)
                    {
                        Log("Модель a_c_crocodile не установлена");
                        _modelWarned = true;
                    }
                    return;
                }
                if (_crocs.Count == 0 && !_modelRequested) return;
                if (!_groupCreated)
                {
                    _group = World.AddRelationshipGroup("MODPACK_CROCODILE");
                    _group.SetRelationshipBetweenGroups(player.RelationshipGroup, Relationship.Hate, true);
                    _groupCreated = true;
                }

                UpdateCrocs(player, now);
                UpdateSoundBank(now);
                UpdateBiteAnimation(now);
                UpdatePendingModel(player, now);
            }
            catch (Exception ex)
            {
                Log("Ошибка: " + ex);
                Stop();
            }
        }

        public string RequestSpawn(Ped player)
        {
            if (!Enabled || player == null || !player.Exists() || player.IsDead)
                return "Крокодил выключен или персонаж недоступен";
            if (!_model.IsValid || !_model.IsInCdImage)
                return "Модель a_c_crocodile не установлена";
            if (_modelRequested) return "Крокодил уже загружается";
            if (LivingCount() >= MaxCrocodiles) return "Достигнут предел в 8 крокодилов";
            if (Function.Call<bool>(Hash.IS_PED_IN_ANY_VEHICLE, player.Handle, false) ||
                Function.Call<bool>(Hash.GET_MISSION_FLAG) ||
                Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player.Handle) != 0)
                return "Нельзя вызвать крокодила здесь";
            Vector3 point;
            if (!FindLand(player, out point)) return "Нет свободной суши в 2-5 м";
            _pendingPoint = point;
            Function.Call(Hash.REQUEST_MODEL, _model.Hash);
            _modelRequested = true;
            _modelRequestedAt = NowMs();
            Log("Ручной вызов крокодила: " + point);
            return null;
        }

        private bool FindLand(Ped player, out Vector3 point)
        {
            point = Vector3.Zero;
            for (int i = 0; i < 16; i++)
            {
                double angle = _random.NextDouble() * Math.PI * 2;
                float radius = MinSpawnDistance + (float)_random.NextDouble() * (SpawnRadius - MinSpawnDistance);
                float x = player.Position.X + (float)Math.Cos(angle) * radius;
                float y = player.Position.Y + (float)Math.Sin(angle) * radius;
                float z;
                if (!World.GetGroundHeight(new Vector3(x, y, player.Position.Z + 80f), out z) ||
                    Math.Abs(z - player.Position.Z) > 2f) continue;
                if (WaterAt(x, y, z) || !GroundIsLevel(x, y, z)) continue;
                Vector3 candidate = new Vector3(x, y, z + 0.05f);
                bool blocked = false;
                foreach (Ped nearby in World.GetNearbyPeds(candidate, 1.5f))
                    if (nearby != null && nearby.Exists() && nearby.Handle != player.Handle) blocked = true;
                if (blocked || World.GetNearbyVehicles(candidate, 2f).Length > 0) continue;
                bool occupied = false;
                foreach (Crocodile c in _crocs)
                {
                    if (!c.Dead && c.Home.DistanceTo(candidate) < 3f)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied) continue;
                point = candidate;
                return true;
            }
            return false;
        }

        private static bool GroundIsLevel(float x, float y, float z)
        {
            const float r = 1.5f;
            Vector3[] samples = {
                new Vector3(x + r, y, z + 50f), new Vector3(x - r, y, z + 50f),
                new Vector3(x, y + r, z + 50f), new Vector3(x, y - r, z + 50f)
            };
            foreach (Vector3 sample in samples)
            {
                float height;
                if (!World.GetGroundHeight(sample, out height) || Math.Abs(height - z) > 0.8f)
                    return false;
            }
            return true;
        }

        private static bool WaterAt(float x, float y, float z)
        {
            var hit = new OutputArgument();
            return Function.Call<bool>(Hash.TEST_PROBE_AGAINST_WATER,
                x, y, z + 20f, x, y, z - 2f, hit);
        }

        private void UpdatePendingModel(Ped player, long now)
        {
            if (!_modelRequested) return;
            if (Function.Call<bool>(Hash.HAS_MODEL_LOADED, _model.Hash))
            {
                try
                {
                    float dx = _pendingPoint.X - player.Position.X;
                    float dy = _pendingPoint.Y - player.Position.Y;
                    float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                    if ((distance < MinSpawnDistance || distance > SpawnRadius) &&
                        !FindLand(player, out _pendingPoint))
                    {
                        Log("Ручной вызов отменён: рядом больше нет свободной суши");
                        return;
                    }
                    if (Enabled && !player.IsDead && !Game.IsPaused &&
                        ScriptCameraDirector.RenderingCam == null &&
                        !WaterAt(_pendingPoint.X, _pendingPoint.Y, _pendingPoint.Z) &&
                        !Function.Call<bool>(Hash.GET_MISSION_FLAG) &&
                        Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player.Handle) == 0)
                        Spawn(player, now);
                }
                finally { ReleaseModel(); }
            }
            else if (now - _modelRequestedAt >= 5000)
            {
                Log("Модель крокодила не загрузилась за 5 секунд");
                ReleaseModel();
            }
        }

        private void Spawn(Ped player, long now)
        {
            Ped ped = World.CreatePed(_model, _pendingPoint, (float)(_random.NextDouble() * 360));
            if (ped == null || !ped.Exists())
            {
                Log("Создание крокодила не удалось");
                return;
            }
            ped.IsPersistent = true;
            ped.MaxHealth = Health;
            ped.Health = Health;
            ped.BlockPermanentEvents = true;
            ped.RelationshipGroup = _group;
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 46, true);
            Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped.Handle, 0, false);
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, ped.Handle, 3);
            Function.Call(Hash.SET_PED_COMBAT_RANGE, ped.Handle, 0);
            Function.Call(Hash.SET_PED_PATH_MAY_ENTER_WATER, ped.Handle, false);
            var c = new Crocodile
            {
                Ped = ped,
                HeadBoneIndex = Function.Call<int>(Hash.GET_PED_BONE_INDEX, ped.Handle, HeadBoneId),
                Home = _pendingPoint,
                Basking = _random.NextDouble() < 0.5,
                NextBellow = now + _random.Next(8000, 30000),
                NextGrowl = now + _random.Next(20000, 50000),
                LastHealth = ped.Health
            };
            _crocs.Add(c);
            c.Blip = ped.AddBlip();
            if (c.Blip != null)
            {
                c.Blip.Sprite = (BlipSprite)1;
                c.Blip.Color = (BlipColor)1;
                c.Blip.Scale = 0.5f;
                c.Blip.IsShortRange = true;
                c.Blip.Name = "Crocodile";
            }
            BeginPursuit(c, player, now);
            Log("Точка пасти: " + (c.HeadBoneIndex > 0 ? "кость головы " + c.HeadBoneIndex : "смещение модели"));
            Log("Крокодил создан вручную; живых " + LivingCount());
        }

        private int LivingCount()
        {
            int count = 0;
            foreach (Crocodile c in _crocs) if (!c.Dead) count++;
            return count;
        }

        private void GoHome(Crocodile c, long now, bool atHome)
        {
            c.State = 0;
            c.NextThink = now + _random.Next(40000, 120000);
            Vector3 position = c.Ped.Position;
            float dx = position.X - c.Home.X;
            float dy = position.Y - c.Home.Y;
            if (!atHome && dx * dx + dy * dy > WanderRadius * WanderRadius)
            {
                c.State = 2;
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, c.Ped.Handle,
                    c.Home.X, c.Home.Y, c.Home.Z, 1f, -1, 2f, 0, 0f);
            }
            else if (c.Basking)
                Function.Call(Hash.TASK_STAND_STILL, c.Ped.Handle, -1);
            else
                Function.Call(Hash.TASK_WANDER_IN_AREA, c.Ped.Handle,
                    c.Home.X, c.Home.Y, c.Home.Z, WanderRadius, 2f, 8f);
        }

        private void UpdateCrocs(Ped player, long now)
        {
            for (int i = _crocs.Count - 1; i >= 0; i--)
            {
                Crocodile c = _crocs[i];
                if (c.Ped == null || !c.Ped.Exists())
                {
                    if (ReferenceEquals(c, _holdingCrocodile)) ReleaseHold(false);
                    Remove(c, false);
                    _crocs.RemoveAt(i);
                    continue;
                }
                float distance = c.Ped.Position.DistanceTo(player.Position);
                if (c.Ped.IsDead)
                {
                    if (ReferenceEquals(c, _holdingCrocodile)) ReleaseHold(false);
                    if (!c.Dead)
                    {
                        c.Dead = true;
                        DeleteBlip(c);
                    }
                    UpdateVoice(c, player, distance, now);
                    if (distance > 120f && !c.Ped.IsOnScreen)
                    {
                        Remove(c, false);
                        _crocs.RemoveAt(i);
                    }
                    continue;
                }
                if (distance > DespawnDistance && !c.Ped.IsOnScreen)
                {
                    if (ReferenceEquals(c, _holdingCrocodile)) ReleaseHold(false);
                    Remove(c, true);
                    _crocs.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(c, _holdingCrocodile))
                {
                    UpdateVoice(c, player, distance, now);
                    continue;
                }
                bool damaged = c.Ped.HasBeenDamagedBy(player) ||
                    Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ANY_PED, c.Ped.Handle);
                if (c.State == 0 || c.State == 2)
                {
                    if (CanAttack(player) && (damaged || distance < AttackRange))
                    {
                        Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, c.Ped.Handle);
                        BeginPursuit(c, player, now);
                        Log("Крокодил перешёл в атаку с " +
                            distance.ToString("0.0", CultureInfo.InvariantCulture) + " м");
                    }
                    else if (c.State == 2)
                    {
                        Vector3 pos = c.Ped.Position;
                        float dx = pos.X - c.Home.X;
                        float dy = pos.Y - c.Home.Y;
                        if (dx * dx + dy * dy < 9f) GoHome(c, now, true);
                    }
                    else if (now >= c.NextThink && distance > 15f)
                    {
                        c.Basking = !c.Basking;
                        GoHome(c, now, false);
                    }
                }
                else if (!CanAttack(player) || distance > 30f)
                {
                    Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, c.Ped.Handle);
                    Function.Call(Hash.CLEAR_PED_TASKS, c.Ped.Handle);
                    GoHome(c, now, false);
                }
                else if (c.State == 1)
                {
                    if (distance <= BiteRange + 0.6f && now >= c.BiteCooldown &&
                        Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                            c.Ped.Handle, player.Handle, 17))
                    {
                        c.State = 3;
                        c.StateUntil = now + 300;
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY,
                            c.Ped.Handle, player.Handle, 300);
                    }
                    else if (now >= c.NextPursuitTask &&
                        Function.Call<bool>(Hash.IS_PED_FLEEING, c.Ped.Handle))
                    {
                        BeginPursuit(c, player, now);
                        Log("Крокодил сбросил бегство и продолжил преследование");
                    }
                }
                else if (c.State == 3 && now >= c.StateUntil)
                {
                    c.State = 4;
                    c.StateUntil = now + 650;
                    c.BiteLanded = false;
                    c.ClosestMouthDistance = float.MaxValue;
                    c.MissReason = "неизвестно";
                    Vector3 target = player.Position;
                    if (_biteClip != null)
                        Function.Call(Hash.TASK_PLAY_ANIM, c.Ped.Handle, MoveAnimDict,
                            _biteClip, 8f, -8f, 650, 0, 0f, false, false, false);
                    else
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, c.Ped.Handle,
                            target.X, target.Y, target.Z, 3f, 650, c.Ped.Heading, 0f);
                    Log("Крокодил сделал выпад" + (_biteClip == null ? " (движение модели)" : " (клип " + _biteClip + ")"));
                }
                else if (c.State == 4)
                {
                    Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, c.Ped.Handle, player.Handle, true);
                    Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, player.Handle, c.Ped.Handle, true);
                    string reason = "нет контакта";
                    if (!c.BiteLanded && now <= c.StateUntil &&
                        BiteContact(c, player, out reason) && TryHold(c, player, now))
                    {
                        if (!player.IsInvincible)
                            Function.Call(Hash.APPLY_DAMAGE_TO_PED, player.Handle, BiteDamage, true);
                        PlayShot(c, "bite");
                        c.BiteLanded = true;
                        c.BiteCooldown = now + BiteIntervalMs;
                        Log(player.IsInvincible
                            ? "Крокодил укусил игрока, но тот неуязвим"
                            : "Крокодил укусил игрока: " + BiteDamage + " урона");
                    }
                    else if (!c.BiteLanded && now <= c.StateUntil)
                        c.MissReason = reason;
                    if (now >= c.StateUntil)
                    {
                        c.State = 5;
                        c.BiteCooldown = Math.Max(c.BiteCooldown, now + BiteIntervalMs);
                        c.StateUntil = c.BiteCooldown;
                        Function.Call(Hash.TASK_STAND_STILL, c.Ped.Handle, 300);
                        if (!c.BiteLanded)
                            Log("Выпад без контакта: " + c.MissReason + "; ближайшая пасть " +
                                c.ClosestMouthDistance.ToString("0.00", CultureInfo.InvariantCulture) + " м");
                    }
                }
                else if (c.State == 5 && now >= c.StateUntil)
                {
                    BeginPursuit(c, player, now);
                }
                UpdateVoice(c, player, distance, now);
            }
        }

        private static bool CanAttack(Ped player)
        {
            return !player.IsDead && !player.IsInVehicle() &&
                !Function.Call<bool>(Hash.IS_PED_SWIMMING, player.Handle) &&
                !Function.Call<bool>(Hash.IS_PED_SWIMMING_UNDER_WATER, player.Handle);
        }

        private static bool BiteContact(Crocodile c, Ped player, out string reason)
        {
            reason = "нет контакта";
            if (!CanAttack(player)) { reason = "цель недоступна"; return false; }
            Vector3 mouth = MouthPosition(c);
            Vector3 playerPos = player.Position;
            float dx = mouth.X - playerPos.X;
            float dy = mouth.Y - playerPos.Y;
            float dz = mouth.Z - Math.Max(playerPos.Z + 0.25f,
                Math.Min(mouth.Z, playerPos.Z + 1.55f));
            float gap = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            c.ClosestMouthDistance = Math.Min(c.ClosestMouthDistance, gap);
            if (gap > MouthRadius) { reason = "пасть не достигла персонажа"; return false; }
            Vector3 forward = Function.Call<Vector3>(Hash.GET_ENTITY_FORWARD_VECTOR, c.Ped.Handle);
            Vector3 fromCroc = playerPos - c.Ped.Position;
            if (forward.X * fromCroc.X + forward.Y * fromCroc.Y < 0.1f)
            { reason = "персонаж позади крокодила"; return false; }
            if (!Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY,
                c.Ped.Handle, player.Handle, 17))
            { reason = "препятствие между крокодилом и персонажем"; return false; }
            return true;
        }

        private static Vector3 MouthPosition(Crocodile c)
        {
            return c.HeadBoneIndex > 0
                ? Function.Call<Vector3>(Hash.GET_PED_BONE_COORDS, c.Ped.Handle,
                    HeadBoneId, 0f, 0.35f, -0.12f)
                : Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS,
                    c.Ped.Handle, 0f, 1.4f, 0.65f);
        }

        private static Vector3 CarryPosition(Crocodile c)
        {
            Vector3 mouth = MouthPosition(c);
            Vector3 forward = Function.Call<Vector3>(Hash.GET_ENTITY_FORWARD_VECTOR, c.Ped.Handle);
            Vector3 right = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS,
                c.Ped.Handle, CarryRight, 0f, 0f) - c.Ped.Position;
            return new Vector3(mouth.X + forward.X * CarryForward + right.X,
                mouth.Y + forward.Y * CarryForward + right.Y, mouth.Z + CarryUp);
        }

        private bool TryHold(Crocodile c, Ped player, long now)
        {
            if (_heldPlayer != null || !_biteControl.Acquire("Крокодил кусает", false)) return false;
            try
            {
                _heldPlayer = player;
                _holdingCrocodile = c;
                _nextRagdollRefresh = now + 1000;
                _carryStartsAt = now + 600;
                _carryStarted = false;
                _nextCarryLift = now + 600;
                _ragdollWarned = false;
                _originalCanRagdoll = Function.Call<bool>(Hash.CAN_PED_RAGDOLL, player.Handle);
                _invincibleAtHold = player.IsInvincible;
                if (_invincibleAtHold)
                    Function.Call(Hash.SET_PLAYER_INVINCIBLE_BUT_HAS_REACTIONS,
                        Game.Player.Handle, true);
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, player.Handle, true);
                Function.Call(Hash.CLEAR_PED_TASKS, player.Handle);
                _pinPoint = CarryPosition(c);
                c.State = 6;
                c.CarryUntil = 0;
                Function.Call(Hash.TASK_STAND_STILL, c.Ped.Handle, 600);
                bool ragdollStarted = Function.Call<bool>(Hash.SET_PED_TO_RAGDOLL,
                    player.Handle, 30000, 30000, 0, false, false, false);
                Log("Крокодил прижал персонажа к земле под пастью; ragdoll=" + ragdollStarted +
                    ", invincible=" + _invincibleAtHold);
                return true;
            }
            catch (Exception ex)
            {
                Log("Захват крокодилом не удался: " + ex.Message);
                ReleaseHold(false);
                return false;
            }
        }

        private void UpdateHold(long now)
        {
            if (_heldPlayer == null) return;
            if (!_biteControl.Valid || _holdingCrocodile == null ||
                _holdingCrocodile.Ped == null || !_holdingCrocodile.Ped.Exists() ||
                _holdingCrocodile.Ped.IsDead)
            {
                ReleaseHold(false);
                return;
            }
            if (!_carryStarted && now >= _carryStartsAt)
            {
                StartCarryRun(_holdingCrocodile, now);
                _carryStarted = true;
            }
            else if (_carryStarted)
                UpdateCarryRun(_holdingCrocodile, now);
            _pinPoint = CarryPosition(_holdingCrocodile);
            bool isRagdoll = Function.Call<bool>(Hash.IS_PED_RAGDOLL, _heldPlayer.Handle);
            if (isRagdoll)
                Function.Call(Hash.RESET_PED_RAGDOLL_TIMER, _heldPlayer.Handle);
            if (isRagdoll)
            {
                Vector3 chest = Function.Call<Vector3>(Hash.GET_PED_BONE_COORDS,
                    _heldPlayer.Handle, 24816, 0f, 0f, 0f);
                Vector3 offset = _pinPoint - chest;
                float gap = (float)Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
                bool relocated = false;
                if (_carryStarted && gap > 6f)
                {
                    Vector3 position = _heldPlayer.Position;
                    float ground;
                    if (World.GetGroundHeight(new Vector3(_pinPoint.X, _pinPoint.Y,
                        _pinPoint.Z + 5f), out ground) &&
                        !WaterAt(_pinPoint.X, _pinPoint.Y, ground))
                    {
                        Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, _heldPlayer.Handle,
                            position.X + offset.X, position.Y + offset.Y,
                            ground + 0.15f,
                            false, false, true);
                        relocated = true;
                    }
                }
                if (!relocated)
                {
                    Vector3 velocity = _holdingCrocodile.Ped.Velocity;
                    float vx = velocity.X + offset.X * 3f;
                    float vy = velocity.Y + offset.Y * 3f;
                    float speed = (float)Math.Sqrt(vx * vx + vy * vy);
                    if (speed > 5f) { vx *= 5f / speed; vy *= 5f / speed; }
                    float vz = _heldPlayer.Velocity.Z;
                    if (now >= _nextCarryLift && offset.Z > 0.15f)
                    {
                        float ground;
                        if (World.GetGroundHeight(new Vector3(_heldPlayer.Position.X,
                            _heldPlayer.Position.Y, _heldPlayer.Position.Z + 5f), out ground) &&
                            _heldPlayer.Position.Z < ground + 0.45f)
                            vz = Math.Max(vz, 1f);
                        _nextCarryLift = now + 250;
                    }
                    Function.Call(Hash.SET_ENTITY_VELOCITY, _heldPlayer.Handle,
                        vx, vy, vz);
                }
            }
            if (now >= _nextRagdollRefresh && !isRagdoll)
            {
                bool started = Function.Call<bool>(Hash.SET_PED_TO_RAGDOLL,
                    _heldPlayer.Handle, 30000, 30000, 0, false, false, false);
                _nextRagdollRefresh = now + 1000;
                if (!started && !_ragdollWarned)
                {
                    _ragdollWarned = true;
                    Log("Ragdoll при удержании не запустился");
                }
            }
            Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY,
                _heldPlayer.Handle, _holdingCrocodile.Ped.Handle, true);
            Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY,
                _holdingCrocodile.Ped.Handle, _heldPlayer.Handle, true);
        }

        private void ReleaseHold(bool ragdoll)
        {
            Ped player = _heldPlayer;
            Crocodile crocodile = _holdingCrocodile;
            _heldPlayer = null;
            _holdingCrocodile = null;
            try
            {
                if (player != null && player.Exists())
                {
                    if (crocodile != null &&
                        ((crocodile.Ped != null && crocodile.Ped.Exists() &&
                          Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY,
                              player.Handle, crocodile.Ped.Handle)) ||
                         (crocodile.Ped == null || !crocodile.Ped.Exists()) &&
                          Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, player.Handle)))
                        Function.Call(Hash.DETACH_ENTITY, player.Handle, true, true);
                    if (_invincibleAtHold && Game.Player.Character != null &&
                        Game.Player.Character.Handle == player.Handle)
                        Function.Call(Hash.SET_PLAYER_INVINCIBLE_BUT_HAS_REACTIONS,
                            Game.Player.Handle, false);
                    Function.Call(Hash.SET_PED_CAN_RAGDOLL, player.Handle, _originalCanRagdoll);
                    if (ragdoll && _originalCanRagdoll && !player.IsDead)
                        Function.Call(Hash.SET_PED_TO_RAGDOLL, player.Handle,
                            1000, 1200, 0, false, false, false);
                }
            }
            catch (Exception ex) { Log("Освобождение из пасти: " + ex.Message); }
            finally
            {
                _invincibleAtHold = false;
                _biteControl.Dispose();
            }
        }

        private static void BeginPursuit(Crocodile c, Ped player, long now)
        {
            c.State = 1;
            c.NextPursuitTask = now + 2000;
            Function.Call(Hash.TASK_GO_TO_ENTITY, c.Ped.Handle, player.Handle,
                -1, 1.5f, 2.5f, 0f, 0);
        }

        private void StartCarryRun(Crocodile c, long now)
        {
            Vector3 origin = c.Ped.Position;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double angle = _random.NextDouble() * Math.PI * 2;
                float distance = 15f + (float)_random.NextDouble() * 15f;
                float x = origin.X + (float)Math.Cos(angle) * distance;
                float y = origin.Y + (float)Math.Sin(angle) * distance;
                float ground;
                if (!World.GetGroundHeight(new Vector3(x, y, origin.Z + 20f), out ground) ||
                    Math.Abs(ground - origin.Z) > 5f || WaterAt(x, y, ground)) continue;
                c.CarryDestination = new Vector3(x, y, ground);
                c.CarryUntil = now + 10000;
                c.NextCarryTask = now + 2500;
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, c.Ped.Handle,
                    x, y, ground, 3.5f, -1, 1.5f, 0, 0f);
                Log("Крокодил уносит персонажа");
                return;
            }
            c.CarryUntil = now + 2000;
        }

        private void UpdateCarryRun(Crocodile c, long now)
        {
            Vector3 position = c.Ped.Position;
            float dx = position.X - c.CarryDestination.X;
            float dy = position.Y - c.CarryDestination.Y;
            if (now >= c.CarryUntil || dx * dx + dy * dy < 9f)
                StartCarryRun(c, now);
            else if (now >= c.NextCarryTask &&
                Function.Call<bool>(Hash.IS_PED_FLEEING, c.Ped.Handle))
            {
                c.NextCarryTask = now + 2500;
                Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, c.Ped.Handle,
                    c.CarryDestination.X, c.CarryDestination.Y,
                    c.CarryDestination.Z, 3.5f, -1, 1.5f, 0, 0f);
            }
        }

        private void UpdateSoundBank(long now)
        {
            if (_crocs.Count > 0) _soundBankLastNeeded = now;
            if (!_soundBankLoaded && _crocs.Count > 0 && now >= _nextSoundBankTry)
            {
                if (_soundBankFirstTry == 0) _soundBankFirstTry = now;
                _soundBankLoaded = Function.Call<bool>(Hash.REQUEST_SCRIPT_AUDIO_BANK, AudioBank, false, -1);
                _nextSoundBankTry = now + 100;
                if (!_soundBankLoaded && !_soundBankWarned && now - _soundBankFirstTry > 15000)
                {
                    Log("Звуковой банк крокодила не загрузился; продолжаем без звуков");
                    _soundBankWarned = true;
                }
            }
            else if (_soundBankLoaded && _crocs.Count == 0 && now - _soundBankLastNeeded > 30000)
                ReleaseSoundBank();
        }

        private void UpdateBiteAnimation(long now)
        {
            if (_biteAnimChecked || _crocs.Count == 0) return;
            if (_animRequestedAt == 0)
            {
                _animRequestedAt = now;
                Function.Call(Hash.REQUEST_ANIM_DICT, MoveAnimDict);
            }
            if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, MoveAnimDict))
            {
                foreach (string clip in new[] { "attack", "bite", "attack_01" })
                {
                    if (Function.Call<float>(Hash.GET_ANIM_DURATION, MoveAnimDict, clip) > 0.1f)
                    {
                        _biteClip = clip;
                        break;
                    }
                }
                _biteAnimChecked = true;
                Log(_biteClip == null
                    ? "Подходящий клип укуса в проверенных именах не найден; используется выпад на движении модели"
                    : "Найден клип укуса: " + MoveAnimDict + "/" + _biteClip);
            }
            else if (now - _animRequestedAt > 5000)
            {
                _biteAnimChecked = true;
                Log("Словарь анимаций крокодила не загрузился; используется выпад на движении модели");
            }
        }

        private void UpdateVoice(Crocodile c, Ped player, float distance, long now)
        {
            if (c.Dead)
            {
                if (!c.DeathPlayed)
                {
                    c.DeathPlayed = true;
                    Play(c, "death", true);
                }
                return;
            }
            if (!_soundBankLoaded || distance > 150f)
            {
                c.LastHealth = c.Ped.Health;
                c.LastState = c.State;
                return;
            }
            int health = c.Ped.Health;
            if (health < c.LastHealth && now >= c.PainCooldown)
            {
                c.PainCooldown = now + 1500;
                Play(c, "pain", true);
            }
            c.LastHealth = health;
            if (c.State != c.LastState)
            {
                if (c.State == 1) Play(c, _random.NextDouble() < 0.5 ? "hiss" : "snarl", true);
                c.NextSnarl = now + _random.Next(2500, 5000);
                c.BellowLeft = 0;
                c.LastState = c.State;
            }
            if (c.State == 1 || c.State == 3 || c.State == 4)
            {
                if (now >= c.NextSnarl && !Busy(c))
                {
                    Play(c, _random.NextDouble() < 0.75 ? "snarl" : "hiss", false);
                    c.NextSnarl = now + _random.Next(3000, 6000);
                }
                return;
            }
            if (!player.IsInVehicle() && !player.IsDead && distance < 12f)
            {
                if (!c.Warned && now >= c.HissCooldown)
                {
                    c.Warned = true;
                    c.HissCooldown = now + 8000;
                    Play(c, "hiss", true);
                    c.BellowLeft = 0;
                }
                return;
            }
            if (distance > 18f) c.Warned = false;
            if (now >= c.NextBellow && !Busy(c))
            {
                if (c.BellowLeft == 0) c.BellowLeft = _random.Next(1, 4);
                if (Play(c, "bellow", false))
                {
                    c.BellowLeft--;
                    c.NextBellow = now + (c.BellowLeft > 0 ? _random.Next(4500, 7500) : _random.Next(60000, 180000));
                    c.NextGrowl = Math.Max(c.NextGrowl, now + 8000);
                }
            }
            else if (distance < 40f && now >= c.NextGrowl && !Busy(c))
            {
                if (Play(c, "growl", false)) c.NextGrowl = now + _random.Next(20000, 50000);
            }
        }

        private bool Play(Crocodile c, string sound, bool interrupt)
        {
            if (!_soundBankLoaded) return false;
            if (c.SoundId != -1)
            {
                if (!Function.Call<bool>(Hash.HAS_SOUND_FINISHED, c.SoundId))
                {
                    if (!interrupt) return false;
                    Function.Call(Hash.STOP_SOUND, c.SoundId);
                }
                Function.Call(Hash.RELEASE_SOUND_ID, c.SoundId);
            }
            c.SoundId = Function.Call<int>(Hash.GET_SOUND_ID);
            Function.Call(Hash.PLAY_SOUND_FROM_ENTITY, c.SoundId, sound,
                c.Ped.Handle, SoundSet, false, 0);
            return true;
        }

        private void PlayShot(Crocodile c, string sound)
        {
            if (_soundBankLoaded)
                Function.Call(Hash.PLAY_SOUND_FROM_ENTITY, -1, sound,
                    c.Ped.Handle, SoundSet, false, 0);
        }

        private static bool Busy(Crocodile c)
        {
            return c.SoundId != -1 && !Function.Call<bool>(Hash.HAS_SOUND_FINISHED, c.SoundId);
        }

        private static void StopVoice(Crocodile c)
        {
            if (c.SoundId == -1) return;
            Function.Call(Hash.STOP_SOUND, c.SoundId);
            Function.Call(Hash.RELEASE_SOUND_ID, c.SoundId);
            c.SoundId = -1;
        }

        private static void DeleteBlip(Crocodile c)
        {
            if (c.Blip != null)
            {
                if (c.Blip.Exists()) c.Blip.Delete();
                c.Blip = null;
            }
        }

        private static void Remove(Crocodile c, bool delete)
        {
            StopVoice(c);
            DeleteBlip(c);
            if (c.Ped != null && c.Ped.Exists())
            {
                if (delete) c.Ped.Delete();
                else c.Ped.MarkAsNoLongerNeeded();
            }
        }

        public void Stop()
        {
            ReleaseHold(false);
            ReleaseModel();
            foreach (Crocodile c in _crocs)
            {
                try { Remove(c, true); }
                catch (Exception ex) { Log("Не удалось убрать крокодила: " + ex.Message); }
            }
            _crocs.Clear();
            ReleaseSoundBank();
            if (_groupCreated)
            {
                try { _group.Remove(); }
                catch (Exception ex) { Log("Не удалось удалить группу: " + ex.Message); }
                _groupCreated = false;
            }
            _playerHandle = 0;
            if (_animRequestedAt != 0)
                Function.Call(Hash.REMOVE_ANIM_DICT, MoveAnimDict);
            _animRequestedAt = 0;
            _biteAnimChecked = false;
            _biteClip = null;
            _soundBankFirstTry = 0;
            _soundBankWarned = false;
            _modelWarned = false;
        }

        private void ReleaseModel()
        {
            if (!_modelRequested) return;
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, _model.Hash);
            _modelRequested = false;
        }

        private void ReleaseSoundBank()
        {
            if (!_soundBankLoaded) return;
            Function.Call(Hash.RELEASE_NAMED_SCRIPT_AUDIO_BANK, AudioBank);
            _soundBankLoaded = false;
        }

        private static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

        private static void Log(string message)
        {
            DevelopmentDiagnostics.Event("Crocodile", message);
            try
            {
                ModPack.Core.BoundedLog.Append(PluginStorage.FilePath("SharkRider", "SharkRider.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Crocodile: " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
