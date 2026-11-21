using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;

using ModPack.Core;

namespace ModPack.Prefabs
{
public sealed class PrefabsPlugin : IGtaPlugin
{
    private const int BallHash = unchecked((int)0xC00C3530);
    private const int Columns = 10;
    private const int Rows = 5;
    private const string RampModelName = "prop_skate_halfpipe";
    private const int RampSamples = 512;
    private const int MaxRampPieces = 64;
    private const int RampRunIntervals = 45;
    private const float RampSpacingFactor = 0.68f;
    private const float RampSlopeDegrees = 35f;
    private const float RampSeamDrop = 0.25f;
    private const float MaxRampYawDegrees = 10f;
    private readonly ObjectPool _pool = new ObjectPool();
    private readonly List<int> _pendingRelease = new List<int>();
    private readonly List<int> _rampPieces = new List<int>();
    private bool[] _rampTouched;
    private int[] _rampDeleteAt;
    private Vector3[] _rampPiecePositions;
    private int _rampUniqueTouches;
    private int _rampFinalCleanupAt;
    private int _rampContactCheckedAt;
    private readonly Random _random = new Random();
    private NativeMenu _menu;
    private bool _preview;
    private bool _hasPlacement;
    private bool _placeKeyWasDown;
    private bool _enterWasDown;
    private int _previewStartedAt;
    private Vector3 _placement;
    private Vector3 _right;
    private float _radius;
    private float _spacing;
    private int _releaseIndex;
    private int _releaseStartTime;
    private bool _rampSelecting;
    private int _rampSelectionStartedAt;
    private bool _rampHasTarget;
    private Vector3 _rampStart;
    private Vector3 _rampEnd;
    private Vector3 _rampDirection;
    private float _rampEndOffsetFactor;
    private float[] _rampLateralWaves;
    private float[] _rampHeightWaves;
    private Vector3[] _rampPreviewPoints;
    private Vector3[] _rampPreviewTangents;
    private float[] _rampPreviewBanks;
    private bool _rampAlongX;
    private Vector3 _rampPreviewStart;
    private int _rampPreviewBuiltAt;

    public void OnStart()
    {
        _placeKeyWasDown = Game.IsKeyPressed(Keys.Oem1);
        _enterWasDown = Game.IsKeyPressed(Keys.Return);
        _menu = new NativeMenu(ModPack.Core.ModuleMenus.Prefabs.Title, "Object Spooner");
        var wall = new NativeItem("Предпросмотр стенки", "10 в ширину, 5 в высоту. Enter или ; — разместить");
        wall.Activated += (s, e) =>
        {
            if (!IsSpoonerActive()) return;
            Function.Call(Hash.REQUEST_MODEL, BallHash);
            _preview = true;
            _previewStartedAt = Game.GameTime;
            _menu.Visible = false;
            GTA.UI.Screen.ShowSubtitle("~b~Наведи стенку. ~w~Enter или ; — разместить, Backspace — отмена.", 6000);
        };
        _menu.Add(wall);
        var rampStart = new NativeItem("Поставить горку", "Наведи на верхнюю точку; синяя точка — начало, красная — конец.");
        rampStart.Activated += (s, e) => BeginRampSelection();
        _menu.Add(rampStart);
        _pool.Add(_menu);
    }

