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

namespace ModPack.Crash
{
    internal static class Program
    {
        private const int HangSeconds = 60;
        private const int DumpLimit = 3;

        private static int Main(string[] args)
        {
            string root = null;
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
                root = Path.Combine(scriptsDirectory, "ReloaderPlugins", "Plugins", "_System", "Diagnostics", "Data", "Crash");

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
                    string failure = root == null
                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CrashWatcher.failed.log")
                        : Path.Combine(root, "CrashMonitor.log");
                    AppendRotating(failure, "Watcher failed: " + ex, 5 * 1024 * 1024);
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
            CaptureSession capture = StartCapture(root, pid, sessionId, monitorLog);

            bool hangObserved = false;
            string hangDump = null;
            DateTime? unresponsiveSince = null;
            int exitCode;
            try
            {
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
                            hangDump = WriteHangDump(root, pid, sessionId, monitorLog);
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
                exitCode = ReadExitCode(process, processHandle);
            }
            finally { if (processHandle != IntPtr.Zero) NativeMethods.CloseHandle(processHandle); }
            DateTime ended = DateTime.UtcNow;
            CompleteCapture(capture, monitorLog);
            AppendRotating(monitorLog, "GTA exited. PID=" + pid + ", exit=" + FormatExitCode(exitCode) +
                ", capture=" + (capture == null ? "off" : capture.DirectoryPath), 5 * 1024 * 1024);

            // WER commonly writes its event a few seconds after the process disappears.
            List<EventEvidence> evidence = new List<EventEvidence>();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) Thread.Sleep(2000);
                evidence = ReadWindowsEvidence(sessionStart, DateTime.UtcNow.AddMinutes(1));
                if (evidence.Any(e => e.IsCrash || e.IsHang) || attempt == 4) break;
            }
            List<string> sessionDumps = FindSessionDumps(root, pid, sessionStart, ended, hangDump, monitorLog);
            if (sessionDumps.Count == 0 && File.Exists(Path.Combine(root, "SilentExitRegistryBackup.json")))
            {
                for (int attempt = 0; attempt < 5 && sessionDumps.Count == 0; attempt++)
                {
                    Thread.Sleep(1000);
                    sessionDumps = FindSessionDumps(root, pid, sessionStart, ended, hangDump, monitorLog);
                }
            }
            if (sessionDumps.Count == 0) AppendRotating(monitorLog,
                "No matching session dump. PID=" + pid + "; WER or silent-exit capture may not have run.", 5 * 1024 * 1024);

            bool managedException = TailContains(contextPath, "MANAGED_EXCEPTION", 256 * 1024);
            bool nativeCrash = evidence.Any(e => e.IsCrash);
            bool windowsHang = evidence.Any(e => e.IsHang);
            bool isProblem = exitCode != 0 || hangObserved || windowsHang || nativeCrash || managedException ||
                (capture != null && (capture.HungTrigger || capture.ExceptionTrigger));
            if (!isProblem)
            {
                AppendRotating(monitorLog, "Normal GTA shutdown. PID=" + pid + ", exit=" + exitCode, 5 * 1024 * 1024);
                if (capture == null && sessionDumps.Count == 0) CleanupSessionFiles(contextPath, pid);
                else WriteReport(root, "штатное закрытие", pid, exitCode, sessionId, sessionStart,
                    ended, gameDirectory, scriptsDirectory, contextPath, evidence, hangObserved, capture, sessionDumps, monitorLog);
                return 0;
            }

