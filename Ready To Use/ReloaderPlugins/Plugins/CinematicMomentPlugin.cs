using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using ModPack;
using PluginLogging;

public sealed class CinematicMomentPlugin : IGtaPlugin
{
    private const int VkMouse4 = 0x05;
    private static readonly uint CurrentProcessId = (uint)Process.GetCurrentProcess().Id;
    private readonly ControlSession _control = new ControlSession();
    private Camera _camera;
    private Entity _target;
    private Vector3 _baseOffset;
    private long _lastTick;
    private long _returnStarted;
    private float _orbitYaw;
    private float _orbitHeight;
    private float _baseFov;
    private bool _wasDown;
    private bool _returning;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public void OnStart()
    {
        _wasDown = IsMouse4Down();
    }

    public void OnTick()
    {
        try
        {
            bool down = IsMouse4Down();
            if (_returning)
            {
                if (!_control.Valid || _camera == null || !_camera.Exists()) Stop();
                else if (down && !_wasDown) Resume();
                else if ((Stopwatch.GetTimestamp() - _returnStarted) * 1000.0 / Stopwatch.Frequency >= 500.0)
                    Stop();
            }
            else if (!down)
            {
                if (_control.Held) BeginReturn();
            }
            else if (!_wasDown && !_control.Held)
            {
                Start();
            }
            else if (_control.Held)
            {
                UpdateCamera();
            }
            _wasDown = down;
        }
        catch (Exception ex)
        {
            PluginLog.Error("CinematicMoment", ex);
            Stop();
        }
    }

    private static bool IsMouse4Down()
    {
        uint foregroundProcess;
        IntPtr window = GetForegroundWindow();
        return window != IntPtr.Zero &&
            GetWindowThreadProcessId(window, out foregroundProcess) != 0 &&
            foregroundProcess == CurrentProcessId &&
            (GetAsyncKeyState(VkMouse4) & 0x8000) != 0;
    }

    private void Start()
    {
        if (!_control.Acquire("Кинематографическая камера", false)) return;
        try
        {
            Ped player = _control.Player;
            Vehicle vehicle = player.CurrentVehicle;
            _target = vehicle != null && vehicle.Exists() ? (Entity)vehicle : player;

            Vector3 startPos = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_COORD);
            Vector3 startRot = Function.Call<Vector3>(Hash.GET_GAMEPLAY_CAM_ROT, 2);
            float startFov = Function.Call<float>(Hash.GET_GAMEPLAY_CAM_FOV);
            _baseFov = startFov;
            _camera = Camera.Create("DEFAULT_SCRIPTED_CAMERA", startPos, startRot, startFov);
            if (_camera == null || !_camera.Exists()) throw new InvalidOperationException("Camera creation failed");

            float pitch = startRot.X * (float)Math.PI / 180f;
            float yaw = startRot.Z * (float)Math.PI / 180f;
            Vector3 viewForward = new Vector3(
                -(float)Math.Sin(yaw) * (float)Math.Cos(pitch),
                (float)Math.Cos(yaw) * (float)Math.Cos(pitch),
                (float)Math.Sin(pitch));
            _baseOffset = startPos - _target.Position - viewForward * (vehicle != null ? 5f : 3f);
            _orbitYaw = 0f;
            _orbitHeight = 0f;
            _returning = false;
            _lastTick = Stopwatch.GetTimestamp();
            _camera.IsActive = true;
            ScriptCameraDirector.StartRendering();
            Function.Call(Hash.RENDER_SCRIPT_CAMS, true, true, 450, true, false);
            _control.TimeScale(_control.OriginalTimeScale * 0.5f);
            DevelopmentDiagnostics.State("CinematicMoment", "Active");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void UpdateCamera()
    {
        if (!_control.Valid || _target == null || !_target.Exists() || _camera == null || !_camera.Exists())
        {
            Stop();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        float dt = (float)((now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;
        dt = Math.Max(0f, Math.Min(dt, 0.1f));

        float lookX = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, 1);
        float lookY = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, 2);
        _orbitYaw = Math.Max(-160f, Math.Min(160f, _orbitYaw - lookX * 2f));
        _orbitHeight = Math.Max(-2.5f, Math.Min(5f, _orbitHeight - lookY * 0.175f));

        bool inVehicle = _target is Vehicle;
        Vector3 focus = _target.Position + new Vector3(0f, 0f, inVehicle ? 0.9f : 0.8f);
        float radians = _orbitYaw * (float)Math.PI / 180f;
        float cosine = (float)Math.Cos(radians);
        float sine = (float)Math.Sin(radians);
        Vector3 desired = _target.Position + new Vector3(
            _baseOffset.X * cosine - _baseOffset.Y * sine,
            _baseOffset.X * sine + _baseOffset.Y * cosine,
            _baseOffset.Z + _orbitHeight);

        float move = 1f - (float)Math.Exp(-4f * dt);
        _camera.Position = Vector3.Lerp(_camera.Position, desired, move);
        Vector3 aim = focus - _camera.Position;
        if (aim.Length() > 0.01f)
        {
            aim.Normalize();
            Vector3 desiredRotation = new Vector3(
                (float)(Math.Asin(aim.Z) * 180.0 / Math.PI), 0f,
                (float)(Math.Atan2(-aim.X, aim.Y) * 180.0 / Math.PI));
            float rotate = 1f - (float)Math.Exp(-7f * dt);
            Vector3 current = _camera.Rotation;
            _camera.Rotation = new Vector3(
                LerpAngle(current.X, desiredRotation.X, rotate), 0f,
                LerpAngle(current.Z, desiredRotation.Z, rotate));
        }
        _camera.FieldOfView = _baseFov;
    }

    private static float LerpAngle(float from, float to, float amount)
    {
        float delta = to - from;
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        return from + delta * amount;
    }

    private void BeginReturn()
    {
        _control.TimeScale(_control.OriginalTimeScale);
        _returning = true;
        _returnStarted = Stopwatch.GetTimestamp();
        Function.Call(Hash.RENDER_SCRIPT_CAMS, false, true, 450, true, false);
        DevelopmentDiagnostics.State("CinematicMoment", "Returning");
    }

    private void Resume()
    {
        _returning = false;
        _lastTick = Stopwatch.GetTimestamp();
        Function.Call(Hash.RENDER_SCRIPT_CAMS, true, true, 250, true, false);
        _control.TimeScale(_control.OriginalTimeScale * 0.5f);
        DevelopmentDiagnostics.State("CinematicMoment", "Active");
    }

    private void Stop()
    {
        try
        {
            if (_control.Held)
            {
                try { ScriptCameraDirector.StopRendering(false); } catch (Exception ex) { PluginLog.Error("CinematicMoment camera stop", ex); }
                if (!_returning)
                    try { Function.Call(Hash.RENDER_SCRIPT_CAMS, false, false, 0, false, false); } catch (Exception ex) { PluginLog.Error("CinematicMoment render stop", ex); }
            }
            if (_camera != null && _camera.Exists()) _camera.Delete();
        }
        finally
        {
            _camera = null;
            _target = null;
            _returning = false;
            _control.Dispose();
            DevelopmentDiagnostics.State("CinematicMoment", "Idle");
        }
    }

    public void OnKeyDown(Keys key) { }
    public void OnAbort() { Stop(); }
}
