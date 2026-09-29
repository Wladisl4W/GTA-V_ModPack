using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;
using ModPack;
using PluginLogging;

public sealed class SpoonerPrefabsPlugin : IGtaPlugin
{
    private const int BallHash = unchecked((int)0xC00C3530);
    private const int Columns = 10;
    private const int Rows = 5;
    private readonly ObjectPool _pool = new ObjectPool();
    private readonly List<int> _pendingRelease = new List<int>();
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

    public void OnStart()
    {
        _placeKeyWasDown = Game.IsKeyPressed(Keys.Oem1);
        _enterWasDown = Game.IsKeyPressed(Keys.Return);
        _menu = new NativeMenu("Заготовки", "Object Spooner");
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
        _pool.Add(_menu);
    }

    public void OnTick()
    {
        try
        {
            ReleaseNextBall();
            _pool.Process();
            if (_menu != null && _menu.Visible && !IsSpoonerActive()) _menu.Visible = false;
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
        Camera camera = ScriptCameraDirector.RenderingCam;
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
        if (key == Keys.Back && _preview)
        {
            CancelPreview();
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

    public void OnAbort()
    {
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
