using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ModPack.CrashWatcher
{
    internal static class Program
    {
        private const int HangSeconds = 60;

        private static int Main(string[] args)
        {
            try
            {
                Dictionary<string, string> options = ParseArguments(args);
                int pid = int.Parse(Required(options, "pid"), CultureInfo.InvariantCulture);
                string gameDirectory = Path.GetFullPath(Required(options, "game-dir"));
                string scriptsDirectory = Path.GetFullPath(Required(options, "scripts-dir"));
                string sessionId = Required(options, "session-id");
                string contextPath = Path.GetFullPath(Required(options, "context"));
                DateTime sessionStart = DateTime.Parse(Required(options, "session-start"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                string root = Path.Combine(scriptsDirectory, "ReloaderPlugins", "CrashLogger");

                bool ownsMutex;
                using (var mutex = new Mutex(true, "Local\\GTA5_ModPack_CrashWatcher_" + pid, out ownsMutex))
                {
                    if (!ownsMutex) return 0;
                    return Monitor(pid, sessionId, sessionStart, gameDirectory, scriptsDirectory, root, contextPath);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CrashWatcher.failed.log");
                    File.AppendAllText(fallback, DateTime.Now.ToString("O") + " " + ex + Environment.NewLine);
                }
                catch { }
                return 1;
            }
        }

        private static int Monitor(int pid, string sessionId, DateTime sessionStart, string gameDirectory,
            string scriptsDirectory, string root, string contextPath)
        {
            Directory.CreateDirectory(root);
            string monitorLog = Path.Combine(root, "CrashMonitor.log");
            AppendRotating(monitorLog, "Watcher started. PID=" + pid + ", session=" + sessionId, 5 * 1024 * 1024);

            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (Exception ex)
            {
                AppendRotating(monitorLog, "Cannot attach to GTA5.exe: " + ex.Message, 5 * 1024 * 1024);
                return 2;
            }
            IntPtr processHandle = NativeMethods.OpenProcess(
                NativeMethods.Synchronize | NativeMethods.QueryLimitedInformation, false, pid);

            bool hangObserved = false;
            DateTime? unresponsiveSince = null;
            while (!process.HasExited)
            {
                bool responding = true;
                try { responding = process.Responding; }
                catch { }
                if (!responding)
                {
                    if (!unresponsiveSince.HasValue) unresponsiveSince = DateTime.UtcNow;
                    if (!hangObserved && (DateTime.UtcNow - unresponsiveSince.Value).TotalSeconds >= HangSeconds)
                    {
                        hangObserved = true;
                        AppendRotating(monitorLog, "GTA5.exe has not responded for 60 seconds.", 5 * 1024 * 1024);
                    }
                }
                else if (unresponsiveSince.HasValue)
                {
                    if (hangObserved)
                        AppendRotating(monitorLog, "GTA5.exe recovered after a long hang.", 5 * 1024 * 1024);
                    unresponsiveSince = null;
                }
                Thread.Sleep(2000);
                try { process.Refresh(); } catch { }
            }

            int exitCode = ReadExitCode(process, processHandle);
            if (processHandle != IntPtr.Zero) NativeMethods.CloseHandle(processHandle);
            DateTime ended = DateTime.UtcNow;

            // WER commonly writes its event a few seconds after the process disappears.
            List<EventEvidence> evidence = new List<EventEvidence>();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) Thread.Sleep(2000);
                evidence = ReadWindowsEvidence(sessionStart.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
                if (evidence.Any(e => e.IsCrash || e.IsHang)) break;
            }

            bool managedException = TailContains(contextPath, "MANAGED_EXCEPTION", 256 * 1024);
            bool nativeCrash = evidence.Any(e => e.IsCrash);
            bool windowsHang = evidence.Any(e => e.IsHang);
            bool isProblem = exitCode != 0 || hangObserved || windowsHang || nativeCrash || managedException;
            if (!isProblem)
            {
                AppendRotating(monitorLog, "Normal GTA shutdown. PID=" + pid + ", exit=" + exitCode, 5 * 1024 * 1024);
                CleanupSessionFiles(contextPath, pid);
                return 0;
            }

            string category = nativeCrash ? "нативный краш" :
                (hangObserved || windowsHang) ? "зависание" :
                managedException ? "управляемое исключение" : "неожиданное завершение без установленной причины";
            string report = BuildReport(category, pid, exitCode, sessionId, sessionStart, ended,
                gameDirectory, scriptsDirectory, contextPath, evidence, hangObserved);
            string reportDirectory = CreateReportDirectory(root, ended.ToLocalTime());
            File.WriteAllText(Path.Combine(reportDirectory, "CrashReport.txt"), report, new UTF8Encoding(true));
            RotateReports(Path.Combine(root, "CrashReports"), 20);
            AppendRotating(monitorLog, "Report created: " + reportDirectory + " (" + category + ")", 5 * 1024 * 1024);
            CleanupSessionFiles(contextPath, pid);
            return 0;
        }

        private static string BuildReport(string category, int pid, int exitCode, string sessionId,
            DateTime started, DateTime ended, string gameDirectory, string scriptsDirectory,
            string contextPath, List<EventEvidence> evidence, bool hangObserved)
        {
            var text = new StringBuilder();
            text.AppendLine("GTA V ModPack crash report");
            text.AppendLine(new string('=', 72));
            text.AppendLine("Category: " + category);
            text.AppendLine("Confidence: " + (evidence.Any(e => e.IsCrash || e.IsHang) ?
                "confirmed by Windows event evidence" : "inferred from process and mod context"));
            text.AppendLine("Session: " + sessionId);
            text.AppendLine("GTA PID: " + pid);
            text.AppendLine("Started (UTC): " + started.ToString("O"));
            text.AppendLine("Ended (UTC): " + ended.ToString("O"));
            text.AppendLine("Exit code: " + FormatExitCode(exitCode));
            text.AppendLine("60-second hang observed by watcher: " + (hangObserved ? "yes" : "no"));
            text.AppendLine();
            text.AppendLine("Interpretation");
            text.AppendLine(new string('-', 72));
            text.AppendLine("A faulting module or exception code reported by Windows is evidence, but it does");
            text.AppendLine("not always identify the original mod that caused memory corruption. Log lines below");
            text.AppendLine("are timing context only unless they contain an explicit exception and stack trace.");

            text.AppendLine();
            text.AppendLine("Windows evidence");
            text.AppendLine(new string('-', 72));
            if (evidence.Count == 0) text.AppendLine("No matching Application Error or Windows Error Reporting event was found.");
            foreach (EventEvidence item in evidence.Take(8))
            {
                text.AppendLine("[" + item.Time.ToString("yyyy-MM-dd HH:mm:ss") + "] " + item.Source + " / event " + item.EventId);
                text.AppendLine(Limit(item.Message, 12000));
                text.AppendLine();
            }

            text.AppendLine("ModPack session context");
            text.AppendLine(new string('-', 72));
            AppendFileTail(text, contextPath, 200, 64 * 1024);
            string heartbeat = Path.Combine(Path.GetDirectoryName(contextPath), "CrashHeartbeat-" + pid + ".txt");
            AppendFileTail(text, heartbeat, 50, 16 * 1024);

            text.AppendLine();
            text.AppendLine("Installed modules and scripts");
            text.AppendLine(new string('-', 72));
            foreach (string file in EnumerateModules(gameDirectory, scriptsDirectory).Take(800))
            {
                try
                {
                    var info = new FileInfo(file);
                    string version = FileVersionInfo.GetVersionInfo(file).FileVersion;
                    text.AppendLine(Relative(gameDirectory, file) + " | " + (version ?? "no version") +
                        " | " + info.Length + " bytes | " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch (Exception ex) { text.AppendLine(file + " | unavailable: " + ex.Message); }
            }

            text.AppendLine();
            text.AppendLine("Recent logs");
            text.AppendLine(new string('-', 72));
            foreach (string log in EnumerateLogs(gameDirectory, scriptsDirectory).Take(60))
            {
                text.AppendLine();
                text.AppendLine("### " + Relative(gameDirectory, log));
                AppendFileTail(text, log, 120, 64 * 1024);
            }

            string dumpDirectory = Path.Combine(scriptsDirectory, "ReloaderPlugins", "CrashLogger", "CrashDumps");
            if (Directory.Exists(dumpDirectory))
            {
                text.AppendLine();
                text.AppendLine("Available minidumps");
                text.AppendLine(new string('-', 72));
                foreach (string dump in Directory.GetFiles(dumpDirectory, "*.dmp").OrderByDescending(File.GetLastWriteTime).Take(3))
                    text.AppendLine(dump);
            }
            return text.ToString();
        }

        private static IEnumerable<string> EnumerateModules(string gameDirectory, string scriptsDirectory)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in new[] { "*.asi", "*.dll" })
                foreach (string file in SafeFiles(gameDirectory, pattern, SearchOption.TopDirectoryOnly)) files.Add(file);
            foreach (string pattern in new[] { "*.dll", "*.cs" })
                foreach (string file in SafeFiles(scriptsDirectory, pattern, SearchOption.AllDirectories)) files.Add(file);
            return files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EnumerateLogs(string gameDirectory, string scriptsDirectory)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in SafeFiles(gameDirectory, "*.log", SearchOption.TopDirectoryOnly)) files.Add(file);
            foreach (string file in SafeFiles(scriptsDirectory, "*.log", SearchOption.AllDirectories)) files.Add(file);
            string pluginRoot = Path.Combine(scriptsDirectory, "ReloaderPlugins");
            foreach (string file in SafeFiles(pluginRoot, "*.txt", SearchOption.TopDirectoryOnly)) files.Add(file);
            return files.OrderByDescending(SafeLastWrite);
        }

        private static IEnumerable<string> SafeFiles(string root, string pattern, SearchOption option)
        {
            try { return Directory.Exists(root) ? Directory.GetFiles(root, pattern, option) : new string[0]; }
            catch { return new string[0]; }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static List<EventEvidence> ReadWindowsEvidence(DateTime fromUtc, DateTime toUtc)
        {
            var result = new List<EventEvidence>();
            try
            {
                using (var log = new EventLog("Application"))
                {
                    for (int i = log.Entries.Count - 1; i >= 0 && result.Count < 12; i--)
                    {
                        EventLogEntry entry = log.Entries[i];
                        DateTime timeUtc = entry.TimeGenerated.ToUniversalTime();
                        if (timeUtc < fromUtc) break;
                        if (timeUtc > toUtc) continue;
                        string message;
                        try { message = entry.Message ?? ""; } catch { message = "Event message unavailable."; }
                        if (message.IndexOf("GTA5.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bool relevantSource = entry.Source.Equals("Application Error", StringComparison.OrdinalIgnoreCase) ||
                            entry.Source.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase);
                        if (!relevantSource) continue;
                        result.Add(new EventEvidence {
                            Time = entry.TimeGenerated, Source = entry.Source, EventId = entry.InstanceId,
                            Message = message, IsHang = message.IndexOf("AppHang", StringComparison.OrdinalIgnoreCase) >= 0,
                            IsCrash = message.IndexOf("APPCRASH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                entry.Source.Equals("Application Error", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Add(new EventEvidence { Time = DateTime.Now, Source = "CrashWatcher", EventId = 0,
                    Message = "Could not read Windows Application log: " + ex.Message });
            }
            return result;
        }

        private static void AppendFileTail(StringBuilder target, string path, int maxLines, int maxBytes)
        {
            if (!File.Exists(path)) { target.AppendLine("Missing: " + path); return; }
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = Math.Min(stream.Length, maxBytes);
                    stream.Seek(-length, SeekOrigin.End);
                    bytes = new byte[length];
                    int read = stream.Read(bytes, 0, bytes.Length);
                    if (read != bytes.Length) Array.Resize(ref bytes, read);
                }
                string[] lines = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
                foreach (string line in lines.Skip(Math.Max(0, lines.Length - maxLines))) target.AppendLine(line);
            }
            catch (Exception ex) { target.AppendLine("Cannot read " + path + ": " + ex.Message); }
        }

        private static bool TailContains(string path, string value, int maxBytes)
        {
            var text = new StringBuilder();
            AppendFileTail(text, path, 1000, maxBytes);
            return text.ToString().IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CreateReportDirectory(string root, DateTime localTime)
        {
            string parent = Path.Combine(root, "CrashReports");
            Directory.CreateDirectory(parent);
            string baseName = localTime.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(parent, baseName);
            int suffix = 1;
            while (Directory.Exists(path)) path = Path.Combine(parent, baseName + "_" + suffix++);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void RotateReports(string root, int keep)
        {
            try
            {
                foreach (DirectoryInfo directory in new DirectoryInfo(root).GetDirectories()
                    .OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
                    directory.Delete(true);
            }
            catch { }
        }

        private static void CleanupSessionFiles(string contextPath, int pid)
        {
            try { if (File.Exists(contextPath)) File.Delete(contextPath); } catch { }
            try
            {
                string heartbeat = Path.Combine(Path.GetDirectoryName(contextPath), "CrashHeartbeat-" + pid + ".txt");
                if (File.Exists(heartbeat)) File.Delete(heartbeat);
            }
            catch { }
        }

        private static void AppendRotating(string path, string message, long maxBytes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    string previous = path + ".previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }

        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Invalid arguments.");
                values[args[i].Substring(2)] = args[i + 1];
            }
            return values;
        }

        private static string Required(Dictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing --" + key);
            return value;
        }

        private static string FormatExitCode(int value)
        {
            return value == int.MinValue ? "unavailable" : value + " (0x" + unchecked((uint)value).ToString("X8") + ")";
        }

        private static int ReadExitCode(Process process, IntPtr handle)
        {
            uint nativeCode;
            if (handle != IntPtr.Zero && NativeMethods.GetExitCodeProcess(handle, out nativeCode) && nativeCode != 259)
                return unchecked((int)nativeCode);
            try { return process.ExitCode; }
            catch { return int.MinValue; }
        }

        private static string Relative(string root, string path)
        {
            try
            {
                string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path);
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : full;
            }
            catch { return path; }
        }

        private static string Limit(string value, int length)
        {
            return value != null && value.Length > length ? value.Substring(0, length) + "\n[truncated]" : value;
        }

        private sealed class EventEvidence
        {
            public DateTime Time;
            public string Source;
            public long EventId;
            public string Message;
            public bool IsHang;
            public bool IsCrash;
        }

        private static class NativeMethods
        {
            internal const uint Synchronize = 0x00100000;
            internal const uint QueryLimitedInformation = 0x00001000;
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
        }
    }
}
