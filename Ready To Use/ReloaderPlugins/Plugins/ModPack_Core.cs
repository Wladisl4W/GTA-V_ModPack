using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ModPack
{
    internal static class CrashDiagnostics
    {
        private static readonly object Sync = new object();
        private static string _path;
        public static void Configure(string path) { lock (Sync) _path = path; }
        public static void Event(string source, string message)
        {
            string path;
            lock (Sync) path = _path;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string line = DateTime.UtcNow.ToString("O") + " EVENT " + source + ": " + message;
                lock (Sync) File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
        public static void Exception(string source, string message, Exception ex)
        {
            Event(source, "MANAGED_EXCEPTION " + message + Environment.NewLine + (ex == null ? "No exception object." : ex.ToString()));
        }
    }

    internal sealed class ControlGate
    {
        private object _owner;
        public string Name { get; private set; }
        public bool Owns(object owner) { return ReferenceEquals(_owner, owner); }
        public bool Acquire(object owner, string name)
        {
            if (_owner != null && !Owns(owner)) return false;
            _owner = owner;
            Name = name;
            return true;
        }
        public void Release(object owner)
        {
            if (!Owns(owner)) return;
            _owner = null;
            Name = null;
        }
    }

    internal static class SafeFiles
    {
        private static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static bool Exists(string path) { return File.Exists(path) || File.Exists(path + ".bak"); }
        private static string Key(string path) { return Path.GetFullPath(path); }
        private static void Validate(string text, Action<string> validate)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty save.");
            validate(text);
        }
        public static string Read(string path, Action<string> validate)
        {
            string key = Key(path);
            try
            {
                string text = File.ReadAllText(path);
                Validate(text, validate);
                Blocked.Remove(key);
                return text;
            }
            catch (Exception original)
            {
                try
                {
                    string backup = File.ReadAllText(path + ".bak");
                    Validate(backup, validate);
                    // Keep evidence before repairing; never replace the good backup with damaged data.
                    if (File.Exists(path))
                        File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    WriteTemporary(path, backup, false);
                    Blocked.Remove(key);
                    DevelopmentDiagnostics.Event("Save", "Recovered " + Path.GetFileName(path));
                    return backup;
                }
                catch (Exception recovery)
                {
                    Blocked.Add(key);
                    throw new IOException("Save and backup unavailable: " + path, new AggregateException(original, recovery));
                }
            }
        }
        public static void Write(string path, string text, Action<string> validate)
        {
            Validate(text, validate);
            if (Blocked.Contains(Key(path)) && !Exists(path)) Blocked.Remove(Key(path));
            // Check existing data before rotating it into the only backup.
            if (Exists(path)) Read(path, validate);
            WriteTemporary(path, text, true);
        }
        private static void WriteTemporary(string path, string text, bool backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Key(path)));
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            Blocked.Remove(Key(path));
        }
        public static bool Rename(string oldPath, string newPath, string text, Action<string> validate)
        {
            if (string.Equals(Key(oldPath), Key(newPath), StringComparison.OrdinalIgnoreCase))
            {
                Write(oldPath, text, validate);
                return true;
            }
            if (Exists(newPath)) throw new IOException("Destination already exists: " + newPath);
            Write(newPath, text, validate);
            Delete(oldPath);
            return true;
        }
    }

    internal sealed class DiagnosticBuffer
    {
        private readonly Dictionary<string, long> _last = new Dictionary<string, long>();
        private readonly Queue<string> _recent = new Queue<string>();
        public string[] Recent { get { return _recent.ToArray(); } }
        public bool Add(string key, long now, string line)
        {
            long previous;
            if (_last.TryGetValue(key, out previous) && now - previous < 3000) return false;
            if (_last.Count >= 512)
                foreach (string expired in _last.Where(p => now - p.Value >= 3000).Select(p => p.Key).ToArray())
                    _last.Remove(expired);
            if (_last.Count >= 512 && !_last.ContainsKey(key)) return false;
            _last[key] = now;
            _recent.Enqueue(line);
            while (_recent.Count > 5) _recent.Dequeue();
            return true;
        }
        public static void Append(string path, string line, long limit)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            if (bytes.Length > limit) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > limit)
            {
                if (File.Exists(path + ".previous")) File.Delete(path + ".previous");
                File.Move(path, path + ".previous");
            }
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
        }
    }

    internal static class DevelopmentDiagnostics
    {
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly DiagnosticBuffer Buffer = new DiagnosticBuffer();
        private static readonly Dictionary<string, string> States = new Dictionary<string, string>();
        public static bool Enabled { get; set; }
        public static bool PanelVisible { get; set; }
        public static string Snapshot
        {
            get { return string.Join("; ", States.Select(p => p.Key + "=" + p.Value).ToArray()); }
        }
        public static void BeginSession()
        {
            Enabled = false;
            PanelVisible = false;
            States["Camera"] = "Idle";
            States["Follow"] = "Disabled";
            States["Shark"] = "Idle";
        }
        public static long Now { get { return Clock.ElapsedMilliseconds; } }
        public static void State(string source, string state)
        {
            string old;
            if (States.TryGetValue(source, out old) && old == state) return;
            States[source] = state;
            Event(source, state);
        }
        public static void Event(string source, string message)
        {
            CrashDiagnostics.Event(source, message);
            if (!Enabled) return;
            string line = source + ": " + message;
            if (!Buffer.Add(line, Now, line)) return;
            try
            {
                DiagnosticBuffer.Append(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "ReloaderPlugins", "Development.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + line, 5 * 1024 * 1024);
            }
            catch (Exception ex) { PluginLogging.PluginLog.Error("Development log", ex); }
        }
        public static string Panel(string owner)
        {
            return "Owner: " + (owner ?? "none") + "\n" +
                string.Join("\n", States.Select(p => p.Key + ": " + p.Value).ToArray()) +
                "\n\n" + string.Join("\n", Buffer.Recent.Select(line =>
                    line.Length > 100 ? line.Substring(0, 97) + "..." : line).ToArray());
        }
    }
}
