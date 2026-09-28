using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ModPack;

public sealed class CrashLoggerPlugin : IGtaPlugin
{
    private string _root;
    private string _contextPath;
    private string _heartbeatPath;
    private int _pid;
    private int _lastHeartbeat;
    private UnhandledExceptionEventHandler _unhandledHandler;

    public void OnStart()
    {
        _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "CrashLogger");
        Directory.CreateDirectory(_root);
        Process current = Process.GetCurrentProcess();
        _pid = current.Id;
        string sessionId = _pid + "-" + current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        _contextPath = Path.Combine(_root, "CrashSession-" + _pid + ".log");
        _heartbeatPath = Path.Combine(_root, "CrashHeartbeat-" + _pid + ".txt");
        CrashDiagnostics.Configure(_contextPath);
        CrashDiagnostics.Event("CrashLogger", "Plugin started; session=" + sessionId);

        _unhandledHandler = delegate(object sender, UnhandledExceptionEventArgs args)
        {
            CrashDiagnostics.Exception("AppDomain", "Unhandled exception", args.ExceptionObject as Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += _unhandledHandler;
        StartWatcher(current, sessionId);
        WriteHeartbeat();
    }

    public void OnTick()
    {
        int now = Environment.TickCount;
        if ((uint)(now - _lastHeartbeat) < 2000) return;
        _lastHeartbeat = now;
        WriteHeartbeat();
    }

    public void OnKeyDown(System.Windows.Forms.Keys key) { }

    public void OnAbort()
    {
        CrashDiagnostics.Event("CrashLogger", "Plugin stopped or reloaded");
        if (_unhandledHandler != null)
            AppDomain.CurrentDomain.UnhandledException -= _unhandledHandler;
        _unhandledHandler = null;
    }

    private void StartWatcher(Process current, string sessionId)
    {
        try
        {
            string packaged = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "ReloaderPlugins", "Plugins", "CrashWatcher.exe");
            if (!File.Exists(packaged))
                throw new FileNotFoundException("CrashWatcher.exe is missing.", packaged);

            string runtime = Path.Combine(_root, "CrashWatcher.runtime." + _pid + ".exe");
            // Keep the running image separate so Update can replace the packaged watcher.
            if (!File.Exists(runtime)) File.Copy(packaged, runtime, false);
            string packagedConfig = packaged + ".config";
            string runtimeConfig = runtime + ".config";
            if (File.Exists(packagedConfig) && !File.Exists(runtimeConfig))
                File.Copy(packagedConfig, runtimeConfig, false);
            CleanupOldRuntimeCopies(runtime);
            var start = new ProcessStartInfo {
                FileName = runtime,
                Arguments = "--pid " + _pid.ToString(CultureInfo.InvariantCulture) +
                    " --game-dir " + Quote(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName) +
                    " --scripts-dir " + Quote(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) +
                    " --session-id " + Quote(sessionId) +
                    " --session-start " + Quote(current.StartTime.ToUniversalTime().ToString("O")) +
                    " --context " + Quote(_contextPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _root
            };
            Process.Start(start);
            CrashDiagnostics.Event("CrashLogger", "Watcher launch requested");
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Exception("CrashLogger", "Watcher start failed", ex);
            PluginLogging.PluginLog.Error("Crash watcher start", ex);
        }
    }

    private void WriteHeartbeat()
    {
        try
        {
            string text = "TimeUtc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine +
                "ProcessId=" + _pid + Environment.NewLine +
                "ControlOwner=" + (ControlSession.Owner ?? "none") + Environment.NewLine +
                "States=" + DevelopmentDiagnostics.Snapshot + Environment.NewLine;
            string temp = _heartbeatPath + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(_heartbeatPath)) File.Replace(temp, _heartbeatPath, null);
            else File.Move(temp, _heartbeatPath);
        }
        catch (Exception ex) { PluginLogging.PluginLog.Error("Crash heartbeat", ex); }
    }

    private void CleanupOldRuntimeCopies(string current)
    {
        try
        {
            foreach (string file in Directory.GetFiles(_root, "CrashWatcher.runtime.*.exe"))
            {
                if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { }
                try { if (File.Exists(file + ".config")) File.Delete(file + ".config"); } catch { }
            }
        }
        catch { }
    }

    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
}
