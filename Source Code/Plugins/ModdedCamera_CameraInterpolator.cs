using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace ModdedCamera
{
    /// <summary>
    /// Camera path interpolation modeled after Rockstar Editor blend markers.
    /// Linear segments stay exact and predictable. SmoothStop keeps the node
    /// exact and eases to/from a full stop. SmoothNoStop treats the marker as
    /// an approximate guide point, like Rockstar Editor Smooth Blend: the
    /// camera bends near it instead of being forced to hit it exactly.
    /// Rotation: the look direction is slerped (constant angular velocity),
    /// roll is lerped along the shortest path. Per-component Euler lerp is
    /// NOT used because it bends the view direction whenever more than one
    /// axis changes at once.
    /// </summary>
    public class CameraInterpolator
    {
        private List<Vector3> _positions;
        private List<Vector3> _rotations;
        private List<int> _durations;
        private List<int> _nodeModes;
        private List<int> _fovs;
        private bool _isPlaying = false;
        private long _playbackElapsedMs = 0;
        private int _totalDurationMs = 0;

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
            _nodeModes = new List<int>();
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

                _nodeModes = new List<int>();
                int modeCount = (nodeModes != null) ? nodeModes.Count : 0;
                for (int i = 0; i < _positions.Count; i++)
                {
                    int mode = (i < modeCount) ? nodeModes[i] : _interpMode;
                    _nodeModes.Add(NormalizeMode(mode));
                }

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

                // The last node duration is a still hold. When the cycle wraps,
                // playback cuts sharply back to node 1 instead of flying there.
                if (currentSegment == -1 || currentSegment == _durations.Count - 1)
                {
                    int lastNode = _positions.Count - 1;
                    position = _positions[lastNode];
                    rotation = _rotations[lastNode];
                    if (_fovs != null && _fovs.Count > 0) fov = _fovs[lastNode];
                    if (currentSegment == -1) PlaybackProgress = 1f;
                    return;
                }

                int segmentDurationMs = Math.Max(0, _durations[currentSegment]);
                double segmentElapsedMs = cycleTime - accumulatedMs;
                float t = (segmentDurationMs > 0) ? (float)(segmentElapsedMs / segmentDurationMs) : 0f;
                t = Math.Min(Math.Max(t, 0f), 1f);

                float interpT = ApplyStopTiming(currentSegment, t);

                position = InterpolatePosition(currentSegment, t);
                rotation = InterpolateRotation(currentSegment, t, interpT);

                if (_fovs != null && currentSegment + 1 < _fovs.Count)
                    fov = InterpolateFov(currentSegment, t, interpT);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Update error - continuing playback");
                position = _positions.Count > 0 ? _positions[_positions.Count - 1] : Vector3.Zero;
                rotation = _rotations.Count > 0 ? _rotations[_rotations.Count - 1] : Vector3.Zero;
                if (_fovs != null && _fovs.Count > 0) fov = _fovs[_fovs.Count - 1];
            }
        }

        private int GetNodeMode(int node)
        {
            if (_nodeModes != null && node >= 0 && node < _nodeModes.Count)
                return NormalizeMode(_nodeModes[node]);
            return NormalizeMode(_interpMode);
        }

        private static int NormalizeMode(int mode)
        {
            return (mode == 0 || mode == 1 || mode == 2) ? mode : 0;
        }

        private float ApplyStopTiming(int segment, float t)
        {
            bool startStop = GetNodeMode(segment) == 1;
            bool endStop = GetNodeMode(segment + 1) == 1;

            if (startStop && endStop)
                return SmoothStep(t);

            if (startStop)
                return EaseOutOfStop(t);

            if (endStop)
                return EaseIntoStop(t);

            return t;
        }

        private static float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }

        // Starts at rest and reaches ordinary segment speed at t = 1.
        private static float EaseOutOfStop(float t)
        {
            return -t * t * t + 2f * t * t;
        }

        // Arrives at rest while retaining ordinary segment speed at t = 0.
        private static float EaseIntoStop(float t)
        {
            return -t * t * t + t * t + t;
        }

        private Vector3 InterpolatePosition(int segment, float t)
        {
            int startMode = GetNodeMode(segment);
            int endMode = GetNodeMode(segment + 1);

            if (startMode == 0 && endMode == 0)
                return Vector3.Lerp(_positions[segment], _positions[segment + 1], t);

            if (startMode != 2 && endMode != 2)
                return Vector3.Lerp(_positions[segment], _positions[segment + 1], ApplyStopTiming(segment, t));

            Vector3 start = GetNodeBlendPosition(segment);
            Vector3 end = GetNodeBlendPosition(segment + 1);
            Vector3 tangentStart = GetNodeTangent(segment, segment);
            Vector3 tangentEnd = GetNodeTangent(segment + 1, segment);
            return CubicHermite(start, tangentStart, end, tangentEnd, t);
        }

        private float InterpolateFov(int segment, float rawT, float timedT)
        {
            int startMode = GetNodeMode(segment);
            int endMode = GetNodeMode(segment + 1);

            if (startMode == 0 && endMode == 0)
                return LerpFloat(_fovs[segment], _fovs[segment + 1], rawT);

            if (startMode != 2 && endMode != 2)
                return LerpFloat(_fovs[segment], _fovs[segment + 1], timedT);

            float start = GetNodeBlendFov(segment);
            float end = GetNodeBlendFov(segment + 1);
            float tangentStart = GetFovTangent(segment, segment);
            float tangentEnd = GetFovTangent(segment + 1, segment);
            return CubicHermite(start, tangentStart, end, tangentEnd, rawT);
        }

        private float GetNodeBlendFov(int node)
        {
            if (GetNodeMode(node) != 2 || node <= 0 || node >= _fovs.Count - 1)
                return _fovs[node];

            return (_fovs[node - 1] + _fovs[node] * 4f + _fovs[node + 1]) * (1f / 6f);
        }

        private float GetFovTangent(int node, int segment)
        {
            int mode = GetNodeMode(node);
            if (mode == 1)
                return 0f;

            float segmentDelta = GetNodeBlendFov(segment + 1) - GetNodeBlendFov(segment);
            if (mode == 0 || node <= 0 || node >= _fovs.Count - 1)
                return segmentDelta;

            float incomingDuration = Math.Max(10, _durations[node - 1]);
            float outgoingDuration = Math.Max(10, _durations[node]);
            float segmentDuration = Math.Max(10, _durations[segment]);
            float incomingVelocity = (GetNodeBlendFov(node) - GetNodeBlendFov(node - 1)) / incomingDuration;
            float outgoingVelocity = (GetNodeBlendFov(node + 1) - GetNodeBlendFov(node)) / outgoingDuration;
            return ((incomingVelocity + outgoingVelocity) * 0.5f) * segmentDuration;
        }

        private static float LerpFloat(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private Vector3 GetNodeBlendPosition(int node)
        {
            if (GetNodeMode(node) != 2 || node <= 0 || node >= _positions.Count - 1)
                return _positions[node];

            // Uniform cubic B-spline point at this marker. The real marker
            // strongly pulls the path, but the camera is allowed to pass near
            // it instead of snapping exactly onto it.
            return (_positions[node - 1] + _positions[node] * 4f + _positions[node + 1]) * (1f / 6f);
        }

        private Vector3 GetNodeTangent(int node, int segment)
        {
            int mode = GetNodeMode(node);
            if (mode == 1)
                return Vector3.Zero;

            Vector3 segmentDelta = GetNodeBlendPosition(segment + 1) - GetNodeBlendPosition(segment);
            if (mode == 0 || node <= 0 || node >= _positions.Count - 1)
                return segmentDelta;

            float incomingDuration = Math.Max(10, _durations[node - 1]);
            float outgoingDuration = Math.Max(10, _durations[node]);
            float segmentDuration = Math.Max(10, _durations[segment]);
            Vector3 incomingVelocity = (GetNodeBlendPosition(node) - GetNodeBlendPosition(node - 1)) * (1f / incomingDuration);
            Vector3 outgoingVelocity = (GetNodeBlendPosition(node + 1) - GetNodeBlendPosition(node)) * (1f / outgoingDuration);
            Vector3 blendedVelocity = (incomingVelocity + outgoingVelocity) * 0.5f;
            return blendedVelocity * segmentDuration;
        }

        private static Vector3 CubicHermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return p0 * h00 + m0 * h10 + p1 * h01 + m1 * h11;
        }

        // Rotation with constant angular velocity: slerp the look direction
        // between the two endpoint orientations, lerp roll shortest-path.
        // Per-component Euler lerp is deliberately avoided: it curves the
        // view direction and varies its speed whenever pitch and yaw change
        // together.
        private Vector3 InterpolateRotation(int segment, float rawT, float timedT)
        {
            int startMode = GetNodeMode(segment);
            int endMode = GetNodeMode(segment + 1);

            if (startMode != 2 && endMode != 2)
                return InterpolateRotationSlerp(segment, timedT);

            return InterpolateRotationSmoothBlend(segment, rawT);
        }

        private Vector3 InterpolateRotationSmoothBlend(int segment, float t)
        {
            return InterpolateRotationEulerSmoothBlend(segment, t);
        }

        private Vector3 InterpolateRotationEulerSmoothBlend(int segment, float t)
        {
            float pitch0 = GetNodeBlendPitch(segment);
            float pitch1 = GetNodeBlendPitch(segment + 1);
            float yaw0 = GetNodeBlendYaw(segment);
            float yaw1 = UnwrapAngleNear(GetNodeBlendYaw(segment + 1), yaw0);
            float roll0 = GetNodeBlendRoll(segment);
            float roll1 = UnwrapAngleNear(GetNodeBlendRoll(segment + 1), roll0);

            float pitch = CubicHermite(pitch0, GetPitchTangent(segment, segment), pitch1, GetPitchTangent(segment + 1, segment), t);
            float yaw = CubicHermite(yaw0, GetYawTangent(segment, segment), yaw1, GetYawTangent(segment + 1, segment), t);
            float roll = CubicHermite(roll0, GetRollTangent(segment, segment), roll1, GetRollTangent(segment + 1, segment), t);
            return new Vector3(pitch, roll, yaw);
        }

        private Vector3 GetNodeBlendDirection(int node)
        {
            if (GetNodeMode(node) != 2 || node <= 0 || node >= _rotations.Count - 1)
                return RotationToDirection(_rotations[node]);

            Vector3 previous = RotationToDirection(_rotations[node - 1]);
            Vector3 current = RotationToDirection(_rotations[node]);
            Vector3 next = RotationToDirection(_rotations[node + 1]);
            Vector3 blended = (previous + current * 4f + next) * (1f / 6f);
            float len = blended.Length();
            return (len > 0.000001f) ? blended * (1f / len) : current;
        }

        private Vector3 GetDirectionTangent(int node, int segment)
        {
            int mode = GetNodeMode(node);
            if (mode == 1)
                return Vector3.Zero;

            Vector3 segmentDelta = GetNodeBlendDirection(segment + 1) - GetNodeBlendDirection(segment);
            if (mode == 0 || node <= 0 || node >= _rotations.Count - 1)
                return segmentDelta;

            float incomingDuration = Math.Max(10, _durations[node - 1]);
            float outgoingDuration = Math.Max(10, _durations[node]);
            float segmentDuration = Math.Max(10, _durations[segment]);
            Vector3 incomingVelocity = (GetNodeBlendDirection(node) - GetNodeBlendDirection(node - 1)) * (1f / incomingDuration);
            Vector3 outgoingVelocity = (GetNodeBlendDirection(node + 1) - GetNodeBlendDirection(node)) * (1f / outgoingDuration);
            Vector3 blendedVelocity = (incomingVelocity + outgoingVelocity) * 0.5f;
            return blendedVelocity * segmentDuration;
        }

        private float InterpolateRollSmoothBlend(int segment, float t)
        {
            float r0 = GetNodeBlendRoll(segment);
            float r1 = UnwrapAngleNear(GetNodeBlendRoll(segment + 1), r0);
            float m0 = GetRollTangent(segment, segment);
            float m1 = GetRollTangent(segment + 1, segment);
            return CubicHermite(r0, m0, r1, m1, t);
        }

        private float GetNodeBlendYaw(int node)
        {
            if (GetNodeMode(node) != 2 || node <= 0 || node >= _rotations.Count - 1)
                return _rotations[node].Z;

            float current = _rotations[node].Z;
            float previous = UnwrapAngleNear(_rotations[node - 1].Z, current);
            float next = UnwrapAngleNear(_rotations[node + 1].Z, current);
            return (previous + current * 4f + next) * (1f / 6f);
        }

        private float GetNodeBlendPitch(int node)
        {
            if (GetNodeMode(node) != 2 || node <= 0 || node >= _rotations.Count - 1)
                return _rotations[node].X;

            float current = _rotations[node].X;
            float previous = UnwrapAngleNear(_rotations[node - 1].X, current);
            float next = UnwrapAngleNear(_rotations[node + 1].X, current);
            return (previous + current * 4f + next) * (1f / 6f);
        }

        private float GetNodeBlendRoll(int node)
        {
            if (GetNodeMode(node) != 2 || node <= 0 || node >= _rotations.Count - 1)
                return _rotations[node].Y;

            float current = _rotations[node].Y;
            float previous = UnwrapAngleNear(_rotations[node - 1].Y, current);
            float next = UnwrapAngleNear(_rotations[node + 1].Y, current);
            return (previous + current * 4f + next) * (1f / 6f);
        }

        private float GetRollTangent(int node, int segment)
        {
            int mode = GetNodeMode(node);
            if (mode == 1)
                return 0f;

            float segmentStart = GetNodeBlendRoll(segment);
            float segmentEnd = UnwrapAngleNear(GetNodeBlendRoll(segment + 1), segmentStart);
            float segmentDelta = segmentEnd - segmentStart;
            if (mode == 0 || node <= 0 || node >= _rotations.Count - 1)
                return segmentDelta;

            float center = GetNodeBlendRoll(node);
            float previous = UnwrapAngleNear(GetNodeBlendRoll(node - 1), center);
            float next = UnwrapAngleNear(GetNodeBlendRoll(node + 1), center);
            float incomingDuration = Math.Max(10, _durations[node - 1]);
            float outgoingDuration = Math.Max(10, _durations[node]);
            float segmentDuration = Math.Max(10, _durations[segment]);
            float incomingVelocity = (center - previous) / incomingDuration;
            float outgoingVelocity = (next - center) / outgoingDuration;
            return ((incomingVelocity + outgoingVelocity) * 0.5f) * segmentDuration;
        }

        private float GetYawTangent(int node, int segment)
        {
            return GetAngleTangent(node, segment, true);
        }

        private float GetPitchTangent(int node, int segment)
        {
            return GetAngleTangent(node, segment, false);
        }

        private float GetAngleTangent(int node, int segment, bool yaw)
        {
            int mode = GetNodeMode(node);
            if (mode == 1)
                return 0f;

            float segmentStart = yaw ? GetNodeBlendYaw(segment) : GetNodeBlendPitch(segment);
            float segmentEnd = yaw ? GetNodeBlendYaw(segment + 1) : GetNodeBlendPitch(segment + 1);
            segmentEnd = UnwrapAngleNear(segmentEnd, segmentStart);
            float segmentDelta = segmentEnd - segmentStart;
            if (mode == 0 || node <= 0 || node >= _rotations.Count - 1)
                return segmentDelta;

            float center = yaw ? GetNodeBlendYaw(node) : GetNodeBlendPitch(node);
            float previous = yaw ? GetNodeBlendYaw(node - 1) : GetNodeBlendPitch(node - 1);
            float next = yaw ? GetNodeBlendYaw(node + 1) : GetNodeBlendPitch(node + 1);
            previous = UnwrapAngleNear(previous, center);
            next = UnwrapAngleNear(next, center);
            float incomingDuration = Math.Max(10, _durations[node - 1]);
            float outgoingDuration = Math.Max(10, _durations[node]);
            float segmentDuration = Math.Max(10, _durations[segment]);
            float incomingVelocity = (center - previous) / incomingDuration;
            float outgoingVelocity = (next - center) / outgoingDuration;
            return ((incomingVelocity + outgoingVelocity) * 0.5f) * segmentDuration;
        }

        private Vector3 InterpolateRotationBetween(Vector3 r1, Vector3 r2, float t)
        {
            float pitch = LerpAngle(r1.X, r2.X, t);
            float roll = LerpAngle(r1.Y, r2.Y, t);
            float yaw = LerpAngle(r1.Z, r2.Z, t);
            return new Vector3(pitch, roll, yaw);
        }

        private Vector3 InterpolateRotationSlerp(int segment, float t)
        {
            Vector3 r1 = _rotations[segment];
            Vector3 r2 = _rotations[segment + 1];
            return InterpolateRotationBetween(r1, r2, t);
        }

        private static float CubicHermite(float p0, float m0, float p1, float m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return p0 * h00 + m0 * h10 + p1 * h01 + m1 * h11;
        }

        private static float UnwrapAngleNear(float angle, float reference)
        {
            float result = angle;
            while (result - reference > 180f) result -= 360f;
            while (result - reference < -180f) result += 360f;
            return result;
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
            _nodeModes.Clear();
            _fovs.Clear();
            _isPlaying = false;
            _totalDurationMs = 0;
            PlaybackProgress = 0f;
        }
    }
}
