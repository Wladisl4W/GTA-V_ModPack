using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Windows.Forms;

// No game or native calls: exercise the real loader against temporary plugins.
namespace GTA
{
    public class Script
    {
        public event EventHandler Tick;
        public event KeyEventHandler KeyDown;
        public event EventHandler Aborted;
    }
}
namespace GTA.UI
{
    public static class Notification
    {
        public static void PostTicker(string message, bool blink, bool brief) { }
    }
}
namespace LemonUI { public class ObjectPool { } }

public static class ReloaderTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Reload(Reloader loader)
    {
        typeof(Reloader).GetMethod("ReloadPlugins", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(loader, null);
    }

    private static IList Plugins(Reloader loader)
    {
        return (IList)typeof(Reloader).GetField("_plugins", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(loader);
    }

    public static int Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins");
        string directory = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "TestPlugin.cs");
        const string good = "public class TestPlugin { public bool Stopped; public void OnStart() {} public void OnTick() {} public void OnAbort() { Stopped = true; } }";
        File.WriteAllText(file, good);
        var loader = new Reloader();
        try
        {
            Require(Plugins(loader).Count == 1, "initial plugin starts");
            object original = Plugins(loader)[0];
            File.WriteAllText(file, "invalid C# source");
            Reload(loader);
            Require(Plugins(loader).Count == 1 && Object.ReferenceEquals(original, Plugins(loader)[0]),
                "compile error retains the original instance");
            Require(!(bool)original.GetType().GetField("Stopped").GetValue(original),
                "compile error does not invoke OnAbort");
            File.WriteAllText(file, "#warning intentional regression fixture\n" + good);
            Reload(loader);
            Require(Plugins(loader).Count == 1 && !Object.ReferenceEquals(original, Plugins(loader)[0]),
                "warnings allow replacement");
            Require((bool)original.GetType().GetField("Stopped").GetValue(original),
                "successful replacement stops the previous instance");
            Require(File.ReadAllText(Path.Combine(root, "compile_errors.txt")).Contains("WARNING"),
                "warnings remain available in diagnostics");
            File.WriteAllText(file, good);
            Reload(loader);
            Require(!File.Exists(Path.Combine(root, "compile_errors.txt")), "clean compilation clears diagnostics");
            original = Plugins(loader)[0];
            File.WriteAllText(Path.Combine(directory, ".deploy.lock"), "");
            File.WriteAllText(file, good + "\n");
            Reload(loader);
            Require(Object.ReferenceEquals(original, Plugins(loader)[0]), "deployment lock defers replacement");
            File.Delete(Path.Combine(directory, ".deploy.lock"));
            File.WriteAllText(file, "public class TestPlugin { public void OnStart() { throw new System.Exception(\"fixture\"); } public void OnTick() {} public void OnAbort() {} }");
            Reload(loader);
            Require(Plugins(loader).Count == 0, "failed initialization is not ticked");
            Require(File.ReadAllText(Path.Combine(root, "Reloader.log")).Contains("Reload incomplete:"),
                "failed initialization cannot report a successful deployment");
            return 0;
        }
        finally
        {
            typeof(Reloader).GetMethod("OnAborted", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(loader, new object[] { null, EventArgs.Empty });
        }
    }
}
