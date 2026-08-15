using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VrcMcp.Compat;
using VrcMcp.Core;
using Newtonsoft.Json.Linq;

namespace VrcMcp.Tools
{
    public static class ConsoleTools
    {
        [McpTool("get_console_logs",
            "Recent Unity console messages. Params: {\"max\":50,\"level\":\"all|log|warning|error\",\"includeHistory\":false}. " +
            "Live capture is public-API based; historical read is best-effort via reflection.")]
        public static string GetConsoleLogs(string argsJson, McpToolContext ctx)
        {
            int max = 50;
            string level = "all";
            bool includeHistory = false;
            try
            {
                int i = argsJson.IndexOf("\"max\"", System.StringComparison.Ordinal);
                if (i >= 0)
                {
                    int colon = argsJson.IndexOf(':', i + 5);
                    int end = colon + 1;
                    while (end < argsJson.Length && (argsJson[end] == ' ' || argsJson[end] == '\t')) end++;
                    int start = end;
                    while (end < argsJson.Length && char.IsDigit(argsJson[end])) end++;
                    if (end > start) max = int.Parse(argsJson.Substring(start, end - start));
                }
                int j = argsJson.IndexOf("\"level\"", System.StringComparison.Ordinal);
                if (j >= 0)
                {
                    int colon = argsJson.IndexOf(':', j + 7);
                    int start = argsJson.IndexOf('"', colon + 1);
                    int end = start > 0 ? argsJson.IndexOf('"', start + 1) : -1;
                    if (end > start) level = argsJson.Substring(start + 1, end - start - 1);
                }
                if (argsJson.IndexOf("\"includeHistory\":true", System.StringComparison.Ordinal) >= 0)
                    includeHistory = true;
            }
            catch { }
            if (max < 1) max = 1;
            if (max > 500) max = 500;

            var root = new JObject();
            var entries = new JArray();
            foreach (var e in ConsoleCapture.Recent(max))
            {
                if (!Matches(e.type, level)) continue;
                entries.Add(WriteEntry(-1, e.type.ToString(), e.message, e.stackTrace));
            }

            var hist = InternalApiRegistry.Get("log_entries");
            bool historyAvailable = hist != null && hist.Available;
            if (includeHistory && historyAvailable)
            {
                var history = InternalApiRegistry.ReadHistory(max);
                if (history != null)
                {
                    foreach (var h in history)
                    {
                        var lt = ModeToLogType(h.mode);
                        if (!Matches(lt, level)) continue;
                        entries.Add(WriteEntry(-2, lt.ToString(), h.message, h.stackTrace));
                    }
                }
            }
            root["entries"] = entries;
            root["historyAvailable"] = historyAvailable;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject WriteEntry(long time, string type, string message, string stackTrace)
        {
            var e = new JObject();
            e["time"] = time;
            e["type"] = type;
            e["message"] = message;
            if (!string.IsNullOrEmpty(stackTrace))
                e["stackTrace"] = stackTrace;
            return e;
        }

        private static bool Matches(LogType type, string level)
        {
            switch (level)
            {
                case "error": return type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
                case "warning": return type == LogType.Warning;
                case "log": return type == LogType.Log;
                default: return true;
            }
        }

        private static LogType ModeToLogType(int mode)
        {
            // mode is a bitmask: 1 << (int)LogType
            if ((mode & (1 << (int)LogType.Error)) != 0) return LogType.Error;
            if ((mode & (1 << (int)LogType.Assert)) != 0) return LogType.Assert;
            if ((mode & (1 << (int)LogType.Warning)) != 0) return LogType.Warning;
            if ((mode & (1 << (int)LogType.Exception)) != 0) return LogType.Exception;
            return LogType.Log;
        }
    }
}