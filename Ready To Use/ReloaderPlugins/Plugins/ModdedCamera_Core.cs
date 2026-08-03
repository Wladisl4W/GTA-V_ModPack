using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using ModdedCamera.Gamepad;

namespace ModdedCamera
{
    public class CameraInterpolator
    {
        private List<Vector3> _positions;
        private List<Vector3> _rotations;
        private List<int> _durations;
        private List<int> _segmentModes;
        private bool _isPlaying = false;
        private int _playbackStartTimeMs = 0;
        private int _totalDurationMs = 0;

        public bool IsPlaying { get { return _isPlaying; } }
        public float PlaybackProgress { get; private set; }

        private int _startNodeIndex = 0;

        public void SetPlaybackOffset(int elapsedMs)
        {
            _playbackStartTimeMs = Game.GameTime - elapsedMs;
        }

        public void SetStartNodeIndex(int index)
        {
            _startNodeIndex = Math.Max(0, index);
        }

        public CameraInterpolator()
        {
            _positions = new List<Vector3>();
            _rotations = new List<Vector3>();
            _durations = new List<int>();
            _segmentModes = new List<int>();
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations)
        {
            SetPath(positions, rotations, durations, null);
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations, List<int> segmentModes)
        {
            try
            {
                if (positions == null) throw new ArgumentNullException("positions", "Path data cannot be null");
                if (rotations == null) throw new ArgumentNullException("rotations", "Path data cannot be null");
                if (durations == null) throw new ArgumentNullException("durations", "Path data cannot be null");
                if (positions.Count < 2) throw new ArgumentException("Need at least 2 waypoints");
                if (positions.Count != rotations.Count || positions.Count != durations.Count)
                    throw new ArgumentException("Position, rotation, and duration counts must match");

                _positions = new List<Vector3>(positions);
                _rotations = new List<Vector3>(rotations);
                _durations = new List<int>(durations);

                _segmentModes = new List<int>();
                int modeCount = (segmentModes != null) ? segmentModes.Count : 0;
                for (int i = 0; i < _positions.Count; i++)
                    _segmentModes.Add((i < modeCount) ? segmentModes[i] : 2);

                _totalDurationMs = 0;
                for (int i = 0; i < _durations.Count; i++)
                    _totalDurationMs += Math.Max(1, _durations[i]);

                Logger.Info("Path set with " + _positions.Count + " waypoints, total duration: " + _totalDurationMs + "ms");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SetPath error");
                throw;
            }
        }

        public void Start()
        {
            try
            {
                if (_positions.Count < 2)
                {
                    Logger.Warn("Cannot start playback - insufficient waypoints");
                    return;
                }
                _isPlaying = true;
                int limit = Math.Min(_startNodeIndex, _durations.Count - 1);
                long offsetMs = 0;
                for (int i = 0; i < limit; i++)
                    offsetMs += Math.Max(0, _durations[i]);
                _playbackStartTimeMs = Game.GameTime - (int)offsetMs;
                _startNodeIndex = 0;
                PlaybackProgress = 0f;
                Logger.Info("Playback started - total duration: " + _totalDurationMs + "ms" + (offsetMs > 0 ? ", offset: " + offsetMs + "ms" : ""));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Start error");
            }
        }

        public void Stop()
        {
            _isPlaying = false;
            PlaybackProgress = 0f;
            Logger.Info("Playback stopped");
        }