    public void OnTick()
    {
        try
        {
            ReleaseNextBall();
            UpdateRampCleanup();
            _pool.Process();
            if (_menu != null && _menu.Visible && !IsSpoonerActive()) _menu.Visible = false;
            if (_rampSelecting)
            {
                UpdateRampSelection();
                return;
            }
            if (!_preview) return;
            if (!IsSpoonerActive())
            {
                CancelPreview();
                return;
            }

            UpdatePlacement();
            bool placeKeyDown = Game.IsKeyPressed(Keys.Oem1);
            bool enterDown = Game.IsKeyPressed(Keys.Return);
            bool confirm = (placeKeyDown && !_placeKeyWasDown) || (enterDown && !_enterWasDown);
            _placeKeyWasDown = placeKeyDown;
            _enterWasDown = enterDown;
            if (confirm && (uint)(Game.GameTime - _previewStartedAt) >= 250u)
            {
                if (_hasPlacement) SpawnWall();
                else GTA.UI.Screen.ShowSubtitle("~y~Наведи прицел на поверхность и дождись загрузки мяча.", 2500);
            }
            if (!_preview) return;
            if (!_hasPlacement) return;
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
                World.DrawMarker(MarkerType.Sphere, BallPosition(column, row), Vector3.Zero, Vector3.Zero,
                    new Vector3(_radius * 2f, _radius * 2f, _radius * 2f),
                    Color.FromArgb(75, 60, 220, 255));
        }
        catch (Exception ex)
        {
            PluginLog.Error("SpoonerPrefabs: Tick", ex);
            CancelPreview();
        }
    }

    private static bool IsSpoonerActive()
    {
        return !Game.IsPaused && ControlSession.Owner == null && ScriptCameraDirector.RenderingCam != null;
    }

