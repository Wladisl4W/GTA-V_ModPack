using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.IO;
using System.Windows.Forms;

namespace SharkRider
{
    /// <summary>
    /// Автономный мод-акула: без меню и кнопок.
    /// - Когда игрок в воде, рядом спавнится акула (tiger_shark) и подплывает к нему
    /// - При близком контакте игрок автоматически садится на спину акулы
    /// - WASD — плавание, Shift — всплытие, Ctrl — погружение
    /// - Выход на сушу автоматически отпускает акулу и удаляет её
    /// </summary>
    public class SharkRiderPlugin : IGtaPlugin
    {
        private enum State { Idle, Spawning, Approaching, Riding }

        private const string SharkModel = "tiger_shark";
        private const int PedType = 26; // PED_TYPE_CREATURE

        private const long CheckIntervalMs = 400;   // как часто проверять "в воде ли игрок"
        private const long AbandonTimeoutMs = 2500; // через сколько без воды отпустить акулу

        private const float SpawnDistance = 18f;    // дистанция спавна акулы от игрока
        private const float RideDistance = 3.0f;    // с какой дистанции игрок садится
        private const float SwimSpeed = 7.5f;       // скорость подплыва акулы к игроку
        private const float RideSpeed = 7.5f;       // скорость катания

        private static readonly Vector3 AttachOffset = new Vector3(0f, 0.4f, 0.9f); // крепление на спине
        private static readonly Vector3 CamOffset = new Vector3(0f, -5.5f, 2.6f);    // камера позади-сверху

        private State _state = State.Idle;
        private Ped _shark = null;
        private Camera _rideCam = null;

        private long _lastCheckMs = 0;
        private long _lastInWaterMs = 0;
        private long _lastDiagMs = 0;
        private long _spawnRequestMs = 0;
        private float _targetDepth = 0f;

        public void OnStart()
        {
            try
            {
                Log("Shark Rider загружен");
                GTA.UI.Notification.PostTicker("~b~Shark Rider~w~ активен~n~Войдите в воду — акула подплывёт сама", false, false);
            }
            catch (Exception ex)
            {
                Log("OnStart: " + ex.Message);
            }
        }

        public void OnTick()
        {
            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists()) return;

                bool inWater = IsPlayerInWater(player);

                switch (_state)
                {
                    case State.Idle:
                        long now = NowMs();
                        if (now - _lastDiagMs >= 5000)
                        {
                            _lastDiagMs = now;
                            Log("Idle: inWater=" + inWater + ", inVehicle=" + IsPedInVehicle(player) +
                                ", playerZ=" + player.Position.Z.ToString("F1"));
                        }
                        if (inWater && now - _lastCheckMs >= CheckIntervalMs)
                        {
                            _lastCheckMs = now;
                            if (!IsPedInVehicle(player))
                            {
                                Log("Игрок в воде — спавним акулу");
                                SpawnShark(player);
                                _state = State.Spawning;
                            }
                        }
                        break;

                    case State.Spawning:
                        UpdateSpawning(player);
                        break;

                    case State.Approaching:
                        UpdateApproaching(player, inWater);
                        break;

                    case State.Riding:
                        UpdateRiding(player, inWater);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("OnTick: " + ex.Message);
            }
        }

        public void OnKeyDown(Keys key)
        {
            // без кнопок — мод полностью автономный
        }

        public void OnAbort()
        {
            try
            {
                Log("Shark Rider выгружается");
                StopRiding(true);
            }
            catch (Exception ex)
            {
                Log("OnAbort: " + ex.Message);
            }
        }

        // === СПАВН ===

        private void SpawnShark(Ped player)
        {
            try
            {
                Vector3 spawnPos = ComputeSpawnPosition(player);
                if (IsInvalid(spawnPos))
                {
                    Log("SpawnShark: невалидная позиция спавна");
                    _state = State.Idle;
                    return;
                }

                _spawnRequestMs = NowMs();
                Function.Call(Hash.REQUEST_MODEL, Game.GenerateHash(SharkModel));
            }
            catch (Exception ex)
            {
                Log("SpawnShark: " + ex.Message);
                _state = State.Idle;
            }
        }

