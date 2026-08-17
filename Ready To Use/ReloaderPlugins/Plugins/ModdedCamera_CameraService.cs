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
        private int _editNodeIndex = -1;

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
                    SplineCamera.DefaultFov = CurrentFov;
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

        public void EnterPointSelectorForNode(int nodeIndex)
        {
            try
            {
                if (PositionSelector == null || SplineCamera == null)
                {
                    GTA.UI.Notification.PostTicker("~r~Камеры не инициализированы!", false, false);
                    return;
                }
                if (nodeIndex < 0 || nodeIndex >= SplineCamera.Nodes.Count)
                {
                    GTA.UI.Notification.PostTicker("~r~Узел не найден!", false, false);
                    return;
                }
                if (IsSelectorActive || IsSplineCamActive)
                {
                    GTA.UI.Notification.PostTicker("Камера уже активна.", false, false);
                    return;
                }

                Logger.Info("CameraService: Entering point selector to edit node " + nodeIndex);
                _editNodeIndex = nodeIndex;
                Game.Player.Character.IsPositionFrozen = true;
                _selectorWasUsed = true;
                var node = SplineCamera.Nodes[nodeIndex];
                PositionSelector.EnterCameraView(node.Item1);
                if (PositionSelector.MainCamera != null)
                    PositionSelector.MainCamera.Rotation = node.Item2;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка!", false, false);
                Logger.Error(ex, "CameraService: Error in EnterPointSelectorForNode");
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
                Function.Call(Hash.SET_TIME_SCALE, 1f);
                _selectorWasUsed = false;
                _editNodeIndex = -1;
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

                if (_editNodeIndex >= 0)
                {
                    if (_editNodeIndex < SplineCamera.Nodes.Count)
                    {
                        SplineCamera.SetNodePosition(_editNodeIndex, pos, rot);
                        Logger.Info("CameraService: Node " + _editNodeIndex + " updated at (" + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1") + ")");
                    }
                    ExitPointSelector();
                    GTA.UI.Notification.PostTicker("~g~Узел обновлён!", false, false);
                    return true;
                }

                SplineCamera.AddNode(pos, rot, NodeDuration, 2, Color.White.ToArgb(), CurrentFov);
                GTA.UI.Notification.PostTicker("Узел добавлен\nПоз: (" + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1") + ")\nДлительность: " + ((float)NodeDuration / 1000f).ToString("F2") + "с", false, false);

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

                if (IsSelectorActive)
                {
                    GTA.UI.Notification.PostTicker("~y~Сначала выйдите из режима расстановки.", false, false);
                    Logger.Warn("CameraService: StartPlayback rejected - selector still active");
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
                if (SplineCamera != null && IsSplineCamActive)
                {
                    long realMs = Utils.NowMs() - _playbackStartMs;
                    Logger.Info("CameraService: Stopping playback. Real elapsed: " + realMs + " ms; nominal duration: "
                        + SplineCamera.NominalDurationMs + " ms; current (speed-adjusted) duration: "
                        + SplineCamera.CurrentDurationMs + " ms; speed x" + SplineCamera.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                        + ". Ratio real/nominal: " + (SplineCamera.NominalDurationMs > 0 ? ((double)realMs / SplineCamera.NominalDurationMs).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "n/a"));
                    Logger.Info("CameraService: Stopping playback");
                    SplineCamera.ExitCameraView();
                    _splineCamWasUsed = false;
                }
                TeleportPlayerBehindCamera();
                RestorePlayerState();
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
                    int nodeFov = (path.NodeFovs != null && i < path.NodeFovs.Count) ? path.NodeFovs[i] : 50;
                    SplineCamera.AddNode(nodes[i].Item1, nodes[i].Item2, dur, nodeMode, nodeColor, nodeFov);
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
                // Кинематографичный slow-mo: когда скорость пролётки < 1, замедляем
                // ВЕСЬ мир пропорционально, чтобы камера и мир двигались синхронно.
                // При выходе из камеры (IsSplineCamActive=false) время всегда сбрасывается в 1.
                float target = 1f;
                if (IsSplineCamActive && SplineCamera != null)
                {
                    float s = CurrentSpeed;
                    if (s > 0.05f && s < 1f)
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

}
