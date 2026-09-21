using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;
using ModPack;

namespace ModdedCamera.Services
{
    public class CameraService
    {
        private readonly ControlSession _control = new ControlSession();
        private bool _splineExitPending;
        public bool HasControl { get { return _control.Held; } }

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

        // Вызывается после правки узла во время воспроизведения, когда
        // воспроизведение было временно остановлено и затем возобновлено.
        public event Action<int> OnNodeEditResumed;

        private bool _selectorWasUsed = false;
        private bool _splineCamWasUsed = false;

        // True между ExitPointSelector() и фактическим завершением фейда.
        // Пока висит — селектор продолжает тикать, чтобы его fade-машина
        // дошла до None (иначе ранний выход клинит FadingOutExit навсегда
        // и игрок потом дёргается "покадрово").
        private bool _selectorExitPending = false;
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
        private bool _resumePlaybackAfterNodeEdit = false;

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
                SplineCamera = new SplineCamera() { HasControl = delegate { return _control.Held; } };
                PositionSelector = new PositionSelector(Vector3.Zero, Vector3.Zero) { HasControl = delegate { return _control.Held; } };
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

                if (!_control.Acquire("Камера", true)) return;
                DevelopmentDiagnostics.State("Camera", "Position selector");
                Logger.Info("CameraService: Entering point selector mode");
                Game.Player.Character.IsPositionFrozen = true;
                _selectorWasUsed = true;
                try
                {
                    // Повторный вход, пока старый выходной фейд ещё не догорел:
                    // гасим его, чтобы поздний onDeactivate не убил новую сессию.
                    if (_selectorExitPending)
                    {
                        PositionSelector.AbortPendingFade();
                        _selectorExitPending = false;
                    }
                    PositionSelector.EnterCameraView(Game.Player.Character.GetOffsetPosition(new Vector3(0f, 0f, 10f)));
                }
                catch
                {
                    // Откат: никогда не оставляем героя замороженным при неудачном входе.
                    // Original player state is restored by the control session.
                    _selectorWasUsed = false;
                    _selectorExitPending = false;
                    throw;
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка!", false, false);
                Logger.Error(ex, "CameraService: Error in EnterPointSelector");
                EndControlImmediately();
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
                if (IsSelectorActive)
                {
                    GTA.UI.Notification.PostTicker("Камера уже активна.", false, false);
                    return;
                }
                if (!_control.Acquire("Камера", true)) return;
                if (IsSplineCamActive)
                {
                    // Во время воспроизведения spline-камера занята. Временно
                    // останавливаем её, чтобы открыть свободную камеру узла для
                    // правки. После применения изменений воспроизведение
                    // возобновится (см. AddNodeAtCurrentPosition).
                    _resumePlaybackAfterNodeEdit = true;
                    StopPlayback();
                    // Сразу гасим spline-камеру и отменяем её отложенный
                    // onDeactivate, иначе он сработает ~1.2с спустя и убьёт
                    // рендер селектора, который сейчас включится.
                    if (SplineCamera != null && SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                        SplineCamera.MainCamera.IsActive = false;
                    if (SplineCamera != null) SplineCamera.AbortPendingFade();
                    _splineExitPending = false;
                    _splineCamWasUsed = false;
                }

                Logger.Info("CameraService: Entering point selector to edit node " + nodeIndex);
                _editNodeIndex = nodeIndex;
                Game.Player.Character.IsPositionFrozen = true;
                _selectorWasUsed = true;
                try
                {
                    if (_selectorExitPending)
                    {
                        PositionSelector.AbortPendingFade();
                        _selectorExitPending = false;
                    }
                    var node = SplineCamera.Nodes[nodeIndex];
                    PositionSelector.EnterCameraView(node.Item1);
                    if (PositionSelector.MainCamera != null)
                        PositionSelector.MainCamera.Rotation = node.Item2;
                }
                catch
                {
                    // Original player state is restored by the control session.
                    _selectorWasUsed = false;
                    _selectorExitPending = false;
                    _editNodeIndex = -1;
                    throw;
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка!", false, false);
                Logger.Error(ex, "CameraService: Error in EnterPointSelectorForNode");
                EndControlImmediately();
            }
        }

        public void ExitPointSelector()
        {
            if (!_control.Held || (!_selectorWasUsed && !IsSelectorActive && !_selectorExitPending)) return;
            try
            {
                Logger.Info("CameraService: Exiting point selector mode");
                try
                {
                    if (PositionSelector != null)
                        PositionSelector.ExitCameraView();
                }
                finally
                {
                    // Разморозка и сброс времени — всегда, даже если выход бросил.
                    // _selectorWasUsed гасится только по завершении фейда (см. Update),
                    // чтобы fade-машина продолжала тикать до None.
                    // Original player state is restored by the control session.
                    try { _control.TimeScale(_control.OriginalTimeScale); } catch { }
                    _selectorExitPending = true;
                }
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
                    int editedNodeIndex = _editNodeIndex;
                    if (_editNodeIndex < SplineCamera.Nodes.Count)
                    {
                        SplineCamera.SetNodePosition(_editNodeIndex, pos, rot);
                        Logger.Info("CameraService: Node " + _editNodeIndex + " updated at (" + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1") + ")");
                    }
                    ExitPointSelector();
                    GTA.UI.Notification.PostTicker("~g~Узел обновлён!", false, false);
                    if (_resumePlaybackAfterNodeEdit)
                    {
                        _resumePlaybackAfterNodeEdit = false;
                        // Камера селектора деактивируется только по завершении
                        // fade-out (асинхронно ~1.2с). Принудительно гасим её
                        // сейчас и отменяем отложенный onDeactivate, иначе
                        // StartPlayback увидит IsSelectorActive==true и не
                        // запустится, а позже onDeactivate убьёт рендер spline-камеры.
                        if (PositionSelector != null && PositionSelector.MainCamera != null && PositionSelector.MainCamera.Exists())
                            PositionSelector.MainCamera.IsActive = false;
                        if (PositionSelector != null) PositionSelector.AbortPendingFade();
                        StartPlayback();
                        if (OnNodeEditResumed != null) OnNodeEditResumed(editedNodeIndex);
                    }
                    return true;
                }

                SplineCamera.AddNode(pos, rot, NodeDuration, 0, Color.White.ToArgb(), CurrentFov);
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

                if (!_control.Acquire("Камера", true)) return false;
                _splineExitPending = false;
                DevelopmentDiagnostics.State("Camera", "Path playback");
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
                EndControlImmediately();
                return false;
            }
        }

