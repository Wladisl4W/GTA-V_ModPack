using System;
using ModPack;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;

namespace SharkRider
{
    /// <summary>
    /// Мод-акула с режимами катания и атаки.
    /// - Когда игрок в воде, рядом спавнится тигровая акула a_c_sharktiger и подплывает к нему
    /// - В режиме катания игрок плавно и автоматически садится на спину акулы
    /// - W/S — скорость, A/D — поворот, Shift/Ctrl — глубина, E — слезть
    /// - В режиме атаки проигрывается безопасная визуальная сцена укуса без урона
    /// - Выход на сушу автоматически отпускает акулу и удаляет её
    /// - Клавиша O — меню режимов и включения мода
    /// </summary>
    public class SharkRiderPlugin : IGtaPlugin
    {
        private enum State { Idle, Spawning, Approaching, Mounting, Riding, AttackAligning, BiteScene }

        private const int PedType = 26; // PED_TYPE_CREATURE
        private const int SharkModelHash = 113504370; // a_c_sharktiger (0x06C3F072)

        private const long CheckIntervalMs = 400;   // как часто проверять "в воде ли игрок"
        private const long AbandonTimeoutMs = 2500; // через сколько без воды отпустить акулу

        private const float SpawnDistance = 40f;    // дистанция спавна акулы от игрока
        private const float RideDistance = 3.0f;    // с какой дистанции игрок садится
        private const float SwimSpeed = 7.5f;       // скорость подплыва акулы к игроку
        private const float RideSpeed = 7.5f;       // скорость катания
        private const float RideVerticalSpeed = 5f;  // скорость вверх/вниз (ед/сек)
        private const long SpawnSettleMs = 400;     // пауза после создания акулы (не трогать физику)
        private const float AttackDistance = 2.4f;
        private const float AttackApproachSpeed = 6.5f;
        private const long MountDurationMs = 650;
        private const long BiteSceneTimeoutMs = 1800;
        private const float BiteStopPhase = 0.30f;
        private const long BiteRetreatDurationMs = 1800;
        private const float RideAcceleration = 6.0f;
        private const float RideVerticalAcceleration = 5.0f;
        private const float RideTurnSpeed = 75.0f;

        private static readonly Vector3 AttachOffset = new Vector3(0f, -0.45f, 0.72f);
        private static readonly Vector3 AttachRotation = new Vector3(0f, 0f, 0f);
        private const string RideAnimDict = "veh@bike@sport@front@base";
        private const string RideAnimName = "sit";
        private const string BiteSceneAnimDict = "creatures@shark@move";

        private readonly ControlSession _control = new ControlSession();
        private bool _interactionBlocked;
        private State _state = State.Idle;
        private Ped _shark = null;

        private long _lastCheckMs = 0;
        private long _lastInWaterMs = 0;
        private long _lastHintMs = 0;
        private long _spawnRequestMs = 0;
        private long _sharkSpawnMs = 0;
        private long _mountStartedMs = 0;
        private long _lastRideUpdateMs = 0;
        private long _bitePhaseStartedMs = 0;
        private long _attackRetreatUntilMs = 0;
        private Vector3 _mountStartPosition = Vector3.Zero;
        private float _rideForwardSpeed = 0f;
        private float _rideVerticalSpeed = 0f;
        private float _ridePitch = 0f;
        private float _rideRoll = 0f;
        private int _activeBiteScene = -1;
        private int _activeLocalBiteScene = -1;
        private bool _biteFallbackActive = false;
        private bool _mountSuppressedUntilWaterExit = false;
        private bool _dismountKeyWasDown = false;
        private bool _modelRequested = false;


        // Настройки мода
        private bool _modEnabled = true;
        private bool _attackMode = false;
        // LemonUI
        private readonly ObjectPool _pool = new ObjectPool();
        private NativeMenu _menu;
        private NativeCheckboxItem _enableCheckbox;
        private NativeCheckboxItem _attackCheckbox;

        // Сохранение настроек (как в RemoveDroppedPeds)
        private class ModSettings
        {
            public bool ModEnabled { get; set; }
            public bool AttackMode { get; set; }
            public int ModelHash { get; set; }

            public ModSettings()
            {
                ModEnabled = true;
                AttackMode = false;
                ModelHash = SharkModelHash;
            }
        }

        private readonly string _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ReloaderPlugins",
            "SharkRiderSettings.json"
        );
        private readonly SettingsSerializer _serializer = new SettingsSerializer();
        private ModSettings _settings;

