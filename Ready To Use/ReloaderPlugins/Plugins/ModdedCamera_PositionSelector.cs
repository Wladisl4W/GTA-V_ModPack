using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using ModdedCamera.Gamepad;

namespace ModdedCamera
{
    public class PositionSelector
    {
        private FadeStateMachine _fadeMachine;
        private Camera _mainCamera;
        private Vector3 _previousPos;
        private Timer _renderSceneTimer;
        private Timer _focusTimer;
        private Scaleform _instructionalButtons;
        private float _currentLerpTime;
        private readonly float LerpTime = 0.5f;
        private readonly float RotationSpeed = 0.7f;
        private readonly float RollSpeed = 90f; // градусов в секунду (наклон горизонта)
        private bool _controlsDisabled = false;

        // Хинты перестраиваются только по dirty-флагу, Render2D — каждый тик
        // (без него scaleform мигает). Переборка ~17 нативов каждый кадр была
        // главным источником просадки в селекторе.
        private bool _instructionalDirty = true;

        // Кэшированные подписи кнопок (статичный текст, GET_CONTROL_ACTION_NAME).
        private string _lblSelect;
        private string _lblIncrease;
        private string _lblDecrease;
        private string _lblExit;
        private string _lblMoveUp;
        private string _lblMoveDown;
        private string _lblMoveLeft;
        private string _lblMoveRight;

        // Состояние фейда наружу: CameraService держит тики селектора, пока
        // незавершённый выход не дойдёт до None (иначе клин FadingOutExit).
        public FadeState FadeState
        {
            get { return _fadeMachine != null ? _fadeMachine.State : FadeState.None; }
        }

        public GamepadHandler GamepadHandler;

        public Camera MainCamera
        {
            get { return _mainCamera; }
        }

        public bool IsCameraAvailable
        {
            get { return _mainCamera != null && _mainCamera.Exists(); }
        }

        public PositionSelector(Vector3 position, Vector3 rotation)
        {
            this.GamepadHandler = new GamepadHandler();
            this.GamepadHandler.LeftStickChanged += LeftStickChanged;
            this.GamepadHandler.RightStickChanged += RightStickChanged;
            this.GamepadHandler.LeftStickPressed += LeftStickPressed;

            _instructionalButtons = Scaleform.RequestMovie("instructional_buttons");

            _mainCamera = Camera.Create("DEFAULT_SCRIPTED_CAMERA", position, rotation, 50f);
            _mainCamera.IsActive = false;
            _previousPos = position;
            _renderSceneTimer = new Timer(5000);
            _renderSceneTimer.Start();

            // Стриминг держим на ИГРОКЕ, пока расставляем точки (не на свободной
            // камере): иначе мир вокруг героя выгружается и всё встаёт рывками.
            _focusTimer = new Timer(500);
            _focusTimer.Start();
            _instructionalDirty = true;

            _fadeMachine = new FadeStateMachine(
                onActivate: () => {
                    this.MainCamera.IsActive = true;
                    ScriptCameraDirector.StartRendering();
                    Function.Call(Hash.DO_SCREEN_FADE_IN, 800);
                },
                onDeactivate: () => {
                    this.MainCamera.IsActive = false;
                    ScriptCameraDirector.StopRendering(false);
                    Function.Call(Hash.DO_SCREEN_FADE_IN, 800);
                },
                logPrefix: "PositionSelector"
            );
        }

