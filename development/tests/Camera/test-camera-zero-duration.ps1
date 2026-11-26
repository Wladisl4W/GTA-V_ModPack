$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$source = Get-Content -LiteralPath (Join-Path $repo 'scripts/ReloaderPlugins/Plugins/Camera/Code/CameraInterpolator.cs') -Raw
$fixture = @'
namespace ModPack.Camera {
    public static class Logger {
        public static void Info(string text) {}
        public static void Warn(string text) {}
        public static void Error(System.Exception error, string text) { throw error; }
    }
}
public static class CameraZeroDurationTests {
    static void Check(bool condition, string message) { if (!condition) throw new System.Exception(message); }
    static ModPack.Camera.CameraInterpolator Make(int[] durations, int[] modes) {
        var positions = new System.Collections.Generic.List<GTA.Math.Vector3>();
        var rotations = new System.Collections.Generic.List<GTA.Math.Vector3>();
        var fovs = new System.Collections.Generic.List<int>();
        foreach (int i in System.Linq.Enumerable.Range(0, durations.Length)) {
            positions.Add(new GTA.Math.Vector3(i * 100f, 0f, 0f));
            rotations.Add(new GTA.Math.Vector3(i * 10f, i * 4f, i * 30f));
            fovs.Add(40 + i * 10);
        }
        var camera = new ModPack.Camera.CameraInterpolator();
        camera.SetPath(positions, rotations, new System.Collections.Generic.List<int>(durations), new System.Collections.Generic.List<int>(modes), fovs);
        camera.Start();
        return camera;
    }
    static void Frame(ModPack.Camera.CameraInterpolator camera, long ms, int node) {
        GTA.Math.Vector3 position, rotation; float fov;
        camera.UpdateAt(ms, out position, out rotation, out fov);
        Check(System.Math.Abs(position.X - node * 100f) < 0.001f, "position at " + ms);
        Check(System.Math.Abs(rotation.X - node * 10f) < 0.001f && System.Math.Abs(rotation.Y - node * 4f) < 0.001f && System.Math.Abs(rotation.Z - node * 30f) < 0.001f, "rotation at " + ms);
        Check(System.Math.Abs(fov - (40 + node * 10)) < 0.001f, "FOV at " + ms);
    }
    public static void Run() {
        var single = Make(new[] { 0, 100, 100 }, new[] { 0, 2, 0 });
        Frame(single, 0, 1);
        Frame(single, 100, 2);
        Frame(single, 199, 2);
        Frame(single, 200, 1);
        GTA.Math.Vector3 position, rotation; float fov;
        single.UpdateAt(1, out position, out rotation, out fov);
        Check(position.X >= 99f && position.X < 110f, "outgoing smooth segment starts at cut marker");
        Check(System.Math.Abs(fov - 50f) < 2f, "outgoing FOV starts at cut marker");
        var chain = Make(new[] { 0, 0, 100, 100 }, new[] { 2, 1, 2, 0 });
        Frame(chain, 0, 2);
        var ending = Make(new[] { 100, 0, 100 }, new[] { 0, 1, 0 });
        Frame(ending, 100, 2);
        Frame(ending, 200, 0);
        var empty = Make(new[] { 0, 0 }, new[] { 0, 0 });
        Frame(empty, 0, 0);
        Frame(empty, 1000, 0);
        var normal = Make(new[] { 100, 100 }, new[] { 0, 0 });
        normal.UpdateAt(50, out position, out rotation, out fov);
        Check(System.Math.Abs(position.X - 50f) < 0.001f && System.Math.Abs(fov - 45f) < 0.001f, "positive duration unchanged");
    }
}
'@
$game = (Get-Content -LiteralPath (Join-Path $repo 'development/config/modpack.local.json') -Raw | ConvertFrom-Json).GameDirectory
$dll = Join-Path $game 'ScriptHookVDotNet3.dll'
[void][Reflection.Assembly]::LoadFrom($dll)
Add-Type -TypeDefinition ($source + "`n" + $fixture) -ReferencedAssemblies @('System.Core', $dll)
[CameraZeroDurationTests]::Run()
Write-Host 'PASS: zero cuts, chained cuts, smooth start, last hold, loop, all-zero path and normal segment.'
