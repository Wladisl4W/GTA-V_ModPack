using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;

namespace ModdedCamera.Services
{
    public class CameraService
    {
        public SplineCamera SplineCamera { get; private set; }
        public PositionSelector PositionSelector { get; private set; }

        public int CurrentFov { get; set; }
        public float CurrentSpeed { get; set; }
        public bool UsePlayerView { get; set; }

        public bool IsSplineCamActive
        {
            get
            {
                if (SplineCamera != null && SplineCamera.MainCamera != null)
                    return SplineCamera.MainCamera.IsActive;
                return false;
            }
        }

        public bool IsSelectorActive
        {
            get
            {
                if (PositionSelector != null && PositionSelector.MainCamera != null)
                    return PositionSelector.MainCamera.IsActive;
                return false;
            }
        }

        public bool IsAnyCameraActive
        {
            get { return IsSplineCamActive || IsSelectorActive; }
        }

        public int NodeDuration { get; set; }

        private bool _selectorWasUsed = false;
        private bool _splineCamWasUsed = false;
        private bool _isPlayerFollowing = false;
        private bool _savedPlayerVisible = true;
        private bool _savedPlayerCollision = true;
        private bool _savedPlayerInvincible = false;
        private bool _savedPlayerPosFrozen = false;
        private long _lastFollowTeleportMs = 0;
        private const int FollowTeleportIntervalMs = 500;
        private float _lastTimeScale = 1f;
        private long _playbackStartMs = 0;

        public CameraService()
        {
            CurrentFov = 50;
            CurrentSpeed = 1.0f;
            UsePlayerView = false;
            NodeDuration = 5000;
        }

        public void Initialize()
        {
            try
            {
                Logger.Info("CameraService: Initializing cameras...");
                SplineCamera = new SplineCamera();
                PositionSelector = new PositionSelector(Vector3.Zero, Vector3.Zero);
                ApplyCameraSettings();
                Logger.Info("CameraService: Cameras initialized");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error during initialization");
                throw;
            }
        }

