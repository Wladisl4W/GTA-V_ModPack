using System;
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
    /// Мод-акула: без меню и кнопок.
    /// - Когда игрок в воде, рядом спавнится акула (a_c_shark_tiger по умолчанию) и подплывает к нему
    /// - При близком контакте игрок автоматически садится на спину акулы
    /// - WASD — плавание, Shift — всплытие, Ctrl — погружение
    /// - Выход на сушу автоматически отпускает акулу и удаляет её
    /// - Клавиша O — меню: выбор модели педа прицелом (сохраняется в файл) + вкл/выкл мода
    /// </summary>
    public class SharkRiderPlugin : IGtaPlugin
    {
        private enum State { Idle, Spawning, Approaching, Riding }

        // ВАЖНО: если пользователь выбирает модель (акула — "a_c_shark_tiger",
        // НЕ "tiger_shark"), перед спавном всегда проверяется IS_MODEL_VALID/IS_MODEL_A_PED
        private const int PedType = 26; // PED_TYPE_CREATURE

        private const long CheckIntervalMs = 400;   // как часто проверять "в воде ли игрок"
        private const long AbandonTimeoutMs = 2500; // через сколько без воды отпустить акулу

        private const float SpawnDistance = 40f;    // дистанция спавна акулы от игрока
        private const float RideDistance = 3.0f;    // с какой дистанции игрок садится
        private const float SwimSpeed = 7.5f;       // скорость подплыва акулы к игроку
        private const float RideSpeed = 7.5f;       // скорость катания
        private const long SpawnSettleMs = 400;     // пауза после создания акулы (не трогать физику)

        private static readonly Vector3 AttachOffset = new Vector3(0f, 0.4f, 0.9f); // крепление на спине
        private static readonly Vector3 CamOffset = new Vector3(0f, -5.5f, 2.6f);    // камера позади-сверху

        private State _state = State.Idle;
        private Ped _shark = null;
        private Camera _rideCam = null;

        private long _lastCheckMs = 0;
        private long _lastInWaterMs = 0;
        private long _lastDiagMs = 0;
        private long _lastHintMs = 0;
        private long _spawnRequestMs = 0;
        private long _sharkSpawnMs = 0;
        private float _targetDepth = 0f;

        // Настройки мода
        private bool _modEnabled = true;
        private int _modelHash = 0; // 0 = модель не выбрана (обязательно выбрать в меню)

        // LemonUI
        private readonly ObjectPool _pool = new ObjectPool();
        private NativeMenu _menu;
        private NativeCheckboxItem _enableCheckbox;
        private NativeItem _pickModelItem;
        private NativeItem _modelDisplayItem;

        // Сохранение настроек (как в RemoveDroppedPeds)
        private class ModSettings
        {
            public bool ModEnabled { get; set; }
            public int ModelHash { get; set; }

            public ModSettings()
            {
                ModEnabled = true;
                ModelHash = 0;
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
        private bool _pendingPick = false;
        private long _pendingPickAtMs = 0;

        public void OnStart()
        {
            try
            {
                _modelHash = 0;

                // Загрузка настроек
                _settings = LoadSettings();
                _modEnabled = _settings.ModEnabled;

                // Валидную модель берём из файла, битую или пустую — мод ждёт выбора в меню
                if (IsModelValidForSpawn(_settings.ModelHash))
                    _modelHash = _settings.ModelHash;
                else
                    _modelHash = 0;

                _lastSaveGameTime = Game.GameTime;
                _lastKeyGameTime = Game.GameTime;

                CreateMenu();

                Log("Shark Rider загружен. Мод: " + (_modEnabled ? "вкл" : "выкл") +
                    ", модель: " + (_modelHash == 0 ? "не выбрана" : "0x" + _modelHash.ToString("X8")));
                GTA.UI.Notification.PostTicker("~b~Shark Rider~w~ активен~n~Войдите в воду — акула подплывёт сама~n~~y~O~w~ — меню мода", false, false);
            }
            catch (Exception ex)
            {
                Log("OnStart: " + ex.Message);
            }
        }

        private void CreateMenu()
        {
            _menu = new NativeMenu("Shark Rider", "Катание на акуле");

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

            _pickModelItem = new NativeItem(
                "Выбрать модель под прицелом",
                "Меню закроется, наведитесь на педа и нажмите Enter — модель запомнится навсегда");
            _pickModelItem.Activated += (s, e) =>
            {
                // Меню (scaleform) ставит игру на паузу — рейкаст в меню не работает.
                // Закрываем меню, рейкаст делаем в ближайший тик, когда игра отыграна.
                _menu.Visible = false;
                _pendingPick = true;
                _pendingPickAtMs = NowMs() + 500;
            };
            _menu.Add(_pickModelItem);

            _modelDisplayItem = new NativeItem("Модель", "");
            _menu.Add(_modelDisplayItem);
            UpdateModelDisplay();

            _pool.Add(_menu);
        }

        private void UpdateModelDisplay()
        {
            if (_modelDisplayItem == null) return;

            if (_modelHash == 0)
            {
                _modelDisplayItem.Title = "Модель: не выбрана";
                _modelDisplayItem.AltTitle = "Наведитесь на педа и выберите — без модели мод не работает";
            }
            else
            {
                _modelDisplayItem.Title = "Модель: 0x" + _modelHash.ToString("X8");
                _modelDisplayItem.AltTitle = "Выбрана прицелом, сохранится в файле";
            }
        }

        /// <summary>
        /// Рейкаст из камеры по педам: выбрал педа — его модель становится моделью спавна.
        /// Возвращает true, если модель выбрана.
        /// </summary>
        private bool PickAimedPedModel()
        {
            try
            {
                Vector3 source = GameplayCamera.Position;
                Vector3 dir = GameplayCamera.Direction;
                if (dir.LengthSquared() < 0.001f)
                    dir = new Vector3(0f, 1f, 0f);
                Vector3 target = source + dir * 200f;

                // 1) Рейкаст по всему: если попал в педа — берём его
                RaycastResult ray = World.Raycast(source, target, IntersectFlags.Everything);
                if (ray.DidHit)
                {
                    Ped ped = ray.HitEntity as Ped;
                    if ((ped != null && ped.Exists() && !ped.IsPlayer) &&
                        IsModelValidForSpawn(ped.Model.Hash))
                    {
                        SaveModel(ped.Model.Hash);
                        return true;
                    }
                    // Попали в машину/стену, а не в педа — ищем ближайшего педа к точке попадания
                    target = ray.HitPosition;
                }

                // 2) Страховка: ближайший пед к точке прицела (игрока исключаем)
                Ped closest = World.GetClosestPed(target, 6f);
                if (closest != null && closest.Exists() && !closest.IsPlayer &&
                    IsModelValidForSpawn(closest.Model.Hash))
                {
                    SaveModel(closest.Model.Hash);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("PickAimedPedModel: " + ex.Message);
            }
            Log("PickAimedPedModel: пед под прицелом не найден");
            return false;
        }

        private void SaveModel(int hash)
        {
            _modelHash = hash;
            _settings.ModelHash = hash;
            MarkSettingsDirty();
            UpdateModelDisplay();
            Log("Выбрана модель педа: 0x" + hash.ToString("X8"));
            GTA.UI.Screen.ShowSubtitle("~g~Модель 0x" + hash.ToString("X8") + " сохранена.~n~Теперь в воде будет спавниться этот пед.", 4000);
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

            // Отложенный выбор модели: меню закрыто, игра распаузена — теперь рейкаст работает
            if (_pendingPick && NowMs() >= _pendingPickAtMs)
            {
                _pendingPick = false;
                if (PickAimedPedModel())
                    _menu.Visible = true;
            }

            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists()) return;

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

                switch (_state)
                {
                    case State.Idle:
                        long now = NowMs();
                        if (now - _lastDiagMs >= 5000)
                        {
                            _lastDiagMs = now;
                            Log("Idle: inWater=" + inWater + ", inVehicle=" + IsPedInVehicle(player) +
                                ", playerZ=" + player.Position.Z.ToString("F1") +
                                ", model=" + (_modelHash == 0 ? "не выбрана" : "0x" + _modelHash.ToString("X8")));
                        }
                        if (inWater && now - _lastCheckMs >= CheckIntervalMs)
                        {
                            _lastCheckMs = now;
                            if (!IsPedInVehicle(player))
                            {
                                if (!IsModelValidForSpawn(_modelHash))
                                {
                                    if (now - _lastHintMs >= 5000)
                                    {
                                        _lastHintMs = now;
                                        GTA.UI.Screen.ShowSubtitle("~y~Shark Rider: модель не выбрана. Наведитесь на педа и нажмите ~b~O~w~.", 4000);
                                    }
                                    break;
                                }
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
            if (key != Keys.O) return;

            // Защита от автоповтора при удержании
            int now = Game.GameTime;
            uint sinceLast = unchecked((uint)(now - _lastKeyGameTime));
            if (sinceLast < 300) return;
            _lastKeyGameTime = now;

            if (_menu == null) return;

            // Меню открыто — закрываем
            if (_menu.Visible)
            {
                _menu.Visible = false;
                return;
            }

            // Меню закрыто (игра не на паузе): сначала вытаскиваем модель педа под прицелом,
            // потом открываем меню. Так работает и в самолёте/машине, и просто с руки.
            if (!PickAimedPedModel())
                GTA.UI.Screen.ShowSubtitle("~y~Под прицелом нет педа. Открываю меню...", 3000);
            _menu.Visible = true;
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
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = _serializer.Deserialize(json);
                    if (settings != null)
                    {
                        Log("Настройки загружены: Enabled=" + settings.ModEnabled + ", ModelHash=" +
                            (settings.ModelHash == 0 ? "не выбрана" : "0x" + settings.ModelHash.ToString("X8")));
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("LoadSettings: " + ex.Message);
            }
            Log("Используются настройки по умолчанию (модель не выбрана)");
            return new ModSettings();
        }

        private void SaveSettings()
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(_settingsPath, _serializer.Serialize(_settings));
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
                Function.Call(Hash.REQUEST_MODEL, _modelHash);
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
                // Страховка: модель обязана существовать и быть педом, иначе CREATE_PED крашит игру нативно
                if (!IsModelValidForSpawn(_modelHash))
                {
                    Log("Модель 0x" + _modelHash.ToString("X8") + " не существует или не пед — спавн отменён");
                    _state = State.Idle;
                    return;
                }

                if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, _modelHash))
                {
                    // Модель не загрузилась — пробуем снова и пишем в лог
                    if (NowMs() - _spawnRequestMs > 5000)
                    {
                        Log("Модель 0x" + _modelHash.ToString("X8") + " не загрузилась за 5с, повторный запрос");
                        _spawnRequestMs = NowMs();
                        Function.Call(Hash.REQUEST_MODEL, _modelHash);
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

                // Не спавним вплотную к игроку (риск коллизии при создании педа)
                Vector3 playerPos = player.Position;
                if (spawnPos.DistanceTo(playerPos) < 15f)
                {
                    Vector3 away = (spawnPos - playerPos).Normalized;
                    spawnPos = playerPos + away * 20f;
                    spawnPos.Z = Clamp(GetWaterHeight(spawnPos), playerPos.Z - 6f, playerPos.Z + 1f);
                    if (IsInvalid(spawnPos) || spawnPos.Z < -10f)
                        spawnPos = playerPos + new Vector3(5f, -5f, 0f);
                    spawnPos.Z = Clamp(playerPos.Z - 1.5f, playerPos.Z - 6f, playerPos.Z + 1f);
                }

                _shark = (Ped)Function.Call<Entity>(Hash.CREATE_PED, PedType, _modelHash,
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

                _sharkSpawnMs = NowMs();
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
                // В точке нет воды — ищем ближе (игрок-то в воде)
                spawnPos = playerPos + dir * 12f;
                waterZ = GetWaterHeight(spawnPos);
            }
            if (waterZ < -10f)
            {
                // Воды нет и вблизи — спавн отменяем
                return new Vector3(float.NaN, float.NaN, float.NaN);
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

                // Только что созданную акулу не трогаем первые ~0.4с (физика ещё не инициализирована)
                if (NowMs() - _sharkSpawnMs < SpawnSettleMs)
                    return;

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