    private void UpdatePlacement()
    {
        _hasPlacement = false;
        GTA.Camera camera = ScriptCameraDirector.RenderingCam;
        Vector3 origin = camera.Position;
        Vector3 forward = camera.Direction;
        RaycastResult ray = World.Raycast(origin, origin + forward * 250f,
            IntersectFlags.Map | IntersectFlags.Objects);
        if (!ray.DidHit) return;

        _placement = ray.HitPosition;
        Vector3 flatForward = new Vector3(forward.X, forward.Y, 0f);
        if (flatForward.Length() < 0.01f) flatForward = new Vector3(0f, 1f, 0f);
        flatForward.Normalize();
        _right = new Vector3(flatForward.Y, -flatForward.X, 0f);

        if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, BallHash)) return;
        Model model = new Model(BallHash);
        var dimensions = model.Dimensions;
        float diameter = Math.Max(dimensions.Item2.X - dimensions.Item1.X,
            Math.Max(dimensions.Item2.Y - dimensions.Item1.Y, dimensions.Item2.Z - dimensions.Item1.Z));
        if (diameter <= 0.05f) return;
        _radius = diameter * 0.5f;
        _spacing = diameter * 0.82f;
        _hasPlacement = true;
    }

    private Vector3 BallPosition(int column, int row)
    {
        return _placement + _right * ((column - (Columns - 1) * 0.5f) * _spacing) +
            new Vector3(0f, 0f, _radius + row * _spacing);
    }

    private Vector3 StagingPosition(int column, int row)
    {
        float safeSpacing = _radius * 2.2f;
        return _placement + _right * ((column - (Columns - 1) * 0.5f) * safeSpacing) +
            new Vector3(0f, 0f, _radius + row * safeSpacing);
    }

   public void OnKeyDown(Keys key)
   {
            if (ModPack.Core.MenuTextInput.Active) return;
        if (key == Keys.Back && _preview)
        {
            CancelPreview();
            return;
        }
        if (key == Keys.Back && _rampSelecting)
        {
            _rampSelecting = false;
            _rampPreviewPoints = null;
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED,
                unchecked((int)StringHash.AtStringHash(RampModelName)));
            return;
        }
        if (_rampSelecting)
        {
            if ((key == Keys.Return || key == Keys.Oem1) &&
                (uint)(Game.GameTime - _rampSelectionStartedAt) >= 250u)
                ConfirmRampSelection();
            return;
        }
        if (key == Keys.Return && _preview)
        {
            if ((uint)(Game.GameTime - _previewStartedAt) >= 250u && _hasPlacement) SpawnWall();
            return;
        }
        if (key != Keys.Oem1) return;
        if (_preview)
        {
            if ((uint)(Game.GameTime - _previewStartedAt) >= 250u && _hasPlacement) SpawnWall();
            return;
        }
        if (!IsSpoonerActive()) return;
        _menu.Visible = !_menu.Visible;
    }

    private void SpawnWall()
    {
        if (_pendingRelease.Count > 0)
        {
            GTA.UI.Screen.ShowSubtitle("~y~Дождись окончания сборки предыдущей стенки.", 2500);
            return;
        }
        if (!Function.Call<bool>(Hash.IS_MODEL_VALID, BallHash)) return;
        Function.Call(Hash.REQUEST_MODEL, BallHash);
        if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, BallHash))
        {
            GTA.UI.Screen.ShowSubtitle("~y~Модель загружается, нажми ; ещё раз.", 2500);
            return;
        }

        List<int> created = new List<int>();
        try
        {
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
            {
                Vector3 pos = StagingPosition(column, row);
                int handle = Function.Call<int>(Hash.CREATE_OBJECT_NO_OFFSET, BallHash, pos.X, pos.Y, pos.Z, false, false, false);
                if (handle == 0) throw new InvalidOperationException("Не удалось создать мяч " + created.Count);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, true);
                created.Add(handle);
            }
            int index = 0;
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
            {
                int handle = created[index++];
                Vector3 pos = BallPosition(column, row);
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, handle, pos.X, pos.Y, pos.Z, false, false, true);
                Function.Call(Hash.SET_ENTITY_DYNAMIC, handle, true);
            }
            _pendingRelease.AddRange(created);
            _releaseIndex = 0;
            _releaseStartTime = Game.GameTime + 150;
            _preview = false;
            GTA.UI.Screen.ShowSubtitle("~g~Стенка собрана. Размораживаю мячи по очереди.", 2500);
        }
        catch (Exception ex)
        {
            foreach (int handle in created)
                try
                {
                    Entity entity = Entity.FromHandle(handle);
                    if (entity != null && entity.Exists()) entity.Delete();
                }
                catch { }
            PluginLog.Error("SpoonerPrefabs: SpawnWall", ex);
            GTA.UI.Screen.ShowSubtitle("~r~Не удалось создать стенку.", 2500);
        }
        finally
        {
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, BallHash);
        }
    }

    private void BeginRampSelection()
    {
        if (!IsSpoonerActive()) return;
        GTA.Camera camera = ScriptCameraDirector.RenderingCam;
        Vector3 forward = new Vector3(camera.Direction.X, camera.Direction.Y, 0f);
        if (forward.Length() < 0.01f) forward = new Vector3(0f, 1f, 0f);
        forward.Normalize();
        _rampDirection = forward;
        _rampEndOffsetFactor = ((float)_random.NextDouble() * 2f - 1f) * 0.08f;
        _rampLateralWaves = null;
        _rampHeightWaves = null;
        _rampPreviewPoints = null;
        _rampPreviewBuiltAt = 0;
        Function.Call(Hash.REQUEST_MODEL, unchecked((int)StringHash.AtStringHash(RampModelName)));
        _rampSelecting = true;
        _rampSelectionStartedAt = Game.GameTime;
        _rampHasTarget = false;
        _placeKeyWasDown = Game.IsKeyPressed(Keys.Oem1);
        _enterWasDown = Game.IsKeyPressed(Keys.Return);
        _menu.Visible = false;
        GTA.UI.Screen.ShowSubtitle("~b~Наведи на верх горки. ~w~Enter или ; — разместить, Backspace — отмена.", 5000);
    }

    private void UpdateRampSelection()
    {
        if (!IsSpoonerActive())
        {
            _rampSelecting = false;
            _rampPreviewPoints = null;
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED,
                unchecked((int)StringHash.AtStringHash(RampModelName)));
            return;
        }
        GTA.Camera camera = ScriptCameraDirector.RenderingCam;
        RaycastResult ray = World.Raycast(camera.Position, camera.Position + camera.Direction * 250f,
            IntersectFlags.Map | IntersectFlags.Objects);
        _rampHasTarget = ray.DidHit;
        if (_rampHasTarget)
        {
            _rampStart = ray.HitPosition;
            Vector3 forward = new Vector3(camera.Direction.X, camera.Direction.Y, 0f);
            if (forward.Length() < 0.01f) forward = _rampDirection;
            forward.Normalize();
            float directionDot = forward.X * _rampDirection.X + forward.Y * _rampDirection.Y;
            if (_rampPreviewPoints == null ||
                ((_rampStart - _rampPreviewStart).Length() > 0.15f ||
                 directionDot < 0.99985f) &&
                (uint)(Game.GameTime - _rampPreviewBuiltAt) >= 150u)
            {
                _rampDirection = forward;
                _rampPreviewPoints = BuildRampPreview();
                _rampPreviewStart = _rampStart;
                _rampPreviewBuiltAt = Game.GameTime;
            }
            World.DrawMarker(MarkerType.Sphere,
                _rampPreviewPoints != null ? _rampPreviewPoints[0] : _rampStart,
                Vector3.Zero, Vector3.Zero,
                new Vector3(0.7f, 0.7f, 0.7f), Color.FromArgb(180, 50, 210, 255));
            if (_rampPreviewPoints != null)
            {
                for (int i = 1; i < _rampPreviewPoints.Length; i++)
                {
                    Vector3 previous = _rampPreviewPoints[i - 1];
                    Vector3 current = _rampPreviewPoints[i];
                    Function.Call(Hash.DRAW_LINE, previous.X, previous.Y, previous.Z,
                        current.X, current.Y, current.Z, 65, 205, 255, 185);
                    if (i == _rampPreviewPoints.Length - 1) continue;
                    World.DrawMarker(MarkerType.Sphere, current, Vector3.Zero, Vector3.Zero,
                        new Vector3(0.38f, 0.38f, 0.38f), Color.FromArgb(185, 70, 210, 255));
                }
                World.DrawMarker(MarkerType.Sphere, _rampPreviewPoints[_rampPreviewPoints.Length - 1],
                    Vector3.Zero, Vector3.Zero,
                    new Vector3(1.5f, 1.5f, 1.5f), Color.FromArgb(190, 255, 75, 75));
            }
        }
        else _rampPreviewPoints = null;
        bool place = Game.IsKeyPressed(Keys.Oem1);
        bool enter = Game.IsKeyPressed(Keys.Return);
        bool confirm = (place && !_placeKeyWasDown) || (enter && !_enterWasDown);
        _placeKeyWasDown = place;
        _enterWasDown = enter;
        if (confirm && (uint)(Game.GameTime - _rampSelectionStartedAt) >= 250u)
            ConfirmRampSelection();
    }

    private void ConfirmRampSelection()
    {
        if (!_rampHasTarget || _rampPreviewPoints == null)
        {
            GTA.UI.Screen.ShowSubtitle("~y~Наведи на поверхность и дождись расчёта горки.", 2500);
            return;
        }
        _rampSelecting = false;
        SpawnRamp();
    }

    private void SpawnRamp()
    {
        if (!IsSpoonerActive()) return;
        if (_rampPreviewPoints == null) return;

        int hash = unchecked((int)StringHash.AtStringHash(RampModelName));
        if (!Function.Call<bool>(Hash.IS_MODEL_VALID, hash))
        {
            GTA.UI.Screen.ShowSubtitle("~r~Модель prop_skate_halfpipe не найдена.", 3000);
            return;
        }
        if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash)) return;

        var created = new List<int>();
        try
        {
            var dimensions = new Model(hash).Dimensions;
            float lengthX = dimensions.Item2.X - dimensions.Item1.X;
            float lengthY = dimensions.Item2.Y - dimensions.Item1.Y;
            float pieceLength = Math.Min(lengthX, lengthY);
            if (pieceLength < 0.5f) throw new InvalidOperationException("Invalid halfpipe dimensions");
            Vector3[] points = _rampPreviewPoints;
            float[] bankAngles = _rampPreviewBanks;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 tangent = _rampPreviewTangents[i];
                float flat = (float)Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
                if (flat < 0.01f) throw new InvalidOperationException("Invalid halfpipe tangent");
                Vector3 pos = points[i];
                int handle = Function.Call<int>(Hash.CREATE_OBJECT_NO_OFFSET, hash,
                    pos.X, pos.Y, pos.Z, false, false, false);
                if (handle == 0) throw new InvalidOperationException("Failed to create halfpipe piece " + i);
                created.Add(handle);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, true);
                SetRampOrientation(handle, tangent, _rampAlongX, bankAngles[i]);
            }
            Entity firstPiece = Entity.FromHandle(created[0]);
            if (firstPiece == null || !firstPiece.Exists())
                throw new InvalidOperationException("First halfpipe piece is missing");
            firstPiece.Delete();
            if (Function.Call<bool>(Hash.DOES_ENTITY_EXIST, created[0]))
                throw new InvalidOperationException("Failed to remove first halfpipe piece");
            created[0] = 0;
            foreach (int handle in _rampPieces)
            {
                if (handle == 0) continue;
                try
                {
                    Entity oldPiece = Entity.FromHandle(handle);
                    if (oldPiece != null && oldPiece.Exists()) oldPiece.Delete();
                }
                catch (Exception ex) { PluginLog.Error("SpoonerPrefabs: ReplaceRamp", ex); }
            }
            _rampPieces.Clear();
            _rampPieces.AddRange(created);
            _rampTouched = new bool[created.Count];
            _rampDeleteAt = new int[created.Count];
            _rampPiecePositions = points;
            _rampUniqueTouches = 0;
            _rampFinalCleanupAt = 0;
            _rampContactCheckedAt = 0;
            _rampPreviewPoints = null;
            GTA.UI.Screen.ShowSubtitle("~g~Горка создана и заморожена.", 3000);
        }
        catch (Exception ex)
        {
            foreach (int handle in created)
            {
                if (handle == 0) continue;
                Entity entity = Entity.FromHandle(handle);
                if (entity != null && entity.Exists()) entity.Delete();
            }
            PluginLog.Error("SpoonerPrefabs: SpawnRamp", ex);
            GTA.UI.Screen.ShowSubtitle("~r~Не удалось создать горку.", 2500);
        }
        finally { Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, hash); }
    }

    private float RandomBend(float magnitude)
    {
        return (_random.Next(2) == 0 ? -1f : 1f) *
            (0.6f + (float)_random.NextDouble() * 0.4f) * magnitude;
    }

    private Vector3[] BuildRampPreview()
    {
        int hash = unchecked((int)StringHash.AtStringHash(RampModelName));
        if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash)) return null;
        var dimensions = new Model(hash).Dimensions;
        float lengthX = dimensions.Item2.X - dimensions.Item1.X;
        float lengthY = dimensions.Item2.Y - dimensions.Item1.Y;
        float pieceLength = Math.Min(lengthX, lengthY);
        if (pieceLength < 0.5f) return null;
        _rampAlongX = lengthX <= lengthY;
        float run = RampRun(pieceLength);
        float drop = RampDrop(run);
        if (_rampLateralWaves == null)
        {
            _rampLateralWaves = new[] {
                RandomBend(run * 0.10f), RandomBend(run * 0.10f),
                RandomBend(run * 0.08f), RandomBend(run * 0.04f),
                RandomBend(run * 0.02f) };
            _rampHeightWaves = new[] { RandomBend(drop * 0.03f), RandomBend(drop * 0.01f) };
        }
        Vector3 sideways = new Vector3(_rampDirection.Y, -_rampDirection.X, 0f);
        float width = Math.Max(lengthX, lengthY);
        float lateralScale = 1f;
        _rampEnd = _rampStart + _rampDirection * run +
            sideways * (run * _rampEndOffsetFactor) - new Vector3(0f, 0f, drop);
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Vector3[] points = BuildRampPoints(sideways, _rampLateralWaves,
                _rampHeightWaves, lateralScale, pieceLength, width);
            if (points != null)
            {
                _rampPreviewTangents = GetRampTangents(points);
                _rampPreviewBanks = GetRampBankAngles(points);
                LowerRampSeams(points);
                return points;
            }
            lateralScale *= 0.85f;
        }
        return null;
    }

    private Vector3[] GetRampTangents(Vector3[] points)
    {
        var tangents = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 tangent = i == 0
                ? _rampDirection - new Vector3(0f, 0f,
                    (float)Math.Tan(RampSlopeDegrees * Math.PI / 180.0))
                : i == points.Length - 1 ? points[i] - points[i - 1]
                : points[i + 1] - points[i - 1];
            tangent.Normalize();
            tangents[i] = tangent;
        }
        return tangents;
    }

    private static void LowerRampSeams(Vector3[] points)
    {
        for (int i = 1; i < points.Length; i++)
            points[i].Z -= i * RampSeamDrop;
    }

    private Vector3[] BuildRampPoints(Vector3 sideways, float[] lateralWaves,
        float[] heightWaves, float lateralScale, float pieceLength, float width)
    {
        var samples = new Vector3[RampSamples + 1];
        var distance = new float[RampSamples + 1];
        float endOffset = RampRun(pieceLength) * _rampEndOffsetFactor;
        for (int i = 0; i <= RampSamples; i++)
        {
            float t = (float)i / RampSamples;
            float eased = t * t * (3f - 2f * t);
            samples[i] = _rampStart + (_rampEnd - _rampStart) * t +
                sideways * (endOffset * (eased - t) +
                    RampWave(t, lateralWaves) * lateralScale) +
                new Vector3(0f, 0f, RampWave(t, heightWaves));
            if (i > 0)
                distance[i] = distance[i - 1] + (samples[i] - samples[i - 1]).Length();
        }
        int firstCount = Math.Max(2,
            (int)Math.Ceiling(distance[RampSamples] / (pieceLength * 0.72f)) + 1);
        for (int count = firstCount; count <= MaxRampPieces; count++)
        {
            var points = new Vector3[count];
            points[0] = _rampStart;
            points[count - 1] = _rampEnd;
            int sample = 1;
            for (int i = 1; i < count - 1; i++)
            {
                float target = distance[RampSamples] * i / (count - 1);
                while (sample < RampSamples && distance[sample] < target) sample++;
                float span = distance[sample] - distance[sample - 1];
                float blend = span > 0f ? (target - distance[sample - 1]) / span : 0f;
                points[i] = samples[sample - 1] +
                    (samples[sample] - samples[sample - 1]) * blend;
            }
            if (RampShapeFits(points, pieceLength, width)) return points;
        }
        return null;
    }

    private static bool RampShapeFits(Vector3[] points, float pieceLength, float width)
    {
        Vector3 previousStep = Vector3.Zero;
        float previousLength = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            Vector3 step = points[i] - points[i - 1];
            float length = step.Length();
            float horizontal = (float)Math.Sqrt(step.X * step.X + step.Y * step.Y);
            float pitch = (float)(Math.Atan2(-step.Z, horizontal) * 180.0 / Math.PI);
            if (length > pieceLength * 0.82f || length < pieceLength * 0.47f ||
                pitch < 15f || pitch > 55f)
                return false;
            if (previousLength > 0f)
            {
                float yawCross = previousStep.X * step.Y - previousStep.Y * step.X;
                float yawDot = previousStep.X * step.X + previousStep.Y * step.Y;
                float yawDegrees = (float)(Math.Abs(Math.Atan2(yawCross, yawDot)) * 180.0 / Math.PI);
                if (yawDegrees > MaxRampYawDegrees) return false;
                float dot = previousStep.X * step.X + previousStep.Y * step.Y +
                    previousStep.Z * step.Z;
                float cosine = dot / (previousLength * length);
                if (cosine < 0.95106f)
                    return false;
                float halfTurn = (float)Math.Acos(Math.Min(1f, cosine)) * 0.5f;
                float sideCoverage = pieceLength * (float)Math.Cos(halfTurn) -
                    width * (float)Math.Sin(halfTurn);
                if (Math.Max(previousLength, length) > sideCoverage)
                    return false;
            }
            previousStep = step;
            previousLength = length;
        }
        return true;
    }

    private static float RampWave(float t, float[] amplitudes)
    {
        float value = amplitudes[0];
        for (int i = 1; i < amplitudes.Length; i++)
            value += amplitudes[i] * (float)Math.Sin(2.0 * Math.PI * i * t);
        float envelope = (float)Math.Sin(Math.PI * t);
        return envelope * envelope * value;
    }

    private float[] GetRampBankAngles(Vector3[] points)
    {
        var turns = new float[points.Length];
        var banks = new float[points.Length];
        for (int i = 1; i < points.Length - 1; i++)
        {
            Vector3 before = points[i] - points[i - 1];
            Vector3 after = points[i + 1] - points[i];
            float cross = before.X * after.Y - before.Y * after.X;
            float dot = before.X * after.X + before.Y * after.Y;
            turns[i] = (float)-Math.Atan2(cross, dot) * 1.5f;
        }
        for (int i = 1; i < points.Length - 1; i++)
        {
            float smooth = (turns[Math.Max(0, i - 2)] +
                2f * turns[i - 1] + 3f * turns[i] +
                2f * turns[i + 1] + turns[Math.Min(points.Length - 1, i + 2)]) / 9f;
            float edgeFade = (float)Math.Sin(Math.PI * i / (points.Length - 1));
            banks[i] = Math.Max(-0.44f, Math.Min(0.44f, smooth * edgeFade));
        }
        return banks;
    }

    private static float RampRun(float pieceLength)
    {
        return pieceLength * RampSpacingFactor * RampRunIntervals *
            (float)Math.Cos(RampSlopeDegrees * Math.PI / 180.0);
    }

    private static float RampDrop(float run)
    {
        return run * (float)Math.Tan(RampSlopeDegrees * Math.PI / 180.0);
    }

    private static void SetRampOrientation(int handle, Vector3 tangent, bool alongX, float bank)
    {
        tangent.Normalize();
        Vector3 lateral = new Vector3(tangent.Y, -tangent.X, 0f);
        lateral.Normalize();
        Vector3 up = Vector3.Cross(lateral, tangent);
        Vector3 bankedLateral = lateral * (float)Math.Cos(bank) -
            up * (float)Math.Sin(bank);
        Vector3 x = alongX ? tangent : bankedLateral;
        Vector3 y = alongX ? -bankedLateral : tangent;
        Vector3 z = Vector3.Cross(x, y);

        float m00 = x.X, m01 = y.X, m02 = z.X;
        float m10 = x.Y, m11 = y.Y, m12 = z.Y;
        float m20 = x.Z, m21 = y.Z, m22 = z.Z;
        float qx, qy, qz, qw;
        float trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            float s = (float)Math.Sqrt(trace + 1f) * 2f;
            qw = 0.25f * s;
            qx = (m21 - m12) / s;
            qy = (m02 - m20) / s;
            qz = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f;
            qw = (m21 - m12) / s;
            qx = 0.25f * s;
            qy = (m01 + m10) / s;
            qz = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f;
            qw = (m02 - m20) / s;
            qx = (m01 + m10) / s;
            qy = 0.25f * s;
            qz = (m12 + m21) / s;
        }
        else
        {
            float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f;
            qw = (m10 - m01) / s;
            qx = (m02 + m20) / s;
            qy = (m12 + m21) / s;
            qz = 0.25f * s;
        }
        Function.Call(Hash.SET_ENTITY_QUATERNION, handle, qx, qy, qz, qw);
    }

    private void ReleaseNextBall()
    {
        if (_pendingRelease.Count == 0 || Game.GameTime < _releaseStartTime) return;
        int handle = _pendingRelease[_releaseIndex++];
        try
        {
            if (Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle))
            {
                Function.Call(Hash.SET_ENTITY_VELOCITY, handle, 0f, 0f, 0f);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, false);
            }
        }
        catch (Exception ex) { PluginLog.Error("SpoonerPrefabs: Release", ex); }
        if (_releaseIndex >= _pendingRelease.Count)
        {
            _pendingRelease.Clear();
            _releaseIndex = 0;
            GTA.UI.Screen.ShowSubtitle("~g~Все мячи динамические.", 2500);
        }
    }

    private void UpdateRampCleanup()
    {
        if (_rampTouched == null || _rampDeleteAt == null || _rampPiecePositions == null) return;
        int now = Game.GameTime;
        if (now - _rampContactCheckedAt >= 100)
        {
            _rampContactCheckedAt = now;
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && player.IsAlive)
            {
                for (int i = 0; i < _rampPieces.Count; i++)
                {
                    int handle = _rampPieces[i];
                    if (handle == 0 || _rampTouched[i] || !IsCurrentRampPiece(i)) continue;
                    if (!Function.Call<bool>(Hash.IS_ENTITY_TOUCHING_ENTITY, player.Handle, handle)) continue;

                    _rampTouched[i] = true;
                    _rampUniqueTouches++;
                    for (int j = 1; j <= i; j++)
                        if (_rampPieces[j] != 0 && _rampDeleteAt[j] == 0)
                            _rampDeleteAt[j] = now + 5000;
                    if (_rampUniqueTouches == 6)
                        _rampFinalCleanupAt = now + 60000;
                }
            }
        }

        for (int i = 1; i < _rampPieces.Count; i++)
        {
            int handle = _rampPieces[i];
            if (handle == 0) continue;
            bool prefixExpired = _rampDeleteAt[i] != 0 && now >= _rampDeleteAt[i];
            bool cleanupExpired = _rampFinalCleanupAt != 0 && now >= _rampFinalCleanupAt;
            if (!prefixExpired && !cleanupExpired) continue;
            try
            {
                if (!IsCurrentRampPiece(i))
                {
                    _rampPieces[i] = 0;
                    continue;
                }
                Entity piece = Entity.FromHandle(handle);
                if (piece != null && piece.Exists()) piece.Delete();
                if (!Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle)) _rampPieces[i] = 0;
            }
            catch (Exception ex)
            {
                PluginLog.Error("SpoonerPrefabs: RemoveRampPiece " + (i + 1), ex);
            }
        }
    }

    private bool IsCurrentRampPiece(int index)
    {
        int handle = _rampPieces[index];
        if (handle == 0 || !Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle) ||
            Function.Call<int>(Hash.GET_ENTITY_MODEL, handle) !=
                unchecked((int)StringHash.AtStringHash(RampModelName))) return false;
        Vector3 position = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, handle, true);
        return (position - _rampPiecePositions[index]).Length() < 0.25f;
    }

    public void OnAbort()
    {
        _rampSelecting = false;
        _rampPreviewPoints = null;
        _rampTouched = null;
        _rampDeleteAt = null;
        _rampPiecePositions = null;
        _rampUniqueTouches = 0;
        _rampFinalCleanupAt = 0;
        Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED,
            unchecked((int)StringHash.AtStringHash(RampModelName)));
        while (_pendingRelease.Count > 0)
        {
            _releaseStartTime = 0;
            ReleaseNextBall();
        }
        CancelPreview();
        if (_menu != null) _menu.Visible = false;
    }

    private void CancelPreview()
    {
        if (!_preview) return;
        _preview = false;
        _hasPlacement = false;
        Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, BallHash);
    }
}
}