            string category = nativeCrash || (capture != null && capture.ExceptionTrigger) ? "нативный краш" :
                (hangObserved || windowsHang || (capture != null && capture.HungTrigger)) ? "зависание" :
                managedException ? "управляемое исключение" : "неожиданное завершение без установленной причины";
            WriteReport(root, category, pid, exitCode, sessionId, sessionStart, ended,
                gameDirectory, scriptsDirectory, contextPath, evidence, hangObserved, capture, sessionDumps, monitorLog);
            return 0;
        }

        private static void WriteReport(string root, string category, int pid, int exitCode, string sessionId,
            DateTime sessionStart, DateTime ended, string gameDirectory, string scriptsDirectory,
            string contextPath, List<EventEvidence> evidence, bool hangObserved, CaptureSession capture,
            List<string> sessionDumps, string monitorLog)
        {
            string report = BuildReport(category, pid, exitCode, sessionId, sessionStart, ended,
                gameDirectory, scriptsDirectory, contextPath, evidence, hangObserved, capture, sessionDumps);
            string reportDirectory = CreateReportDirectory(root, ended.ToLocalTime());
            string reportPath = Path.Combine(reportDirectory, "CrashReport.txt");
            File.WriteAllText(reportPath, report, new UTF8Encoding(true));
            if (capture != null)
                File.WriteAllText(Path.Combine(capture.DirectoryPath, "CrashReport.txt"), report, new UTF8Encoding(true));
            RotateReports(Path.Combine(root, "CrashReports"), 20);
            AppendRotating(monitorLog, "Report created: " + reportDirectory + " (" + category + ")", 5 * 1024 * 1024);
            CleanupSessionFiles(contextPath, pid);
        }

        private static CaptureSession StartCapture(string root, int pid, string sessionId, string monitorLog)
        {
            if (!File.Exists(Path.Combine(root, "CrashCapture.enabled"))) return null;
            string tool = Path.Combine(root, "Tools", "procdump64.exe");
            if (!File.Exists(tool))
            {
                AppendRotating(monitorLog, "Capture enabled but procdump64.exe is missing.", 5 * 1024 * 1024);
                return null;
            }
            try
            {
                string dumpsRoot = Path.Combine(root, "CrashDumps");
                Directory.CreateDirectory(dumpsRoot);
                string folder = Path.Combine(dumpsRoot, "Capture-" + sessionId);
                Directory.CreateDirectory(folder);
                var capture = new CaptureSession {
                    DirectoryPath = folder,
                    LogPath = Path.Combine(folder, "ProcDump.log")
                };
                File.WriteAllText(Path.Combine(folder, "CaptureInfo.txt"),
                    "PID=" + pid + Environment.NewLine + "Session=" + sessionId + Environment.NewLine +
                    "StartedUtc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine +
                    "Triggers=unhandled exception, hung window, process termination" + Environment.NewLine);
                var start = new ProcessStartInfo {
                    FileName = tool,
                    Arguments = "-accepteula -mm -e -h -t -n 3 " + pid.ToString(CultureInfo.InvariantCulture) + " " + Quote(folder),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = root
                };
                capture.Process = new Process { StartInfo = start };
                capture.Process.OutputDataReceived += (sender, e) => CaptureLine(capture, e.Data);
                capture.Process.ErrorDataReceived += (sender, e) => CaptureLine(capture, e.Data);
                capture.Process.Start();
                capture.Process.BeginOutputReadLine();
                capture.Process.BeginErrorReadLine();
                AppendRotating(monitorLog, "ProcDump attached/requested. PID=" + pid + ", folder=" + folder, 5 * 1024 * 1024);
                return capture;
            }
            catch (Exception ex)
            {
                AppendRotating(monitorLog, "ProcDump start failed: " + ex, 5 * 1024 * 1024);
                return null;
            }
        }

        private static void CaptureLine(CaptureSession capture, string line)
        {
            if (line == null) return;
            lock (capture.Sync)
            {
                if (line.IndexOf("Hung Window", StringComparison.OrdinalIgnoreCase) >= 0) capture.HungTrigger = true;
                if (line.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase) >= 0) capture.ExceptionTrigger = true;
                try { File.AppendAllText(capture.LogPath, line + Environment.NewLine); }
                catch { }
            }
        }

        private static void CompleteCapture(CaptureSession capture, string monitorLog)
        {
            if (capture == null) return;
            try
            {
                if (capture.Process.WaitForExit(120000))
                {
                    capture.Process.WaitForExit();
                    AppendRotating(monitorLog, "ProcDump finished. Exit=" + capture.Process.ExitCode +
                        ", dumps=" + SafeFiles(capture.DirectoryPath, "*.dmp", SearchOption.TopDirectoryOnly).Count(),
                        5 * 1024 * 1024);
                }
                else AppendRotating(monitorLog, "ProcDump did not exit within 120 seconds; inspect " +
                    capture.LogPath, 5 * 1024 * 1024);
            }
            catch (Exception ex) { AppendRotating(monitorLog, "ProcDump completion failed: " + ex, 5 * 1024 * 1024); }
            finally
            {
                capture.Process.Dispose();
                RotateCaptures(Path.GetDirectoryName(capture.DirectoryPath), 3);
            }
        }

        private static void RotateCaptures(string root, int keep)
        {
            try
            {
                foreach (DirectoryInfo directory in new DirectoryInfo(root).GetDirectories("Capture-*")
                    .OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
                    directory.Delete(true);
            }
            catch { }
        }

        private static string WriteHangDump(string root, int pid, string sessionId, string monitorLog)
        {
            string path = Path.Combine(root, "CrashDumps", "Hang", "Hang-" + sessionId + ".dmp");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var target = Process.GetProcessById(pid))
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!NativeMethods.MiniDumpWriteDump(target.Handle, pid, output.SafeFileHandle.DangerousGetHandle(),
                        NativeMethods.MiniDumpWithThreadInfo | NativeMethods.MiniDumpWithUnloadedModules,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                AppendRotating(monitorLog, "Hang dump created: " + path, 5 * 1024 * 1024);
                foreach (string old in SafeFiles(Path.GetDirectoryName(path), "*.dmp", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(SafeLastWrite).Skip(DumpLimit))
                    try { File.Delete(old); } catch { }
                return path;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                AppendRotating(monitorLog, "Hang dump failed: " + ex, 5 * 1024 * 1024);
                return null;
            }
        }

        private static List<string> FindSessionDumps(string root, int pid, DateTime started, DateTime ended,
            string hangDump, string monitorLog)
        {
            string folder = Path.Combine(root, "CrashDumps");
            var matches = new List<string>();
            // WER and Silent Process Exit use different names; only accept the session PID and time window.
            foreach (string file in SafeFiles(folder, "*.dmp", SearchOption.AllDirectories))
            {
                try
                {
                    string name = Path.GetFileName(file);
                    bool pidMatch = IsSessionDumpName(name, pid) ||
                        string.Equals(file, hangDump, StringComparison.OrdinalIgnoreCase);
                    DateTime time = File.GetLastWriteTimeUtc(file);
                    if (pidMatch && time >= started.AddSeconds(-5) && time <= ended.AddMinutes(2)) matches.Add(file);
                }
                catch (Exception ex) { AppendRotating(monitorLog, "Dump inspection failed: " + ex.Message, 5 * 1024 * 1024); }
            }
            return matches.Where(File.Exists).OrderByDescending(SafeLastWrite).ToList();
        }

        private static bool IsSessionDumpName(string name, int pid)
        {
            if (!name.StartsWith("GTA5.exe", StringComparison.OrdinalIgnoreCase)) return false;
            return name.IndexOf("." + pid + ".", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("-(PID-" + pid + ").", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("-" + pid + "-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("_" + pid + "_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        private static string BuildReport(string category, int pid, int exitCode, string sessionId,
            DateTime started, DateTime ended, string gameDirectory, string scriptsDirectory,
            string contextPath, List<EventEvidence> evidence, bool hangObserved, CaptureSession capture,
            List<string> sessionDumps)
        {
            var text = new StringBuilder();
            text.AppendLine("GTA V ModPack crash report");
            text.AppendLine(new string('=', 72));
            text.AppendLine("Category: " + category);
            text.AppendLine("Confidence: " + (evidence.Any(e => e.IsCrash || e.IsHang) ?
                "confirmed by Windows event evidence" : capture != null &&
                (capture.HungTrigger || capture.ExceptionTrigger) ?
                "confirmed by ProcDump trigger" : "inferred from process and mod context"));
            text.AppendLine("Session: " + sessionId);
            text.AppendLine("GTA PID: " + pid);
            text.AppendLine("Started (UTC): " + started.ToString("O"));
            text.AppendLine("Ended (UTC): " + ended.ToString("O"));
            text.AppendLine("Exit code: " + FormatExitCode(exitCode));
            text.AppendLine("60-second hang observed by watcher: " + (hangObserved ? "yes" : "no"));
            text.AppendLine("ProcDump capture: " + (capture == null ? "off" : capture.DirectoryPath));
            text.AppendLine("Session dumps: " + (sessionDumps.Count == 0 ? "none found" : sessionDumps.Count.ToString()));
            foreach (string dump in sessionDumps) text.AppendLine("Session dump: " + dump);
            if (capture != null)
            {
                foreach (string dump in SafeFiles(capture.DirectoryPath, "*.dmp", SearchOption.TopDirectoryOnly))
                    text.AppendLine("Session dump: " + dump);
                text.AppendLine("ProcDump output: " + capture.LogPath);
            }
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
            foreach (string file in SafeFiles(scriptsDirectory, "*.log", SearchOption.TopDirectoryOnly)) files.Add(file);
            string pluginRoot = Path.Combine(scriptsDirectory, "ReloaderPlugins");
            foreach (string file in SafeFiles(scriptsDirectory, "*.log", SearchOption.AllDirectories))
                if (!file.StartsWith(pluginRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) files.Add(file);
            foreach (string name in new[] { "Reloader.log", "compile_errors.txt", "PluginErrors.log", "Development.log",
                "ModdedCamera.log", "SharkRider.log", "RemoveDroppedPeds.log", "FrozenDynamic.log" })
            {
                string path = Path.Combine(pluginRoot, name);
                if (File.Exists(path)) files.Add(path);
            }
            foreach (string relative in new[] { "Logs/PluginErrors.log", "Logs/StorageMigration.log", "Development/Development.log",
                "ModdedCamera/ModdedCamera.log", "SharkRider/SharkRider.log", "RemoveDroppedPeds/RemoveDroppedPeds.log",
                "FrozenDynamic/FrozenDynamic.log", "MenyooStreamer/MenyooStreamer.log", "MenyooStreamer/error.log",
                "Plugins/_System/Diagnostics/Data/Loader/Logs/Reloader.log", "Plugins/_System/Diagnostics/Data/Loader/Logs/compile_errors.txt",
                "Plugins/Core/Data/Logs/PluginErrors.log", "Plugins/Core/Data/Logs/StorageMigration.log",
                "Plugins/Dev/Data/Logs/Development.log",
                "Plugins/Camera/Data/Logs/ModdedCamera.log", "Plugins/Wildlife/Data/Logs/SharkRider.log",
                "Plugins/Cleanup/Data/Logs/RemoveDroppedPeds.log", "Plugins/PedControl/Data/Logs/FrozenDynamic.log",
                "Plugins/Streamer/Data/Logs/MenyooStreamer.log", "Plugins/Streamer/Data/Logs/error.log" })
            {
                string path = Path.Combine(pluginRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) files.Add(path);
            }
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
                            entry.Source.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) ||
                            entry.Source.Equals("Process Exit Monitor", StringComparison.OrdinalIgnoreCase);
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

        private sealed class CaptureSession
        {
            public readonly object Sync = new object();
            public string DirectoryPath;
            public string LogPath;
            public Process Process;
            public bool HungTrigger;
            public bool ExceptionTrigger;
        }

        private static class NativeMethods
        {
            internal const uint Synchronize = 0x00100000;
            internal const uint QueryLimitedInformation = 0x00001000;
            internal const uint MiniDumpWithUnloadedModules = 0x00000020;
            internal const uint MiniDumpWithThreadInfo = 0x00001000;
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("dbghelp.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool MiniDumpWriteDump(IntPtr process, int processId, IntPtr file, uint dumpType,
                IntPtr exception, IntPtr userStream, IntPtr callback);
        }
    }
}
