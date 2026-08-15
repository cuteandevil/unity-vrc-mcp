using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VrcMcp.Compat
{
    /// <summary>
    /// Public-API console capture (primary source for get_console_logs).
    /// Keeps a bounded ring buffer of recent log messages.
    /// </summary>
    public static class ConsoleCapture
    {
        public sealed class Entry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public double time;
        }

        private const int MaxEntries = 2000;
        private static readonly List<Entry> Buffer = new List<Entry>();
        private static bool _active;

        public static void Start()
        {
            if (_active) return;
            _active = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void Stop()
        {
            if (!_active) return;
            _active = false;
            Application.logMessageReceivedThreaded -= OnLog;
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            lock (Buffer)
            {
                Buffer.Add(new Entry { message = message, stackTrace = stackTrace, type = type, time = EditorApplication.timeSinceStartup });
                if (Buffer.Count > MaxEntries)
                    Buffer.RemoveRange(0, Buffer.Count - MaxEntries);
            }
        }

        public static List<Entry> Recent(int maxCount)
        {
            lock (Buffer)
            {
                if (Buffer.Count <= maxCount) return new List<Entry>(Buffer);
                return Buffer.GetRange(Buffer.Count - maxCount, maxCount);
            }
        }
    }
}