        public void Dispose()
        {
            try
            {
                CameraRenderer.ClearFocus();
                EnablePlayerControls();
                if (_renderSceneTimer != null)
                {
                    try { _renderSceneTimer.Stop(); } catch { }
                }
                if (_focusTimer != null)
                {
                    try { _focusTimer.Stop(); } catch { }
                }
                if (_fadeMachine != null)
                {
                    try { _fadeMachine.Reset(); } catch { }
                }
                if (GamepadHandler != null)
                {
                    GamepadHandler.LeftStickChanged -= LeftStickChanged;
                    GamepadHandler.RightStickChanged -= RightStickChanged;
                    GamepadHandler.LeftStickPressed -= LeftStickPressed;
                    GamepadHandler.Dispose();
                    GamepadHandler = null;
                }
                if (_instructionalButtons != null)
                {
                    _instructionalButtons.Dispose();
                    _instructionalButtons = null;
                }
                if (_mainCamera != null && _mainCamera.Exists())
                {
                    if (_mainCamera.IsActive) _mainCamera.IsActive = false;
                    Function.Call(Hash.DESTROY_CAM, _mainCamera.Handle);
                    _mainCamera = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error disposing PositionSelector");
            }
        }

        private void LeftStickChanged(object sender, AnalogStickChangedEventArgs e)
        {
            // Кламп дельты: на просадках камера замедляется, а не телепортируется
            // на десятки метров за событие (отсюда была "покадровость").
            float rawDt = Game.LastFrameTime;
            float deltaTime = rawDt > 0.05f ? 0.05f : (rawDt < 0f ? 0f : rawDt);
            bool flag = e.X > 127;
            if (flag)
            {
                _previousPos -= Utils.RotationToDirection(_mainCamera.Rotation).RightVector(new Vector3(0f, 0f, 1f)) *
                    (Function.Call<float>(NativeHashes.GET_CONTROL_VALUE, 2, 218) * -75f * deltaTime);
            }
            bool flag2 = e.X < 127;
            if (flag2)
            {
                _previousPos += Utils.RotationToDirection(_mainCamera.Rotation).LeftVector(new Vector3(0f, 0f, 1f)) *
                    (Function.Call<float>(NativeHashes.GET_CONTROL_VALUE, 2, 218) * -75f * deltaTime);
            }
            bool flag3 = e.Y != 127;
            if (flag3)
            {
                _previousPos += Utils.RotationToDirection(_mainCamera.Rotation) *
                    (Function.Call<float>(NativeHashes.GET_CONTROL_VALUE, 0, 8) * -125f * deltaTime);
            }
            // Время-зависимый бленд (было фиксированные +0.02 за событие):
            // накопление вдвое быстрее затухания (как в оригинале +0.02/-0.01),
            // полный за ~0.8с удержания при любом FPS.
            _currentLerpTime += deltaTime * 1.2f;
            if (_currentLerpTime > LerpTime) _currentLerpTime = LerpTime;
            float num = _currentLerpTime / LerpTime;
            _mainCamera.Position = Vector3.Lerp(_mainCamera.Position, _previousPos, num);
        }

        private void RightStickChanged(object sender, AnalogStickChangedEventArgs e)
        {
            float rawDt = Game.LastFrameTime;
            float deltaTime = rawDt > 0.05f ? 0.05f : (rawDt < 0f ? 0f : rawDt);
            Camera cam = _mainCamera;
            cam.Rotation += new Vector3(
                Function.Call<float>(NativeHashes.GET_CONTROL_VALUE, 2, 221) * -400f * deltaTime,
                0f,
                Function.Call<float>(NativeHashes.GET_CONTROL_VALUE, 2, 220) * -500f * deltaTime
            ) * RotationSpeed;
        }

        private void LeftStickPressed(object sender, ButtonPressedEventArgs e)
        {
            _previousPos += Utils.RotationToDirection(_mainCamera.Rotation) *
                (Function.Call<float>(NativeHashes.GET_CONTROL_VALUE, 2, 230) * -5f);
        }

        public void EnterCameraView(Vector3 position)
        {
            // Сбрасываем залипший выходной фейд: его поздний onDeactivate
            // иначе убьёт эту свежую сессию.
            AbortPendingFade();

            _mainCamera.Position = position;
            _previousPos = position;
            _currentLerpTime = 0f;
            _instructionalDirty = true;
            if (_focusTimer != null) _focusTimer.Start();
            _fadeMachine.StartFadeOut(1200);
            DisablePlayerControls();
        }

        public void ExitCameraView()
        {
            // Игнорируем повторные выходы, пока выходной фейд уже идёт:
            // перевооружение гоняет FadingOutExit/Deactivating по кругу.
            if (_fadeMachine != null && (_fadeMachine.State == FadeState.FadingOutExit || _fadeMachine.State == FadeState.Deactivating))
                return;

            CameraRenderer.ClearFocus();
            _fadeMachine.StartFadeOutExit(1200);
            EnablePlayerControls();
        }

        // Принудительно гасим состояние fade-машины, чтобы её отложенный
        // onDeactivate (который глобально выключает рендер script-cams) не
        // сработал уже после возобновления воспроизведения spline-камеры.
        public void AbortPendingFade()
        {
            try { _fadeMachine.Reset(); } catch (Exception ex) { Logger.Debug("AbortPendingFade warning: " + ex.Message); }
        }

        private void DisablePlayerControls()
        {
            // Глушим ВСЕ действия управления каждый кадр: игра заново включает
            // контролы каждый тик, поэтому однократного вызова недостаточно.
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 2);

            // Контролы, которыми САМА камера расстановки управляет своим
            // движением/взглядом (стики 218-221, вперёд/назад 8, нажатие
            // стика 230), нужно ВЕРНУТЬ во включённое состояние — иначе
            // GET_CONTROL_VALUE возвращает 0 и камеру нельзя будет двигать.
            // Герой при этом заморожен (IsPositionFrozen), поэтому на него
            // это не влияет, а всё остальное (ходьба, стрельба, колесо оружия)
            // остаётся выключенным.
            int[] cameraControls = new int[] { 8, 218, 219, 220, 221, 230 };
            foreach (int c in cameraControls)
            {
                Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, c, true);
                Function.Call(Hash.ENABLE_CONTROL_ACTION, 2, c, true);
            }