        private int _lastSaveGameTime = 0;
        private bool _settingsDirty = false;
        private int _lastKeyGameTime = 0;
        public void OnStart()
        {
            try
            {
                // Загрузка настроек
                _settings = LoadSettings();
                _modEnabled = _settings.ModEnabled;
                _attackMode = _settings.AttackMode;

                _settings.ModelHash = SharkModelHash;

                _lastSaveGameTime = Game.GameTime;
                _lastKeyGameTime = Game.GameTime;

                CreateMenu();

                Log("Shark Rider загружен. Мод: " + (_modEnabled ? "вкл" : "выкл") +
                    ", режим: " + (_attackMode ? "атака" : "катание") +
                    ", модель: a_c_sharktiger (0x" + SharkModelHash.ToString("X8") + ")");
                GTA.UI.Notification.PostTicker("~b~Shark Rider~w~ активен~n~Войдите в воду — акула подплывёт сама~n~~y~O~w~ — меню мода", false, false);
            }
            catch (Exception ex)
            {
                Log("OnStart: " + ex.Message);
            }
        }

        private void CreateMenu()
        {
            _menu = new NativeMenu("Shark Rider", "Акула");

            _enableCheckbox = new NativeCheckboxItem(
                "Включить мод",
                "Выкл — акула не спавнится, а текущая удаляется",
                _modEnabled);
            _enableCheckbox.CheckboxChanged += (s, e) =>
            {
                _modEnabled = _enableCheckbox.Checked;
                _settings.ModEnabled = _modEnabled;
                MarkSettingsDirty();
                if (!_modEnabled && _state != State.Idle)
                {
                    StopRiding(true);
                    _state = State.Idle;
                }
            };
            _menu.Add(_enableCheckbox);

            _attackCheckbox = new NativeCheckboxItem(
                "Акула кусает",
                "Вкл — акула подплывает и атакует игрока. Выкл — на акуле можно кататься.",
                _attackMode);
            _attackCheckbox.CheckboxChanged += (s, e) =>
            {
                _attackMode = _attackCheckbox.Checked;
                _settings.AttackMode = _attackMode;
                MarkSettingsDirty();

                if (_state != State.Idle || (_shark != null && _shark.Exists()))
                    StopRiding(true);

                Log("Режим изменён: " + (_attackMode ? "акула кусает" : "катание"));
            };
            _menu.Add(_attackCheckbox);

            _pool.Add(_menu);
        }