        public void Update(out Vector3 position, out Vector3 rotation)
        {
            position = Vector3.Zero;
            rotation = Vector3.Zero;

            if (!_isPlaying || _positions.Count < 2 || _totalDurationMs <= 0)
                return;

            try
            {
                long now = Game.GameTime;
                long elapsedMs = now - (long)_playbackStartTimeMs;

                if (elapsedMs < 0)
                {
                    Logger.Warn("Timing overflow detected, resetting playback");
                    _playbackStartTimeMs = Game.GameTime;
                    elapsedMs = 0;
                }

                if (_totalDurationMs == 0)
                {
                    Logger.Warn("Update called with zero total duration - returning last position");
                    position = _positions[_positions.Count - 1];
                    rotation = _rotations[_rotations.Count - 1];
                    return;
                }

                double cycleTime = elapsedMs % _totalDurationMs;
                PlaybackProgress = (float)cycleTime / _totalDurationMs;

                double accumulatedMs = 0;
                int currentSegment = -1;

                for (int i = 0; i < _durations.Count; i++)
                {
                    int segmentDuration = Math.Max(0, _durations[i]);
                    if (cycleTime < accumulatedMs + segmentDuration)
                    {
                        currentSegment = i;
                        break;
                    }
                    accumulatedMs += segmentDuration;
                }

                if (currentSegment == -1)
                {
                    position = _positions[_positions.Count - 1];
                    rotation = _rotations[_rotations.Count - 1];
                    PlaybackProgress = 1f;
                    return;
                }

                if (currentSegment == _durations.Count - 1)
                {
                    position = _positions[_positions.Count - 1];
                    rotation = _rotations[_rotations.Count - 1];
                    return;
                }

                int segmentDurationMs = Math.Max(0, _durations[currentSegment]);
                double segmentElapsedMs = cycleTime - accumulatedMs;
                float t = (float)segmentElapsedMs / segmentDurationMs;
                t = Math.Min(Math.Max(t, 0f), 1f);

                int modeOut = (currentSegment < _segmentModes.Count) ? _segmentModes[currentSegment] : 2;
                int modeIn = (currentSegment + 1 < _segmentModes.Count) ? _segmentModes[currentSegment + 1] : modeOut;

                float fStart = Ease(modeOut, t);
                float fEnd = Ease(modeIn, t);
                float blend = t * t * (3f - 2f * t);
                float f = fStart + (fEnd - fStart) * blend;

                position = Vector3.Lerp(_positions[currentSegment], _positions[currentSegment + 1], f);
                rotation = InterpolateRotationShortest(currentSegment, f);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Update error - continuing playback");
                position = _positions.Count > 0 ? _positions[_positions.Count - 1] : Vector3.Zero;
                rotation = _rotations.Count > 0 ? _rotations[_rotations.Count - 1] : Vector3.Zero;
            }
        }

        private Vector3 InterpolateRotationShortest(int segment, float t)
        {
            Vector3 r1 = _rotations[segment];
            Vector3 r2 = _rotations[segment + 1];
            float x = LerpAngle(r1.X, r2.X, t);
            float y = LerpAngle(r1.Y, r2.Y, t);
            float z = LerpAngle(r1.Z, r2.Z, t);
            return new Vector3(x, y, z);
        }

        private float LerpAngle(float a, float b, float t)
        {
            float delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return a + delta * t;
        }

        private float Ease(int mode, float t)
        {
            if (mode == 0) return t;
            if (mode == 1) return 0.5f * t + 0.5f * Smootherstep(t);
            return t * t * (3f - 2f * t);
        }

        private float Smootherstep(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        public void Clear()
        {
            _positions.Clear();
            _rotations.Clear();
            _durations.Clear();
            _segmentModes.Clear();
            _isPlaying = false;
            _totalDurationMs = 0;
            PlaybackProgress = 0f;
        }
    }

    public class SplineCamera
    {
        private CameraInterpolator _interpolator;
        private FadeStateMachine _fadeMachine;
        private Camera _mainCamera;
        private List<Tuple<Vector3, Vector3>> _nodes;
        private List<int> _durations = new List<int>();
        private List<int> _baseDurations = new List<int>();
        private List<int> _nodeInterpolationModes = new List<int>();
        private int _defaultDuration = 5000;
        private float _currentSpeedMult = 1.0f;
        private bool _usePlayerView;
        private int _startNodeIndex = 0;
        private Vector3 _startPos;
        private bool _hasStartPosition = false;
        private Vector3 _previousPos;
        private Timer _renderSceneTimer;

        public Camera MainCamera
        {
            get { return _mainCamera; }
        }

        public bool IsCameraAvailable
        {
            get { return _mainCamera != null && _mainCamera.Exists(); }
        }

        public bool UsePlayerView
        {
            get { return _usePlayerView; }
            set
            {
                if (value)
                {
                    _startPos = Game.Player.Character.Position;
                    _hasStartPosition = true;
                    Game.Player.Character.IsInvincible = true;
                    Game.Player.Character.IsVisible = false;
                }
                else
                {
                    if (_hasStartPosition)
                        Game.Player.Character.Position = _startPos;
                    Game.Player.Character.IsInvincible = false;
                    Game.Player.Character.IsVisible = true;
                    _hasStartPosition = false;
                }
                _usePlayerView = value;
            }
        }

