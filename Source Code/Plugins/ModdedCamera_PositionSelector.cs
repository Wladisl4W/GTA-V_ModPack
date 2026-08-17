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
        private Scaleform _instructionalButtons;
        private float _currentLerpTime;
        private readonly float LerpTime = 0.5f;
        private readonly float RotationSpeed = 0.7f;
        private readonly float RollSpeed = 90f; // градусов в секунду (наклон горизонта)
        private bool _controlsDisabled = false;

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
            float deltaTime = Game.LastFrameTime;
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
            _currentLerpTime += 0.02f;
            if (_currentLerpTime > LerpTime) _currentLerpTime = LerpTime;
            float num = _currentLerpTime / LerpTime;
            _mainCamera.Position = Vector3.Lerp(_mainCamera.Position, _previousPos, num);
        }

        private void RightStickChanged(object sender, AnalogStickChangedEventArgs e)
        {
            float deltaTime = Game.LastFrameTime;
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
            _mainCamera.Position = position;
            _fadeMachine.StartFadeOut(1200);
            DisablePlayerControls();
        }

        public void ExitCameraView()
        {
            CameraRenderer.ClearFocus();
            _fadeMachine.StartFadeOutExit(1200);
            EnablePlayerControls();
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
                        bool shouldRender = _renderSceneTimer.Enabled && _renderSceneTimer.Check();
                        if (shouldRender)
                        {
                            CameraRenderer.UpdateFocusArea(_mainCamera.Position);
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

                        if (_currentLerpTime > 0f) _currentLerpTime -= 0.01f;
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
            _instructionalButtons.CallFunction("CLEAR_ALL", new object[0]);
            _instructionalButtons.CallFunction("TOGGLE_MOUSE_BUTTONS", new object[] { false });

            string text = Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 2, 24, 0);
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 4, text, "Выбрать позицию" });

            text = Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 3, 17, 0);
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 3, text, "Длительность +" });

            text = Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 1, 16, 0);
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 2, text, "Длительность -" });

            text = Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 2, 25, 0);
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 1, text, "Выход" });

            string[] array = new string[]
            {
                Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 2, 32, 0),
                Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 2, 34, 0),
                Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 2, 33, 0),
                Function.Call<string>(NativeHashes.GET_CONTROL_ACTION_NAME, 2, 35, 0)
            };
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 0, array[3], array[2], array[1], array[0], "Движение" });
            _instructionalButtons.CallFunction("SET_DATA_SLOT", new object[] { 5, "Z / X / C", "Наклон (влево/вправо/сброс)" });
            _instructionalButtons.CallFunction("SET_BACKGROUND_COLOUR", new object[] { 0, 0, 0, 80 });
            _instructionalButtons.CallFunction("DRAW_INSTRUCTIONAL_BUTTONS", new object[] { 0 });
            _instructionalButtons.Render2D();
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
