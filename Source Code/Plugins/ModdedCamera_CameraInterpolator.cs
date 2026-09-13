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
        private long _playbackElapsedMs = 0;
        private int _totalDurationMs = 0;

        // ===== Режим 1 («плавно без остановки»): срез углов дугой =====
        // Для каждого внутреннего узла с mode==1 предвычисляется квадратичная
        // дуга Безье (A = узел-d*inDir, control = узел, B = узел+d*outDir).
        // Касательные строго вдоль ног — overshoot невозможен по построению.
        // Радиус адаптивный: R = v^2 / CUT_LAT_ACCEL (ограничение бокового
        // ускорения), обрезка d = R*tan(θ/2), но не больше 40% короткой ноги.
        // Скорость непрерывна через стыки (профиль + таблица время-дистанция),
        // длительность каждого сегмента соблюдается точно. Дуги делятся
        // пополам между соседними сегментами по длине дуги.
        private List<CornerCut> _cornerCuts;
        private List<SegPlan> _segPlans;
        private bool _cutsValid = false;

        // Развёртка углов в непрерывную цепочку (без скачков ±360): нужна,
        // чтобы касательные Эрмита слева и справа от стыка совпадали ТОЧНО
        // и угловая скорость не рвалась на нодах.
        private List<Vector3> _rotationsU;

        private const float CUT_LAT_ACCEL = 5f;
        private const float CUT_MIN_D = 0.05f;
        private const float CUT_MIN_ANGLE_DEG = 4f;
        private const float CUT_MAX_LEG_FRAC = 0.45f;
        // Гарантированный минимум среза от геометрии: на медленных пролётках
        // скоростной радиус даёт сантиметры и срез невидим — держим минимум
        // 35% короткой ноги, чтобы скругление было видно всегда.
        private const float CUT_MIN_FRAC = 0.35f;
        // Сэмплов таблицы "время-дистанция" на сегмент. Линейная инверсия
        // между узлами даёт ступенчатую скорость с шагом в узел таблицы,
        // поэтому сетка плотная: ступеньки ниже порога заметности.
        private const int SPEED_SAMPLES = 96;
        // Сэмплов таблицы длины дуги (та же причина — пульсация скорости).
        private const int ARC_SAMPLES = 32;

        private struct CornerCut
        {
            public bool active;
            public Vector3 A;
            public Vector3 P;
            public Vector3 B;
            public float d;
            public float arcLen;
            public float[] arcTable;
        }

        private struct SegPlan
        {
            public Vector3 dir;
            public Vector3 s0;
            public float straightLen;
            public float arcPrevHalf;
            public float arcNextHalf;
            public float totalLen;
            // Скорость непрерывна через стыки: профиль wL→vEff→wR, где wL/wR —
            // полусуммы со скоростями соседей. Таблица отображает долю времени
            // в дистанцию, нормирована точно на длительность сегмента.
            public float vEff;
            public float wL;
            public float wR;
            public float[] timeTable;
            public float timeTotal;
        }

        public bool IsPlaying { get { return _isPlaying; } }
        public float PlaybackProgress { get; private set; }

        // Accumulated playback time in ms. Advanced by the caller each active
        // frame (clamped frame delta). Playback pauses automatically whenever
        // no Update is fed (e.g. dropped ticks / IsActive flicker) instead of
        // jumping forward by wall-clock time.
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
                _durations = new List<int>(durations.Count);
                for (int i = 0; i < durations.Count; i++)
                    _durations.Add(Math.Max(0, durations[i]));

                _segmentModes = new List<int>();
                int modeCount = (segmentModes != null) ? segmentModes.Count : 0;
                for (int i = 0; i < _positions.Count; i++)
                    _segmentModes.Add((i < modeCount) ? segmentModes[i] : 2);

                _fovs = new List<int>();
                for (int i = 0; i < _positions.Count; i++)
                    _fovs.Add(50);

                _totalDurationMs = 0;
                for (int i = 0; i < _durations.Count; i++)
                    _totalDurationMs += _durations[i];

                _rotationsU = new List<Vector3>(_positions.Count);
                for (int i = 0; i < _positions.Count; i++)
                {
                    if (i == 0) _rotationsU.Add(_rotations[0]);
                    else _rotationsU.Add(UnwrapRotation(_rotationsU[i - 1], _rotations[i]));
                }

                BuildCornerData();

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

        private void BuildCornerData()
        {
            _cutsValid = false;
            _cornerCuts = new List<CornerCut>();
            _segPlans = new List<SegPlan>();
            try
            {
                int n = _positions.Count;
                for (int i = 0; i < n; i++)
                {
                    CornerCut empty = new CornerCut();
                    empty.active = false;
                    _cornerCuts.Add(empty);
                }
                int activeCuts = 0;
                for (int j = 1; j <= n - 2; j++)
                {
                    int mode = (j < _segmentModes.Count) ? _segmentModes[j] : 2;
                    if (mode != 1) continue;
                    _cornerCuts[j] = BuildCut(j);
                    if (_cornerCuts[j].active) activeCuts++;
                }
                Logger.Info("BuildCornerData: " + activeCuts + " active corner cut(s)");
                for (int i = 0; i <= n - 2; i++)
                    _segPlans.Add(BuildSegPlan(i));
                // Второй проход: таблицы скорости (нужны vEff соседей).
                for (int i = 0; i <= n - 2; i++)
                    BuildSpeedTable(i);
                _cutsValid = true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BuildCornerData error - corner cutting disabled");
                _cutsValid = false;
            }
        }

        private float SegmentSpeed(int seg, float legLen)
        {
            if (seg < 0 || seg >= _durations.Count) return 0f;
            int dur = _durations[seg];
            if (dur <= 0 || legLen <= 0f) return 0f;
            return legLen / (dur / 1000f);
        }

        private CornerCut BuildCut(int j)
        {
            CornerCut c = new CornerCut();
            c.active = false;
            try
            {
                Vector3 prev = _positions[j - 1];
                Vector3 node = _positions[j];
                Vector3 next = _positions[j + 1];
                Vector3 inLeg = node - prev;
                Vector3 outLeg = next - node;
                float L1 = inLeg.Length();
                float L2 = outLeg.Length();
                if (L1 < 0.001f || L2 < 0.001f) return c;
                Vector3 inDir = inLeg * (1f / L1);
                Vector3 outDir = outLeg * (1f / L2);

                float cosT = inDir.X * outDir.X + inDir.Y * outDir.Y + inDir.Z * outDir.Z;
                if (cosT > 1f) cosT = 1f;
                if (cosT < -1f) cosT = -1f;
                float angDeg = (float)Math.Acos(cosT) * 57.29578f;
                if (angDeg < CUT_MIN_ANGLE_DEG) return c;

                float v1 = SegmentSpeed(j - 1, L1);
                float v2 = SegmentSpeed(j, L2);
                if (v1 <= 0f && v2 <= 0f) return c;
                if (v1 <= 0f) v1 = v2;
                if (v2 <= 0f) v2 = v1;
                float vAvg = (v1 + v2) * 0.5f;

                float minLeg = (L1 < L2) ? L1 : L2;
                float dMax = minLeg * CUT_MAX_LEG_FRAC;
                if (dMax < CUT_MIN_D) return c;

                // tan(θ/2) = sinθ / (1+cosθ); при развороте (cosθ→-1) — сразу clamp.
                // Скоростной радиус + геометрический минимум (см. CUT_MIN_FRAC).
                float d;
                float denom = cosT + 1f;
                if (denom < 0.000001f)
                {
                    d = dMax;
                }
                else
                {
                    float sinT = (float)Math.Sqrt(Math.Max(0f, 1f - cosT * cosT));
                    float R = vAvg * vAvg / CUT_LAT_ACCEL;
                    d = R * (sinT / denom);
                    float dGeom = minLeg * CUT_MIN_FRAC;
                    if (d < dGeom) d = dGeom;
                    if (d > dMax) d = dMax;
                }
                if (d < CUT_MIN_D) return c;

                c.A = node - inDir * d;
                c.P = node;
                c.B = node + outDir * d;
                c.d = d;
                c.arcTable = new float[ARC_SAMPLES + 1];
                c.arcLen = BuildArcTable(c, c.arcTable);
                if (c.arcLen < 0.001f) return c;
                c.active = true;
            }
            catch (Exception ex)
            {
                Logger.Debug("BuildCut warning: " + ex.Message);
            }
            return c;
        }

        private float BuildArcTable(CornerCut c, float[] table)
        {
            Vector3 prev = c.A;
            table[0] = 0f;
            float acc = 0f;
            for (int k = 1; k <= ARC_SAMPLES; k++)
            {
                float u = (float)k / ARC_SAMPLES;
                Vector3 p = BezPoint(c.A, c.P, c.B, u);
                acc += (p - prev).Length();
                table[k] = acc;
                prev = p;
            }
            return acc;
        }

        private static Vector3 BezPoint(Vector3 a, Vector3 p, Vector3 b, float u)
        {
            float iu = 1f - u;
            return a * (iu * iu) + p * (2f * iu * u) + b * (u * u);
        }

        private float InvertArc(CornerCut c, float dist)
        {
            float[] t = c.arcTable;
            if (t == null || t.Length == 0) return 0f;
            if (dist <= 0f) return 0f;
            if (dist >= c.arcLen) return 1f;
            for (int k = 1; k < t.Length; k++)
            {
                if (dist <= t[k])
                {
                    float span = t[k] - t[k - 1];
                    float f = (span > 0.000001f) ? (dist - t[k - 1]) / span : 0f;
                    return ((k - 1) + f) / (t.Length - 1);
                }
            }
            return 1f;
        }

        private SegPlan BuildSegPlan(int i)
        {
            SegPlan sp = new SegPlan();
            Vector3 p1 = _positions[i];
            Vector3 p2 = _positions[i + 1];
            Vector3 leg = p2 - p1;
            float Li = leg.Length();
            sp.dir = (Li > 0.000001f) ? leg * (1f / Li) : new Vector3(0f, 1f, 0f);
            sp.s0 = p1;
            sp.straightLen = Li;
            sp.arcPrevHalf = 0f;
            sp.arcNextHalf = 0f;
            sp.totalLen = Li;
            try
            {
                CornerCut prev = _cornerCuts[i];
                CornerCut next = _cornerCuts[i + 1];
                float sStart = prev.active ? prev.d : 0f;
                float sEnd = next.active ? next.d : 0f;
                // Перекрытие невозможно по построению (каждый срез ≤40% общей
                // ноги, в сумме ≤80% сегмента), страховка ниже — только от NaN.
                sp.s0 = p1 + sp.dir * sStart;
                sp.straightLen = Li - sStart - sEnd;
                if (sp.straightLen < 0f) sp.straightLen = 0f;
                sp.arcPrevHalf = prev.active ? prev.arcLen * 0.5f : 0f;
                sp.arcNextHalf = next.active ? next.arcLen * 0.5f : 0f;
                sp.totalLen = sp.arcPrevHalf + sp.straightLen + sp.arcNextHalf;
                int dur = (i < _durations.Count) ? _durations[i] : 0;
                sp.vEff = (dur > 0 && sp.totalLen > 0f) ? sp.totalLen / (dur / 1000f) : 0f;
                sp.wL = sp.vEff;
                sp.wR = sp.vEff;
                sp.timeTable = null;
                sp.timeTotal = 0f;
            }
            catch (Exception ex)
            {
                Logger.Debug("BuildSegPlan warning: " + ex.Message);
            }
            return sp;
        }

        private void BuildSpeedTable(int i)
        {
            try
            {
                SegPlan sp = _segPlans[i];
                int nseg = _segPlans.Count;
                float v0 = sp.vEff;
                if (v0 <= 0f || sp.totalLen <= 0f) return;
                float wL = (i > 0) ? (_segPlans[i - 1].vEff + v0) * 0.5f : v0;
                float wR = (i < nseg - 1) ? (v0 + _segPlans[i + 1].vEff) * 0.5f : v0;
                if (wL <= 0f) wL = v0;
                if (wR <= 0f) wR = v0;
                sp.wL = wL;
                sp.wR = wR;
                float[] tt = new float[SPEED_SAMPLES + 1];
                float tacc = 0f;
                float prevS = 0f;
                tt[0] = 0f;
                for (int k = 1; k <= SPEED_SAMPLES; k++)
                {
                    float s = sp.totalLen * k / SPEED_SAMPLES;
                    // Скорость в середине подотрезка (трапеции точнее).
                    float v = SpeedAt(sp, wL, wR, (s + prevS) * 0.5f);
                    if (v <= 0.0001f) v = v0;
                    tacc += (s - prevS) / v;
                    tt[k] = tacc;
                    prevS = s;
                }
                sp.timeTable = tt;
                sp.timeTotal = tacc;
                _segPlans[i] = sp;
            }
            catch (Exception ex)
            {
                Logger.Debug("BuildSpeedTable warning: " + ex.Message);
            }
        }

        // Профиль скорости по дистанции: плавный переход wL→vEff→wR.
        // На стыке сегментов скорости совпадают по построению
        // (wR[i] == wL[i+1]), поэтому рывков скорости нет.
        private float SpeedAt(SegPlan sp, float wL, float wR, float s)
        {
            float v0 = sp.vEff;
            if (sp.totalLen <= 0f) return v0;
            float q = s / sp.totalLen;
            if (q <= 0f) return wL;
            if (q >= 1f) return wR;
            if (q < 0.25f) return wL + (v0 - wL) * (q / 0.25f);
            if (q < 0.75f) return v0;
            return v0 + (wR - v0) * ((q - 0.75f) / 0.25f);
        }

        // Доля времени [0..1] -> дистанция по таблице. Таблица нормирована
        // точно на длительность сегмента, поэтому тайминг не плывёт.
        private double InvertTime(SegPlan sp, double frac)
        {
            if (frac <= 0.0) return 0.0;
            if (frac >= 1.0) return sp.totalLen;
            float[] tt = sp.timeTable;
            if (tt == null || tt.Length == 0 || sp.timeTotal <= 0f || sp.totalLen <= 0f)
                return frac * sp.totalLen;
            double t = frac * sp.timeTotal;
            for (int k = 1; k < tt.Length; k++)
            {
                if (t <= tt[k])
                {
                    float span = tt[k] - tt[k - 1];
                    float f = (span > 0.0000001f) ? (float)((t - tt[k - 1]) / span) : 0f;
                    return (sp.totalLen * (k - 1 + f)) / (tt.Length - 1);
                }
            }
            return sp.totalLen;
        }

        private void EvaluateCutSegment(int seg, double segElapsedMs, int durMs, out Vector3 position, out Vector3 rotation, out float fov)
        {
            position = _positions[seg + 1];
            rotation = _rotations[seg + 1];
            fov = (_fovs != null && seg + 1 < _fovs.Count) ? _fovs[seg + 1] : 50f;
            try
            {
                SegPlan sp = _segPlans[seg];
                if (durMs <= 0 || sp.totalLen <= 0f) return;

                // Дистанция через таблицу скорости: скорость непрерывна через
                // стыки, а суммарное время сегмента соблюдается точно.
                double frac = segElapsedMs / (double)durMs;
                double dist = InvertTime(sp, frac);

                // Доворот и FOV — кубический Эрмит по ДОЛЕ ДИСТАНЦИИ q.
                // Касательные берутся из глобально развёрнутой цепочки углов,
                // поэтому слева и справа от стыка они совпадают точно: угловая
                // скорость непрерывна (C1) через ноду. Линейный lerp давал здесь
                // ступеньку угловой скорости — взгляд дёргался на каждой ноде.
                Vector3 hr0;
                Vector3 hr1;
                Vector3 hr2;
                Vector3 hr3;
                if (_rotationsU != null && _rotationsU.Count == _positions.Count)
                {
                    hr0 = (seg > 0) ? _rotationsU[seg - 1] : _rotationsU[seg];
                    hr1 = _rotationsU[seg];
                    hr2 = _rotationsU[seg + 1];
                    hr3 = (seg + 2 < _rotationsU.Count) ? _rotationsU[seg + 2] : _rotationsU[seg + 1];
                }
                else
                {
                    hr0 = (seg > 0) ? _rotations[seg - 1] : _rotations[seg];
                    hr1 = _rotations[seg];
                    hr2 = UnwrapRotation(hr1, _rotations[seg + 1]);
                    hr3 = (seg + 2 < _rotations.Count) ? UnwrapRotation(hr2, _rotations[seg + 2]) : hr2;
                }

                float f0 = (_fovs != null && seg < _fovs.Count) ? _fovs[seg] : 50f;
                float f1 = (_fovs != null && seg + 1 < _fovs.Count) ? _fovs[seg + 1] : 50f;
                float fp0 = (seg > 0 && _fovs != null && seg - 1 < _fovs.Count) ? _fovs[seg - 1] : f0;
                float fp3 = (_fovs != null && seg + 2 < _fovs.Count) ? _fovs[seg + 2] : f1;

                // Доля дистанции сегмента: углы и FOV идут по ней Эрмитом —
                // равномерно во времени (скорость постоянна) и гладко через узлы.
                float q = (sp.totalLen > 0f) ? (float)(dist / sp.totalLen) : 0f;
                if (q < 0f) q = 0f;
                if (q > 1f) q = 1f;
                rotation = CubicHermiteRot(hr0, hr1, hr2, hr3, q, _tanScale);
                fov = ClampFov(CubicHermiteScalar(fp0, f0, f1, fp3, q, _tanScale), f0, f1);

                if (dist < sp.arcPrevHalf && sp.arcPrevHalf > 0f)
                {
                    // Вторая половина входной дуги.
                    CornerCut cut = _cornerCuts[seg];
                    float half = sp.arcPrevHalf;
                    float u = InvertArc(cut, half + (float)dist);
                    position = BezPoint(cut.A, cut.P, cut.B, u);
                }
                else if (dist < sp.arcPrevHalf + sp.straightLen || sp.arcNextHalf <= 0f)
                {
                    float dd = (float)(dist - sp.arcPrevHalf);
                    if (dd < 0f) dd = 0f;
                    if (dd > sp.straightLen) dd = sp.straightLen;
                    position = sp.s0 + sp.dir * dd;
                }
                else
                {
                    // Первая половина выходной дуги.
                    CornerCut cut = _cornerCuts[seg + 1];
                    float half = sp.arcNextHalf;
                    float d3 = (float)(dist - sp.arcPrevHalf - sp.straightLen);
                    if (d3 < 0f) d3 = 0f;
                    if (d3 > half) d3 = half;
                    float u = InvertArc(cut, d3);
                    position = BezPoint(cut.A, cut.P, cut.B, u);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "EvaluateCutSegment error");
            }
        }

        private Vector3 LerpAngleVec(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(LerpAngle(a.X, b.X, t), LerpAngle(a.Y, b.Y, t), LerpAngle(a.Z, b.Z, t));
        }

        private static float ClampFov(float v, float a, float b)
        {
            float lo = (a < b) ? a : b;
            float hi = (a < b) ? b : a;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
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

                int modeNodeA = (currentSegment < _segmentModes.Count) ? _segmentModes[currentSegment] : 2;
                int modeNodeB = (currentSegment + 1 < _segmentModes.Count) ? _segmentModes[currentSegment + 1] : modeNodeA;

                // Режим 1 («плавно без остановки»): срез углов дугой Безье с
                // адаптивным радиусом. Прямые ноги + касательная дуга, скорость
                // постоянна внутри сегмента (длина/длительность). Ни остановки
                // в узле, ни «желейного» морфинга со сплайном.
                bool useCut = (modeNodeA == 1 || modeNodeB == 1)
                    && _cutsValid && _segPlans != null && currentSegment < _segPlans.Count;
                if (useCut)
                {
                    EvaluateCutSegment(currentSegment, segmentElapsedMs, segmentDurationMs, out position, out rotation, out fov);
                }
                else
                {
                    float fStart = Ease(modeNodeA, t);
                    float fEnd = Ease(modeNodeB, t);
                    float blend = t * t * (3f - 2f * t);
                    float f = fStart + (fEnd - fStart) * blend;

                    Vector3 p0 = (currentSegment > 0) ? _positions[currentSegment - 1] : _positions[currentSegment];
                    Vector3 p1 = _positions[currentSegment];
                    Vector3 p2 = _positions[currentSegment + 1];
                    Vector3 p3 = (currentSegment + 2 < _positions.Count) ? _positions[currentSegment + 2] : _positions[currentSegment + 1];

                    Vector3 straightPos = Vector3.Lerp(p1, p2, f);
                    Vector3 straightRot = InterpolateRotationShortest(currentSegment, f);

                    // Сюда попадаем только без mode 1 на концах, поэтому s = 0:
                    // чистая прямая (0) или трапеция с остановками (2).
                    float s = 0f;

                    Vector3 splinePos = CubicHermite(p0, p1, p2, p3, f, _tanScale);
                    Vector3 r0 = (currentSegment > 0) ? _rotations[currentSegment - 1] : _rotations[currentSegment];
                    Vector3 r1 = _rotations[currentSegment];
                    Vector3 r2 = UnwrapRotation(r1, _rotations[currentSegment + 1]);
                    Vector3 r3 = (currentSegment + 2 < _rotations.Count) ? UnwrapRotation(r2, _rotations[currentSegment + 2]) : r2;
                    Vector3 splineRot = CubicHermiteRot(r0, r1, r2, r3, f, _tanScale);

                    position = Vector3.Lerp(straightPos, splinePos, s);
                    rotation = LerpRotation(straightRot, splineRot, s);

                    if (_fovs != null && currentSegment + 1 < _fovs.Count)
                    {
                        int fc = _fovs.Count;
                        float fp0 = _fovs[Math.Max(0, currentSegment - 1)];
                        float fp1 = _fovs[currentSegment];
                        float fp2 = _fovs[currentSegment + 1];
                        float fp3 = _fovs[Math.Min(fc - 1, currentSegment + 2)];
                        float straightFov = fp1 + (fp2 - fp1) * f;
                        float splineFov = CubicHermiteScalar(fp0, fp1, fp2, fp3, f, _tanScale);
                        fov = straightFov + (splineFov - straightFov) * s;
                    }
                }
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

        private static float Smooth01(float x)
        {
            x = Math.Min(Math.Max(x, 0f), 1f);
            return x * x * (3f - 2f * x);
        }

        // C2-гладкое окно (функция 5-го порядка): первая И вторая производные
        // равны нулю на краях, поэтому кривизна нарастает плавно — нет рывка
        // при входе/выходе из скругления узла.
        private static float Smoother01(float x)
        {
            x = Math.Min(Math.Max(x, 0f), 1f);
            return x * x * x * (x * (x * 6f - 15f) + 10f);
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

        private Vector3 LerpRotation(Vector3 a, Vector3 b, float s)
        {
            return new Vector3(
                LerpAngle(a.X, b.X, s),
                LerpAngle(a.Y, b.Y, s),
                LerpAngle(a.Z, b.Z, s));
        }

        private const float _easeFrac = 0.2f;

        private float Ease(int mode, float t)
        {
            if (mode == 0 || mode == 1) return t;
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
            if (_cornerCuts != null) _cornerCuts.Clear();
            if (_segPlans != null) _segPlans.Clear();
            if (_rotationsU != null) _rotationsU.Clear();
            _cutsValid = false;
            _isPlaying = false;
            _totalDurationMs = 0;
            PlaybackProgress = 0f;
        }
    }

}