        public void StopPlayback()
        {
            if (!_control.Held) return;
            try
            {
                // Гасим флаг даже если камера уже не активна (гонка с AbortPendingFade
                // или повторный Stop из меню) — иначе _splineCamWasUsed залипает true
                // и SplineCamera.Update тикает вечно.
                bool wasActive = SplineCamera != null && IsSplineCamActive;
                bool hadSession = _splineCamWasUsed;
                if ((wasActive || hadSession) && SplineCamera != null)
                {
                    long realMs = Utils.NowMs() - _playbackStartMs;
                    Logger.Info("CameraService: Stopping playback. Real elapsed: " + realMs + " ms; nominal duration: "
                        + SplineCamera.NominalDurationMs + " ms; current (speed-adjusted) duration: "
                        + SplineCamera.CurrentDurationMs + " ms; speed x" + SplineCamera.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                        + ". Ratio real/nominal: " + (SplineCamera.NominalDurationMs > 0 ? ((double)realMs / SplineCamera.NominalDurationMs).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "n/a"));
                    Logger.Info("CameraService: Stopping playback");
                    SplineCamera.ExitCameraView();
                }
                // Флаг чистим в любом случае, даже если Exit бросил исключение.
                _splineExitPending = hadSession || wasActive;
                _splineCamWasUsed = false;

                _lastTimeScale = 1f;
                try { _control.TimeScale(_control.OriginalTimeScale); } catch { }
                TeleportPlayerBehindCamera();
                RestorePlayerState();
            }
            catch (Exception ex)
            {
                // Страховка: не оставляем вечный тик spline.
                _splineCamWasUsed = false;
                Logger.Error(ex, "CameraService: Error in StopPlayback");
                EndControlImmediately();
                try { RestorePlayerState(); } catch { }
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
                if (!_control.Held || !_isPlayerFollowing) return;
                var player = _control.Player;
                // Флаг чистим ВСЕГДА, даже если пед временно null (стриминг/телепорт/
                // Menyoo-карта в океане). Иначе _isPlayerFollowing залипает true
                // и UpdatePlayerFollow вечно телепортирует к мёртвой камере.
                bool restored = false;
                if (player != null && player.Exists())
                {
                    try
                    {
                        player.IsVisible = _savedPlayerVisible;
                        player.IsCollisionEnabled = _savedPlayerCollision;
                        player.IsInvincible = _savedPlayerInvincible;
                        player.IsPositionFrozen = _savedPlayerPosFrozen;
                        restored = true;
                    }
                    catch (Exception ex2)
                    {
                        Logger.Debug("RestorePlayerState apply warning: " + ex2.Message);
                    }
                }
                _isPlayerFollowing = false;
                if (restored) Logger.Info("CameraService: Player state restored");
                else Logger.Info("CameraService: Player follow flag cleared (player not available)");
            }
            catch (Exception ex)
            {
                // Страховка: даже при исключении флаг не должен залипнуть.
                _isPlayerFollowing = false;
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
                    int nodeMode = (path.NodeInterpolationModes.Count > i) ? path.NodeInterpolationModes[i] : 0;
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
                if (_control.Held && !_control.Valid) { EndControlImmediately(); return; }
                ApplyTimeScale();

                if (IsSplineCamActive || _splineCamWasUsed || _splineExitPending)
                {
                    if (SplineCamera != null && SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                        SplineCamera.Update();
                    else
                    {
                        DevelopmentDiagnostics.Event("Camera", "Stopped: camera entity missing");
                        EndControlImmediately();
                        return;
                    }
                }

                if (_splineExitPending && (SplineCamera == null || SplineCamera.FadeState == FadeState.None))
                {
                    _splineExitPending = false;
                    _splineCamWasUsed = false;
                }
                UpdatePlayerFollow();

                if (IsSelectorActive || _selectorWasUsed || _selectorExitPending)
                {
                    if (SplineCamera != null && SplineCamera.Nodes.Count > 0)
                        SplineCamera.DrawNodeMarkers();
                    if (PositionSelector != null && PositionSelector.MainCamera != null && PositionSelector.MainCamera.Exists())
                    {
                        PositionSelector.Update();
                        // Выход считается завершённым только когда fade-машина
                        // селектора дошла до None. Только тогда гасим флаги.
                        if (_selectorExitPending && PositionSelector.FadeState == FadeState.None)
                        {
                            _selectorExitPending = false;
                            _selectorWasUsed = false;
                        }
                    }
                    else
                    {
                        if (PositionSelector != null)
                            Logger.Warn("CameraService: PositionSelector no longer exists");
                        _selectorExitPending = false;
                        _selectorWasUsed = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CameraService: Error in Update");
                EndControlImmediately();
            }
            finally
            {
                if (_control.Held && !IsAnyCameraActive && !_selectorWasUsed &&
                    !_splineCamWasUsed && !_selectorExitPending && !_splineExitPending)
                {
                    _control.Dispose();
                    DevelopmentDiagnostics.State("Camera", "Idle");
                }
            }
        }

        private void ApplyTimeScale()
        {
            try
            {
                // Кинематографичный slow-mo: когда скорость пролётки < 1, замедляем
                // ВЕСЬ мир пропорционально, чтобы камера и мир двигались синхронно.
                // При выходе из камеры (IsSplineCamActive=false) время всегда сбрасывается в 1.
                if (!_control.Held) return;
                float target = _control.OriginalTimeScale;
                if (_splineCamWasUsed && IsSplineCamActive && SplineCamera != null)
                {
                    float s = CurrentSpeed;
                    if (s > 0.05f && s < 1f)
                        target = s;
                }
                if (Math.Abs(target - _lastTimeScale) > 0.001f)
                {
                    _control.TimeScale(target);
                    _lastTimeScale = target;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("ApplyTimeScale warning: " + ex.Message);
            }
        }

        private void EndControlImmediately()
        {
            if (!_control.Held) return;
            try
            {
                if (SplineCamera != null)
                {
                    SplineCamera.AbortPendingFade();
                    if (SplineCamera.MainCamera != null && SplineCamera.MainCamera.Exists())
                        SplineCamera.MainCamera.IsActive = false;
                }
                if (PositionSelector != null)
                {
                    PositionSelector.AbortPendingFade();
                    if (PositionSelector.MainCamera != null && PositionSelector.MainCamera.Exists())
                        PositionSelector.MainCamera.IsActive = false;
                }
                ScriptCameraDirector.StopRendering(false);
                Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                Function.Call(NativeHashes.UNDO_SCREEN_FADE);
                CameraRenderer.ClearFocus();
                RestorePlayerState();
            }
            catch (Exception ex) { Logger.Error(ex, "Camera cleanup"); }
            finally
            {
                _selectorWasUsed = _splineCamWasUsed = _selectorExitPending = _splineExitPending = _isPlayerFollowing = false;
                _control.Dispose();
                DevelopmentDiagnostics.State("Camera", "Idle");
            }
        }

        public void ResetAll()
        {
            try
            {
                Logger.Info("CameraService: ResetAll called");
                if (_control.Held) Function.Call(NativeHashes.UNDO_SCREEN_FADE);
                if (_control.Held) CameraRenderer.ClearFocus();
                RestorePlayerState();
                _lastTimeScale = 1f;
                try { _control.TimeScale(_control.OriginalTimeScale); } catch { }
                _isPlayerFollowing = false;
                _selectorExitPending = false;
                _selectorWasUsed = false;
                _splineCamWasUsed = false;

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

                if (_control.Held) ScriptCameraDirector.StopRendering(false);
                if (_control.Held) Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                if (_control.Held) CameraRenderer.ClearFocus();

                // Original player state is restored by the control session.

                SplineCamera = new SplineCamera() { HasControl = delegate { return _control.Held; } };
                PositionSelector = new PositionSelector(Vector3.Zero, Vector3.Zero) { HasControl = delegate { return _control.Held; } };
                _selectorWasUsed = false;
                _splineCamWasUsed = false;
                _selectorExitPending = false;
                _isPlayerFollowing = false;
                ApplyCameraSettings();

                Logger.Info("CameraService: ResetAll completed");
            }
            catch (Exception ex)
            {
                _isPlayerFollowing = false;
                _selectorExitPending = false;
                _selectorWasUsed = false;
                _splineCamWasUsed = false;
                _lastTimeScale = 1f;
                try { _control.TimeScale(_control.OriginalTimeScale); } catch { }
                try { if (_control.Held) CameraRenderer.ClearFocus(); } catch { }
                Logger.Error(ex, "CameraService: Error in ResetAll");
            }
            finally { EndControlImmediately(); }
        }

        public void Dispose()
        {
            try
            {
                Logger.Info("CameraService: Disposing...");
                if (_control.Held) Function.Call(NativeHashes.UNDO_SCREEN_FADE);
                if (_control.Held) CameraRenderer.ClearFocus();
                _lastTimeScale = 1f;
                try { _control.TimeScale(_control.OriginalTimeScale); } catch { }
                _isPlayerFollowing = false;
                _selectorExitPending = false;

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

                if (_control.Held) ScriptCameraDirector.StopRendering(false);
                if (_control.Held) Function.Call(NativeHashes.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                if (_control.Held) CameraRenderer.ClearFocus();
                try { RestorePlayerState(); } catch { }
                _isPlayerFollowing = false;
                _selectorExitPending = false;
                _selectorWasUsed = false;
                _splineCamWasUsed = false;

                Logger.Info("CameraService: Disposed");
            }
            catch (Exception ex)
            {
                _isPlayerFollowing = false;
                _selectorExitPending = false;
                _selectorWasUsed = false;
                _splineCamWasUsed = false;
                _lastTimeScale = 1f;
                try { _control.TimeScale(_control.OriginalTimeScale); } catch { }
                try { if (_control.Held) CameraRenderer.ClearFocus(); } catch { }
                Logger.Error(ex, "CameraService: Error during Dispose");
            }
            finally { EndControlImmediately(); }
        }
    }

}
