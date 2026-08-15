using System;
using UnityEditor;
using UnityEngine;
using VrcMcp.Batch;
using VrcMcp.Compat;
using VrcMcp.Core;
using VrcMcp.Handshake;
using VrcMcp.Transport;

namespace VrcMcp.Bootstrap
{
    /// <summary>
    /// Bridge lifecycle: picks a transport (auto: MPE if bound, else TcpWs),
    /// binds the registry, writes the channel file, wires disconnect/quit/reload
    /// cleanup. Menu: Tools/VRChat MCP/...
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeBootstrap
    {
        private const string PrefAutoStart = "VrcMcp.AutoStart";
        private const string PrefTransport = "VrcMcp.Transport";

        private static ITransport _transport;
        private static bool _started;

        static BridgeBootstrap()
        {
            // Import workers (AssetImportWorker*, -batchMode) also initialize the plugin
            // and would write bogus channel files + open MPE channels that never serve
            // replies. Never start the bridge outside the interactive editor.
            if (Environment.CommandLine.IndexOf("-batchMode", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (EditorPrefs.GetBool(PrefAutoStart, true))
                StartBridge();
        }

        public static bool IsRunning => _transport != null && _transport.IsRunning;
        public static ITransport Transport => _transport;

        [MenuItem("Tools/VRChat MCP/Start Bridge")]
        public static void StartBridgeMenu() => StartBridge();

        [MenuItem("Tools/VRChat MCP/Stop Bridge")]
        public static void StopBridgeMenu() => StopBridge();

        [MenuItem("Tools/VRChat MCP/Start Bridge", true)]
        public static bool CanStart() => !IsRunning;

        [MenuItem("Tools/VRChat MCP/Stop Bridge", true)]
        public static bool CanStop() => IsRunning;

        public static void StartBridge()
        {
            if (_started) return;
            _started = true;

            EditorApplication.quitting += StopBridge;
            AssemblyReloadEvents.beforeAssemblyReload += StopBridge;

            _transport = CreateTransport();
            if (_transport == null)
            {
                Debug.LogError("[VrcMcp] no transport available; bridge not started");
                return;
            }

            McpToolRegistry.Instance.BindTransport(_transport);

            _transport.ClientDisconnected += () =>
            {
                BatchStateMachine.Instance.EndOnDisconnect();
                Debug.Log("[VrcMcp] all clients disconnected");
            };

            _transport.Start();
            ConsoleCapture.Start();

            WriteChannelFile();
            Debug.Log($"[VrcMcp] bridge started (transport={_transport.Name}, port={_transport.Port})");
        }

        private static ITransport CreateTransport()
        {
            string mode = EditorPrefs.GetString(PrefTransport, "auto").ToLowerInvariant();
            string channelName = ChannelFile.ChannelName;

            if (mode == "mpe" || mode == "auto")
            {
                var mpe = MpeTransport.TryCreate(channelName);
                if (mpe != null)
                {
                    Debug.Log("[VrcMcp] using MPE transport (reflection-bound)");
                    return mpe;
                }
                Debug.LogWarning("[VrcMcp] MPE unavailable (" + (MpeTransport.BindError ?? "unknown") + "); falling back to TcpWs");
            }
            return new TcpWsTransport();
        }

        private static void WriteChannelFile()
        {
            if (_transport == null) return;
            var compat = new System.Text.StringBuilder();
            var statuses = InternalApiRegistry.GetStatus();
            compat.Append('[');
            for (int i = 0; i < statuses.Count; i++)
            {
                if (i > 0) compat.Append(',');
                var s = statuses[i];
                compat.Append("{\"key\":\"").Append(s.Key).Append("\",")
                      .Append("\"available\":").Append(s.Available ? "true" : "false").Append(',')
                      .Append("\"description\":\"").Append(Escape(s.Description)).Append("\",")
                      .Append("\"reason\":\"").Append(Escape(s.FailureReason ?? "")).Append("\"}");
            }
            compat.Append(']');
            ChannelFile.Write(_transport.Port, _transport.Name, compat.ToString());
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        public static void StopBridge()
        {
            if (!_started) return;
            _started = false;

            ConsoleCapture.Stop();
            BatchStateMachine.Instance.EndOnDisconnect();
            ChannelFile.Delete();

            if (_transport != null)
            {
                _transport.Stop();
                _transport.Dispose();
                _transport = null;
            }
        }
    }
}