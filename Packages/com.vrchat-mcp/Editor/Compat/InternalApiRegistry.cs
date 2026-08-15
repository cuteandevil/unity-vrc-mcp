using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace VrcMcp.Compat
{
    /// <summary>
    /// Central registry for reflection access to internal/experimental Unity APIs.
    /// Every entry resolves at startup; failures are surfaced (not swallowed)
    /// via GetStatus() and get_project_info.compat[].
    /// Public APIs are preferred; reflection is only an enhancement layer.
    /// </summary>
    public static class InternalApiRegistry
    {
        public sealed class ApiEntry
        {
            public string Key;
            public string Description;
            public bool Available;
            public string FailureReason;
        }

        private static readonly List<ApiEntry> Entries = new List<ApiEntry>();
        private static readonly Dictionary<string, ApiEntry> ByKey = new Dictionary<string, ApiEntry>();
        private static bool _initialized;

        [InitializeOnLoadMethod]
        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            Register("mpe.channel_service", "UnityEditor.MPE.ChannelService (experimental transport)", ResolveMpe());
            Register("log_entries", "UnityEditor.LogEntries (historical console read)", ResolveLogEntries());
        }

        public static void Register(string key, string description, string failureReason)
        {
            var entry = new ApiEntry
            {
                Key = key,
                Description = description,
                Available = failureReason == null,
                FailureReason = failureReason
            };
            Entries.Add(entry);
            ByKey[key] = entry;
        }

        public static ApiEntry Get(string key) => ByKey.TryGetValue(key, out var e) ? e : null;

        public static List<ApiEntry> GetStatus()
        {
            EnsureInitialized();
            return new List<ApiEntry>(Entries);
        }

        // ---- MPE ----

        private static string ResolveMpe()
        {
            var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.MPE.ChannelService");
            if (type == null) return "type UnityEditor.MPE.ChannelService not found in UnityEditor assembly";
            var flags = BindingFlags.Public | BindingFlags.Static;
            if (type.GetMethod("StartChannel", flags, null, new[] { typeof(string) }, null) == null)
                return "StartChannel(string) not found";
            if (type.GetMethod("CloseChannel", flags, null, new[] { typeof(string) }, null) == null)
                return "CloseChannel(string) not found";
            return null;
        }

        // ---- LogEntries (historical console) ----

        private static Type _logEntriesType;
        private static MethodInfo _startGettingEntries;
        private static MethodInfo _getCount;
        private static MethodInfo _getEntryInternal;
        private static MethodInfo _endGettingEntries;
        private static Type _logEntryType;
        private static FieldInfo _entryMessageField;
        private static FieldInfo _entryCallstackStartField;
        private static FieldInfo _entryModeField;

        private static string ResolveLogEntries()
        {
            var editorAsm = typeof(EditorWindow).Assembly;
            _logEntriesType = editorAsm.GetType("UnityEditor.LogEntries");
            if (_logEntriesType == null) return "UnityEditor.LogEntries not found";
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _startGettingEntries = _logEntriesType.GetMethod("StartGettingEntries", flags);
            _getCount = _logEntriesType.GetMethod("GetCount", flags);
            _getEntryInternal = _logEntriesType.GetMethod("GetEntryInternal", flags);
            _endGettingEntries = _logEntriesType.GetMethod("EndGettingEntries", flags);
            if (_startGettingEntries == null || _getCount == null || _getEntryInternal == null || _endGettingEntries == null)
                return "LogEntries method set incomplete (Start/Count/GetEntryInternal/End)";

            _logEntryType = editorAsm.GetType("UnityEditor.LogEntry");
            if (_logEntryType == null) return "UnityEditor.LogEntry not found";
            var entryFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _entryMessageField = _logEntryType.GetField("message", entryFlags);
            _entryCallstackStartField = _logEntryType.GetField("callstackTextStartUTF16", entryFlags);
            _entryModeField = _logEntryType.GetField("mode", entryFlags);
            if (_entryMessageField == null) return "LogEntry.message field not found";
            return null;
        }

        /// <summary>Reads the tail of the editor console. Returns null when unavailable.</summary>
        public static List<ConsoleEntry> ReadHistory(int maxCount)
        {
            if (!ByKey.TryGetValue("log_entries", out var entry) || !entry.Available)
                return null;
            try
            {
                var result = new List<ConsoleEntry>();
                _startGettingEntries.Invoke(null, null);
                int count = (int)_getCount.Invoke(null, null);
                int start = Math.Max(0, count - maxCount);
                for (int i = start; i < count; i++)
                {
                    var entryObj = Activator.CreateInstance(_logEntryType);
                    object[] args = { i, entryObj };
                    if ((bool)_getEntryInternal.Invoke(null, args))
                    {
                        entryObj = args[1];
                        var e = new ConsoleEntry
                        {
                            message = (string)_entryMessageField.GetValue(entryObj),
                            mode = (int)(_entryModeField?.GetValue(entryObj) ?? 0)
                        };
                        int stackStart = _entryCallstackStartField != null
                            ? (int)_entryCallstackStartField.GetValue(entryObj) : 0;
                        if (stackStart > 0 && stackStart < e.message.Length)
                        {
                            e.stackTrace = e.message.Substring(stackStart);
                            e.message = e.message.Substring(0, stackStart);
                        }
                        result.Add(e);
                    }
                }
                _endGettingEntries.Invoke(null, null);
                return result;
            }
            catch (Exception e)
            {
                entry.Available = false;
                entry.FailureReason = "runtime failure: " + e.Message;
                Debug.LogWarning("[VrcMcp] LogEntries runtime failure: " + e);
                return null;
            }
        }
    }

    public struct ConsoleEntry
    {
        public string message;
        public string stackTrace;
        /// <summary>Bitmask: 1&lt;&lt;LogType (0=Error,1=Assert,2=Warning,3=Log,4=Exception).</summary>
        public int mode;
    }
}