        public void ApplyCameraSettings()
        {
            try
            {
                if (SplineCamera != null && SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                {
                    SplineCamera.MainCamera.FieldOfView = (float)CurrentFov;
                    SplineCamera.Speed = CurrentSpeed;
                    SplineCamera.UsePlayerView = UsePlayerView;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error applying camera settings");
            }
        }

        public void EnterPointSelector()
        {
            try
            {
                if (PositionSelector == null || SplineCamera == null)
                {
                    GTA.UI.Notification.PostTicker("~r~Камеры не инициализированы!", false, false);
                    Logger.Warn("CameraService: EnterPointSelector called but cameras not initialized");
                    return;
                }

                if (IsSelectorActive || IsSplineCamActive)
                {
                    GTA.UI.Notification.PostTicker("Камера уже активна.", false, false);
                    Logger.Warn("CameraService: EnterPointSelector rejected - camera already active");
                    return;
                }

                Logger.Info("CameraService: Entering point selector mode");
                Game.Player.Character.IsPositionFrozen = true;
                _selectorWasUsed = true;
                PositionSelector.EnterCameraView(Game.Player.Character.GetOffsetPosition(new Vector3(0f, 0f, 10f)));
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка!", false, false);
                Logger.Error(ex, "CameraService: Error in EnterPointSelector");
            }
        }

        public void ExitPointSelector()
        {
            try
            {
                Logger.Info("CameraService: Exiting point selector mode");
                if (PositionSelector != null)
                    PositionSelector.ExitCameraView();
                Game.Player.Character.IsPositionFrozen = false;
                _selectorWasUsed = false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error in ExitPointSelector");
            }
        }

        public bool AddNodeAtCurrentPosition()
        {
            try
            {
                if (SplineCamera == null) return false;
                if (PositionSelector == null || PositionSelector.MainCamera == null) return false;

                Vector3 pos = PositionSelector.MainCamera.Position;
                Vector3 rot = PositionSelector.MainCamera.Rotation;
                SplineCamera.AddNode(pos, rot, NodeDuration);

                Logger.Info("CameraService: Node added at (" + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1") + ")");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error adding node");
                return false;
            }
        }

        public bool StartPlayback()
        {
            try
            {
                if (SplineCamera == null)
                {
                    GTA.UI.Notification.PostTicker("~r~Камера не инициализирована!", false, false);
                    return false;
                }

                if (SplineCamera.Nodes.Count < 2)
                {
                    GTA.UI.Notification.PostTicker("Сначала создайте минимум 2 узла!", false, false);
                    Logger.Warn("CameraService: StartPlayback rejected - only " + SplineCamera.Nodes.Count + " nodes");
                    return false;
                }

                Logger.Info("CameraService: Starting playback with " + SplineCamera.Nodes.Count + " nodes");
                _splineCamWasUsed = true;
                _playbackStartMs = Utils.NowMs();
                SplineCamera.EnterCameraView(Game.Player.Character.GetOffsetPosition(new Vector3(0f, 0f, 10f)));
                SetupPlayerForFollow();
                return true;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка!", false, false);
                Logger.Error(ex, "CameraService: Error in StartPlayback");
                return false;
            }
        }

        public void StopPlayback()
        {
            try
            {
                TeleportPlayerBehindCamera();
                RestorePlayerState();
                if (SplineCamera != null && IsSplineCamActive)
                {
                    long realMs = Utils.NowMs() - _playbackStartMs;
                    Logger.Info("CameraService: Stopping playback. Real elapsed: " + realMs + " ms; nominal duration: "
                        + SplineCamera.NominalDurationMs + " ms; current (speed-adjusted) duration: "
                        + SplineCamera.CurrentDurationMs + " ms; speed x" + SplineCamera.Speed.ToString("F2")
                        + ". Ratio real/nominal: " + (SplineCamera.NominalDurationMs > 0 ? ((double)realMs / SplineCamera.NominalDurationMs).ToString("F2") : "n/a"));
                    Logger.Info("CameraService: Stopping playback");
                    SplineCamera.ExitCameraView();
                    _splineCamWasUsed = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error in StopPlayback");
            }
        }

        private void TeleportPlayerBehindCamera()
        {
            try
            {
                if (!_isPlayerFollowing || SplineCamera == null || SplineCamera.MainCamera == null || !SplineCamera.MainCamera.Exists())
                    return;
                var cam = SplineCamera.MainCamera;
                Vector3 dir = Utils.RotationToDirection(cam.Rotation);
                Vector3 followPos = cam.Position - dir * 2.0f + new Vector3(0f, 0f, 0.5f);
                Game.Player.Character.Position = followPos;
                _lastFollowTeleportMs = Utils.NowMs();
            }
            catch (Exception ex)
            {
                Logger.Debug("TeleportPlayerBehindCamera warning: " + ex.Message);
            }
        }

        public void RestartPlaybackIfActive()
        {
            try
            {
                if (SplineCamera == null || !IsSplineCamActive) return;
                Logger.Info("CameraService: Restarting playback due to settings change");
                if (SplineCamera.Nodes.Count > 0)
                    SplineCamera.RebuildSplineWithCurrentMode();
                SplineCamera.RestartInterpolator();
                Logger.Info("CameraService: Interpolator restarted");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error in RestartPlaybackIfActive");
            }
        }

        private void SetupPlayerForFollow()
        {
            try
            {
                var player = Game.Player.Character;
                if (player == null) return;
                _savedPlayerVisible = player.IsVisible;
                _savedPlayerCollision = player.IsCollisionEnabled;
                _savedPlayerInvincible = player.IsInvincible;
                _savedPlayerPosFrozen = player.IsPositionFrozen;
                player.IsVisible = false;
                player.IsCollisionEnabled = false;
                player.IsInvincible = true;
                player.IsPositionFrozen = true;
                _isPlayerFollowing = true;
                Logger.Info("CameraService: Player follow enabled");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error setting up player follow");
            }
        }

        private void RestorePlayerState()
        {
            try
            {
                if (!_isPlayerFollowing) return;
                var player = Game.Player.Character;
                if (player == null) return;
                player.IsVisible = _savedPlayerVisible;
                player.IsCollisionEnabled = _savedPlayerCollision;
                player.IsInvincible = _savedPlayerInvincible;
                player.IsPositionFrozen = _savedPlayerPosFrozen;
                _isPlayerFollowing = false;
                Logger.Info("CameraService: Player state restored");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error restoring player state");
            }
        }

        private void UpdatePlayerFollow()
        {
            if (!_isPlayerFollowing || SplineCamera == null || SplineCamera.MainCamera == null || !SplineCamera.MainCamera.Exists())
                return;
            try
            {
                long now = Utils.NowMs();
                if (now - _lastFollowTeleportMs < FollowTeleportIntervalMs)
                    return;
                _lastFollowTeleportMs = now;

                var cam = SplineCamera.MainCamera;
                Game.Player.Character.Position = cam.Position;
            }
            catch (Exception ex)
            {
                Logger.Debug("UpdatePlayerFollow warning: " + ex.Message);
            }
        }

        public bool LoadPath(CameraPath path)
        {
            try
            {
                if (path == null) return false;
                ResetAll();

                var nodes = path.ToNodes();
                for (int i = 0; i < nodes.Count; i++)
                {
                    int dur = (path.Durations.Count > i) ? path.Durations[i] : path.DefaultDuration;
                    int nodeMode = (path.NodeInterpolationModes.Count > i) ? path.NodeInterpolationModes[i] : 2;
                    int nodeColor = path.GetNodeColor(i);
                    SplineCamera.AddNode(nodes[i].Item1, nodes[i].Item2, dur, nodeMode, nodeColor);
                }

                NodeDuration = path.DefaultDuration;
                CurrentFov = path.Fov;
                CurrentSpeed = path.Speed;
                ApplyCameraSettings();

                Logger.Info("CameraService: Path loaded with " + nodes.Count + " nodes");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error loading path");
                return false;
            }
        }

        public Vector3 GetActiveCameraPosition()
        {
            if (IsSelectorActive && PositionSelector != null && PositionSelector.MainCamera != null)
                return PositionSelector.MainCamera.Position;
            if (IsSplineCamActive && SplineCamera != null && SplineCamera.MainCamera != null)
                return SplineCamera.MainCamera.Position;
            return Vector3.Zero;
        }

        public void Update()
        {
            try
            {
                ApplyTimeScale();

                if (IsSplineCamActive || _splineCamWasUsed)
                {
                    if (SplineCamera != null && SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                        SplineCamera.Update();
                    else if (SplineCamera != null)
                        Logger.Warn("CameraService: SplineCamera no longer exists");
                }

                UpdatePlayerFollow();

                if (IsSelectorActive || _selectorWasUsed)
                {
                    if (SplineCamera != null && SplineCamera.Nodes.Count > 0)
                        SplineCamera.DrawNodeMarkers();
                    if (PositionSelector != null && PositionSelector.MainCamera != null && PositionSelector.MainCamera.Exists())
                        PositionSelector.Update();
                    else if (PositionSelector != null)
                        Logger.Warn("CameraService: PositionSelector no longer exists");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error in Update");
            }
        }

        private void ApplyTimeScale()
        {
            try
            {
                float target = 1f;
                if (IsSplineCamActive && SplineCamera != null)
                {
                    float s = SplineCamera.Speed;
                    if (s > 0f && s < 1f)
                        target = s;
                }
                if (Math.Abs(target - _lastTimeScale) > 0.001f)
                {
                    _lastTimeScale = target;
                    Function.Call(Hash.SET_TIME_SCALE, target);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("ApplyTimeScale warning: " + ex.Message);
            }
        }

        public void ResetAll()
        {
            try
            {
                Logger.Info("CameraService: ResetAll called");
                Function.Call(NativeHashes.UNDO_SCREEN_FADE);
                CameraRenderer.ClearFocus();
                RestorePlayerState();
                _lastTimeScale = 1f;
                Function.Call(Hash.SET_TIME_SCALE, 1f);

                if (SplineCamera != null)
                {
                    if (SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                        SplineCamera.MainCamera.IsActive = false;
                    SplineCamera.Dispose();
                    SplineCamera = null;
                }

                if (PositionSelector != null)
                {
                    if (PositionSelector.MainCamera != null && PositionSelector.MainCamera.Exists())
                        PositionSelector.MainCamera.IsActive = false;
                    PositionSelector.Dispose();
                    PositionSelector = null;
                }

                ScriptCameraDirector.StopRendering(false);
                Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);

                Game.Player.Character.IsPositionFrozen = false;

                SplineCamera = new SplineCamera();
                PositionSelector = new PositionSelector(Vector3.Zero, Vector3.Zero);
                _selectorWasUsed = false;
                _splineCamWasUsed = false;
                ApplyCameraSettings();

                Logger.Info("CameraService: ResetAll completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error in ResetAll");
            }
        }

        public void Dispose()
        {
            try
            {
                Logger.Info("CameraService: Disposing...");
                Function.Call(NativeHashes.UNDO_SCREEN_FADE);
                CameraRenderer.ClearFocus();
                _lastTimeScale = 1f;
                Function.Call(Hash.SET_TIME_SCALE, 1f);

                if (SplineCamera != null)
                {
                    if (SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                    {
                        if (SplineCamera.UsePlayerView) SplineCamera.UsePlayerView = false;
                        SplineCamera.MainCamera.IsActive = false;
                    }
                    SplineCamera.Dispose();
                    SplineCamera = null;
                }

                if (PositionSelector != null)
                {
                    if (PositionSelector.MainCamera != null && PositionSelector.MainCamera.Exists())
                        PositionSelector.MainCamera.IsActive = false;
                    PositionSelector.Dispose();
                    PositionSelector = null;
                }

                ScriptCameraDirector.StopRendering(false);
                Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                CameraRenderer.ClearFocus();
                RestorePlayerState();

                Logger.Info("CameraService: Disposed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error during Dispose");
            }
        }
    }

    public class SaveService
    {
        public enum SaveState { None, Typing, ConfirmOverwrite }
        public SaveState State { get; private set; }

        private readonly CameraService _cameraService;
        private string _pendingPathName = "";
        private int _nameInputTimer = 0;
        private const int NAME_INPUT_TIMEOUT = 60000;

        public event Action<string> OnPathSaved;
        public event Action<string> OnPathLoaded;
        public event Action<string> OnPathDeleted;
        public event Action<string> OnError;

        public SaveService(CameraService cameraService)
        {
            if (cameraService == null) throw new ArgumentNullException("cameraService");
            _cameraService = cameraService;
            State = SaveState.None;
        }

        public bool StartSave()
        {
            if (_cameraService.SplineCamera != null && _cameraService.SplineCamera.Nodes.Count < 2)
            {
                GTA.UI.Notification.PostTicker("Нужно минимум 2 узла!", false, false);
                return false;
            }

            State = SaveState.Typing;
            _nameInputTimer = Game.GameTime;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 1, "FMMC_MPM_NA", "", "", "", "", "", 64);
            Logger.Info("SaveService: Save initiated");
            return true;
        }

        public void Update()
        {
            if (State == SaveState.None) return;
            try
            {
                if (State == SaveState.Typing)
                    UpdateTypingState();
                else if (State == SaveState.ConfirmOverwrite)
                    UpdateConfirmOverwriteState();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SaveService: Error in Update");
                State = SaveState.None;
            }
        }

        private void UpdateTypingState()
        {
            int currentTime = Game.GameTime;
            int elapsed = currentTime - _nameInputTimer;
            if (elapsed < 0) elapsed = int.MaxValue;
            if (elapsed > NAME_INPUT_TIMEOUT)
            {
                Logger.Warn("SaveService: Name input timed out");
                State = SaveState.None;
                return;
            }

            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 0) return;
            if (status == 2)
            {
                Logger.Info("SaveService: Save cancelled");
                State = SaveState.None;
                return;
            }

            string text = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
            if (string.IsNullOrEmpty(text))
            {
                State = SaveState.None;
                return;
            }

            if (PathManager.PathExists(text))
            {
                _pendingPathName = text;
                State = SaveState.ConfirmOverwrite;
                GTA.UI.Notification.PostTicker("~r~Имя уже существует! ~y~Пробел~w~=перезаписать, ~b~B~w~=переименовать", false, false);
                return;
            }

            DoSave(text);
            State = SaveState.None;
        }

        private void UpdateConfirmOverwriteState()
        {
            GTA.UI.Notification.PostTicker("~r~'" + _pendingPathName + "' уже существует! ~y~Пробел~w~=перезаписать, ~r~B~w~=переименовать", false, false);
            if (Game.IsControlJustPressed((GTA.Control)223))
            {
                DoSave(_pendingPathName);
                State = SaveState.None;
            }
            else if (Game.IsControlJustPressed(GTA.Control.FrontendAccept))
            {
                State = SaveState.Typing;
                _nameInputTimer = Game.GameTime;
                Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 1, "FMMC_MPM_NA", "", _pendingPathName, "", "", "", 64);
            }
            else if (Game.IsControlJustPressed(GTA.Control.FrontendCancel) || Game.IsControlJustPressed(GTA.Control.FrontendPause))
            {
                Logger.Info("SaveService: Overwrite cancelled");
                State = SaveState.None;
                _pendingPathName = "";
            }
        }

        private void DoSave(string name)
        {
            try
            {
                if (_cameraService.SplineCamera == null)
                {
                    GTA.UI.Notification.PostTicker("~r~SplineCamera не найдена!", false, false);
                    Logger.Error("DoSave: SplineCamera is null");
                    return;
                }

                var positions = _cameraService.SplineCamera.GetPositions();
                var rotations = _cameraService.SplineCamera.GetRotations();
                var durations = _cameraService.SplineCamera.GetDurations();
                var nodeModes = _cameraService.SplineCamera.GetNodeInterpolationModes();
                var nodeColors = _cameraService.SplineCamera.GetNodeColors();

                if (positions.Count < 2)
                {
                    GTA.UI.Notification.PostTicker("~r~Нужно минимум 2 узла!", false, false);
                    Logger.Warn("DoSave: Insufficient nodes");
                    return;
                }

                CameraPath cp = new CameraPath(
                    name, positions, rotations, durations, nodeModes,
                    _cameraService.NodeDuration,
                    _cameraService.CurrentFov,
                    _cameraService.CurrentSpeed,
                    2
                );
                cp.NodeColors = new List<int>(nodeColors);

                string result = PathManager.SavePath(cp);
                if (result != null)
                {
                    GTA.UI.Notification.PostTicker("~g~Сохранено: " + name, false, false);
                    _pendingPathName = "";
                    Logger.Info("SaveService: Path saved: " + result);
                    if (OnPathSaved != null) OnPathSaved(name);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось сохранить! Смотрите лог.", false, false);
                    Logger.Error("DoSave: PathManager.SavePath returned null");
                    if (OnError != null) OnError("Save failed");
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка: " + ex.Message, false, false);
                Logger.Error(ex, "SaveService: Error in DoSave");
                if (OnError != null) OnError(ex.Message);
            }
        }

        public bool LoadPath(string pathName)
        {
            try
            {
                CameraPath cp = PathManager.LoadPath(pathName);
                if (cp == null)
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось загрузить!", false, false);
                    if (OnError != null) OnError("Failed to load path");
                    return false;
                }

                bool success = _cameraService.LoadPath(cp);
                if (success)
                {
                    GTA.UI.Notification.PostTicker("~g~Загружено: " + pathName, false, false);
                    if (OnPathLoaded != null) OnPathLoaded(pathName);
                    Logger.Info("SaveService: Path loaded: " + pathName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось применить путь!", false, false);
                    if (OnError != null) OnError("Failed to apply path");
                }
                return success;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Failed to load!", false, false);
                Logger.Error(ex, "SaveService: Error loading path");
                if (OnError != null) OnError("Error loading path: " + ex.Message);
                return false;
            }
        }

        public bool DeletePath(string pathName)
        {
            try
            {
                bool success = PathManager.DeletePath(pathName);
                if (success)
                {
                    GTA.UI.Notification.PostTicker("~g~Удалено: " + pathName, false, false);
                    if (OnPathDeleted != null) OnPathDeleted(pathName);
                    Logger.Info("SaveService: Path deleted: " + pathName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось удалить!", false, false);
                    if (OnError != null) OnError("Failed to delete path");
                }
                return success;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка: " + ex.Message, false, false);
                Logger.Error(ex, "SaveService: Error deleting path");
                if (OnError != null) OnError(ex.Message);
                return false;
            }
        }

        public bool RenamePath(string oldName, string newName)
        {
            try
            {
                if (string.IsNullOrEmpty(newName))
                {
                    GTA.UI.Notification.PostTicker("~r~Имя не может быть пустым!", false, false);
                    return false;
                }
                bool success = PathManager.RenamePath(oldName, newName);
                if (success)
                {
                    GTA.UI.Notification.PostTicker("~g~Переименовано: " + oldName + " → " + newName, false, false);
                    Logger.Info("SaveService: Path renamed: " + oldName + " → " + newName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось переименовать!", false, false);
                    if (OnError != null) OnError("Failed to rename path");
                }
                return success;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка: " + ex.Message, false, false);
                Logger.Error(ex, "SaveService: Error renaming path");
                if (OnError != null) OnError(ex.Message);
                return false;
            }
        }

        public void Cancel()
        {
            State = SaveState.None;
            _pendingPathName = "";
        }
    }

    public class InputService
    {
        private static readonly int[] WeaponControls = { 24, 25, 26, 45, 138, 140, 141, 142, 143, 241, 242 };

        public event Action OnToggleMenu;
        public event Action OnBackNavigation;
        public event Action OnAddNode;
        public event Action OnExitPointSelector;
        public event Action OnScrollDurationUp;
        public event Action OnScrollDurationDown;

        public void ProcessKeyUp(Keys key)
        {
            if (key == Keys.T)
            {
                if (OnToggleMenu != null) OnToggleMenu();
            }
        }

        public bool ProcessKeyDown(Keys key)
        {
            if (key == Keys.Back)
            {
                if (OnBackNavigation != null) OnBackNavigation();
                return true;
            }
            return false;
        }

        public void ProcessPointSelectorInput()
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 24))
                {
                    if (OnAddNode != null) OnAddNode();
                }

                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 241, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 242, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 241, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 242, true);

                float scrollUp = Function.Call<float>(NativeHashes.GET_DISABLED_CONTROL_NORMAL, 0, 241);
                float scrollDown = Function.Call<float>(NativeHashes.GET_DISABLED_CONTROL_NORMAL, 0, 242);

                bool scrollUpPressed = scrollUp > 0.5f || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 241);
                bool scrollDownPressed = scrollDown > 0.5f || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 242);

                if (scrollUpPressed)
                {
                    if (OnScrollDurationUp != null) OnScrollDurationUp();
                }
                else if (scrollDownPressed)
                {
                    if (OnScrollDurationDown != null) OnScrollDurationDown();
                }

                if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 25))
                {
                    if (OnExitPointSelector != null) OnExitPointSelector();
                }
                else if (Game.IsControlJustPressed(GTA.Control.FrontendAccept))
                {
                    if (OnExitPointSelector != null) OnExitPointSelector();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "InputService: Error in ProcessPointSelectorInput");
            }
        }

        public void DisableWeaponControls()
        {
            foreach (int control in WeaponControls)
            {
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, control, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, control, true);
            }
        }

        public void DisableInterferingControls()
        {
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 199, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 200, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 199, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 200, true);
        }
    }

    public class MenuService
    {
        public NativeMenu MainMenu { get; private set; }
        public NativeMenu CameraOptionsMenu { get; private set; }
        public NativeMenu SavedPathsMenu { get; private set; }
        public NativeMenu NodeEditorMenu { get; private set; }

        private NativeItem _startItem;
        private NativeItem _stopItem;
        private NativeItem _setupNodesItem;
        private NativeItem _savePathItem;
        private NativeItem _loadPathItem;
        private NativeItem _cameraOptionsItem;
        private NativeItem _resetItem;
        private NativeItem _editNodesItem;
        private NativeItem _closeItem;

        private NativeListItem<string> _speedListItem;
        private NativeListItem<string> _fovListItem;
        private NativeCheckboxItem _usePlayerViewCheckbox;

        private readonly List<NativeMenu> _pathSubMenus = new List<NativeMenu>();
        private readonly List<NativeMenu> _nodeSubMenus = new List<NativeMenu>();

        private bool _editingSavedPath = false;
        private CameraPath _editingPath = null;
        private NativeMenu _editingPathBackMenu = null;
        private string _savedPathsSearch = "";
        private int _lastSelectedNodeIndex = -1;

        public ObjectPool ActivePool { get; private set; }

        private readonly CameraService _cameraService;
        private readonly SaveService _saveService;
        private readonly InputService _inputService;

        public MenuService(CameraService cameraService, SaveService saveService, InputService inputService)
        {
            if (cameraService == null) throw new ArgumentNullException("cameraService");
            if (saveService == null) throw new ArgumentNullException("saveService");
            if (inputService == null) throw new ArgumentNullException("inputService");
            _cameraService = cameraService;
            _saveService = saveService;
            _inputService = inputService;
        }

        public void Initialize()
        {
            CreateCameraOptionsMenu();
            CreateSavedPathsMenu();
            CreateNodeEditorMenu();
            CreateMainMenu();

            ActivePool = new ObjectPool();
            ActivePool.Add(MainMenu);
            ActivePool.Add(CameraOptionsMenu);
            ActivePool.Add(SavedPathsMenu);
            ActivePool.Add(NodeEditorMenu);

            RefreshSavedPathsMenu();
        }

        public void Process()
        {
            if (ActivePool != null) ActivePool.Process();
        }

        public bool AreAnyVisible
        {
            get { return (ActivePool != null) ? ActivePool.AreAnyVisible : false; }
        }

        public void ToggleMenu()
        {
            if (AreAnyVisible)
                ActivePool.HideAll();
            else
                MainMenu.Visible = true;
        }

        public void HideAll()
        {
            if (ActivePool != null) ActivePool.HideAll();
        }

        public void ShowMainMenu()
        {
            SavedPathsMenu.Visible = false;
            CameraOptionsMenu.Visible = false;
            MainMenu.Visible = true;
        }

        public void SyncCameraOptionsWithMenu()
        {
            try
            {
                _fovListItem.SelectedItem = _cameraService.CurrentFov.ToString();
                _speedListItem.SelectedItem = SnapSpeedToNearest(_cameraService.CurrentSpeed);
                _usePlayerViewCheckbox.Checked = _cameraService.UsePlayerView;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in SyncCameraOptionsWithMenu");
            }
        }

        public void RefreshSavedPathsMenu()
        {
            try
            {
                _pathSubMenus.Clear();
                SavedPathsMenu.Clear();

                List<string> allPaths = PathManager.GetAllSavedPaths();

                NativeItem searchItem = new NativeItem("~b~Поиск", string.IsNullOrEmpty(_savedPathsSearch) ? "Нажмите, чтобы ввести текст" : "Фильтр: \"" + _savedPathsSearch + "\"");
                searchItem.Activated += delegate
                {
                    try
                    {
                        Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, true, "R*", "", "", "", "", "", 64);
                        while (Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) == 0)
                            Script.Yield();
                        if (Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) == 1)
                        {
                            string input = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
                            if (!string.IsNullOrEmpty(input))
                            {
                                _savedPathsSearch = input.Trim();
                                RefreshSavedPathsMenu();
                            }
                        }
                        SavedPathsMenu.Visible = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Search input error");
                    }
                };
                SavedPathsMenu.Add(searchItem);

                if (!string.IsNullOrEmpty(_savedPathsSearch))
                {
                    NativeItem clearSearch = new NativeItem("~y~Сбросить поиск", "Показать все сохранённые пути");
                    clearSearch.Activated += delegate { _savedPathsSearch = ""; RefreshSavedPathsMenu(); };
                    SavedPathsMenu.Add(clearSearch);
                }

                List<string> filteredPaths;
                if (string.IsNullOrEmpty(_savedPathsSearch))
                    filteredPaths = allPaths;
                else
                    filteredPaths = allPaths.FindAll(p => p.IndexOf(_savedPathsSearch, StringComparison.OrdinalIgnoreCase) >= 0);

                if (allPaths.Count == 0)
                {
                    SavedPathsMenu.Add(new NativeItem("~y~Нет сохранённых путей", "Сначала сохраните путь!"));
                    return;
                }

                if (filteredPaths.Count == 0)
                {
                    SavedPathsMenu.Add(new NativeItem("~r~Совпадений нет", "Нет путей по запросу \"" + _savedPathsSearch + "\""));
                    return;
                }

                foreach (string pathName in filteredPaths)
                {
                    NativeMenu pathSubMenu = new NativeMenu(pathName, "Действия");
                    ActivePool.Add(pathSubMenu);
                    _pathSubMenus.Add(pathSubMenu);

                    NativeItem backBtn = new NativeItem("< Назад", "Вернуться назад");
                    string currentPathName = pathName;
                    backBtn.Activated += delegate { pathSubMenu.Visible = false; SavedPathsMenu.Visible = true; };
                    pathSubMenu.Add(backBtn);

                    NativeItem loadBtn = new NativeItem("~g~Загрузить", "Загрузить и воспроизвести");
                    string pn1 = pathName;
                    loadBtn.Activated += delegate
                    {
                        try
                        {
                            _saveService.LoadPath(pn1);
                            ActivePool.HideAll();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error loading: " + pn1);
                            GTA.UI.Notification.PostTicker("~r~Не удалось загрузить: " + ex.Message, false, false);
                        }
                    };
                    pathSubMenu.Add(loadBtn);

                    NativeItem renameBtn = new NativeItem("~y~Переименовать", "Переименовать этот путь");
                    string renamePn = pathName;
                    renameBtn.Activated += delegate
                    {
                        try
                        {
                            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, true, "R*", "", "", "", "", "", 64);
                            while (Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) == 0)
                                Script.Yield();
                            if (Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) == 1)
                            {
                                string newName = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
                                if (!string.IsNullOrEmpty(newName))
                                {
                                    newName = newName.Trim();
                                    if (_saveService.RenamePath(renamePn, newName))
                                    {
                                        pathSubMenu.Visible = false;
                                        RefreshSavedPathsMenu();
                                        SavedPathsMenu.Visible = true;
                                    }
                                }
                            }
                            SavedPathsMenu.Visible = true;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error renaming path");
                            GTA.UI.Notification.PostTicker("~r~Не удалось переименовать: " + ex.Message, false, false);
                        }
                    };
                    pathSubMenu.Add(renameBtn);

                    NativeMenu delMenu = new NativeMenu("Удалить: " + pathName, "Вы уверены?");
                    ActivePool.Add(delMenu);
                    _pathSubMenus.Add(delMenu);

                    NativeItem delBackBtn = new NativeItem("< Назад", "Отмена");
                    delBackBtn.Activated += delegate { delMenu.Visible = false; pathSubMenu.Visible = true; };
                    delMenu.Add(delBackBtn);

                    NativeItem delYesBtn = new NativeItem("~r~Да, удалить", "Подтвердить");
                    string pn2 = pathName;
                    delYesBtn.Activated += delegate
                    {
                        try
                        {
                            _saveService.DeletePath(pn2);
                            RefreshSavedPathsMenu();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error deleting: " + pn2);
                            GTA.UI.Notification.PostTicker("~r~Не удалось удалить: " + ex.Message, false, false);
                        }
                        delMenu.Visible = false;
                        pathSubMenu.Visible = true;
                    };
                    delMenu.Add(delYesBtn);

                    NativeItem deleteBtn = new NativeItem("~r~Удалить", "Удалить этот путь");
                    deleteBtn.Activated += delegate { pathSubMenu.Visible = false; delMenu.Visible = true; };
                    pathSubMenu.Add(deleteBtn);

                    NativeItem pathItem = new NativeItem(pathName, "Нажмите, чтобы открыть действия");
                    pathItem.Activated += delegate { SavedPathsMenu.Visible = false; pathSubMenu.Visible = true; };
                    SavedPathsMenu.Add(pathItem);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in RefreshSavedPathsMenu");
            }
        }

        public bool HandleBackNavigation()
        {
            try
            {
                foreach (var m in _pathSubMenus)
                {
                    if (m.Visible && m.BannerText.Text.StartsWith("Удалить: "))
                    {
                        m.Visible = false;
                        string pathName = m.BannerText.Text.Substring("Удалить: ".Length);
                        foreach (var pm in _pathSubMenus)
                        {
                            if (pm.BannerText.Text == pathName && !pm.Visible)
                            {
                                pm.Visible = true;
                                break;
                            }
                        }
                        return true;
                    }
                }

                foreach (var m in _pathSubMenus)
                {
                    if (m.Visible)
                    {
                        m.Visible = false;
                        SavedPathsMenu.Visible = true;
                        return true;
                    }
                }

                if (SavedPathsMenu.Visible)
                {
                    SavedPathsMenu.Visible = false;
                    MainMenu.Visible = true;
                    return true;
                }

                foreach (var m in _nodeSubMenus)
                {
                    if (m.Visible)
                    {
                        m.Visible = false;
                        NodeEditorMenu.Visible = true;
                        return true;
                    }
                }

                if (NodeEditorMenu.Visible)
                {
                    NodeEditorMenu.Visible = false;
                    if (_editingSavedPath)
                    {
                        if (_editingPath != null) PathManager.SavePath(_editingPath);
                        _editingSavedPath = false;
                        _editingPath = null;
                        NativeMenu backMenu = _editingPathBackMenu;
                        _editingPathBackMenu = null;
                        if (backMenu != null) backMenu.Visible = true;
                    }
                    else
                    {
                        MainMenu.Visible = true;
                    }
                    return true;
                }

                if (CameraOptionsMenu.Visible)
                {
                    CameraOptionsMenu.Visible = false;
                    MainMenu.Visible = true;
                    return true;
                }

                if (_cameraService.IsSelectorActive)
                {
                    _cameraService.ExitPointSelector();
                    return true;
                }

                if (MainMenu.Visible) return true;
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in HandleBackNavigation");
                return false;
            }
        }

        public int GetNodeDuration() { return _cameraService.NodeDuration; }

        public void IncreaseNodeDuration()
        {
            _cameraService.NodeDuration += 250;
        }

        public void DecreaseNodeDuration()
        {
            _cameraService.NodeDuration = Math.Max(250, _cameraService.NodeDuration - 250);
        }

        public void Dispose()
        {
            if (ActivePool != null)
            {
                ActivePool.HideAll();
                ActivePool = null;
            }
        }

        private void CreateMainMenu()
        {
            MainMenu = new NativeMenu("ModdedCamera", "Кинематографическая камера");

            _startItem = new NativeItem("~g~Воспроизвести путь", "");
            _startItem.Activated += (s, e) => _cameraService.StartPlayback();
            MainMenu.Add(_startItem);

            _stopItem = new NativeItem("~r~Остановить воспроизведение", "");
            _stopItem.Activated += (s, e) => _cameraService.StopPlayback();
            MainMenu.Add(_stopItem);

            _setupNodesItem = new NativeItem("~y~Настроить узлы", "");
            _setupNodesItem.Activated += (s, e) =>
            {
                ActivePool.HideAll();
                _cameraService.EnterPointSelector();
            };
            MainMenu.Add(_setupNodesItem);

            _savePathItem = new NativeItem("Сохранить текущий путь", "");
            _savePathItem.Activated += (s, e) =>
            {
                ActivePool.HideAll();
                _saveService.StartSave();
            };
            MainMenu.Add(_savePathItem);

            _loadPathItem = new NativeItem("Загрузить путь", "Выберите сохранённый путь для загрузки");
            _loadPathItem.Activated += (s, e) => { MainMenu.Visible = false; SavedPathsMenu.Visible = true; };
            MainMenu.Add(_loadPathItem);

            _cameraOptionsItem = new NativeItem("Настройки камеры", "Настроить параметры камеры");
            _cameraOptionsItem.Activated += (s, e) => { MainMenu.Visible = false; CameraOptionsMenu.Visible = true; };
            MainMenu.Add(_cameraOptionsItem);

            _editNodesItem = new NativeItem("~y~Редактор узлов", "Изменить длительность и интерполяцию каждого узла");
            _editNodesItem.Activated += (s, e) =>
            {
                _editingSavedPath = false;
                _editingPath = null;
                _editingPathBackMenu = null;
                _lastSelectedNodeIndex = -1;
                RefreshNodeEditorMenu();
                MainMenu.Visible = false;
                NodeEditorMenu.Visible = true;
            };
            MainMenu.Add(_editNodesItem);

            _resetItem = new NativeItem("Сбросить все камеры", "");
            _resetItem.Activated += (s, e) => _cameraService.ResetAll();
            MainMenu.Add(_resetItem);

            _closeItem = new NativeItem("Закрыть", "");
            _closeItem.Activated += (s, e) => ActivePool.HideAll();
            MainMenu.Add(_closeItem);
        }

        private void CreateCameraOptionsMenu()
        {
            CameraOptionsMenu = new NativeMenu("Настройки камеры", "");

            _speedListItem = new NativeListItem<string>("Скорость", "Множитель скорости воспроизведения");
            string[] speedValues = new string[] { "x0.10", "x0.25", "x0.50", "x0.75", "x1.00", "x1.25", "x1.50", "x1.75", "x2.00", "x2.50", "x3.00", "x4.00", "x5.00", "x10.00" };
            foreach (string sv in speedValues)
                _speedListItem.Items.Add(sv);
            _speedListItem.SelectedItem = "x1.00";
            CameraOptionsMenu.Add(_speedListItem);

            _fovListItem = new NativeListItem<string>("Поле зрения (FOV)", "");
            for (int i = 1; i <= 100; i++)
                _fovListItem.Items.Add(i.ToString());
            _fovListItem.SelectedItem = "50";
            CameraOptionsMenu.Add(_fovListItem);

            _usePlayerViewCheckbox = new NativeCheckboxItem("Вид от игрока", "(Плавнее рендеринг местности, но ограничено движение)");
            CameraOptionsMenu.Add(_usePlayerViewCheckbox);

            _speedListItem.ItemChanged += OnSpeedChanged;
            _fovListItem.ItemChanged += OnFovChanged;
            _usePlayerViewCheckbox.CheckboxChanged += OnCheckboxChanged;
        }

        private void CreateSavedPathsMenu()
        {
            SavedPathsMenu = new NativeMenu("Сохранённые пути", "");
        }

        private void CreateNodeEditorMenu()
        {
            NodeEditorMenu = new NativeMenu("Редактор узлов", "Выберите узел для редактирования");
        }

        public void RefreshNodeEditorMenu()
        {
            try
            {
                _nodeSubMenus.Clear();
                NodeEditorMenu.Clear();

                if (_editingSavedPath && _editingPath == null)
                {
                    _editingSavedPath = false;
                }

                int nodeCount;
                if (_editingSavedPath)
                {
                    if (_editingPath.Positions == null || _editingPath.Positions.Count == 0 ||
                        _editingPath.Rotations == null || _editingPath.Rotations.Count == 0)
                    {
                        NodeEditorMenu.Add(new NativeItem("~r~Пустой или повреждённый путь", "В этом пути нет узлов"));
                        return;
                    }
                    nodeCount = _editingPath.Positions.Count;
                }
                else
                {
                    var spline = _cameraService.SplineCamera;
                    if (spline == null || spline.Nodes.Count == 0)
                    {
                        NodeEditorMenu.Add(new NativeItem("~y~Нет узлов", "Сначала добавьте узлы (Настроить узлы)"));
                        return;
                    }
                    nodeCount = spline.Nodes.Count;
                }

                NativeItem backMain = new NativeItem("< Назад", _editingSavedPath ? "Вернуться к сохранённому пути" : "Вернуться в главное меню");
                backMain.Activated += delegate
                {
                    if (_editingSavedPath)
                    {
                        if (_editingPath != null) PathManager.SavePath(_editingPath);
                        _editingSavedPath = false;
                        _editingPath = null;
                        NativeMenu backMenu = _editingPathBackMenu;
                        _editingPathBackMenu = null;
                        NodeEditorMenu.Visible = false;
                        if (backMenu != null) backMenu.Visible = true;
                    }
                    else
                    {
                        NodeEditorMenu.Visible = false;
                        MainMenu.Visible = true;
                    }
                };
                NodeEditorMenu.Add(backMain);

                float totalSec = 0f;
                for (int i = 0; i < nodeCount; i++)
                {
                    try
                    {
                        int nodeIndex = i;
                        Vector3 pos;
                        int duration;
                        int nodeMode;

                        if (_editingSavedPath)
                        {
                            pos = _editingPath.Positions[i];
                            duration = (i < _editingPath.Durations.Count) ? _editingPath.Durations[i] : _editingPath.DefaultDuration;
                            nodeMode = _editingPath.GetNodeMode(i);
                        }
                        else
                        {
                            var spline = _cameraService.SplineCamera;
                            pos = spline.Nodes[i].Item1;
                            duration = spline.GetDurations()[i];
                            nodeMode = (i < spline.GetNodeInterpolationModes().Count) ? spline.GetNodeInterpolationModes()[i] : 2;
                        }

                        string modeLabel = (nodeMode == 0) ? "Линейно" : (nodeMode == 1) ? "Плавно (без остановки)" : "Плавно";
                        float durSec = (float)duration / 1000f;
                        totalSec += durSec;
                        string label = "Узел " + (i + 1) + "  (" + durSec.ToString("F2") + "с, " + modeLabel + ") | всего: " + totalSec.ToString("F2") + "с";
                        string desc = "Поз: " + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1");

                        NativeMenu nodeMenu = new NativeMenu("Узел " + (i + 1), "Длительность и интерполяция");
                        ActivePool.Add(nodeMenu);
                        _nodeSubMenus.Add(nodeMenu);

                        NativeItem nodeBack = new NativeItem("< Назад", "К списку узлов");
                        nodeBack.Activated += delegate { nodeMenu.Visible = false; NodeEditorMenu.Visible = true; if (_lastSelectedNodeIndex >= 0) NodeEditorMenu.SelectedIndex = _lastSelectedNodeIndex + 1; };
                        nodeMenu.Add(nodeBack);

                        // Duration list item: 0.00..30.00 in 0.25s steps
                        NativeListItem<string> durItem = new NativeListItem<string>("Длительность", "Длительность узла в секундах");
                        for (int d = 0; d <= 30000; d += 250)
                            durItem.Items.Add(((float)d / 1000f).ToString("F2"));
                        string foundDur = durSec.ToString("F2");
                        if (durItem.Items.Contains(foundDur))
                            durItem.SelectedItem = foundDur;
                        int capturedIndex = nodeIndex;
                        durItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            float newDurSec;
                            if (float.TryParse(args.Object, out newDurSec) && newDurSec >= 0f)
                            {
                                int newDurMs = (int)(newDurSec * 1000f);
                                if (_editingSavedPath && _editingPath != null)
                                {
                                    while (_editingPath.Durations.Count <= capturedIndex)
                                        _editingPath.Durations.Add(_editingPath.DefaultDuration);
                                    _editingPath.Durations[capturedIndex] = newDurMs;
                                    PathManager.SavePath(_editingPath);
                                    RefreshNodeEditorMenu();
                                }
                                else
                                {
                                    var sp = _cameraService.SplineCamera;
                                    if (sp != null)
                                    {
                                        sp.SetNodeDuration(capturedIndex, newDurMs);
                                        sp.SetStartNodeIndex(capturedIndex);
                                        _cameraService.RestartPlaybackIfActive();
                                        RefreshNodeEditorMenu();
                                    }
                                }
                            }
                        };
                        nodeMenu.Add(durItem);

                        // Interpolation mode
                        NativeListItem<string> modeItem = new NativeListItem<string>("Интерполяция", "Режим движения камеры для узла");
                        modeItem.Items.Add("Линейно");
                        modeItem.Items.Add("Плавно (без остановки)");
                        modeItem.Items.Add("Плавно");
                        modeItem.SelectedItem = (nodeMode == 0) ? "Линейно" : (nodeMode == 1) ? "Плавно (без остановки)" : "Плавно";
                        int capturedIndex2 = nodeIndex;
                        modeItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            int newMode = (args.Object == "Линейно") ? 0 : (args.Object == "Плавно (без остановки)") ? 1 : 2;
                            if (_editingSavedPath && _editingPath != null)
                            {
                                while (_editingPath.NodeInterpolationModes.Count <= capturedIndex2)
                                    _editingPath.NodeInterpolationModes.Add(newMode);
                                _editingPath.NodeInterpolationModes[capturedIndex2] = newMode;
                                PathManager.SavePath(_editingPath);
                                RefreshNodeEditorMenu();
                            }
                            else
                            {
                                var sp = _cameraService.SplineCamera;
                                if (sp != null)
                                {
                                    sp.SetNodeInterpolationMode(capturedIndex2, newMode);
                                    sp.SetStartNodeIndex(capturedIndex2);
                                    _cameraService.RestartPlaybackIfActive();
                                    RefreshNodeEditorMenu();
                                }
                            }
                        };
                        nodeMenu.Add(modeItem);

                        // Node color
                        string[] colorNames = new string[]
                        {
                            "Белый", "Жёлтый", "Красный", "Оранжевый", "Зелёный",
                            "Голубой", "Синий", "Фиолетовый", "Розовый", "Серый",
                            "Бирюзовый", "Коричневый"
                        };
                        Color[] colorValues = new Color[]
                        {
                            Color.White, Color.Yellow, Color.Red, Color.Orange, Color.Lime,
                            Color.Cyan, Color.DodgerBlue, Color.Purple, Color.DeepPink, Color.Gray,
                            Color.Turquoise, Color.SaddleBrown
                        };
                        int curArgb = _editingSavedPath && _editingPath != null
                            ? _editingPath.GetNodeColor(nodeIndex)
                            : _cameraService.SplineCamera.GetNodeColor(nodeIndex);
                        NativeListItem<string> colorItem = new NativeListItem<string>("Цвет", "Цвет маркера узла для ориентации при редактировании");
                        for (int ci = 0; ci < colorNames.Length; ci++)
                            colorItem.Items.Add(colorNames[ci]);
                        string foundColor = "Белый";
                        for (int ci = 0; ci < colorValues.Length; ci++)
                        {
                            if (colorValues[ci].ToArgb() == curArgb)
                            {
                                foundColor = colorNames[ci];
                                break;
                            }
                        }
                        colorItem.SelectedItem = foundColor;
                        int capturedColorIndex = nodeIndex;
                        colorItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
                        {
                            int newArgb = Color.White.ToArgb();
                            for (int ci = 0; ci < colorNames.Length; ci++)
                            {
                                if (colorNames[ci] == args.Object)
                                {
                                    newArgb = colorValues[ci].ToArgb();
                                    break;
                                }
                            }
                            if (_editingSavedPath && _editingPath != null)
                            {
                                _editingPath.SetNodeColor(capturedColorIndex, newArgb);
                                PathManager.SavePath(_editingPath);
                                RefreshNodeEditorMenu();
                            }
                            else
                            {
                                var sp = _cameraService.SplineCamera;
                                if (sp != null)
                                {
                                    sp.SetNodeColor(capturedColorIndex, newArgb);
                                    RefreshNodeEditorMenu();
                                }
                            }
                        };
                        nodeMenu.Add(colorItem);

                        NativeItem nodeItem = new NativeItem(label, desc);
                        if (curArgb != Color.White.ToArgb())
                        {
                            Color nodeTextColor = Color.FromArgb(curArgb);
                            nodeItem.Colors.TitleNormal = nodeTextColor;
                            nodeItem.Colors.TitleHovered = nodeTextColor;
                        }
                        int capturedNodeIndex = nodeIndex;
                        nodeItem.Activated += delegate { _lastSelectedNodeIndex = capturedNodeIndex; NodeEditorMenu.Visible = false; nodeMenu.Visible = true; };
                        NodeEditorMenu.Add(nodeItem);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "MenuService: Error creating node item " + i);
                        NodeEditorMenu.Add(new NativeItem("~r~Узел " + (i + 1) + " (ошибка)", ex.Message));
                    }
                }
                int restoreIdx = _lastSelectedNodeIndex + 1;
                if (_lastSelectedNodeIndex >= 0 && restoreIdx < NodeEditorMenu.Items.Count)
                    NodeEditorMenu.SelectedIndex = restoreIdx;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in RefreshNodeEditorMenu");
            }
        }

        private void OnSpeedChanged(object sender, ItemChangedEventArgs<string> e)
        {
            string sel = e.Object;
            if (sel != null && sel.StartsWith("x"))
            {
                float v;
                if (float.TryParse(sel.Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0f)
                {
                    _cameraService.CurrentSpeed = v;
                    var sc = _cameraService.SplineCamera;
                    if (sc != null && sc.Nodes.Count >= 2)
                    {
                        sc.Speed = v;
                        sc.RestartInterpolator();
                    }
                    Logger.Info("MenuService: Speed changed to x" + v.ToString("F2"));
                }
            }
        }

        private void OnFovChanged(object sender, ItemChangedEventArgs<string> e)
        {
            int v;
            if (int.TryParse(_fovListItem.SelectedItem, out v) && v > 0)
            {
                _cameraService.CurrentFov = v;
                _cameraService.ApplyCameraSettings();
                Logger.Info("MenuService: FOV changed to: " + v);
            }
        }

        private void OnCheckboxChanged(object sender, EventArgs e)
        {
            try
            {
                if (sender == _usePlayerViewCheckbox)
                    _cameraService.UsePlayerView = _usePlayerViewCheckbox.Checked;
                _cameraService.ApplyCameraSettings();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuService: Error in OnCheckboxChanged");
            }
        }

        private static string SnapSpeedToNearest(float speed)
        {
            float[] validSpeeds = new float[] { 0.10f, 0.25f, 0.50f, 0.75f, 1.00f, 1.25f, 1.50f, 1.75f, 2.00f, 2.50f, 3.00f, 4.00f, 5.00f, 10.00f };
            float nearest = validSpeeds[0];
            float minDiff = Math.Abs(speed - nearest);
            for (int i = 1; i < validSpeeds.Length; i++)
            {
                float diff = Math.Abs(speed - validSpeeds[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearest = validSpeeds[i];
                }
            }
            return "x" + nearest.ToString("F2");
        }
    }
}
