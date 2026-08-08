using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GTA;
using GTA.Native;

namespace ModdedCamera
{
    public static class Logger
    {
        private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "ModdedCamera.log");
        private static readonly object _lockObj = new object();
        private static readonly Queue<string> _logBuffer = new Queue<string>(256);
        private static readonly object _bufferLock = new object();
        private static System.Threading.Timer _flushTimer;
        private const int FLUSH_INTERVAL_MS = 2000;
        private const int MAX_BUFFER_SIZE = 100;

        static Logger()
        {
            _flushTimer = new System.Threading.Timer(FlushBuffer, null, FLUSH_INTERVAL_MS, FLUSH_INTERVAL_MS);
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Error(Exception ex, string context)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(context))
            {
                sb.Append(context);
                sb.Append(": ");
            }
            sb.Append(ex.GetType().Name);
            sb.Append(": ");
            sb.Append(ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.Append("\nStack Trace: ");
                sb.Append(ex.StackTrace);
            }
            Exception inner = ex.InnerException;
            while (inner != null)
            {
                sb.Append("\n  Inner Exception: ");
                sb.Append(inner.GetType().Name);
                sb.Append(": ");
                sb.Append(inner.Message);
                if (!string.IsNullOrEmpty(inner.StackTrace))
                {
                    sb.Append("\n  Stack Trace: ");
                    sb.Append(inner.StackTrace);
                }
                inner = inner.InnerException;
            }
            Write("ERROR", sb.ToString());
        }

        public static void Debug(string message)
        {
#if DEBUG
            Write("DEBUG", message);
#endif
        }

        private static void Write(string level, string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + message;
                lock (_bufferLock)
                {
                    _logBuffer.Enqueue(line);
                    if (_logBuffer.Count >= MAX_BUFFER_SIZE)
                    {
                        FlushBufferInternal();
                    }
                }
            }
            catch
            {
            }
        }

        private static void FlushBuffer(object state)
        {
            FlushBufferInternal();
        }

        private static void FlushBufferInternal()
        {
            try
            {
                List<string> linesToFlush;
                lock (_bufferLock)
                {
                    if (_logBuffer.Count == 0) return;
                    linesToFlush = new List<string>(_logBuffer.Count);
                    while (_logBuffer.Count > 0)
                    {
                        linesToFlush.Add(_logBuffer.Dequeue());
                    }
                }
                if (linesToFlush.Count > 0)
                {
                    lock (_lockObj)
                    {
                        File.AppendAllLines(LogFile, linesToFlush);
                    }
                }
            }
            catch
            {
            }
        }

        public static void Flush()
        {
            FlushBufferInternal();
        }
    }

    public class Timer
    {
        public bool Enabled { get; set; }
        public int Interval { get; set; }
        public int Waiter { get; set; }

        public Timer(int interval)
        {
            this.Interval = interval;
            this.Waiter = 0;
            this.Enabled = false;
        }

        public Timer()
        {
            this.Interval = 0;
            this.Waiter = 0;
            this.Enabled = false;
        }

        public void Stop()
        {
            this.Enabled = false;
        }

        public void Start()
        {
            unchecked
            {
                this.Waiter = (int)(Utils.NowMs() + this.Interval);
            }
            this.Enabled = true;
        }

        public void Reset()
        {
            unchecked
            {
                this.Waiter = (int)(Utils.NowMs() + this.Interval);
            }
        }

        public bool Check()
        {
            if (!Enabled) return false;
            long current = Utils.NowMs();
            long target = (long)this.Waiter;
            return current >= target;
        }
    }

    public static class NativeHashes
    {
        public const Hash UNDO_SCREEN_FADE = unchecked((Hash)(-3104983138485256141L));
        public const Hash RENDER_SCRIPT_CAMS = Hash.RENDER_SCRIPT_CAMS;
        public const Hash SET_FOCUS_AREA = (Hash)658611830838489950L;
        public const Hash DRAW_MARKER = (Hash)2902427857584726153L;
        public const Hash DRAW_MARKER_SPRITE = unchecked((Hash)(-4939229729199161819L));
        public const Hash GET_DISABLED_CONTROL_NORMAL = unchecked((Hash)(-2783653480577029081L));
        public const Hash IS_DISABLED_CONTROL_PRESSED = (Hash)6342219533232326959L;
        public const Hash GET_CONTROL_NORMAL = unchecked((Hash)(-2783653480577029081L));

        // Native function hashes (raw values not in SHVDN3 Hash enum)
        public const Hash GET_CONTROL_VALUE = unchecked((Hash)(-1424092350868114077L));
        public const Hash GET_CONTROL_ACTION_NAME = (Hash)331533201183454215L;
    }
}