        public void OnTick()
        {
            // LemonUI требует вызова Process() каждый кадр
            try
            {
                _pool.Process();
            }
            catch (Exception ex)
            {
                Log("OnTick pool: " + ex.Message);
            }

            // Отложенное сохранение настроек (раз в 3 секунды)
            if (_settingsDirty && HasElapsed(_lastSaveGameTime, 3000))
            {
                try
                {
                    SaveSettings();
                    _settingsDirty = false;
                    _lastSaveGameTime = Game.GameTime;
                }
                catch (Exception ex)
                {
                    Log("OnTick save: " + ex.Message);
                }
            }

            try
            {
                Ped player = Game.Player.Character;
                DevelopmentDiagnostics.State("Shark", _state.ToString());
                if (_control.Held && !_control.Valid)
                {
                    DevelopmentDiagnostics.Event("Shark", "Stopped: player died or changed");
                    StopRiding(true);
                    return;
                }
                if (player == null || !player.Exists() || player.IsDead) return;
                if (_interactionBlocked && (!IsPlayerInWater(player) || _shark == null || !_shark.Exists() ||
                    player.Position.DistanceTo(_shark.Position) > RideDistance + 3f))
                    _interactionBlocked = false;

                if (!_modEnabled)
                {
                    if (_state != State.Idle)
                    {
                        StopRiding(true);
                        _state = State.Idle;
                    }
                    return;
                }

                bool inWater = IsPlayerInWater(player);
                if (!inWater)
                    _mountSuppressedUntilWaterExit = false;

                switch (_state)
                {
                    case State.Idle:
                        long now = NowMs();
                        if (inWater && now - _lastCheckMs >= CheckIntervalMs)
                        {
                            _lastCheckMs = now;
                            if (!IsPedInVehicle(player))
                            {
                                if (!IsModelValidForSpawn(SharkModelHash))
                                {
                                    if (now - _lastHintMs >= 5000)
                                    {
                                        _lastHintMs = now;
                                        GTA.UI.Screen.ShowSubtitle("~r~Shark Rider: модель a_c_sharktiger недоступна.", 4000);
                                    }
                                    break;
                                }
                                Log("Игрок в воде — спавним акулу");
                                _state = State.Spawning;
                                SpawnShark(player);
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

                    case State.Mounting:
                        UpdateMounting(player, inWater);
                        break;

                    case State.AttackAligning:
                        UpdateAttackAligning(player, inWater);
                        break;

                    case State.BiteScene:
                        UpdateBiteScene(player, inWater);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("OnTick: " + ex.Message);
                StopRiding(true);
            }
        }

        public void OnKeyDown(Keys key)
        {
            if (key != Keys.O) return;

            // Защита от автоповтора при удержании
            int now = Game.GameTime;
            uint sinceLast = unchecked((uint)(now - _lastKeyGameTime));
            if (sinceLast < 300) return;
            _lastKeyGameTime = now;

            if (_menu == null) return;

            // O переключает меню.
            _menu.Visible = !_menu.Visible;
        }

        public void OnAbort()
        {
            try
            {
                Log("Shark Rider выгружается");
                StopRiding(true);

                if (_menu != null)
                    _menu.Visible = false;

                if (_settingsDirty)
                    SaveSettings();
            }
            catch (Exception ex)
            {
                Log("OnAbort: " + ex.Message);
            }
        }

        // === НАСТРОЙКИ ===

        /// <summary>
        /// Проверяет, прошло ли достаточно времени с учётом переполнения Game.GameTime.
        /// </summary>
        private static bool HasElapsed(int lastTime, int intervalMs)
        {
            int currentTime = Game.GameTime;
            uint elapsed = unchecked((uint)(currentTime - lastTime));
            return elapsed >= (uint)intervalMs;
        }

        private void MarkSettingsDirty()
        {
            _settingsDirty = true;
        }

        private ModSettings LoadSettings()
        {
            try
            {
                if (SafeFiles.Exists(_settingsPath))
                {
                    string json = SafeFiles.Read(_settingsPath, ValidateSettings);
                    var settings = _serializer.Deserialize(json);
                    if (settings != null)
                    {
                        settings.ModelHash = SharkModelHash;
                        Log("Настройки загружены: Enabled=" + settings.ModEnabled +
                            ", AttackMode=" + settings.AttackMode + ", модель=a_c_sharktiger");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("LoadSettings: " + ex.Message);
            }
            Log("Используются настройки по умолчанию (модель a_c_sharktiger)");
            return new ModSettings();
        }

        private void ValidateSettings(string text)
        {
            if (new JavaScriptSerializer().Deserialize<ModSettings>(text) == null)
                throw new InvalidDataException("Invalid settings");
        }

        private void SaveSettings()
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                _settings.ModelHash = SharkModelHash;
                SafeFiles.Write(_settingsPath, _serializer.Serialize(_settings), ValidateSettings);
                Log("Настройки сохранены в файл");
            }
            catch (Exception ex)
            {
                Log("SaveSettings: " + ex.Message);
            }
        }

        /// <summary>
        /// Обёртка для сериализации/десериализации настроек.
        /// </summary>
        private sealed class SettingsSerializer
        {
            private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

            public ModSettings Deserialize(string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                    return new ModSettings();

                var settings = _serializer.Deserialize<ModSettings>(json);
                return settings ?? new ModSettings();
            }

            public string Serialize(ModSettings settings)
            {
                return _serializer.Serialize(settings);
            }
        }

        // === СПАВН ===

        /// <summary>
        /// Модель обязана существовать и быть педом, иначе CREATE_PED крашит игру нативно.
        /// </summary>
        private bool IsModelValidForSpawn(int modelHash)
        {
            if (modelHash == 0) return false;
            try
            {
                if (!Function.Call<bool>(Hash.IS_MODEL_VALID, modelHash)) return false;
                if (!Function.Call<bool>(Hash.IS_MODEL_A_PED, modelHash)) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

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
                Function.Call(Hash.REQUEST_MODEL, SharkModelHash);
                _modelRequested = true;
            }
            catch (Exception ex)
            {
                Log("SpawnShark: " + ex.Message);
                ReleaseRequestedModel();
                _state = State.Idle;
            }
        }

        private void UpdateSpawning(Ped player)
        {
            try
            {
                // Страховка: модель обязана существовать и быть педом, иначе CREATE_PED крашит игру нативно
                if (!IsModelValidForSpawn(SharkModelHash))
                {
                    Log("Модель a_c_sharktiger не существует или не является педом — спавн отменён");
                    ReleaseRequestedModel();
                    _state = State.Idle;
                    return;
                }

                if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, SharkModelHash))
                {
                    // Модель не загрузилась — пробуем снова и пишем в лог
                    if (NowMs() - _spawnRequestMs > 5000)
                    {
                        Log("Модель a_c_sharktiger не загрузилась за 5с, повторный запрос");
                        _spawnRequestMs = NowMs();
                        Function.Call(Hash.REQUEST_MODEL, SharkModelHash);
                    }
                    return;
                }

                if (!IsPlayerInWater(player))
                {
                    Log("UpdateSpawning: игрок вышел из воды, отмена спавна");
                    ReleaseRequestedModel();
                    _state = State.Idle;
                    return;
                }

                Vector3 spawnPos = ComputeSpawnPosition(player);
                if (IsInvalid(spawnPos))
                {
                    Log("UpdateSpawning: невалидная позиция спавна");
                    ReleaseRequestedModel();
                    _state = State.Idle;
                    return;
                }

                // Не спавним вплотную к игроку (риск коллизии при создании педа)
                Vector3 playerPos = player.Position;
                if (spawnPos.DistanceTo(playerPos) < 15f)
                {
                    Vector3 away = (spawnPos - playerPos).Normalized;
                    spawnPos = playerPos + away * 20f;
                    spawnPos.Z = Clamp(playerPos.Z - 1.5f, playerPos.Z - 6f, playerPos.Z + 1f);
                    if (IsInvalid(spawnPos) || spawnPos.Z < -10f)
                        spawnPos = playerPos + new Vector3(5f, -5f, 0f);
                    spawnPos.Z = Clamp(playerPos.Z - 1.5f, playerPos.Z - 6f, playerPos.Z + 1f);
                }

                _shark = (Ped)Function.Call<Entity>(Hash.CREATE_PED, PedType, SharkModelHash,
                    spawnPos.X, spawnPos.Y, spawnPos.Z, playerPos.ToHeading(), false, false);
                ReleaseRequestedModel();

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

                _sharkSpawnMs = NowMs();
                _lastInWaterMs = NowMs();
                ResetInteractionState();
                Log("Акула создана на " + spawnPos.ToString());
                _state = State.Approaching;
            }
            catch (Exception ex)
            {
                Log("UpdateSpawning: " + ex.Message);
                ReleaseRequestedModel();
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

            // Глубину берём от игрока (он уже в воде). Координаты X/Y не ограничиваем:
            // дополнительные карты и острова GTA могут находиться далеко за +/-4000.
            spawnPos.Z = Clamp(playerPos.Z - 1.5f, playerPos.Z - 6f, playerPos.Z + 1f);
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
                    StopRiding(true);
                    return;
                }

                Vector3 playerPos = player.Position;
                Vector3 sharkPos = _shark.Position;

                if (IsInvalid(sharkPos))
                {
                    Log("UpdateApproaching: невалидная позиция акулы");
                    StopRiding(true);
                    return;
                }

                // Только что созданную акулу не трогаем первые ~0.4с (физика ещё не инициализирована)
                if (NowMs() - _sharkSpawnMs < SpawnSettleMs)
                    return;

                float dist = playerPos.DistanceTo(sharkPos);
                if (dist < RideDistance && inWater)
                {
                    if (_attackMode)
                        StartAttackAligning();
                    else if (!_mountSuppressedUntilWaterExit)
                        StartMounting(player);
                    return;
                }

                // Акула далеко (глюк стриминга) — пересоздаём рядом
                if (dist > 60f)
                {
                    Log("UpdateApproaching: акула слишком далеко (" + dist.ToString("F0") + "м), пересоздаём");
                    StopRiding(true);
                    _state = State.Spawning;
                    SpawnShark(player);
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

        // === АТАКА ===

        private void StartAttackAligning()
        {
            if (_shark == null || !_shark.Exists()) return;

            Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
            RequestBiteAnimations();
            _state = State.AttackAligning;
            Log("Акула готовится к визуальному укусу");
        }

        private void UpdateAttackAligning(Ped player, bool inWater)
        {
            try
            {
                long now = NowMs();
                bool attackInWater = inWater || IsEntityInWater(_shark);
                if (attackInWater) _lastInWaterMs = now;
                if ((!attackInWater && now - _lastInWaterMs > AbandonTimeoutMs) ||
                    player.IsDead || _shark == null || !_shark.Exists() || _shark.IsDead)
                {
                    StopRiding(true);
                    return;
                }

                RequestBiteAnimations();

                Vector3 sharkPos = _shark.Position;
                Vector3 playerPos = player.Position;
                if (IsInvalid(sharkPos) || IsInvalid(playerPos))
                {
                    StopRiding(true);
                    return;
                }

                if (now < _attackRetreatUntilMs)
                {
                    Vector3 away = sharkPos - playerPos;
                    away.Z = 0f;
                    if (away.LengthSquared() < 0.01f) away = new Vector3(1f, 0f, 0f);
                    away.Normalize();
                    MoveSharkToward(sharkPos + away * 6f, sharkPos, 5f);
                    return;
                }

                Vector3 playerForward = GetPlayerLookDirection(player);
                Vector3 stagingPoint = playerPos + playerForward * AttackDistance;
                stagingPoint.Z = playerPos.Z - 0.25f;
                float stagingDistance = sharkPos.DistanceTo(stagingPoint);

                if (stagingDistance > 0.65f)
                {
                    MoveSharkToward(stagingPoint, sharkPos, AttackApproachSpeed);
                    return;
                }

                Vector3 facePlayer = playerPos - sharkPos;
                facePlayer.Z = 0f;
                if (facePlayer.LengthSquared() > 0.01f)
                    _shark.Heading = facePlayer.Normalized.ToHeading();
                _shark.Velocity = Vector3.Zero;

                if (AreBiteAnimationsLoaded())
                    StartBiteScene(player);
            }
            catch (Exception ex)
            {
                Log("UpdateAttackAligning: " + ex.Message);
                StopRiding(true);
            }
        }

        private void MoveSharkToward(Vector3 targetPos, Vector3 sharkPos, float speed)
        {
            Vector3 toTarget = targetPos - sharkPos;
            Vector3 flat = new Vector3(toTarget.X, toTarget.Y, 0f);
            if (flat.LengthSquared() <= 0.01f) return;

            Vector3 dir = flat.Normalized;
            _shark.Heading = dir.ToHeading();
            float targetZ = Clamp(targetPos.Z + 0.25f, targetPos.Z - 3f, targetPos.Z + 2f);
            Vector3 velocity = new Vector3(
                dir.X * speed,
                dir.Y * speed,
                Clamp((targetZ - sharkPos.Z) * 1.5f, -speed, speed));
            _shark.Velocity = ClampSpeed(velocity, 12f);
        }

        private void StartBiteScene(Ped player)
        {
            if (!AcquireInteraction("Укус акулы")) return;
            _biteFallbackActive = false;
            player.IsPositionFrozen = true;
            player.Velocity = Vector3.Zero;
            _shark.Velocity = Vector3.Zero;

            if (!StartNetworkBiteScene(player))
                StartFallbackBite(player);

            _bitePhaseStartedMs = NowMs();
            _state = State.BiteScene;
            Log("Запущена визуальная сцена укуса");
        }

        private void UpdateBiteScene(Ped player, bool inWater)
        {
            try
            {
                long now = NowMs();
                bool biteInWater = inWater || IsEntityInWater(_shark);
                if (biteInWater) _lastInWaterMs = now;
                if ((!biteInWater && now - _lastInWaterMs > AbandonTimeoutMs) ||
                    player.IsDead || _shark == null || !_shark.Exists())
                {
                    StopRiding(true);
                    return;
                }

                long elapsed = now - _bitePhaseStartedMs;
                if (_biteFallbackActive)
                {
                    if (elapsed >= BiteSceneTimeoutMs)
                        FinishBiteScene(player);
                    return;
                }

                float phase = GetActiveBiteScenePhase();
                if (phase >= BiteStopPhase || elapsed >= BiteSceneTimeoutMs)
                {
                    FinishBiteScene(player);
                    return;
                }

                if (elapsed > 500 && phase <= 0f && !IsBiteAnimationPlaying(player))
                {
                    StopActiveBiteScene();
                    StartFallbackBite(player);
                    _bitePhaseStartedMs = NowMs();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("UpdateBiteScene: " + ex.Message);
                FinishBiteScene(player);
            }
        }

        private bool StartNetworkBiteScene(Ped player)
        {
            try
            {
                Vector3 origin = _shark.Position;
                Vector3 rotation = player.Rotation;

                int scene = Function.Call<int>(Hash.NETWORK_CREATE_SYNCHRONISED_SCENE,
                    origin.X, origin.Y, origin.Z,
                    rotation.X, rotation.Y, rotation.Z,
                    2, true, false, 1f, 0f, 1f);
                if (scene < 0) return false;

                Function.Call(Hash.NETWORK_ADD_PED_TO_SYNCHRONISED_SCENE,
                    _shark.Handle, scene, BiteSceneAnimDict, "attack",
                    8f, 8f, -1, 0, 1f, 0);
                Function.Call(Hash.NETWORK_ADD_PED_TO_SYNCHRONISED_SCENE,
                    player.Handle, scene, BiteSceneAnimDict, "attack_player",
                    8f, 8f, -1, 0, 1f, 0);
                Function.Call(Hash.NETWORK_START_SYNCHRONISED_SCENE, scene);
                _activeBiteScene = scene;
                _activeLocalBiteScene = Function.Call<int>(Hash.NETWORK_GET_LOCAL_SCENE_FROM_NETWORK_ID, scene);
                return true;
            }
            catch (Exception ex)
            {
                Log("StartNetworkBiteScene: " + ex.Message);
                _activeBiteScene = -1;
                _activeLocalBiteScene = -1;
                return false;
            }
        }

        private void StartFallbackBite(Ped player)
        {
            _biteFallbackActive = true;
            Function.Call(Hash.TASK_PLAY_ANIM, _shark.Handle, BiteSceneAnimDict, "attack",
                8f, -8f, (int)BiteSceneTimeoutMs, 0, 0f, false, false, false);
            Function.Call(Hash.TASK_PLAY_ANIM, player.Handle, BiteSceneAnimDict, "attack_player",
                8f, -8f, (int)BiteSceneTimeoutMs, 0, 0f, false, false, false);
        }

        private bool IsBiteAnimationPlaying(Ped player)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, player.Handle, BiteSceneAnimDict, "attack_player", 3) ||
                   Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, _shark.Handle, BiteSceneAnimDict, "attack", 3);
        }

        private float GetActiveBiteScenePhase()
        {
            if (_activeLocalBiteScene < 0) return 0f;
            try
            {
                return Function.Call<float>(Hash.GET_SYNCHRONIZED_SCENE_PHASE, _activeLocalBiteScene);
            }
            catch
            {
                return 0f;
            }
        }

        private void FinishBiteScene(Ped player)
        {
            StopActiveBiteScene();
            if (!_control.Held) return;
            try { Function.Call(Hash.CLEAR_PED_TASKS, player.Handle); } catch { }
            try { Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle); } catch { }
            _control.Dispose();
            DevelopmentDiagnostics.Event("Shark", "Bite finished");
            _biteFallbackActive = false;
            _attackRetreatUntilMs = NowMs() + BiteRetreatDurationMs;
            _state = State.AttackAligning;
        }

