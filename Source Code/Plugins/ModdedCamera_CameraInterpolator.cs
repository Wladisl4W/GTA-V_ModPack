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
        private List<int> _fovs;
        private bool _isPlaying = false;
        private long _playbackStartTimeMs = 0;
        private int _totalDurationMs = 0;

        public bool IsPlaying { get { return _isPlaying; } }
        public float PlaybackProgress { get; private set; }

        private int _startNodeIndex = 0;

        public void SetPlaybackOffset(int elapsedMs)
        {
            _playbackStartTimeMs = Utils.NowMs() - elapsedMs;
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

                _fovs = new List<int>();
                for (int i = 0; i < _positions.Count; i++)
                    _fovs.Add(50);

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

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations, List<int> segmentModes, List<int> fovs)
        {
            SetPath(positions, rotations, durations, segmentModes);
            if (_fovs == null) _fovs = new List<int>();
            _fovs.Clear();
            int fovCount = (fovs != null) ? fovs.Count : 0;
            for (int i = 0; i < _positions.Count; i++)
                _fovs.Add((i < fovCount) ? fovs[i] : 50);
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
                _playbackStartTimeMs = Utils.NowMs() - (int)offsetMs;
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
            UpdateAt(Utils.NowMs(), out position, out rotation, out fov);
        }

        public void Update(out Vector3 position, out Vector3 rotation, out float fov)
        {
            UpdateAt(Utils.NowMs(), out position, out rotation, out fov);
        }

        public void UpdateAt(long now, out Vector3 position, out Vector3 rotation, out float fov)
        {
            position = Vector3.Zero;
            rotation = Vector3.Zero;
            fov = 50f;

            if (!_isPlaying || _positions.Count < 2 || _totalDurationMs <= 0)
                return;

            try
            {
                long elapsedMs = now - (long)_playbackStartTimeMs;

                if (elapsedMs < 0)
                {
                    Logger.Warn("Timing overflow detected, resetting playback");
                    _playbackStartTimeMs = Utils.NowMs();
                    elapsedMs = 0;
                }

                if (_totalDurationMs == 0)
                {
                    Logger.Warn("Update called with zero total duration - returning last position");
                    position = _positions[_positions.Count - 1];
                    rotation = _rotations[_rotations.Count - 1];
                    if (_fovs != null && _fovs.Count > 0) fov = _fovs[_fovs.Count - 1];
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
                    if (_fovs != null && _fovs.Count > 0) fov = _fovs[_fovs.Count - 1];
                    PlaybackProgress = 1f;
                    return;
                }

                if (currentSegment == _durations.Count - 1)
                {
                    position = _positions[_positions.Count - 1];
                    rotation = _rotations[_rotations.Count - 1];
                    if (_fovs != null && _fovs.Count > 0) fov = _fovs[_fovs.Count - 1];
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

                bool round = (modeOut == 1 || modeIn == 1);

                Vector3 p0 = (currentSegment > 0) ? _positions[currentSegment - 1] : _positions[currentSegment];
                Vector3 p1 = _positions[currentSegment];
                Vector3 p2 = _positions[currentSegment + 1];
                Vector3 p3 = (currentSegment + 2 < _positions.Count) ? _positions[currentSegment + 2] : _positions[currentSegment + 1];

                Vector3 straightPos = Vector3.Lerp(p1, p2, f);
                Vector3 straightRot = InterpolateRotationShortest(currentSegment, f);

                if (round)
                {
                    Vector3 splinePos = CubicHermite(p0, p1, p2, p3, f, _tanScale);
                    Vector3 r0 = (currentSegment > 0) ? _rotations[currentSegment - 1] : _rotations[currentSegment];
                    Vector3 r1 = _rotations[currentSegment];
                    Vector3 r2 = UnwrapRotation(r1, _rotations[currentSegment + 1]);
                    Vector3 r3 = (currentSegment + 2 < _rotations.Count) ? UnwrapRotation(r2, _rotations[currentSegment + 2]) : r2;
                    Vector3 splineRot = CubicHermiteRot(r0, r1, r2, r3, f, _tanScale);
                    position = splinePos;
                    rotation = splineRot;
                }
                else
                {
                    position = straightPos;
                    rotation = straightRot;
                }

                if (_fovs != null && currentSegment + 1 < _fovs.Count)
                    fov = _fovs[currentSegment] + (_fovs[currentSegment + 1] - _fovs[currentSegment]) * f;
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

        private Vector3 CubicHermite(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t, float tanScale)
        {
            Vector3 m1 = (p2 - p0) * (0.5f * tanScale);
            Vector3 m2 = (p3 - p1) * (0.5f * tanScale);
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * p1 + h10 * m1 + h01 * p2 + h11 * m2;
        }

        private Vector3 CubicHermiteRot(Vector3 r0, Vector3 r1, Vector3 r2, Vector3 r3, float t, float tanScale)
        {
            Vector3 m1 = (r2 - r0) * (0.5f * tanScale);
            Vector3 m2 = (r3 - r1) * (0.5f * tanScale);
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * r1 + h10 * m1 + h01 * r2 + h11 * m2;
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

        private const float _easeFrac = 0.2f;
        private const float _noStopFloor = 0.25f;

        private float Ease(int mode, float t)
        {
            if (mode == 0) return t;
            if (mode == 1) return TrapezoidEase(t, _easeFrac, _noStopFloor);
            return TrapezoidEase(t, _easeFrac, 0f);
        }

        private float TrapezoidEase(float t, float ease, float floor)
        {
            if (ease < 0.001f) ease = 0.001f;
            if (ease > 0.49f) ease = 0.49f;
            if (floor < 0f) floor = 0f;
            if (floor > 0.95f) floor = 0.95f;

            float A = 1f - ease * (1f - floor);
            if (t <= ease)
            {
                float F = floor * t + (1f - floor) * (t * t) / (2f * ease);
                return F / A;
            }
            if (t >= 1f - ease)
            {
                float u = 1f - t;
                float F1mE = ease * (floor + 1f) / 2f + (1f - 2f * ease);
                float F = F1mE + floor * (ease - u) + (1f - floor) * (ease * ease - u * u) / (2f * ease);
                return F / A;
            }
            float Fe = ease * (floor + 1f) / 2f;
            float Fmid = Fe + (t - ease);
            return Fmid / A;
        }

        private const float _tanScale = 0.5f;

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

}