        private void UpdateSpawning(Ped player)
        {
            try
            {
                int modelHash = Game.GenerateHash(SharkModel);
                if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, modelHash))
                {
                    // Модель не загрузилась — пробуем снова и пишем в лог
                    if (NowMs() - _spawnRequestMs > 5000)
                    {
                        Log("Модель " + SharkModel + " не загрузилась за 5с (hash " + modelHash + "), повторный запрос");
                        _spawnRequestMs = NowMs();
                        Function.Call(Hash.REQUEST_MODEL, modelHash);
                    }
                    return;
                }

                if (!IsPlayerInWater(player))
                {
                    Log("UpdateSpawning: игрок вышел из воды, отмена спавна");
                    _state = State.Idle;
                    return;
                }

                Vector3 spawnPos = ComputeSpawnPosition(player);
                if (IsInvalid(spawnPos))
                {
                    Log("UpdateSpawning: невалидная позиция спавна");
                    _state = State.Idle;
                    return;
                }

                // Спавним рядом с игроком, чтобы акула точно была в зоне стриминга
                Vector3 playerPos = player.Position;
                if (spawnPos.DistanceTo(playerPos) > 25f)
                {
                    spawnPos = playerPos + new Vector3(2f, -2f, 0f);
                    spawnPos.Z = Clamp(playerPos.Z - 1.5f, playerPos.Z - 6f, playerPos.Z + 1f);
                }

                _shark = (Ped)Function.Call<Entity>(Hash.CREATE_PED, PedType, modelHash,
                    spawnPos.X, spawnPos.Y, spawnPos.Z, playerPos.ToHeading(), true, false);

