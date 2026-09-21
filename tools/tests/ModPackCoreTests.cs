using System;
using System.IO;
using ModPack;

namespace PluginLogging { public static class PluginLog { public static void Error(string message, Exception ex) { } } }
namespace GTA
{
    public class Ped
    {
        public int Handle;
        public bool IsDead, IsPositionFrozen, IsVisible = true, IsCollisionEnabled = true, IsInvincible;
        public bool Exists() { return true; }
    }
    public sealed class Player { public Ped Character; }
    public static class Game
    {
        public static Player Player = new Player();
        public static float TimeScale = 0.5f;
    }
}
namespace GTA.UI { public static class Screen { public static void ShowSubtitle(string message, int ms) { } } }
namespace GTA.Native
{
    public enum Hash { SET_TIME_SCALE, SET_GRAVITY_LEVEL }
    public static class Function
    {
        public static int Gravity;
        public static void Call(Hash hash, object value)
        {
            if (hash == Hash.SET_TIME_SCALE) GTA.Game.TimeScale = (float)value;
            else Gravity = (int)value;
        }
    }
}

public static class ModPackCoreTests
{
    private static int _passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _passed++;
        Console.WriteLine("PASS: " + message);
    }
    private static void Fails(Action action, string message)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Check(failed, message);
    }
    private static void Validate(string value)
    {
        int number;
        if (!int.TryParse(value, out number)) throw new InvalidDataException("fixture");
    }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ModPackCore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = new ControlSession();
            var second = new ControlSession();
            var original = new GTA.Ped { Handle = 1, IsPositionFrozen = true };
            GTA.Game.Player.Character = original;
            Check(first.Acquire("camera", false), "first owner acquires control");
            Check(!second.Acquire("shark", false), "second owner cannot interrupt");
            second.Dispose();
            Check(first.Held, "non-owner cleanup cannot release current owner");
            first.TimeScale(0.25f);
            first.Gravity(2);
            original.IsVisible = false;
            original.IsPositionFrozen = false;
            GTA.Game.Player.Character = new GTA.Ped { Handle = 2 };
            Check(!first.Valid, "character change invalidates session");
            first.Dispose();
            first.Dispose();
            Check(original.IsVisible && original.IsPositionFrozen, "original character snapshot restored");
            Check(!GTA.Game.Player.Character.IsPositionFrozen, "replacement character untouched");
            Check(GTA.Game.TimeScale == 0.5f && GTA.Native.Function.Gravity == 0, "time restored and gravity fallback applied");
            Check(second.Acquire("shark", false), "new owner acquires after release");
            first.Dispose();
            Check(second.Held, "old owner repeated cleanup does not release the new owner");
            GTA.Game.Player.Character.IsDead = true;
            Check(!second.Valid, "death invalidates the current session");
            try { throw new Exception(); }
            catch { second.Dispose(); }
            Check(ControlSession.Owner == null, "error cleanup releases ownership");

            string path = Path.Combine(root, "settings.json");
            SafeFiles.Write(path, "1", Validate);
            SafeFiles.Write(path, "2", Validate);
            Check(File.ReadAllText(path + ".bak") == "1", "previous valid version backed up");
            File.WriteAllText(path, "broken");
            Check(SafeFiles.Read(path, Validate) == "1", "corrupt primary recovers backup");
            Check(File.ReadAllText(path) == "1" && Directory.GetFiles(root, "*.corrupt-*").Length == 1, "recovery repairs primary and retains corrupt evidence");
            File.WriteAllText(path, "broken");
            File.WriteAllText(path + ".bak", "broken backup");
            Fails(() => SafeFiles.Read(path, Validate), "both damaged copies fail");
            Fails(() => SafeFiles.Write(path, "0", Validate), "defaults cannot overwrite damaged copies");
            Check(File.ReadAllText(path) == "broken", "damaged original remains intact");
            File.WriteAllText(path, "3");
            SafeFiles.Write(path, "4", Validate);
            Check(File.ReadAllText(path) == "4", "manual repair unlocks saving");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Fails(() => SafeFiles.Write(path, "5", Validate), "failed atomic replacement reported");
            Check(File.ReadAllText(path) == "4", "failed replacement retains original");
            Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "temporary files removed after error");
            string destination = Path.Combine(root, "renamed.json");
            File.WriteAllText(destination, "7");
            Fails(() => SafeFiles.Rename(path, destination, "8", Validate), "rename rejects occupied destination");
            Check(File.ReadAllText(path) == "4" && File.ReadAllText(destination) == "7", "failed rename preserves both files");
            File.Delete(destination);
            SafeFiles.Rename(path, destination, "8", Validate);
            Check(!SafeFiles.Exists(path) && File.ReadAllText(destination) == "8", "rename writes new before deleting old and backup");
            SafeFiles.Write(destination, "9", Validate);
            SafeFiles.Delete(destination);
            Check(!SafeFiles.Exists(destination), "explicit delete removes backup too");
            var buffer = new DiagnosticBuffer();
            Check(buffer.Add("same", 0, "one") && !buffer.Add("same", 2999, "two") && buffer.Add("same", 3000, "three"), "diagnostic throttle is three seconds");
            for (int i = 0; i < 8; i++) buffer.Add("event" + i, 4000, "event" + i);
            Check(buffer.Recent.Length == 5 && buffer.Recent[4] == "event7", "panel keeps last five events");
            string log = Path.Combine(root, "test.log");
            DiagnosticBuffer.Append(log, new string('x', 60), 100);
            DiagnosticBuffer.Append(log, new string('y', 60), 100);
            Check(File.Exists(log + ".previous") && new FileInfo(log).Length <= 100, "diagnostic file rotates at size limit");
            DevelopmentDiagnostics.Enabled = DevelopmentDiagnostics.PanelVisible = true;
            DevelopmentDiagnostics.BeginSession();
            Check(!DevelopmentDiagnostics.PanelVisible && !DevelopmentDiagnostics.Enabled, "new session never shows developer panel");
            string beforePanel = DevelopmentDiagnostics.Panel(null);
            DevelopmentDiagnostics.Event("fixture", "disabled event");
            Check(DevelopmentDiagnostics.Panel(null) == beforePanel, "disabled diagnostics do not collect events");
            Console.WriteLine(_passed + " checks passed.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
