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
        _lastFileChange = DateTime.Now;
        _reloadPending = true;
        _reloadCooldown = 30;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (_reloadPending && _reloadCooldown-- <= 0)
        {
            if ((DateTime.Now - _lastFileChange).TotalMilliseconds > 500)
            {
                ReloadPlugins();
                _reloadPending = false;
            }
            else
            {
                _reloadCooldown = 15;
            }
        }

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
                GTA.UI.Notification.Show("~r~Plugin error: " + tie.InnerException?.Message);
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
            _reloadPending = true;
            _reloadCooldown = 5;
            e.Handled = true;
            GTA.UI.Notification.Show("~y~Reloading plugins...");
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
        var csFiles = Directory.GetFiles(_pluginsDir, "*.cs");
        csFiles = csFiles.Where(f => !Path.GetFileName(f).StartsWith("_")).ToArray();

        var interfaceFile = Path.Combine(_pluginsDir, "_PluginInterface.cs");
        if (File.Exists(interfaceFile))
            csFiles = new[] { interfaceFile }.Concat(csFiles).ToArray();

        if (csFiles.Length == 0)
        {
            Log("No .cs plugin files found in " + _pluginsDir);
            return;
        }

        Log("Compiling " + csFiles.Length + " file(s)...");

        var provider = new CSharpCodeProvider();
        var options = new CompilerParameters
        {
            GenerateInMemory = true,
            GenerateExecutable = false,
            TreatWarningsAsErrors = false,
            TempFiles = new TempFileCollection(Path.GetTempPath(), keepFiles: false)
        };

        var scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
        var pluginsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins");
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.GetFiles(scriptsDir, "*.dll"))
            refs.Add(dll);
        if (Directory.Exists(pluginsRoot))
            foreach (var dll in Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories))
                refs.Add(dll);

        refs.Add("System.dll");
        refs.Add("System.Core.dll");
        refs.Add("System.Data.dll");
        refs.Add("System.Drawing.dll");
        refs.Add("System.Windows.Forms.dll");
        refs.Add("System.Xml.dll");
        refs.Add("System.Web.Extensions.dll");
        refs.Add(typeof(Script).Assembly.Location);
        refs.Add(typeof(LemonUI.ObjectPool).Assembly.Location);

        foreach (var r in refs) options.ReferencedAssemblies.Add(r);
        Log("References: " + refs.Count + " (" + refs.Count(r => r.EndsWith(".dll")) + " dlls)");

        var results = provider.CompileAssemblyFromFile(options, csFiles);

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
                GTA.UI.Notification.Show("~r~" + errCount + " error(s)~s~, ~y~" + warnCount + " warning(s)~s~. See compile_errors.txt");
            return;
        }

        try { if (File.Exists(_errorsPath)) File.Delete(_errorsPath); }
        catch { }

        Log("Compilation OK, assembly: " + results.CompiledAssembly.GetName().Name);

        Assembly asm = results.CompiledAssembly;
        Type interfaceType = asm.GetType("IGtaPlugin");
        int loadedCount = 0;

        foreach (Type t in asm.GetExportedTypes())
        {
            if (t.IsInterface || t.IsAbstract) continue;

            if (interfaceType != null && interfaceType.IsAssignableFrom(t))
            {
                LoadPluginInstance(t);
                loadedCount++;
            }
            else if (t.GetMethod("OnTick") != null || t.GetMethod("OnStart") != null)
            {
                LoadPluginInstance(t);
                loadedCount++;
            }
        }

        Log("Loaded " + loadedCount + " plugin(s)");
        GTA.UI.Notification.Show("~g~Loaded~s~ " + loadedCount + " plugin(s)");
    }

    private void LoadPluginInstance(Type t)
    {
        try
        {
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                Log("  Skip " + t.Name + ": no parameterless constructor");
                return;
            }

            object instance = ctor.Invoke(null);
            _plugins.Add(instance);
            Log("  + " + t.Name);

            t.GetMethod("OnStart")?.Invoke(instance, null);
        }
        catch (Exception ex)
        {
            Log("  Failed to load " + t.Name + ": " + ex.InnerException?.Message ?? ex.Message);
        }
    }

    private void ReloadPlugins()
    {
        Log("Reloading plugins...");
        StopPlugins();
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
