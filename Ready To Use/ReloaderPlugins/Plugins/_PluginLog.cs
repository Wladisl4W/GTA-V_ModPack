// Общий троттлинг-логгер для плагинов без собственного лога.
// Пишет в scripts\ReloaderPlugins\PluginErrors.log.
// Один и тот же источник ошибки логируется не чаще 1 раза в 3 секунды,
// чтобы кадровые исключения не спамили диск.
using System;
using System.Collections.Generic;
using System.IO;

namespace PluginLogging
{
    public static class PluginLog
    {
        private static readonly string LogFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "PluginErrors.log");

        private static readonly object LockObj = new object();
        private static readonly Dictionary<string, int> LastWrite = new Dictionary<string, int>();
        private const int CooldownMs = 3000;

        public static void Error(string context, string message)
        {
            Write(context + (string.IsNullOrEmpty(message) ? "" : ": " + message));
        }

        public static void Error(string context, Exception ex)
        {
            Error(context, ex == null ? "" : ex.GetType().Name + ": " + ex.Message);
        }

        private static void Write(string line)
        {
            try
            {
                string key = line.Length > 60 ? line.Substring(0, 60) : line;
                int now = Environment.TickCount;
                lock (LockObj)
                {
                    int last;
                    if (LastWrite.TryGetValue(key, out last))
                    {
                        if ((uint)(now - last) < CooldownMs) return;
                    }
                    LastWrite[key] = now;
                    if (LastWrite.Count > 200) LastWrite.Clear();
                }

                var dir = Path.GetDirectoryName(LogFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(LogFile,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + line + "\n");
            }
            catch
            {
            }
        }
    }
}
