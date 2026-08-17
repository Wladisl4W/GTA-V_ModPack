using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using ModdedCamera.Gamepad;

namespace ModdedCamera
{
    public class SplineCamera
    {
        private CameraInterpolator _interpolator;
        private FadeStateMachine _fadeMachine;
        private Camera _mainCamera;
        private List<Tuple<Vector3, Vector3>> _nodes;
        private List<int> _durations = new List<int>();
        private List<int> _baseDurations = new List<int>();
        private List<int> _nodeInterpolationModes = new List<int>();
        private List<int> _nodeColors = new List<int>();
        private List<int> _nodeFovs = new List<int>();
        private int _defaultDuration = 5000;
        private int _defaultFov = 50;
        private float _currentSpeedMult = 1.0f;
        private long _lastFrameMs = 0;
        private bool _usePlayerView;
        private int _startNodeIndex = 0;
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
                bool changed = _usePlayerView != value;
                _usePlayerView = value;
                if (changed)
                    Logger.Info("UsePlayerView set to " + value);
            }
        }

        public int DefaultFov
        {
            get { return _defaultFov; }
            set { _defaultFov = Math.Max(1, Math.Min(130, value)); }
        }

        public float Speed
        {
            get { return _currentSpeedMult; }
            set
            {
                try
                {
                    float mult = Math.Max(0.1f, Math.Min(10f, value));
                    if (Math.Abs(mult - _currentSpeedMult) < 0.001f)
                        return;
                    float oldMult = _currentSpeedMult;
                    _currentSpeedMult = mult;
                    for (int i = 0; i < _baseDurations.Count; i++)
                    {
                        _durations[i] = (int)Math.Max(0, _baseDurations[i] / mult);
                    }
                    Logger.Info("Speed changed from x" + oldMult.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " to x" + mult.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
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

        public int NominalDurationMs
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < _baseDurations.Count; i++)
                    sum += Math.Max(0, _baseDurations[i]);
                return sum;
            }
        }

        public int CurrentDurationMs
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < _durations.Count; i++)
                    sum += Math.Max(0, _durations[i]);
                return sum;
            }
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
                        _lastFrameMs = Utils.NowMs();
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

        public void AbortPendingFade()
        {
            try { _fadeMachine.Reset(); } catch (Exception ex) { Logger.Debug("AbortPendingFade warning: " + ex.Message); }
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
            AddNode(position, rotation, duration, 2, Color.White.ToArgb(), _defaultFov);
        }

        public void AddNode(Vector3 position, Vector3 rotation, int duration, int interpolationMode)
        {
            AddNode(position, rotation, duration, interpolationMode, Color.White.ToArgb(), _defaultFov);
        }

        public void AddNode(Vector3 position, Vector3 rotation, int duration, int interpolationMode, int color)
        {
            AddNode(position, rotation, duration, interpolationMode, color, _defaultFov);
        }

        public void AddNode(Vector3 position, Vector3 rotation, int duration, int interpolationMode, int color, int fov)
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
                _nodeColors.Add(color);
                _nodeFovs.Add(fov);
                _defaultDuration = duration;

                Logger.Debug("Node added: pos=(" + position.X.ToString("F1") + ", " + position.Y.ToString("F1") + ", " + position.Z.ToString("F1") +
                    ") rot=(" + rotation.X.ToString("F1") + ", " + rotation.Y.ToString("F1") + ", " + rotation.Z.ToString("F1") + ") duration=" + duration + "ms fov=" + fov);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error adding node");
            }
        }

        public List<int> GetNodeFovs()
        {
            return new List<int>(_nodeFovs);
        }

        public int GetNodeFov(int index)
        {
            if (index >= 0 && index < _nodeFovs.Count)
                return _nodeFovs[index];
            return 50;
        }

        public void SetNodeFov(int index, int fov)
        {
            if (index >= 0 && index < _nodeFovs.Count)
                _nodeFovs[index] = Math.Max(1, Math.Min(130, fov));
        }

        public void ClearNodes()
        {
            try
            {
                _nodes.Clear();
                _durations.Clear();
                _baseDurations.Clear();
                _nodeInterpolationModes.Clear();
                _nodeColors.Clear();
                _nodeFovs.Clear();
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
                var savedColors = new List<int>(_nodeColors);
                var savedFovs = new List<int>(_nodeFovs);

                _nodes.Clear();
                _durations.Clear();
                _baseDurations.Clear();
                _nodeInterpolationModes.Clear();
                _nodeColors.Clear();

                for (int i = 0; i < savedNodes.Count; i++)
                {
                    int originalDuration = (savedBaseDurations.Count > i) ? savedBaseDurations[i] : _defaultDuration;
                    int nodeMode = (savedModes.Count > i) ? savedModes[i] : 2;
                    int nodeColor = (savedColors.Count > i) ? savedColors[i] : Color.White.ToArgb();
                    int nodeFov = (savedFovs.Count > i) ? savedFovs[i] : _defaultFov;
                    AddNode(savedNodes[i].Item1, savedNodes[i].Item2, originalDuration, nodeMode, nodeColor, nodeFov);
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

        public List<int> GetNodeColors()
        {
            return new List<int>(_nodeColors);
        }

        public int GetNodeColor(int index)
        {
            if (index >= 0 && index < _nodeColors.Count)
                return _nodeColors[index];
            return Color.White.ToArgb();
        }

        public void SetNodeColor(int index, int argb)
        {
            if (index >= 0 && index < _nodeColors.Count)
                _nodeColors[index] = argb;
        }

        public bool RemoveNode(int index)
        {
            try
            {
                if (index < 0 || index >= _nodes.Count) return false;
                if (_nodes.Count <= 2)
                {
                    Logger.Warn("RemoveNode: cannot remove - minimum 2 nodes required");
                    return false;
                }
                _nodes.RemoveAt(index);
                _baseDurations.RemoveAt(index);
                _durations.RemoveAt(index);
                _nodeInterpolationModes.RemoveAt(index);
                _nodeColors.RemoveAt(index);
                _nodeFovs.RemoveAt(index);
                if (_startNodeIndex > index) _startNodeIndex--;
                Logger.Info("Node removed at index " + index + ", remaining: " + _nodes.Count);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error removing node");
                return false;
            }
        }

        public bool DuplicateNode(int index)
        {
            try
            {
                if (index < 0 || index >= _nodes.Count) return false;
                int insertAt = index + 1;
                _nodes.Insert(insertAt, new Tuple<Vector3, Vector3>(_nodes[index].Item1, _nodes[index].Item2));
                _baseDurations.Insert(insertAt, _baseDurations[index]);
                _durations.Insert(insertAt, _durations[index]);
                _nodeInterpolationModes.Insert(insertAt, _nodeInterpolationModes[index]);
                _nodeColors.Insert(insertAt, _nodeColors[index]);
                _nodeFovs.Insert(insertAt, _nodeFovs[index]);
                Logger.Info("Node duplicated at index " + insertAt + ", total: " + _nodes.Count);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error duplicating node");
                return false;
            }
        }

        public void SetNodePosition(int index, Vector3 position, Vector3 rotation)
        {
            if (index >= 0 && index < _nodes.Count)
                _nodes[index] = new Tuple<Vector3, Vector3>(position, rotation);
        }

        public void DrawNodeMarkers()
        {
            try
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    int argb = GetNodeColor(i);
                    Color c = Color.FromArgb(argb);
                    Function.Call(NativeHashes.DRAW_MARKER,
                        1,
                        _nodes[i].Item1.X, _nodes[i].Item1.Y, _nodes[i].Item1.Z,
                        0f, 0f, 0f,
                        0f, 0f, 0f,
                        0.6f, 0.6f, 0.6f,
                        (int)c.R, (int)c.G, (int)c.B, (int)c.A,
                        false, true, 2, false,
                        false, false, false);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("DrawNodeMarkers warning: " + ex.Message);
            }
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
                var fovs = GetNodeFovs();
                _interpolator.SetPath(positions, rotations, durations, modes, fovs);
                _interpolator.SetStartNodeIndex(_startNodeIndex);
                _interpolator.Start();
                _startNodeIndex = 0;
                Logger.Info("Interpolator restarted");
                _lastFrameMs = Utils.NowMs();
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
                    var fovs = GetNodeFovs();
                    _interpolator.SetPath(positions, rotations, durations, modes, fovs);
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
                float interpFov;
                long realNow = Utils.NowMs();
                long frameDelta = realNow - _lastFrameMs;
                _lastFrameMs = realNow;
                if (frameDelta < 0) frameDelta = 0;
                if (frameDelta > 250) frameDelta = 250;
                _interpolator.Advance(frameDelta);
                _interpolator.UpdateAt(_interpolator.ElapsedMs, out interpPos, out interpRot, out interpFov);

                _mainCamera.Position = interpPos;
                _mainCamera.Rotation = interpRot;
                _mainCamera.FieldOfView = interpFov;
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
                    _renderSceneTimer.Reset();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "UpdateRenderScene: Error");
            }
        }
    }

}