        private void StopActiveBiteScene()
        {
            if (_activeBiteScene < 0) return;
            try { Function.Call(Hash.NETWORK_STOP_SYNCHRONISED_SCENE, _activeBiteScene); } catch { }
            _activeBiteScene = -1;
            _activeLocalBiteScene = -1;
        }

        private static void RequestBiteAnimations()
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, BiteSceneAnimDict);
        }

        private static bool AreBiteAnimationsLoaded()
        {
            return Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, BiteSceneAnimDict);
        }

        // === КАТАНИЕ ===

        private bool AcquireInteraction(string name)
        {
            if (_interactionBlocked) return false;
            if (_control.Acquire(name, false)) return true;
            _interactionBlocked = true;
            if (_shark != null && _shark.Exists()) _shark.Velocity = Vector3.Zero;
            return false;
        }

        private void StartMounting(Ped player)
        {
            try
            {
                if (_shark == null || !_shark.Exists()) return;
                if (!AcquireInteraction("Катание на акуле")) return;

                Function.Call(Hash.CLEAR_PED_TASKS, _shark.Handle);
                Function.Call(Hash.REQUEST_ANIM_DICT, RideAnimDict);
                _shark.Velocity = Vector3.Zero;
                _mountStartPosition = player.Position;
                _mountStartedMs = NowMs();
                player.IsPositionFrozen = true;
                _state = State.Mounting;
                Log("Началась плавная посадка на акулу");
            }
            catch (Exception ex)
            {
                Log("StartMounting: " + ex.Message);
                StopRiding(true);
            }
        }

        private void UpdateMounting(Ped player, bool inWater)
        {
            try
            {
                long now = NowMs();
                bool rideInWater = inWater || IsEntityInWater(_shark);
                if (rideInWater) _lastInWaterMs = now;
                if ((!rideInWater && now - _lastInWaterMs > AbandonTimeoutMs) ||
                    _shark == null || !_shark.Exists() || _shark.IsDead)
                {
                    StopRiding(true);
                    return;
                }

                Function.Call(Hash.REQUEST_ANIM_DICT, RideAnimDict);
                float t = Clamp((now - _mountStartedMs) / (float)MountDurationMs, 0f, 1f);
                float smooth = t * t * (3f - 2f * t);
                Vector3 target = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS,
                    _shark.Handle, AttachOffset.X, AttachOffset.Y, AttachOffset.Z);
                Vector3 position = Lerp(_mountStartPosition, target, smooth);
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, player.Handle,
                    position.X, position.Y, position.Z, false, false, false);
                player.Heading = LerpAngle(player.Heading, _shark.Heading, 0.18f);

                if (t < 1f) return;

                player.AttachTo(_shark, AttachOffset, AttachRotation);
                PlayRideAnimation(player);
                _rideForwardSpeed = 0f;
                _rideVerticalSpeed = 0f;
                _ridePitch = 0f;
                _rideRoll = 0f;
                _lastRideUpdateMs = NowMs();
                _dismountKeyWasDown = false;
                _state = State.Riding;
                Log("Игрок сел верхом на акулу");
            }
            catch (Exception ex)
            {
                Log("UpdateMounting: " + ex.Message);
                StopRiding(true);
            }
        }

        private void UpdateRiding(Ped player, bool inWater)
        {
            try
            {
                long now = NowMs();

                bool sharkOk = _shark != null && _shark.Exists() && !_shark.IsDead;
                bool rideInWater = inWater || IsEntityInWater(_shark);
                if (rideInWater) _lastInWaterMs = now;
                if ((!rideInWater && now - _lastInWaterMs > AbandonTimeoutMs) || !sharkOk)
                {
                    StopRiding(true);
                    _state = State.Idle;
                    return;
                }

                bool dismountDown = Game.IsKeyPressed(Keys.E);
                if (dismountDown && !_dismountKeyWasDown)
                {
                    _mountSuppressedUntilWaterExit = true;
                    StopRiding(true);
                    return;
                }
                _dismountKeyWasDown = dismountDown;

                PlayRideAnimation(player);

                Vector3 sharkPos = _shark.Position;
                if (IsInvalid(sharkPos))
                {
                    Log("UpdateRiding: невалидная позиция акулы");
                    StopRiding(true);
                    _state = State.Idle;
                    return;
                }

                float dt = Clamp((now - _lastRideUpdateMs) / 1000f, 0.001f, 0.05f);
                _lastRideUpdateMs = now;

                float forwardInput = (Game.IsKeyPressed(Keys.W) ? 1f : 0f) - (Game.IsKeyPressed(Keys.S) ? 1f : 0f);
                float turnInput = (Game.IsKeyPressed(Keys.D) ? 1f : 0f) - (Game.IsKeyPressed(Keys.A) ? 1f : 0f);
                float verticalInput = (Game.IsKeyPressed(Keys.ShiftKey) ? 1f : 0f) -
                    (Game.IsKeyPressed(Keys.ControlKey) ? 1f : 0f);

                float targetForward = forwardInput >= 0f ? forwardInput * RideSpeed : forwardInput * RideSpeed * 0.4f;
                _rideForwardSpeed = MoveTowards(_rideForwardSpeed, targetForward, RideAcceleration * dt);
                _rideVerticalSpeed = MoveTowards(_rideVerticalSpeed, verticalInput * RideVerticalSpeed,
                    RideVerticalAcceleration * dt);

                float heading = _shark.Heading + turnInput * RideTurnSpeed * dt;
                float targetPitch = -verticalInput * 10f;
                float targetRoll = -turnInput * 8f;
                _ridePitch = MoveTowards(_ridePitch, targetPitch, 30f * dt);
                _rideRoll = MoveTowards(_rideRoll, targetRoll, 35f * dt);
                _shark.Rotation = new Vector3(_ridePitch, _rideRoll, heading);

                Vector3 direction = RotationToDirection(heading);
                Vector3 velocity = new Vector3(
                    direction.X * _rideForwardSpeed,
                    direction.Y * _rideForwardSpeed,
                    _rideVerticalSpeed);
                _shark.Velocity = ClampSpeed(velocity, 12f);
            }
            catch (Exception ex)
            {
                Log("UpdateRiding: " + ex.Message);
                StopRiding(true);
            }
        }

        private static void PlayRideAnimation(Ped player)
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, RideAnimDict);
            if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, RideAnimDict)) return;
            if (Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, player.Handle, RideAnimDict, RideAnimName, 3)) return;

            Function.Call(Hash.TASK_PLAY_ANIM, player.Handle, RideAnimDict, RideAnimName,
                8f, -8f, -1, 1, 0f, false, false, false);
        }

        private void StopRiding(bool deleteShark)
        {
            DevelopmentDiagnostics.Event("Shark", "Stop " + _state + ": " +
                (!_modEnabled ? "disabled" :
                _control.Held && !_control.Valid ? "player unavailable" :
                _shark == null || !_shark.Exists() ? "shark missing" : "exit/mode change/cleanup"));
            try
            {
                StopActiveBiteScene();
                Ped player = _control.Player;
                if (_control.Held && player != null && player.Exists())
                {
                    if (player.IsAttached())
                        player.Detach();
                    try { Function.Call(Hash.CLEAR_PED_TASKS, player.Handle); } catch { }
                }

                if (deleteShark)
                    DeleteShark();
                else if (_shark != null && _shark.Exists())
                {
                    _shark.Velocity = Vector3.Zero;
                }

                _state = State.Idle;
            }
            catch (Exception ex)
            {
                Log("StopRiding: " + ex.Message);
            }
            finally
            {
                _control.Dispose();
                _state = State.Idle;
                DevelopmentDiagnostics.State("Shark", "Idle");
            }
        }

        // === УТИЛИТЫ ===

        private void DeleteShark()
        {
            // Защита: игрок может быть прикреплён к акуле — удалять сущность до открепления
            // нельзя (игра падает с AccessViolation). Открепляем и снимаем заморозку.
            try
            {
                Ped player = _control.Player;
                if (_control.Held && player != null && player.Exists())
                {
                    if (player.IsAttached())
                        player.Detach();
                }
            }
            catch (Exception ex)
            {
                Log("DeleteShark (detach): " + ex.Message);
            }
            try
            {
                if (_shark != null && _shark.Exists())
                {
                    // ВАЖНО: SET_ENTITY_AS_NO_LONGER_NEEDED принимает Entity* — передавать
                    // сюда _shark.Handle (int) нельзя: маленькое число трактуется как
                    // указатель и игра падает с AccessViolation. Entity.Delete() сам
                    // корректно помечает и удаляет сущность, поэтому этот натив не нужен.
                    try { _shark.Delete(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("DeleteShark: " + ex.Message);
            }
            _shark = null;
            _control.Dispose();
            ResetInteractionState();
            ReleaseRequestedModel();
        }

        private void ResetInteractionState()
        {
            StopActiveBiteScene();
            _mountStartedMs = 0;
            _lastRideUpdateMs = 0;
            _bitePhaseStartedMs = 0;
            _attackRetreatUntilMs = 0;
            _rideForwardSpeed = 0f;
            _rideVerticalSpeed = 0f;
            _ridePitch = 0f;
            _rideRoll = 0f;
            _biteFallbackActive = false;
            _dismountKeyWasDown = false;
        }

        private bool IsSpawnedShark(Ped ped)
        {
            return ped != null && _shark != null && ped.Exists() && _shark.Exists() && ped.Handle == _shark.Handle;
        }

        private void ReleaseRequestedModel()
        {
            if (!_modelRequested) return;

            try
            {
                Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, SharkModelHash);
            }
            catch (Exception ex)
            {
                Log("ReleaseRequestedModel: " + ex.Message);
            }
            _modelRequested = false;
        }

        /// <summary>
        /// Проверка "игрок в воде". IS_ENTITY_IN_WATER иногда даёт false у самой поверхности,
        /// поэтому дополнительно сверяемся с высотой воды в точке игрока.
        /// </summary>
        // Только IS_ENTITY_IN_WATER. GET_WATER_HEIGHT в этой сборке SHVDN3 имеет
        // неверный хеш в Hash-енуме и вызывает нативный AccessViolation (краш игры),
        // поэтому высоту воды не запрашиваем вообще.
        private bool IsPlayerInWater(Ped ped)
        {
            try
            {
                return Function.Call<bool>(Hash.IS_ENTITY_IN_WATER, ped.Handle);
            }
            catch
            {
            }
            return false;
        }

        private static bool IsEntityInWater(Entity entity)
        {
            try
            {
                return entity != null && entity.Exists() &&
                    Function.Call<bool>(Hash.IS_ENTITY_IN_WATER, entity.Handle);
            }
            catch
            {
                return false;
            }
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

        private static float MoveTowards(float current, float target, float maxDelta)
        {
            float delta = target - current;
            if (Math.Abs(delta) <= maxDelta) return target;
            return current + Math.Sign(delta) * maxDelta;
        }

        private static Vector3 Lerp(Vector3 from, Vector3 to, float amount)
        {
            return new Vector3(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount);
        }

        private static float LerpAngle(float from, float to, float amount)
        {
            float delta = to - from;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return from + delta * amount;
        }

        private static Vector3 RotationToDirection(float heading)
        {
            double radians = heading * Math.PI / 180.0;
            return new Vector3((float)(-Math.Sin(radians)), (float)Math.Cos(radians), 0f);
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
            DevelopmentDiagnostics.Event("Shark", message);
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