        public float Speed
        {
            set
            {
                try
                {
                    float mult = Math.Max(0.1f, Math.Min(10f, value));
                    float oldMult = _currentSpeedMult;
                    _currentSpeedMult = mult;
                    for (int i = 0; i < _baseDurations.Count; i++)
                    {
                        _durations[i] = (int)Math.Max(0, _baseDurations[i] / mult);
                    }
                    Logger.Info("Speed changed from x" + oldMult.ToString("F2") + " to x" + mult.ToString("F2"));
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error setting speed");
                }
            }
        }

        public List<Tuple<Vector3, Vector3>> Nodes
        {
            get { return _nodes; }
        }

        public List<Vector3> GetPositions()
        {
            List<Vector3> positions = new List<Vector3>();
            foreach (var node in _nodes)
                positions.Add(node.Item1);
            return positions;
        }

        public List<Vector3> GetRotations()
        {
            List<Vector3> rotations = new List<Vector3>();
            foreach (var node in _nodes)
                rotations.Add(node.Item2);
            return rotations;
        }

        public List<int> GetDurations()
        {
            return new List<int>(_durations);
        }

        public SplineCamera()
        {
            try
            {
                _interpolator = new CameraInterpolator();

                int cameraHandle = Function.Call<int>(Hash.CREATE_CAM, "DEFAULT_SCRIPTED_CAMERA", 0);
                if (cameraHandle == 0)
                    throw new Exception("Failed to create DEFAULT_SCRIPTED_CAMERA");

                _mainCamera = new Camera(cameraHandle);
                if (_mainCamera == null || !_mainCamera.Exists())
                    throw new Exception("Camera creation failed");

                _nodes = new List<Tuple<Vector3, Vector3>>();
                _renderSceneTimer = new Timer(5000);
                _renderSceneTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error creating SplineCamera - attempting fallback");
                try
                {
                    int fallbackHandle = Function.Call<int>(Hash.CREATE_CAM, "DEFAULT_SPLINE_CAMERA", 0);
                    if (fallbackHandle == 0)
                        throw new Exception("Fallback camera creation also failed");
                    _mainCamera = new Camera(fallbackHandle);
                    _interpolator = new CameraInterpolator();
                    _nodes = new List<Tuple<Vector3, Vector3>>();
                    _renderSceneTimer = new Timer(5000);
                    _renderSceneTimer.Start();
                    Logger.Warn("Using fallback DEFAULT_SPLINE_CAMERA");
                }
                catch (Exception ex2)
                {
                    Logger.Error(ex2, "CRITICAL: Failed to create any camera!");
                    throw;
                }
            }

            _fadeMachine = new FadeStateMachine(
                onActivate: () => {
                    this.MainCamera.IsActive = true;
                    ScriptCameraDirector.StartRendering();
                    Function.Call(Hash.RENDER_SCRIPT_CAMS, true, 0, 0, false, false);
                    if (_interpolator != null)
                    {
                        _interpolator.Start();
                        Logger.Info("Interpolator playback STARTED");
                    }
                    Function.Call(Hash.DO_SCREEN_FADE_IN, 800);
                },
                onDeactivate: () => {
                    if (this.UsePlayerView) this.UsePlayerView = false;
                    if (_interpolator != null)
                    {
                        _interpolator.Stop();
                        Logger.Info("Interpolator playback STOPPED");
                    }
                    this.MainCamera.IsActive = false;
                    ScriptCameraDirector.StopRendering(false);
                    Function.Call(Hash.RENDER_SCRIPT_CAMS, false, 0, 0, false, false);
                    Function.Call(Hash.DO_SCREEN_FADE_IN, 800);
                },
                logPrefix: "SplineCamera"
            );
        }