            // Явно держим колесо переключения оружия (мышь) выключенным,
            // чтобы не всплывало меню оружия при прокрутке для смены длительности.
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 237, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 237, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 238, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 238, true);
        }

        private void EnablePlayerControls()
        {
            Function.Call(Hash.ENABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.ENABLE_ALL_CONTROL_ACTIONS, 2);
        }

        public void Update()
        {
            try
            {
                _fadeMachine.Update();
                if (_mainCamera == null || !_mainCamera.Exists())
                {
                    Logger.Warn("PositionSelector.Update: Camera not available");
                    return;
                }

                bool isActive = _mainCamera.IsActive;
                // Демобилизуем героя на ВЕСЬ сеанс расстановки (включая фейды входа/выхода),
                // чтобы нельзя было ничего делать за него: движение, стрельба, меню оружия и т.п.
                bool sessionActive = isActive || _fadeMachine.State != FadeState.None;
                if (sessionActive)
                {
                    DisablePlayerControls();
                    _controlsDisabled = true;

                    if (isActive)
                    {
                        // Стриминг — на игроке (каждые 500мс), а не на свободной
                        // камере: иначе область героя выгружается и игра встаёт.
                        if (_focusTimer != null && _focusTimer.Enabled && _focusTimer.Check())
                        {
                            try
                            {
                                Ped player = Game.Player.Character;
                                if (player != null && player.Exists())
                                    CameraRenderer.UpdateFocusArea(player.Position);
                            }
                            catch (Exception ex) { Logger.Debug("PositionSelector focus update: " + ex.Message); }
                            _focusTimer.Reset();
                        }

                        bool shouldRender = _renderSceneTimer.Enabled && _renderSceneTimer.Check();
                        if (shouldRender)
                        {
                            CameraRenderer.DrawPositionMarker(_mainCamera.Position, _previousPos);
                            _renderSceneTimer.Reset();
                        }

                        _previousPos = _mainCamera.Position;
                        RenderEntityPosition();

                        try { GamepadHandler.Update(); } catch (Exception ex) { Logger.Debug("GamepadHandler.Update warning: " + ex.Message); }

                        // Roll (наклон горизонта / Dutch-angle): Z — влево, X — вправо, C — сброс.
                        // IsRawKeyDown — сырое состояние клавиш через WinAPI, работает даже
                        // при DisablePlayerControls() (IS_CONTROL_PRESSED был бы заблокирован).
                        if (IsRawKeyDown(Keys.C) && _mainCamera != null && _mainCamera.Exists())
                        {
                            Vector3 rotC = _mainCamera.Rotation;
                            _mainCamera.Rotation = new Vector3(rotC.X, 0f, rotC.Z);
                        }
                        float rollDelta = 0f;
                        if (IsRawKeyDown(Keys.Z)) rollDelta -= RollSpeed * Game.LastFrameTime;
                        if (IsRawKeyDown(Keys.X)) rollDelta += RollSpeed * Game.LastFrameTime;
                        if (rollDelta != 0f && _mainCamera != null && _mainCamera.Exists())
                        {
                            Vector3 rot = _mainCamera.Rotation;
                            _mainCamera.Rotation = new Vector3(rot.X, rot.Y + rollDelta, rot.Z);
                        }

                        // Подсказку управления рисуем КАЖДЫЙ кадр: scaleform держится на
                        // экране только пока вызывается Render2D() каждый тик, иначе он
                        // мигает (пропадает между редкими перерисовками).
                        try { RenderInstructionalButtons(); } catch (Exception ex) { Logger.Debug("RenderInstructionalButtons warning: " + ex.Message); }

                        // Время-зависимое затухание бленда (было -0.01 за тик).
                        float rawDecayDt = Game.LastFrameTime;
                        float decayDt = rawDecayDt > 0.05f ? 0.05f : (rawDecayDt < 0f ? 0f : rawDecayDt);
                        if (_currentLerpTime > 0f)
                        {
                            _currentLerpTime -= decayDt * 0.6f;
                            if (_currentLerpTime < 0f) _currentLerpTime = 0f;
                        }
                    }
                }
                else if (_controlsDisabled)
                {
                    _controlsDisabled = false;
                    EnablePlayerControls();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PositionSelector.Update: Critical error");
            }
        }

        private void RenderEntityPosition()
        {
            Vector3 position = Game.Player.Character.Position + Game.Player.Character.UpVector * 1.8f;
            Vector3 worldDown = Vector3.WorldDown;
            Vector3 rotation = new Vector3(90f, 0f, 0f);
            Vector3 scale3D = new Vector3(2f, 2f, 2f);
            Color yellow = Color.Yellow;
            DrawMarker(20, position, worldDown, rotation, scale3D, yellow, true, false, false);
        }

        private void DrawMarker(int type, Vector3 position, Vector3 direction, Vector3 rotation, Vector3 scale3D, Color color, bool animate, bool faceCam, bool rotate)
        {
            Function.Call(NativeHashes.DRAW_MARKER,
                type,
                position.X, position.Y, position.Z,
                direction.X, direction.Y, direction.Z,
                rotation.X, rotation.Y, rotation.Z,
                scale3D.X, scale3D.Y, scale3D.Z,
                (int)color.R, (int)color.G, (int)color.B, (int)color.A,
                animate, faceCam, 2, rotate,
                0, 0, 0);
        }

        private void RenderInstructionalButtons()
        {
            // Данные scaleform статичны: перестраиваем только по dirty-флагу
            // (вход в селектор). Показ каждый тик обязателен, иначе мигает.
            if (_instructionalDirty)
            {
                try
                {
                    EnsureInstructionalLabels();
                    BuildInstructionalButtons();
                }
                catch (Exception ex) { Logger.Debug("BuildInstructionalButtons warning: " + ex.Message); }
                _instructionalDirty = false;
            }

            _instructionalButtons.Render2D();
        }

        private void EnsureInstructionalLabels()
        {
            // Повторяем, пока пусто (игра могла быть не готова при первом построении).
            if (_lblSelect != null && _lblSelect.Length > 0)
                return;

            _lblSelect = GetControlLabel(2, 24);
            _lblIncrease = GetControlLabel(3, 17);
            _lblDecrease = GetControlLabel(1, 16);
            _lblExit = GetControlLabel(2, 25);
            _lblMoveUp = GetControlLabel(2, 32);
            _lblMoveDown = GetControlLabel(2, 34);
            _lblMoveLeft = GetControlLabel(2, 33);
            _lblMoveRight = GetControlLabel(2, 35);
        }

        private string GetControlLabel(int padIndex, int control)
        {
            try
            {
                return Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, padIndex, control, 0);
            }
            catch (Exception ex)
            {
                Logger.Debug("GetControlLabel warning: " + ex.Message);
                return string.Empty;
            }
        }

        private void BuildInstructionalButtons()
        {
            _instructionalButtons.CallFunction("CLEAR_ALL", new object[0]);
            _instructionalButtons.CallFunction("TOGGLE_MOUSE_BUTTONS", new object[] { false });

            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 4, _lblSelect, "Выбрать позицию" });
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 3, _lblIncrease, "Длительность +" });
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 2, _lblDecrease, "Длительность -" });
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 1, _lblExit, "Выход" });
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 0, _lblMoveRight, _lblMoveLeft, _lblMoveDown, _lblMoveUp, "Движение" });
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 5, "Z / X / C", "Наклон (влево/вправо/сброс)" });
            _instructionalButtons.CallFunction("SET_BACKGROUND_COLOUR", new object[] { 0, 0, 0, 80 });
            _instructionalButtons.CallFunction("DRAW_INSTRUCTIONAL_BUTTONS", new object[] { 0 });
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsRawKeyDown(Keys key)
        {
            // Сырое состояние клавиши через WinAPI — не зависит от GTA-контролов
            // и работает даже при DisablePlayerControls() в селекторе.
            return (GetAsyncKeyState((int)key) & 0x8000) != 0;
        }
    }
}
