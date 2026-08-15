using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using VrcMcp.Core;

namespace VrcMcp.Transport
{
    /// <summary>
    /// Experimental MPE transport: binds UnityEditor.MPE.ChannelService via reflection.
    /// Editor hosts the channel; external clients connect over WebSocket to the editor's
    /// global MPE port with the channel name in the URL (exact URL shape is verified
    /// empirically during e2e; see docs/DESIGN.md transport section).
    /// Bindings verified against Unity 6000.4.0a2 (see verify/api-dump2.txt).
    /// </summary>
    public class MpeTransport : ITransport
    {
        public string Name => "mpe";
        public int Port => _getPort != null ? (int)_getPort.Invoke(null, null) : -1;
        public string Status { get; private set; } = "not started";
        public bool IsRunning { get; private set; }

        public event Action<string> MessageReceived;
        public event Action ClientDisconnected;

        private const string ServiceTypeName = "UnityEditor.MPE.ChannelService";

        private static Type _serviceType;
        private static MethodInfo _start;
        private static MethodInfo _stop;
        private static MethodInfo _isRunning;
        private static MethodInfo _getPort;
        private static MethodInfo _getOrCreateChannel;
        private static MethodInfo _closeChannel;
        private static MethodInfo _channelNameToId;
        private static MethodInfo _sendToConnection;
        private static MethodInfo _broadcast;

        private static readonly string[] MissingMembers = { };
        public static string BindError { get; private set; }
        public static bool IsBound { get; private set; }

        private readonly string _channelName;
        private Action _unsubscribe;
        private int _channelId = -1;
        private int _connectionId = -1;
        private bool _hadClient;
        private long _lastActivityTicks;
        private static bool _pollHooked;

        /// <summary>
        /// MPE's GetChannelClientList does not reliably shed dead connections (observed
        /// during e2e: after several connect/disconnect cycles it kept stale entries, so
        /// a "no clients" window was never seen and disconnect was never detected).
        /// Instead we detect disconnect by data activity: the Python client sends a
        /// heartbeat notification every 10s; no activity for this long => client is gone.
        /// </summary>
        private const double DisconnectTimeoutSeconds = 30.0;

        private MpeTransport(string channelName) { _channelName = channelName; }

        public static MpeTransport TryCreate(string channelName)
        {
            if (!EnsureBound()) return null;
            return new MpeTransport(channelName);
        }

        private static bool EnsureBound()
        {
            if (IsBound || BindError != null) return IsBound;
            var editorAsm = typeof(EditorWindow).Assembly;
            _serviceType = editorAsm.GetType(ServiceTypeName);
            if (_serviceType == null) { BindError = ServiceTypeName + " not found"; return false; }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _start = _serviceType.GetMethod("Start", flags, null, Type.EmptyTypes, null);
            _stop = _serviceType.GetMethod("Stop", flags, null, Type.EmptyTypes, null);
            _isRunning = _serviceType.GetMethod("IsRunning", flags, null, Type.EmptyTypes, null);
            _getPort = _serviceType.GetMethod("GetPort", flags, null, Type.EmptyTypes, null);
            _closeChannel = _serviceType.GetMethod("CloseChannel", flags, null, new[] { typeof(string) }, null);
            _channelNameToId = _serviceType.GetMethod("ChannelNameToId", flags, null, new[] { typeof(string) }, null);

            var handlerType = typeof(Action<,>).MakeGenericType(typeof(int), typeof(byte[]));
            _getOrCreateChannel = _serviceType.GetMethod("GetOrCreateChannel", flags, null, new[] { typeof(string), handlerType }, null);
            _broadcast = _serviceType.GetMethod("Broadcast", flags, null, new[] { typeof(int), typeof(string) }, null);
            _sendToConnection = _serviceType.GetMethod("Send", flags, null, new[] { typeof(int), typeof(string) }, null);

            if (_start == null) { BindError = "ChannelService.Start() not found"; return false; }
            if (_getOrCreateChannel == null) { BindError = "ChannelService.GetOrCreateChannel(string, Action<int,byte[]>) not found"; return false; }
            if (_sendToConnection == null) { BindError = "ChannelService.Send(int,string) not found"; return false; }

            IsBound = true;
            return true;
        }

        public void Start()
        {
            if (IsRunning) return;
            if (!IsBound) { Status = "not bound: " + BindError; return; }

            try
            {
                if (!(bool)_isRunning.Invoke(null, null))
                    _start.Invoke(null, null);

                // GetOrCreateChannel appends a handler every call (not idempotent);
                // close first so a restart never stacks duplicate handlers.
                if (_closeChannel != null)
                    _closeChannel.Invoke(null, new object[] { _channelName });

                ActiveInstance = this;
                var handler = Delegate.CreateDelegate(
                    typeof(Action<,>).MakeGenericType(typeof(int), typeof(byte[])),
                    typeof(MpeTransport).GetMethod("OnChannelData", BindingFlags.NonPublic | BindingFlags.Static));
                _unsubscribe = (Action)_getOrCreateChannel.Invoke(null, new object[] { _channelName, handler });
                _channelId = (int)_channelNameToId.Invoke(null, new object[] { _channelName });

                if (!_pollHooked)
                {
                    EditorApplication.update += PollClients;
                    _pollHooked = true;
                }

                IsRunning = true;
                Status = $"mpe port={Port} channel={_channelName} id={_channelId}";
                Debug.Log("[VrcMcp] MPE channel open: " + Status);
            }
            catch (Exception e)
            {
                IsRunning = false;
                Status = "failed: " + e.Message;
                Debug.LogError("[VrcMcp] MPE start failed: " + e);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            Status = "stopped";
            try
            {
                if (ActiveInstance == this) ActiveInstance = null;
                _unsubscribe?.Invoke();
                _unsubscribe = null;
                _closeChannel?.Invoke(null, new object[] { _channelName });
                if (_pollHooked)
                {
                    EditorApplication.update -= PollClients;
                    _pollHooked = false;
                }
                _hadClient = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VrcMcp] MPE stop: " + e.Message);
            }
        }

        public void Send(string json)
        {
            if (!IsRunning) return;
            try
            {
                // Reply to the requesting connection; fall back to channel broadcast
                // (reaches all clients) when no connection is tracked yet.
                if (_connectionId > 0 && _sendToConnection != null)
                    _sendToConnection.Invoke(null, new object[] { _connectionId, json });
                else if (_channelId >= 0 && _broadcast != null)
                    _broadcast.Invoke(null, new object[] { _channelId, json });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VrcMcp] MPE send failed: " + e.Message);
            }
        }

        private static MpeTransport ActiveInstance;

        private static void OnChannelData(int connectionId, byte[] data)
        {
            var self = ActiveInstance;
            if (self == null) return;
            if (connectionId > 0) self._connectionId = connectionId;
            self._lastActivityTicks = DateTime.UtcNow.Ticks;
            self._hadClient = true;
            var json = Encoding.UTF8.GetString(data);
            self.MessageReceived?.Invoke(json);
        }

        private void PollClients()
        {
            if (!IsRunning || !_hadClient) return;
            double idle = (DateTime.UtcNow - new DateTime(_lastActivityTicks)).TotalSeconds;
            if (idle <= DisconnectTimeoutSeconds) return;
            _hadClient = false;
            Debug.Log($"[VrcMcp] MPE client disconnected (no activity for {DisconnectTimeoutSeconds}s)");
            ClientDisconnected?.Invoke();
        }

        public void Dispose() { Stop(); }
    }
}