        public void Dispose()
        {
            try
            {
                CameraRenderer.ClearFocus();
                if (_renderSceneTimer != null)
                {
                    try { _renderSceneTimer.Stop(); } catch { }
                    _renderSceneTimer = null;
                }
                if (_interpolator != null)
                {
                    try { _interpolator.Stop(); } catch { }
                }
                if (_mainCamera != null && _mainCamera.Exists())
                {
                    if (_mainCamera.IsActive) _mainCamera.IsActive = false;
                    Function.Call(Hash.DESTROY_CAM, _mainCamera.Handle);
                    _mainCamera = null;
                }
                if (_nodes != null) _nodes.Clear();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error disposing SplineCamera");
            }
        }

        public void AddNode(Vector3 position, Vector3 rotation, int duration)
        {
            AddNode(position, rotation, duration, 2);
        }

        public void AddNode(Vector3 position, Vector3 rotation, int duration, int interpolationMode)
        {
            try
            {
                if (_mainCamera == null)
                {
                    Logger.Error("AddNode: Camera is null!");
                    return;
                }
                if (!_mainCamera.Exists())
                {
                    Logger.Error("AddNode: Camera does not exist!");
                    return;
                }
                if (duration < 0)
                {
                    Logger.Warn("AddNode: Negative duration, using 0ms");
                    duration = 0;
                }

                _nodes.Add(new Tuple<Vector3, Vector3>(position, rotation));
                _baseDurations.Add(duration);
                int adjustedDuration = (int)Math.Max(0, duration / _currentSpeedMult);
                _durations.Add(adjustedDuration);
                _nodeInterpolationModes.Add(interpolationMode);
                _defaultDuration = duration;

                Logger.Debug("Node added: pos=(" + position.X.ToString("F1") + ", " + position.Y.ToString("F1") + ", " + position.Z.ToString("F1") +
                    ") rot=(" + rotation.X.ToString("F1") + ", " + rotation.Y.ToString("F1") + ", " + rotation.Z.ToString("F1") + ") duration=" + duration + "ms");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error adding node");
            }
        }

        public void ClearNodes()
        {
            try
            {
                _nodes.Clear();
                _durations.Clear();
                _baseDurations.Clear();
                _nodeInterpolationModes.Clear();
                _startNodeIndex = 0;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error clearing nodes");
            }
        }

        public void RebuildSplineWithCurrentMode()
        {
            try
            {
                if (_nodes.Count == 0) return;
                Logger.Info("Rebuilding spline with " + _nodes.Count + " nodes");

                var savedNodes = new List<Tuple<Vector3, Vector3>>(_nodes);
                var savedBaseDurations = new List<int>(_baseDurations);
                var savedModes = new List<int>(_nodeInterpolationModes);

                _nodes.Clear();
                _durations.Clear();
                _baseDurations.Clear();
                _nodeInterpolationModes.Clear();

                for (int i = 0; i < savedNodes.Count; i++)
                {
                    int originalDuration = (savedBaseDurations.Count > i) ? savedBaseDurations[i] : _defaultDuration;
                    int nodeMode = (savedModes.Count > i) ? savedModes[i] : 2;
                    AddNode(savedNodes[i].Item1, savedNodes[i].Item2, originalDuration, nodeMode);
                }
                Logger.Info("Spline rebuilt: " + _nodes.Count + " nodes");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error rebuilding spline");
            }
        }

        public List<int> GetNodeInterpolationModes()
        {
            return new List<int>(_nodeInterpolationModes);
        }

        public void SetNodeInterpolationMode(int index, int mode)
        {
            if (index >= 0 && index < _nodeInterpolationModes.Count)
            {
                _nodeInterpolationModes[index] = mode;
            }
        }

        public void SetNodeDuration(int index, int durationMs)
        {
            if (index < 0 || index >= _baseDurations.Count) return;
            if (durationMs < 0) durationMs = 0;
            _baseDurations[index] = durationMs;
            _durations[index] = (int)Math.Max(0, durationMs / _currentSpeedMult);
        }

        public void SetStartNodeIndex(int index)
        {
            _startNodeIndex = Math.Max(0, index);
        }

