using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Process = System.Diagnostics.Process;

namespace VrcMcp.Handshake
{
    /// <summary>
    /// Channel-file handshake: the bridge writes <project>/.unity-mcp/channel-{pid}.json
    /// with the ephemeral OS-assigned port; the Python server picks the file whose pid
    /// is alive. The plugin deletes its own file on quit / assembly reload and prunes
    /// stale files from dead processes on startup.
    /// </summary>
    public static class ChannelFile
    {
        public const string DirName = ".unity-mcp";

        private static string _activePath;

        public static string ProjectDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string ChannelDir => Path.Combine(ProjectDir, DirName);

        public static string ChannelName
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var ch in Application.productName)
                {
                    if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                    else sb.Append('_');
                }
                if (sb.Length == 0) sb.Append("project");
                if (sb.Length > 48) sb.Length = 48;
                return "unity-mcp-" + sb;
            }
        }

        public static string ActivePath => _activePath;

        public static void Write(int port, string transport, string statusJson)
        {
            try
            {
                Directory.CreateDirectory(ChannelDir);
                PruneStale();

                var pid = Process.GetCurrentProcess().Id;
                var payload = new StringBuilder();
                payload.Append('{');
                payload.Append("\"channelName\":\"").Append(ChannelName).Append("\",");
                payload.Append("\"port\":").Append(port).Append(',');
                payload.Append("\"protocol\":\"ws\",");
                payload.Append("\"projectPath\":\"").Append(Escape(ProjectDir)).Append("\",");
                payload.Append("\"projectName\":\"").Append(Escape(Application.productName)).Append("\",");
                payload.Append("\"unityVersion\":\"").Append(Escape(Application.unityVersion)).Append("\",");
                payload.Append("\"transport\":\"").Append(Escape(transport)).Append("\",");
                payload.Append("\"pid\":").Append(pid).Append(',');
                payload.Append("\"startedAt\":\"").Append(Escape(DateTime.UtcNow.ToString("O"))).Append('"');
                if (!string.IsNullOrEmpty(statusJson))
                    payload.Append(",\"compatStatus\":").Append(statusJson);
                payload.Append('}');

                var path = Path.Combine(ChannelDir, $"channel-{pid}.json");
                File.WriteAllText(path, payload.ToString(), new UTF8Encoding(false));
                _activePath = path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VrcMcp] failed to write channel file: " + e.Message);
            }
        }

        public static void Delete()
        {
            if (_activePath == null) return;
            try { if (File.Exists(_activePath)) File.Delete(_activePath); }
            catch { }
            _activePath = null;
        }

        /// <summary>Removes channel-*.json files whose pid is no longer alive.</summary>
        public static void PruneStale()
        {
            try
            {
                if (!Directory.Exists(ChannelDir)) return;
                foreach (var file in Directory.GetFiles(ChannelDir, "channel-*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var pidStr = name.StartsWith("channel-") ? name.Substring("channel-".Length) : null;
                        if (pidStr == null) continue;
                        if (!int.TryParse(pidStr, out int pid)) continue;
                        if (pid == Process.GetCurrentProcess().Id) continue;
                        Process.GetProcessById(pid); // throws if dead
                    }
                    catch
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VrcMcp] prune failed: " + e.Message);
            }
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }
}