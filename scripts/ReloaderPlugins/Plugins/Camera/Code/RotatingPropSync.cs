using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ModPack.Camera.Services
{
    // Menyoo's relative Snap To Rotation uses real-time script ticks, so GTA's
    // time scale does not slow it. Correct observed rotation steps of static props.
    internal sealed class RotatingPropSync
    {
        private const float Radius = 120f;
        private const int MaxProps = 256;
        private const int RescanMs = 1000;
        private readonly Dictionary<int, Snapshot> _props = new Dictionary<int, Snapshot>();
        private long _lastScan;

        private sealed class Snapshot
        {
            public Prop Prop;
            public int Model;
            public Vector3 Position;
            public Vector3 AppliedRotation;
        }

        public void Clear()
        {
            _props.Clear();
            _lastScan = 0;
        }

        public void Update(Vector3 cameraPosition, float speed)
        {
            if (speed >= 1f || speed <= 0f) { Clear(); return; }
            long now = Utils.NowMs();
            if (_lastScan == 0 || now - _lastScan >= RescanMs)
            {
                Scan(cameraPosition);
                _lastScan = now;
            }

            var invalid = new List<int>();
            foreach (KeyValuePair<int, Snapshot> entry in _props)
            {
                Snapshot state = entry.Value;
                Prop prop = state.Prop;
                if (prop == null || !prop.Exists() || prop.Model.Hash != state.Model ||
                    !prop.IsPositionFrozen || Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, prop.Handle) ||
                    (prop.Position - state.Position).LengthSquared() > 0.01f ||
                    (prop.Position - cameraPosition).LengthSquared() > Radius * Radius)
                {
                    invalid.Add(entry.Key);
                    continue;
                }

                Vector3 actual = prop.Rotation;
                float pitchStep = SignedAngle(actual.X - state.AppliedRotation.X);
                float rollStep = SignedAngle(actual.Y - state.AppliedRotation.Y);
                float yawStep = SignedAngle(actual.Z - state.AppliedRotation.Z);
                // Ignore abrupt manual changes instead of pulling the prop back.
                if (Math.Abs(pitchStep) > 12f || Math.Abs(rollStep) > 12f ||
                    Math.Abs(yawStep) > 12f)
                {
                    state.AppliedRotation = actual;
                    continue;
                }
                if (Math.Abs(pitchStep) < 0.001f && Math.Abs(rollStep) < 0.001f &&
                    Math.Abs(yawStep) < 0.001f) continue;
                actual.X = state.AppliedRotation.X + pitchStep * speed;
                actual.Y = state.AppliedRotation.Y + rollStep * speed;
                actual.Z = state.AppliedRotation.Z + yawStep * speed;
                prop.Rotation = actual;
                state.AppliedRotation = prop.Rotation;
            }
            foreach (int handle in invalid) _props.Remove(handle);
        }

        private void Scan(Vector3 cameraPosition)
        {
            var nearby = new List<Prop>(World.GetNearbyProps(cameraPosition, Radius));
            nearby.Sort((a, b) =>
                (a.Position - cameraPosition).LengthSquared().CompareTo(
                    (b.Position - cameraPosition).LengthSquared()));
            var seen = new HashSet<int>();
            foreach (Prop prop in nearby)
            {
                if (seen.Count >= MaxProps) break;
                if (prop == null || !prop.Exists() || !prop.IsPositionFrozen ||
                    Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, prop.Handle)) continue;
                seen.Add(prop.Handle);
                if (_props.ContainsKey(prop.Handle)) continue;
                _props.Add(prop.Handle, new Snapshot
                {
                    Prop = prop, Model = prop.Model.Hash,
                    Position = prop.Position, AppliedRotation = prop.Rotation
                });
            }
            var stale = new List<int>();
            foreach (int handle in _props.Keys)
                if (!seen.Contains(handle)) stale.Add(handle);
            foreach (int handle in stale) _props.Remove(handle);
        }

        private static float SignedAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;
            return angle;
        }
    }
}
