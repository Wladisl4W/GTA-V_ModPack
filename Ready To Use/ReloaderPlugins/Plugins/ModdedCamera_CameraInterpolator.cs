using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace ModdedCamera
{
    public class CameraInterpolator
    {
        private List<Vector3> _positions;
        private List<Vector3> _rotations;
        private List<int> _durations;
        private List<NodeInterpMode> _nodeModes;
        private List<int> _fovs;
        private bool _isPlaying = false;
        private long _playbackElapsedMs = 0;
        private int _totalDurationMs = 0;

        private List<Vector3> _rotationsU;
        private List<SegmentPlan> _segmentPlans;

        private const float BLEND_ZONE_FRAC = 0.15f;
        private const int SPEED_SAMPLES = 96;
        private const float TAN_SCALE = 0.5f;

        private int _interpMode = 0;
        public int InterpolationMode
        {
            get { return _interpMode; }
            set
            {
                _interpMode = value;
                if (_nodeModes != null)
                {
                    for (int i = 0; i < _nodeModes.Count; i++)
                        _nodeModes[i] = (NodeInterpMode)value;
                }
            }
        }

        public bool IsPlaying { get { return _isPlaying; } }
        public float PlaybackProgress { get; private set; }

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
            _nodeModes = new List<NodeInterpMode>();
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations)
        {
            SetPath(positions, rotations, durations, null, null);
        }

        public void SetPath(List<Vector3> positions, List<Vector3> rotations, List<int> durations, List<int> nodeModes, List<int> fovs = null)
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

                _nodeModes = new List<NodeInterpMode>();
                int modeCount = (nodeModes != null) ? nodeModes.Count : 0;
                for (int i = 0; i < _positions.Count; i++)
                {
                    int modeVal = (i < modeCount) ? nodeModes[i] : 0;
                    _nodeModes.Add((NodeInterpMode)modeVal);
                }

                _fovs = new List<int>();
                int fovCount = (fovs != null) ? fovs.Count : 0;
                for (int i = 0; i < _positions.Count; i++)
                    _fovs.Add((i < fovCount) ? fovs[i] : 50);

                _totalDurationMs = 0;
                for (int i = 0; i < _durations.Count; i++)
                    _totalDurationMs += _durations[i];

                BuildRotationUnwrap();
                BuildSegmentPlans();
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

        private void BuildSegmentPlans()
        {
            _segmentPlans = new List<SegmentPlan>();
            int n = _positions.Count;
            if (n < 2) return;

            var blendZones = new List<BlendZone>();
            for (int node = 1; node <= n - 2; node++)
            {
                var mode = _nodeModes[node];
                if (mode == NodeInterpMode.Linear) continue;

                var pPrev = _positions[node - 1];
                var pNode = _positions[node];
                var pNext = _positions[node + 1];
                var inLeg = pNode - pPrev;
                var outLeg = pNext - pNode;
                float L1 = inLeg.Length();
                float L2 = outLeg.Length();
                if (L1 < 0.001f || L2 < 0.001f) continue;

                var inDir = inLeg * (1f / L1);
                var outDir = outLeg * (1f / L2);

                float blendDist1 = Math.Min(L1 * BLEND_ZONE_FRAC, L1 * 0.5f);
                float blendDist2 = Math.Min(L2 * BLEND_ZONE_FRAC, L2 * 0.5f);

                if (mode == NodeInterpMode.SmoothStop)
                {
                    blendZones.Add(new BlendZone
                    {
                        nodeIndex = node, type = SegmentType.SmoothStop, segIndex = node - 1,
                        startDist = L1 - blendDist1, endDist = L1, isEndOfSegment = true, cornerNode = node
                    });
                    blendZones.Add(new BlendZone
                    {
                        nodeIndex = node, type = SegmentType.SmoothStop, segIndex = node,
                        startDist = 0f, endDist = blendDist2, isEndOfSegment = false, cornerNode = node
                    });
                }
            }

            blendZones.Sort((a, b) =>
            {
                int cmp = a.segIndex.CompareTo(b.segIndex);
                if (cmp != 0) return cmp;
                return a.startDist.CompareTo(b.startDist);
            });

            for (int seg = 0; seg <= n - 2; seg++)
            {
                var pA = _positions[seg];
                var pB = _positions[seg + 1];
                var leg = pB - pA;
                float L = leg.Length();
                if (L < 0.001f) continue;
                var dir = leg * (1f / L);

                var segZones = new List<BlendZone>();
                foreach (var bz in blendZones)
                {
                    if (bz.segIndex == seg) segZones.Add(bz);
                }

                float cursor = 0f;
                foreach (var bz in segZones)
                {
                    if (bz.startDist > cursor + 0.001f)
                    {
                        _segmentPlans.Add(CreateLinearPlan(seg, pA, dir, cursor, bz.startDist, _fovs[seg], _fovs[seg + 1], _rotationsU[seg], _rotationsU[seg + 1]));
                    }
                    if (bz.endDist > bz.startDist + 0.001f)
                    {
                        _segmentPlans.Add(CreateBlendPlan(seg, pA, dir, bz, _fovs[seg], _fovs[seg + 1], _rotationsU[seg], _rotationsU[seg + 1]));
                    }
                    cursor = bz.endDist;
                }
                if (L > cursor + 0.001f)
                {
                    _segmentPlans.Add(CreateLinearPlan(seg, pA, dir, cursor, L, _fovs[seg], _fovs[seg + 1], _rotationsU[seg], _rotationsU[seg + 1]));
                }
            }

            var plansBySeg = new Dictionary<int, List<SegmentPlan>>();
            foreach (var sp in _segmentPlans)
            {
                if (!plansBySeg.ContainsKey(sp.segIndex))
                    plansBySeg[sp.segIndex] = new List<SegmentPlan>();
                plansBySeg[sp.segIndex].Add(sp);
            }

            foreach (var kvp in plansBySeg)
            {
                int segIdx = kvp.Key;
                var plans = kvp.Value;
                int segDur = _durations[segIdx];
                float segTotalLen = 0f;
                foreach (var p in plans) segTotalLen += p.totalLen;
                foreach (var sp in plans)
                {
                    float timeFrac = segTotalLen > 0f ? sp.totalLen / segTotalLen : 1f / plans.Count;
                    sp.BuildTimeTableProportional(segDur, timeFrac);
                }
            }
        }

        private class BlendZone
        {
            public int nodeIndex;
            public SegmentType type;
            public int segIndex;
            public float startDist;
            public float endDist;
            public int cornerNode;
            public bool isEndOfSegment;
        }

        private SegmentPlan CreateLinearPlan(int seg, Vector3 pA, Vector3 dir, float startDist, float endDist, int fovStart, int fovEnd, Vector3 rotStart, Vector3 rotEnd)
        {
            return new SegmentPlan
            {
                type = SegmentType.Linear,
                segIndex = seg,
                startDist = startDist,
                endDist = endDist,
                totalLen = endDist - startDist,
                dir = dir,
                startPos = pA + dir * startDist,
                nodeIndex = seg,
                fovStart = fovStart,
                fovEnd = fovEnd,
                rotStart = rotStart,
                rotEnd = rotEnd,
                prevRot = (seg > 0) ? _rotationsU[seg - 1] : rotStart,
                nextRot = (seg + 2 < _rotationsU.Count) ? _rotationsU[seg + 2] : rotEnd
            };
        }

        private SegmentPlan CreateBlendPlan(int seg, Vector3 pA, Vector3 dir, BlendZone bz, int fovStart, int fovEnd, Vector3 rotStart, Vector3 rotEnd)
        {
            return new SegmentPlan
            {
                type = bz.type,
                segIndex = seg,
                startDist = bz.startDist,
                endDist = bz.endDist,
                dir = dir,
                startPos = pA + dir * bz.startDist,
                nodeIndex = bz.cornerNode,
                fovStart = fovStart,
                fovEnd = fovEnd,
                rotStart = rotStart,
                rotEnd = rotEnd,
                prevRot = (seg > 0) ? _rotationsU[seg - 1] : rotStart,
                nextRot = (seg + 2 < _rotationsU.Count) ? _rotationsU[seg + 2] : rotEnd,
                isEndOfSegment = bz.isEndOfSegment
            };
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

                int currentPlan = -1;
                double accumulatedMs = 0;
                for (int i = 0; i < _segmentPlans.Count; i++)
                {
                    var sp = _segmentPlans[i];
                    if (cycleTime < accumulatedMs + sp.timeTotal)
                    {
                        currentPlan = i;
                        break;
                    }
                    accumulatedMs += sp.timeTotal;
                }

                if (currentPlan == -1)
                {
                    position = _positions[_positions.Count - 1];
                    rotation = _rotations[_rotations.Count - 1];
                    fov = _fovs[_fovs.Count - 1];
                    PlaybackProgress = 1f;
                    return;
                }

                var plan = _segmentPlans[currentPlan];
                double planElapsedMs = cycleTime - accumulatedMs;
                double frac = plan.timeTotal > 0 ? planElapsedMs / plan.timeTotal : 0;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;

                double dist = plan.InvertTime(frac);
                float q = plan.totalLen > 0f ? (float)(dist / plan.totalLen) : 0f;
                if (q < 0f) q = 0f;
                if (q > 1f) q = 1f;

                if (plan.type == SegmentType.Linear)
                {
                    position = plan.startPos + plan.dir * (float)dist;
                }
                else if (plan.type == SegmentType.SmoothStop)
                {
                    position = plan.startPos + plan.dir * (float)dist;
                }
                else // SmoothNoStop
                {
                    position = plan.startPos + plan.dir * (float)dist;
                }

                if (plan.type == SegmentType.Linear)
                {
                    rotation = CubicHermiteRot(plan.prevRot, plan.rotStart, plan.rotEnd, plan.nextRot, q, TAN_SCALE);
                }
                else if (plan.type == SegmentType.SmoothStop)
                {
                    float easeQ = Smoother01(q);
                    rotation = CubicHermiteRot(plan.prevRot, plan.rotStart, plan.rotEnd, plan.nextRot, easeQ, TAN_SCALE);
                }
                else // SmoothNoStop
                {
                    rotation = CubicHermiteRot(plan.prevRot, plan.rotStart, plan.rotEnd, plan.nextRot, q, TAN_SCALE);
                }

                if (plan.type == SegmentType.Linear)
                {
                    fov = plan.fovStart + (plan.fovEnd - plan.fovStart) * q;
                }
                else if (plan.type == SegmentType.SmoothStop)
                {
                    float easeQ = Smoother01(q);
                    fov = plan.fovStart + (plan.fovEnd - plan.fovStart) * easeQ;
                }
                else // SmoothNoStop
                {
                    float fp0 = (plan.segIndex > 0) ? _fovs[plan.segIndex - 1] : plan.fovStart;
                    float fp3 = (plan.segIndex + 2 < _fovs.Count) ? _fovs[plan.segIndex + 2] : plan.fovEnd;
                    fov = CubicHermiteScalar(fp0, plan.fovStart, plan.fovEnd, fp3, q, TAN_SCALE);
                    fov = Math.Max(Math.Min(fov, Math.Max(plan.fovStart, plan.fovEnd)), Math.Min(plan.fovStart, plan.fovEnd));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Update error - continuing playback");
                position = _positions[_positions.Count - 1];
                rotation = _rotations[_rotations.Count - 1];
                fov = _fovs[_fovs.Count - 1];
            }
        }

        private enum SegmentType
        {
            Linear,
            SmoothStop,
            SmoothNoStop
        }

        private class SegmentPlan
        {
            public SegmentType type;
            public int segIndex;
            public float startDist;
            public float endDist;
            public float totalLen;
            public Vector3 dir;
            public Vector3 startPos;
            public int nodeIndex;
            public Vector3 rotStart, rotEnd;
            public Vector3 prevRot, nextRot;
            public int fovStart, fovEnd;
            public bool isEndOfSegment;
            public float[] timeTable;
            public float timeTotal;

            public void BuildTimeTableProportional(int segDur, float timeFraction)
            {
                if (segDur <= 0 || totalLen <= 0f)
                {
                    timeTable = new float[SPEED_SAMPLES + 1];
                    timeTotal = 0f;
                    return;
                }

                float planDur = segDur * timeFraction;
                timeTable = new float[SPEED_SAMPLES + 1];
                float tacc = 0f;
                float prevS = 0f;
                timeTable[0] = 0f;

                for (int k = 1; k <= SPEED_SAMPLES; k++)
                {
                    float s = totalLen * k / SPEED_SAMPLES;
                    float ds = s - prevS;
                    float midS = (s + prevS) * 0.5f;
                    float q = midS / totalLen;
                    float vRel = GetRelativeSpeed(q);
                    if (vRel < 0.001f) vRel = 0.001f;
                    tacc += ds / vRel;
                    timeTable[k] = tacc;
                    prevS = s;
                }

                if (tacc > 0f)
                {
                    float scale = planDur / tacc;
                    for (int k = 0; k <= SPEED_SAMPLES; k++)
                        timeTable[k] *= scale;
                    timeTotal = planDur;
                }
                else
                {
                    timeTotal = planDur;
                }
            }

            private float GetRelativeSpeed(float q)
            {
                if (type == SegmentType.Linear || type == SegmentType.SmoothNoStop)
                    return 1f;
                else // SmoothStop
                {
                    const float vMin = 0.3f;
                    if (isEndOfSegment)
                        return vMin + (1f - vMin) * (1f - Smoother01(q));
                    else
                        return vMin + (1f - vMin) * Smoother01(q);
                }
            }

            public double InvertTime(double frac)
            {
                if (frac <= 0.0) return 0.0;
                if (frac >= 1.0) return totalLen;
                if (timeTable == null || timeTable.Length == 0 || timeTotal <= 0f || totalLen <= 0f)
                    return frac * totalLen;

                double t = frac * timeTotal;
                for (int k = 1; k < timeTable.Length; k++)
                {
                    if (t <= timeTable[k])
                    {
                        float span = timeTable[k] - timeTable[k - 1];
                        float f = (span > 0.0000001f) ? (float)((t - timeTable[k - 1]) / span) : 0f;
                        return (totalLen * (k - 1 + f)) / (timeTable.Length - 1);
                    }
                }
                return totalLen;
            }
        }

        private static float Smoother01(float x)
        {
            x = Math.Min(Math.Max(x, 0f), 1f);
            return x * x * x * (x * (x * 6f - 15f) + 10f);
        }

        private static float Smootherstep(float x)
        {
            x = Math.Min(Math.Max(x, 0f), 1f);
            return x * x * (3f - 2f * x);
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

        private static Vector3 CubicHermiteRot(Vector3 r0, Vector3 r1, Vector3 r2, Vector3 r3, float t, float tanScale)
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

        private static float CubicHermiteScalar(float a, float b, float c, float d, float t, float tanScale)
        {
            float m1 = (c - a) * (0.5f * tanScale);
            float m2 = (d - b) * (0.5f * tanScale);
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * b + h10 * m1 + h01 * c + h11 * m2;
        }

        public void Clear()
        {
            _positions.Clear();
            _rotations.Clear();
            _durations.Clear();
            _nodeModes.Clear();
            _fovs.Clear();
            if (_segmentPlans != null) _segmentPlans.Clear();
            if (_rotationsU != null) _rotationsU.Clear();
            _isPlaying = false;
            _totalDurationMs = 0;
            PlaybackProgress = 0f;
        }
    }
}