                if (_shark == null || !_shark.Exists() || IsInvalid(_shark.Position))
                {
                    Log("Не удалось создать акулу");
                    DeleteShark();
                    _state = State.Idle;
                    return;
                }

                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _shark.Handle, true, true, true);
                Function.Call(Hash.SET_ENTITY_INVINCIBLE, _shark.Handle, true);
                Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, _shark.Handle, false);
                Function.Call(Hash.SET_PED_ALERTNESS, _shark.Handle, 0);
                Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
                _shark.IsPositionFrozen = false;
                _shark.Velocity = Vector3.Zero;

                _lastInWaterMs = NowMs();
                Log("Акула создана на " + spawnPos.ToString());
                _state = State.Approaching;
            }
            catch (Exception ex)
            {
                Log("UpdateSpawning: " + ex.Message);
                DeleteShark();
                _state = State.Idle;
            }
        }

        /// <summary>
        /// Считает безопасную точку спавна рядом с игроком (только в воде, без выхода за карту)
        /// </summary>
        private Vector3 ComputeSpawnPosition(Ped player)
        {
            Vector3 playerPos = player.Position;

            Vector3 dir = GetPlayerLookDirection(player);
            Vector3 spawnPos = playerPos + dir * SpawnDistance;

            float waterZ = GetWaterHeight(spawnPos);
            if (waterZ < -10f)
            {
                spawnPos = playerPos + dir * 8f;
                waterZ = GetWaterHeight(spawnPos);
            }
            if (waterZ < -10f)
            {
                spawnPos = playerPos;
                waterZ = playerPos.Z;
            }

            // Никогда не уходим глубоко под воду и не выходим за карту
            spawnPos.Z = Clamp(waterZ - 1.5f, playerPos.Z - 6f, playerPos.Z + 1f);
            spawnPos.X = Clamp(spawnPos.X, -4000f, 4000f);
            spawnPos.Y = Clamp(spawnPos.Y, -4000f, 4000f);
            return spawnPos;
        }

        // === ПОДПЛЫВ К ИГРОКУ ===

        private void UpdateApproaching(Ped player, bool inWater)
        {
            try
            {
                long now = NowMs();

                if (!inWater && now - _lastInWaterMs > AbandonTimeoutMs)
                {
                    Log("Игрок вышел из воды, акула удаляется");
                    DeleteShark();
                    _state = State.Idle;
                    return;
                }
                if (inWater) _lastInWaterMs = now;

                if (_shark == null || !_shark.Exists())
                {
                    DeleteShark();
                    _state = State.Idle;
                    return;
                }

                Vector3 playerPos = player.Position;
                Vector3 sharkPos = _shark.Position;

                if (IsInvalid(sharkPos))
                {
                    Log("UpdateApproaching: невалидная позиция акулы");
                    DeleteShark();
                    _state = State.Idle;
                    return;
                }

                float dist = playerPos.DistanceTo(sharkPos);
                if (dist < RideDistance && inWater)
                {
                    StartRiding(player);
                    return;
                }

                // Акула далеко (глюк стриминга) — пересоздаём рядом
                if (dist > 60f)
                {
                    Log("UpdateApproaching: акула слишком далеко (" + dist.ToString("F0") + "м), пересоздаём");
                    DeleteShark();
                    _state = State.Spawning;
                    return;
                }

                Vector3 toPlayer = playerPos - sharkPos;
                Vector3 toPlayerFlat = new Vector3(toPlayer.X, toPlayer.Y, 0f);
                if (toPlayerFlat.LengthSquared() > 0.01f)
                {
                    Vector3 dir = toPlayerFlat.Normalized;
                    _shark.Heading = dir.ToHeading();

                    float targetZ = Clamp(playerPos.Z + 0.5f, playerPos.Z - 4f, playerPos.Z + 3f);
                    Vector3 vel = new Vector3(
                        dir.X * SwimSpeed,
                        dir.Y * SwimSpeed,
                        Clamp((targetZ - sharkPos.Z) * 1.5f, -SwimSpeed, SwimSpeed));
                    _shark.Velocity = ClampSpeed(vel, 12f);
                }
            }
            catch (Exception ex)
            {
                Log("UpdateApproaching: " + ex.Message);
            }
        }

        // === КАТАНИЕ ===

        private void StartRiding(Ped player)
        {
            try
            {
                if (_shark == null || !_shark.Exists()) return;

                Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
                _shark.Heading = player.Heading;

                player.IsPositionFrozen = true;
                player.AttachTo(_shark, AttachOffset, new Vector3(0f, 0f, 0f));

                _targetDepth = _shark.Position.Z;

                if (_rideCam == null)
                {
                    _rideCam = World.CreateCamera(_shark.Position + CamOffset, new Vector3(0f, 0f, 0f), 60f);
                    _rideCam.AttachTo(_shark, CamOffset);
                    World.RenderingCamera = _rideCam;
                }

                Log("Игрок сел на акулу");
                GTA.UI.Notification.PostTicker("~b~WASD~w~ — плыть, ~b~Shift~w~ — вверх, ~b~Ctrl~w~ — вниз", false, false);
                _state = State.Riding;
            }
            catch (Exception ex)
            {
                Log("StartRiding: " + ex.Message);
                StopRiding(false);
                _state = State.Idle;
            }
        }

        private void UpdateRiding(Ped player, bool inWater)
        {
            try
            {
                long now = NowMs();

                bool sharkOk = _shark != null && _shark.Exists() && !_shark.IsDead;
                if (!inWater || !sharkOk)
                {
                    StopRiding(true);
                    _state = State.Idle;
                    return;
                }
                if (inWater) _lastInWaterMs = now;

                Vector3 sharkPos = _shark.Position;
                if (IsInvalid(sharkPos))
                {
                    Log("UpdateRiding: невалидная позиция акулы");
                    StopRiding(true);
                    _state = State.Idle;
                    return;
                }

                Vector3 fwd = (_rideCam != null) ? _rideCam.ForwardVector : new Vector3(1f, 0f, 0f);
                Vector3 right = (_rideCam != null) ? _rideCam.RightVector : new Vector3(0f, 1f, 0f);

                if (IsInvalid(fwd)) fwd = new Vector3(1f, 0f, 0f);
                if (IsInvalid(right)) right = new Vector3(0f, 1f, 0f);

                float fwdIn = (Game.IsKeyPressed(Keys.W) ? 1f : 0f) - (Game.IsKeyPressed(Keys.S) ? 1f : 0f);
                float strafe = (Game.IsKeyPressed(Keys.D) ? 1f : 0f) - (Game.IsKeyPressed(Keys.A) ? 1f : 0f);

                Vector3 move = fwd * fwdIn + right * strafe;
                move.Z = 0f;

                Vector3 vel;
                if (move.LengthSquared() > 0.01f)
                {
                    Vector3 dir = move.Normalized;
                    vel = new Vector3(dir.X * RideSpeed, dir.Y * RideSpeed, 0f);
                    _shark.Heading = dir.ToHeading();
                }
                else
                {
                    vel = new Vector3(0f, 0f, 0f);
                }

                // Shift — вверх, Ctrl — вниз (глубина ограничена, чтобы не улететь под карту)
                float depthStep = 0f;
                if (Game.IsKeyPressed(Keys.ShiftKey)) depthStep = +1f;
                else if (Game.IsKeyPressed(Keys.ControlKey)) depthStep = -1f;
                if (depthStep != 0f) _targetDepth = Clamp(_targetDepth + depthStep * 1.5f, sharkPos.Z - 25f, sharkPos.Z + 25f);

                float depthDiff = Clamp(_targetDepth - sharkPos.Z, -12f, 12f);
                vel.Z = depthDiff * 1.2f;
                if (depthDiff > 1.5f) vel.Z = Math.Max(vel.Z, 1.0f);
                if (depthDiff < -1.5f) vel.Z = Math.Min(vel.Z, -1.0f);

                _shark.Velocity = ClampSpeed(vel, 12f);
            }
            catch (Exception ex)
            {
                Log("UpdateRiding: " + ex.Message);
            }
        }

        private void StopRiding(bool deleteShark)
        {
            try
            {
                Ped player = Game.Player.Character;
                if (player != null && player.Exists())
                {
                    if (player.IsAttached())
                        player.Detach();
                    player.IsPositionFrozen = false;
                }

                if (_rideCam != null)
                {
                    if (World.RenderingCamera == _rideCam)
                        World.RenderingCamera = null;
                    _rideCam.Delete();
                    _rideCam = null;
                }

                if (deleteShark)
                    DeleteShark();
                else if (_shark != null && _shark.Exists())
                {
                    _shark.Velocity = Vector3.Zero;
                }

                _targetDepth = 0f;
                _state = State.Idle;
            }
            catch (Exception ex)
            {
                Log("StopRiding: " + ex.Message);
            }
        }

        // === УТИЛИТЫ ===

        private void DeleteShark()
        {
            try
            {
                if (_shark != null && _shark.Exists())
                {
                    Function.Call(Hash.SET_ENTITY_AS_NO_LONGER_NEEDED, _shark.Handle, false);
                    _shark.Delete();
                }
            }
            catch (Exception ex)
            {
                Log("DeleteShark: " + ex.Message);
            }
            _shark = null;
        }

        /// <summary>
        /// Проверка "игрок в воде". IS_ENTITY_IN_WATER иногда даёт false у самой поверхности,
        /// поэтому дополнительно сверяемся с высотой воды в точке игрока.
        /// </summary>
        private bool IsPlayerInWater(Ped ped)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_ENTITY_IN_WATER, ped.Handle))
                    return true;

                Vector3 pos = ped.Position;
                float waterZ = GetWaterHeight(pos);
                if (waterZ > -10f && pos.Z <= waterZ + 1.5f)
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private bool IsPedInVehicle(Ped ped)
        {
            try
            {
                return Function.Call<bool>(Hash.IS_PED_IN_ANY_VEHICLE, ped.Handle, false);
            }
            catch
            {
                return false;
            }
        }

        private float GetWaterHeight(Vector3 pos)
        {
            try
            {
                return Function.Call<float>(Hash.GET_WATER_HEIGHT, pos.X, pos.Y, pos.Z, 0f);
            }
            catch
            {
                return -100f;
            }
        }

        private Vector3 GetPlayerLookDirection(Ped player)
        {
            try
            {
                Vector3 rot = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_ROT, 0);
                float heading = -rot.Z; // рысканье камеры
                Vector3 dir = new Vector3(
                    (float)Math.Sin(heading * Math.PI / 180.0),
                    (float)Math.Cos(heading * Math.PI / 180.0),
                    0f);
                if (dir.LengthSquared() < 0.01f)
                    dir = new Vector3(1f, 0f, 0f);
                return dir.Normalized;
            }
            catch
            {
                float h = player.Heading;
                return new Vector3(
                    (float)Math.Sin(h * Math.PI / 180.0),
                    (float)Math.Cos(h * Math.PI / 180.0),
                    0f).Normalized;
            }
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
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

        /// <summary>
        /// Ограничивает длину вектора скорости, чтобы не разгонять физику до краша
        /// </summary>
        private static Vector3 ClampSpeed(Vector3 v, float maxSpeed)
        {
            float len = v.Length();
            if (len <= maxSpeed || len < 0.0001f) return v;
            float k = maxSpeed / len;
            return new Vector3(v.X * k, v.Y * k, v.Z * k);
        }

        private void Log(string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "SharkRider.log"),
                    line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
