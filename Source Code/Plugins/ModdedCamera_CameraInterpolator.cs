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
    /// Rotation: the look direction is slerped (constant angular velocity),
    /// roll is lerped along the shortest path. Per-component Euler lerp is
    /// NOT used because it bends the view direction whenever more than one
    /// axis changes at once.
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
                // on the last waypoint.
                if (currentSegment == -1 || currentSegment == _durations.Count - 1)
                {
                    position = _positions[_positions.Count - 1];
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
                rotation = InterpolateRotationSlerp(currentSegment, t);

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

        // Rotation with constant angular velocity: slerp the look direction
        // between the two endpoint orientations, lerp roll shortest-path.
        // Per-component Euler lerp is deliberately avoided: it curves the
        // view direction and varies its speed whenever pitch and yaw change
        // together.
        private Vector3 InterpolateRotationSlerp(int segment, float t)
        {
            Vector3 r1 = _rotations[segment];
            Vector3 r2 = _rotations[segment + 1];

            Vector3 f0 = RotationToDirection(r1);
            Vector3 f1 = RotationToDirection(r2);
            Vector3 f = SlerpDirection(f0, f1, t);

            float fallbackYaw = LerpAngle(r1.Z, r2.Z, t);
            Vector3 pr = DirectionToRotation(f, fallbackYaw);
            float roll = LerpAngle(r1.Y, r2.Y, t);
            return new Vector3(pr.X, roll, pr.Z);
        }

        private float LerpAngle(float a, float b, float t)
        {
            float delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return a + delta * t;
        }

        // Look direction of a camera Euler rotation (pitch=X, yaw=Z, degrees).
        // Same convention as Utils.RotationToDirection.
        private static Vector3 RotationToDirection(Vector3 rotation)
        {
            double yaw = (double)(rotation.Z * 0.01745329f);
            double pitch = (double)(rotation.X * 0.01745329f);
            double cp = Math.Abs(Math.Cos(pitch));
            return new Vector3(
                (float)(-(Math.Sin(yaw) * cp)),
                (float)(Math.Cos(yaw) * cp),
                (float)Math.Sin(pitch));
        }

        // Inverse of RotationToDirection: pitch/yaw (degrees) for a unit
        // direction. Roll is left at 0 - the caller fills it in.
        private static Vector3 DirectionToRotation(Vector3 dir, float fallbackYaw)
        {
            float len = dir.Length();
            Vector3 d = (len > 0.000001f) ? dir * (1f / len) : new Vector3(0f, 1f, 0f);
            float z = d.Z;
            if (z > 1f) z = 1f;
            if (z < -1f) z = -1f;
            float pitch = (float)(Math.Asin(z) * 57.29578f);
            float yaw;
            float cp = (float)Math.Sqrt(Math.Max(0f, 1f - z * z));
            if (cp < 0.0001f)
                yaw = fallbackYaw; // looking straight up/down: yaw undefined
            else
                yaw = (float)(Math.Atan2(-d.X, d.Y) * 57.29578f);
            return new Vector3(pitch, 0f, yaw);
        }

        // Spherical interpolation between two unit direction vectors:
        // constant angular velocity from 'from' (t=0) to 'to' (t=1).
        private static Vector3 SlerpDirection(Vector3 from, Vector3 to, float t)
        {
            float dot = from.X * to.X + from.Y * to.Y + from.Z * to.Z;
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;

            if (dot > 0.9995f)
            {
                // Nearly identical: normalized lerp avoids division by ~0.
                Vector3 l = from + (to - from) * t;
                float len = l.Length();
                return (len > 0.000001f) ? l * (1f / len) : from;
            }

            if (dot < -0.9995f)
            {
                // Opposite directions: no unique great circle. Rotate around
                // an arbitrary axis perpendicular to 'from'.
                Vector3 axis = (Math.Abs(from.Z) < 0.99f)
                    ? new Vector3(0f, 0f, 1f)
                    : new Vector3(0f, 1f, 0f);
                float d = axis.X * from.X + axis.Y * from.Y + axis.Z * from.Z;
                axis = axis - from * d;
                float alen = axis.Length();
                if (alen < 0.000001f)
                    return from;
                axis = axis * (1f / alen);
                float ang = (float)Math.PI * t;
                float c = (float)Math.Cos(ang);
                float s = (float)Math.Sin(ang);
                return from * c + axis * s;
            }

            float omega = (float)Math.Acos(dot);
            float sinO = (float)Math.Sin(omega);
            float s0 = (float)Math.Sin((1f - t) * omega) / sinO;
            float s1 = (float)Math.Sin(t * omega) / sinO;
            return from * s0 + to * s1;
        }

        public void Clear()
        {
            _positions.Clear();
            _rotations.Clear();
            _durations.Clear();
            _fovs.Clear();
            _isPlaying = false;
            _totalDurationMs = 0;
            PlaybackProgress = 0f;
        }
    }
}
