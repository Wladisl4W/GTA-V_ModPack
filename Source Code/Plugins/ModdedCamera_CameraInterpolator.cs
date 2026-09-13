using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace ModdedCamera
{
    /// <summary>
    /// Pure linear baseline: straight lines between waypoints, constant speed
    /// within each segment (segment duration defines speed). All interpolation
    /// modes were removed and are ignored - nodeModes / InterpolationMode are
    /// accepted only for backward compatibility (callers, menu, saves).
    /// TODO: design and implement the new interpolation system on top of this.
    /// </summary>
    public class CameraInterpolator
    {
        private List<Vector3> _positions;
        private List<Vector3> _rotations;
        private List<int> _durations;
        private List<int> _fovs;
        private bool _isPlaying = false;
        private long _playbackElapsedMs = 0;
        private int _totalDurationMs = 0;

        // Unwrapped rotation chain (no +/-360 jumps), built in SetPath.
        private List<Vector3> _rotationsU;

        // Accepted but ignored: kept so existing callers keep compiling.
        private int _interpMode = 0;
        public int InterpolationMode
        {
            get { return _interpMode; }
            set { _interpMode = value; }
        }

        public bool IsPlaying { get { return _isPlaying; } }
        public float PlaybackProgress { get; private set; }

        // Accumulated playback time in ms. Advanced by the caller each active
        // frame (clamped frame delta). Playback pauses automatically whenever
        // no Update is fed instead of jumping forward by wall-clock time.
        public void Advance(long ms) { if (ms > 0) _playbackElapsedMs += ms; }
        public long ElapsedMs { get { return _playbackElapsedMs; } }

        private int _startNodeIndex = 0;

        public void SetPlaybackOffset(int elapsedMs)
        {
            _playbackElapsedMs = elapsedMs;
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
            _fovs = new List<int>();
            _rotationsU = new List<Vector3>();
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations)
        {
            SetPath(positions, rotations, durations, null, null);
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations, List<int> nodeModes)
        {
            SetPath(positions, rotations, durations, nodeModes, null);
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations, List<int> nodeModes, List<int> fovs)
        {
            // nodeModes is intentionally ignored: pure linear baseline.
            try
            {
                if (positions == null) throw new ArgumentNullException("positions");
                if (rotations == null) throw new ArgumentNullException("rotations");
                if (durations == null) throw new ArgumentNullException("durations");
                if (positions.Count < 2) throw new ArgumentException("Need at least 2 waypoints");
                if (positions.Count != rotations.Count || positions.Count != durations.Count)
                    throw new ArgumentException("Position, rotation, and duration counts must match");

                _positions = new List<Vector3>(positions);
                _rotations = new List<Vector3>(rotations);
                _durations = new List<int>(durations.Count);
                for (int i = 0; i < durations.Count; i++)
                    _durations.Add(Math.Max(10, durations[i]));

                _fovs = new List<int>();
                int fovCount = (fovs != null) ? fovs.Count : 0;
                for (int i = 0; i < _positions.Count; i++)
                    _fovs.Add((i < fovCount) ? fovs[i] : 50);

                _totalDurationMs = 0;
                for (int i = 0; i < _durations.Count; i++)
                    _totalDurationMs += _durations[i];

                BuildRotationUnwrap();

                Logger.Info("Path set with " + _positions.Count + " waypoints, total duration: " + _totalDurationMs + "ms");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SetPath error");
                throw;
            }
        }

        private void BuildRotationUnwrap()
        {
            _rotationsU = new List<Vector3>(_positions.Count);
            for (int i = 0; i < _positions.Count; i++)
            {
                if (i == 0) _rotationsU.Add(_rotations[0]);
                else _rotationsU.Add(UnwrapRotation(_rotationsU[i - 1], _rotations[i]));
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
                    offsetMs += _durations[i];
                _playbackElapsedMs = offsetMs;
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
            float fov;
            UpdateAt(_playbackElapsedMs, out position, out rotation, out fov);
        }

        public void Update(out Vector3 position, out Vector3 rotation, out float fov)
        {
            UpdateAt(_playbackElapsedMs, out position, out rotation, out fov);
        }

        public void UpdateAt(long elapsedMs, out Vector3 position, out Vector3 rotation, out float fov)
        {
            position = Vector3.Zero;
            rotation = Vector3.Zero;
            fov = 50f;

            if (!_isPlaying || _positions.Count < 2 || _totalDurationMs <= 0)
                return;

            try
            {
                if (elapsedMs < 0) elapsedMs = 0;

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

                // Past the end, or inside the final dwell period (the last
                // duration is a hold at the final node, not a segment): park
                // on the last waypoint. Use unwrapped rotation for continuity
                // (raw would jump 360° when the path wrapped around).
                if (currentSegment == -1 || currentSegment == _durations.Count - 1)
                {
                    position = _positions[_positions.Count - 1];
                    if (_rotationsU != null && _rotationsU.Count > 0)
                        rotation = _rotationsU[_rotationsU.Count - 1];
                    else
                        rotation = _rotations[_rotations.Count - 1];
                    if (_fovs != null && _fovs.Count > 0) fov = _fovs[_fovs.Count - 1];
                    if (currentSegment == -1) PlaybackProgress = 1f;
                    return;
                }

                int segmentDurationMs = Math.Max(0, _durations[currentSegment]);
                double segmentElapsedMs = cycleTime - accumulatedMs;
                float t = (segmentDurationMs > 0) ? (float)(segmentElapsedMs / segmentDurationMs) : 0f;
                t = Math.Min(Math.Max(t, 0f), 1f);

                position = Vector3.Lerp(_positions[currentSegment], _positions[currentSegment + 1], t);
                rotation = InterpolateRotationShortest(currentSegment, t);

                if (_fovs != null && currentSegment + 1 < _fovs.Count)
                    fov = _fovs[currentSegment] + (_fovs[currentSegment + 1] - _fovs[currentSegment]) * t;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Update error - continuing playback");
                position = _positions.Count > 0 ? _positions[_positions.Count - 1] : Vector3.Zero;
                rotation = _rotations.Count > 0 ? _rotations[_rotations.Count - 1] : Vector3.Zero;
                if (_fovs != null && _fovs.Count > 0) fov = _fovs[_fovs.Count - 1];
            }
        }

        private Vector3 InterpolateRotationShortest(int segment, float t)
        {
            Vector3 r1 = (_rotationsU != null && segment + 1 < _rotationsU.Count)
                ? _rotationsU[segment]
                : _rotations[segment];
            Vector3 r2 = (_rotationsU != null && segment + 1 < _rotationsU.Count)
                ? _rotationsU[segment + 1]
                : UnwrapRotation(r1, _rotations[segment + 1]);
            return new Vector3(
                LerpAngle(r1.X, r2.X, t),
                LerpAngle(r1.Y, r2.Y, t),
                LerpAngle(r1.Z, r2.Z, t));
        }

        private float LerpAngle(float a, float b, float t)
        {
            float delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return a + delta * t;
        }

        private Vector3 UnwrapRotation(Vector3 reference, Vector3 target)
        {
            return new Vector3(
                reference.X + DeltaAngle(reference.X, target.X),
                reference.Y + DeltaAngle(reference.Y, target.Y),
                reference.Z + DeltaAngle(reference.Z, target.Z));
        }

        private float DeltaAngle(float a, float b)
        {
            float delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return delta;
        }

        public void Clear()
        {
            _positions.Clear();
            _rotations.Clear();
            _durations.Clear();
            _fovs.Clear();
            if (_rotationsU != null) _rotationsU.Clear();
            _isPlaying = false;
            _totalDurationMs = 0;
            PlaybackProgress = 0f;
        }
    }
}