        public void RestartInterpolator()
        {
            try
            {
                if (_interpolator == null)
                {
                    Logger.Warn("Cannot restart: interpolator is null");
                    return;
                }
                if (_nodes.Count < 2)
                {
                    Logger.Warn("Cannot restart: insufficient nodes (" + _nodes.Count + ")");
                    return;
                }

                Logger.Info("Restarting interpolator" + (_startNodeIndex > 0 ? " from node " + _startNodeIndex : ""));
                var positions = GetPositions();
                var rotations = GetRotations();
                var durations = GetDurations();
                var modes = GetNodeInterpolationModes();
                _interpolator.SetPath(positions, rotations, durations, modes);
                _interpolator.SetStartNodeIndex(_startNodeIndex);
                _interpolator.Start();
                _startNodeIndex = 0;
                Logger.Info("Interpolator restarted");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error restarting interpolator");
            }
        }

        public void UpdateSpeed(float speed)
        {
            if (_interpolator == null || _nodes.Count < 2) return;
            Speed = speed;
            RestartInterpolator();
        }

        public void EnterCameraView(Vector3 position)
        {
            _mainCamera.Position = position;
            _startNodeIndex = 0;
            if (_nodes.Count >= 2)
            {
                try
                {
                    var positions = GetPositions();
                    var rotations = GetRotations();
                    var durations = GetDurations();
                    var modes = GetNodeInterpolationModes();
                    _interpolator.SetPath(positions, rotations, durations, modes);
                    Logger.Info("Interpolator ready: " + positions.Count + " waypoints");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error setting interpolator path");
                }
            }
            _fadeMachine.StartFadeOut(1200);
        }

        public void ExitCameraView()
        {
            CameraRenderer.ClearFocus();
            _fadeMachine.StartFadeOutExit(1200);
        }

        public void Update()
        {
            _fadeMachine.Update();
            bool isActive = _mainCamera.IsActive;
            if (isActive)
            {
                try
                {
                    UpdateWithInterpolator();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error in interpolator update");
                    try { GTA.UI.Notification.PostTicker("~r~Ошибка обновления камеры!", false, false); } catch { }
                }
            }
        }

        private void UpdateWithInterpolator()
        {
            try
            {
                if (_mainCamera == null || !_mainCamera.Exists())
                {
                    Logger.Warn("UpdateWithInterpolator: Camera not available");
                    return;
                }

                Vector3 interpPos;
                Vector3 interpRot;
                _interpolator.Update(out interpPos, out interpRot);

                _mainCamera.Position = interpPos;
                _mainCamera.Rotation = interpRot;
                _previousPos = _mainCamera.Position;

                UpdateRenderScene();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "UpdateWithInterpolator: Critical error");
            }
        }

        private void UpdateRenderScene()
        {
            try
            {
                if (_mainCamera == null || !_mainCamera.Exists())
                {
                    Logger.Warn("UpdateRenderScene: Camera not available");
                    return;
                }

                bool shouldRender = _renderSceneTimer.Enabled && _renderSceneTimer.Check();
                if (shouldRender)
                {
                    CameraRenderer.UpdateFocusArea(_mainCamera.Position);
                    CameraRenderer.DrawRenderScene();
                    _renderSceneTimer.Reset();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "UpdateRenderScene: Error");
            }
        }
    }

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
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 2);
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
                if (isActive)
                {
                    if (!_controlsDisabled)
                    {
                        _controlsDisabled = true;
                        DisablePlayerControls();
                    }
                    bool shouldRender = _renderSceneTimer.Enabled && _renderSceneTimer.Check();
                    if (shouldRender)
                    {
                        CameraRenderer.UpdateFocusArea(_mainCamera.Position);
                        CameraRenderer.DrawRenderScene();
                        CameraRenderer.DrawPositionMarker(_mainCamera.Position, _previousPos);
                        _renderSceneTimer.Reset();
                    }

                    _previousPos = _mainCamera.Position;
                    RenderEntityPosition();

                    try { GamepadHandler.Update(); } catch (Exception ex) { Logger.Debug("GamepadHandler.Update warning: " + ex.Message); }

                    try { RenderInstructionalButtons(); } catch (Exception ex) { Logger.Debug("RenderInstructionalButtons warning: " + ex.Message); }

                    if (_currentLerpTime > 0f) _currentLerpTime -= 0.01f;
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
            _instructionalButtons.CallFunction("SET_BACKGROUND_COLOUR", new object[] { 0, 0, 0, 80 });
            _instructionalButtons.CallFunction("DRAW_INSTRUCTIONAL_BUTTONS", new object[] { 0 });
            _instructionalButtons.Render2D();
        }
    }
}
