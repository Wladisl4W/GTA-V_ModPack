using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GTA;
using Microsoft.CSharp;

public class Reloader : Script
{
    private readonly string _pluginsDir;
    private readonly string _logPath;
    private readonly string _errorsPath;
    private List<object> _plugins = new List<object>();
    private FileSystemWatcher _watcher;
    private bool _reloadPending;
    private int _reloadCooldown;
    private DateTime _lastFileChange = DateTime.MinValue;
    private readonly object _reloadLock = new object();

    public Reloader()
    {
        _pluginsDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "Plugins");
        Directory.CreateDirectory(_pluginsDir);

        _logPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "Reloader.log");
        _errorsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "compile_errors.txt");

        SetupFileWatcher();

        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;

        Log("Reloader started. Plugins dir: " + _pluginsDir);
        LoadPlugins();
    }

    private void SetupFileWatcher()
    {
        _watcher = new FileSystemWatcher(_pluginsDir, "*.cs");
        _watcher.Created += OnPluginFileChanged;
        _watcher.Changed += OnPluginFileChanged;
        _watcher.Deleted += OnPluginFileChanged;
        _watcher.Renamed += (s, e) => OnPluginFileChanged(s, new FileSystemEventArgs(WatcherChangeTypes.Changed, _pluginsDir, e.Name));
        _watcher.IncludeSubdirectories = false;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnPluginFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_reloadLock)
        {
            _lastFileChange = DateTime.UtcNow;
            _reloadPending = true;
            _reloadCooldown = 30;
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        bool reload = false;
        lock (_reloadLock)
        {
            if (_reloadPending && !File.Exists(Path.Combine(_pluginsDir, ".deploy.lock")) &&
                _reloadCooldown-- <= 0 && (DateTime.UtcNow - _lastFileChange).TotalMilliseconds > 500)
            {
                _reloadPending = false;
                reload = true;
            }
        }
        if (reload) ReloadPlugins();

        foreach (var plugin in _plugins)
        {
            try
            {
                var method = plugin.GetType().GetMethod("OnTick");
                if (method != null)
                {
                    var result = method.Invoke(plugin, null);
                    if (result is bool && !(bool)result)
                        return;
                }
            }
            catch (TargetInvocationException tie)
            {
                GTA.UI.Notification.PostTicker("~r~Plugin error: " + tie.InnerException?.Message, false, false);
                Log("Tick error: " + tie.InnerException);
            }
            catch (Exception ex)
            {
                Log("Tick unexpected: " + ex);
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            lock (_reloadLock)
            {
                _reloadPending = true;
                _reloadCooldown = 5;
            }
            e.Handled = true;
            GTA.UI.Notification.PostTicker("~y~Reloading plugins...", false, false);
            return;
        }

        foreach (var plugin in _plugins)
        {
            try
            {
                var method = plugin.GetType().GetMethod("OnKeyDown");
                method?.Invoke(plugin, new object[] { e.KeyCode });
            }
            catch { }
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        Log("Game closing, aborting plugins...");
        StopPlugins();
        _watcher?.Dispose();
    }

    private void StopPlugins()
    {
        foreach (var plugin in _plugins)
        {
            try { plugin.GetType().GetMethod("OnAbort")?.Invoke(plugin, null); }
            catch { }
        }
        _plugins.Clear();
    }

    private void LoadPlugins()
    {
        if (File.Exists(Path.Combine(_pluginsDir, ".deploy.lock")))
        {
            lock (_reloadLock) _reloadPending = true;
            return;
        }
        try
        {
            LoadCompiledPlugins();
        }
        catch (Exception ex)
        {
            Log("Reload failed: " + ex);
        }
    }

    private void LoadCompiledPlugins()
    {
        string fingerprint;
        var results = PluginCompiler.Compile(_pluginsDir,
            AppDomain.CurrentDomain.BaseDirectory,
            typeof(Script).Assembly.Location,
            typeof(LemonUI.ObjectPool).Assembly.Location, out fingerprint);

        if (results.Errors.HasErrors || results.Errors.HasWarnings)
        {
            var allErrors = results.Errors.Cast<CompilerError>().ToList();
            var lines = allErrors.Select(e =>
                $"[{e.FileName ?? "?"}:{e.Line}] {(e.IsWarning ? "WARNING" : "ERROR")}: {e.ErrorText}");

            try { File.WriteAllLines(_errorsPath, lines); }
            catch { }

            foreach (var err in allErrors.Take(3))
                Log($"Compile {(!err.IsWarning ? "error" : "warning")} [{err.FileName}:{err.Line}]: {err.ErrorText}");

            int errCount = allErrors.Count(e => !e.IsWarning);
            int warnCount = allErrors.Count(e => e.IsWarning);
            if (errCount > 0)
                GTA.UI.Notification.PostTicker("~r~" + errCount + " error(s)~s~, ~y~" + warnCount + " warning(s)~s~. Previous plugins retained.", false, false);
            if (errCount > 0) return;
        }

        try { if (!results.Errors.HasWarnings && File.Exists(_errorsPath)) File.Delete(_errorsPath); }
        catch { }

        Log("Compilation OK, assembly: " + results.CompiledAssembly.GetName().Name);

        Assembly asm = results.CompiledAssembly;
        Type[] types = asm.GetExportedTypes();
        Type interfaceType = asm.GetType("IGtaPlugin");
        StopPlugins();
        int loadedCount = 0;
        int failedCount = 0;

        foreach (Type t in types)
        {
            if (t.IsInterface || t.IsAbstract) continue;

            if (interfaceType != null && interfaceType.IsAssignableFrom(t))
            {
                if (LoadPluginInstance(t)) loadedCount++; else failedCount++;
            }
            else if (t.GetMethod("OnTick") != null || t.GetMethod("OnStart") != null)
            {
                if (LoadPluginInstance(t)) loadedCount++; else failedCount++;
            }
        }

        Log("Loaded " + loadedCount + " plugin(s)");
        Log((failedCount == 0 ? "Reload OK: " : "Reload incomplete: ") + fingerprint);
        GTA.UI.Notification.PostTicker("~g~Loaded~s~ " + loadedCount + " plugin(s)", false, false);
    }

    private bool LoadPluginInstance(Type t)
    {
        object instance = null;
        try
        {
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                Log("  Skip " + t.Name + ": no parameterless constructor");
                return false;
            }

            instance = ctor.Invoke(null);
            t.GetMethod("OnStart")?.Invoke(instance, null);
            _plugins.Add(instance);
            Log("  + " + t.Name);
            return true;
        }
        catch (Exception ex)
        {
            Log("  Failed to load " + t.Name + ": " + (ex.InnerException ?? ex));
            try { if (instance != null) t.GetMethod("OnAbort")?.Invoke(instance, null); } catch { }
            return false;
        }
    }

    private void ReloadPlugins()
    {
        Log("Reloading plugins...");
        LoadPlugins();
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Debug.WriteLine("[Reloader] " + message);
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch { }
    }
}
