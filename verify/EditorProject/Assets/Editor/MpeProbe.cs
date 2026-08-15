using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class MpeProbe
{
    private const string ChannelName = "unity-mcp-test";
    private static StringBuilder _log = new StringBuilder();
    private static double _startedAt;
    private static Type _csType;
    private static MethodInfo _isRunning, _start, _getPort, _getAddress, _getOrCreate, _closeChannel, _getClientList;
    private static FieldInfo _clientNameField;
    private static Action _unsub;

    public static void Run()
    {
        var editorAsm = typeof(EditorWindow).Assembly;
        _csType = editorAsm.GetType("UnityEditor.MPE.ChannelService");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        _isRunning = _csType.GetMethod("IsRunning", flags);
        _start = _csType.GetMethod("Start", flags);
        _getPort = _csType.GetMethod("GetPort", flags);
        _getAddress = _csType.GetMethod("GetAddress", flags);
        _closeChannel = _csType.GetMethod("CloseChannel", flags, null, new[] { typeof(string) }, null);
        _getClientList = _csType.GetMethod("GetChannelClientList", flags);
        var handlerType = typeof(Action<,>).MakeGenericType(typeof(int), typeof(byte[]));
        _getOrCreate = _csType.GetMethod("GetOrCreateChannel", flags, null, new[] { typeof(string), handlerType }, null);
        var infoType = editorAsm.GetType("UnityEditor.MPE.ChannelClientInfo");
        if (infoType != null)
            _clientNameField = infoType.GetField("m_ChannelName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (_isRunning == null || _start == null || _getPort == null || _getAddress == null ||
            _getOrCreate == null || _closeChannel == null || _getClientList == null)
        {
            _log.AppendLine("BIND FAILURE");
            WriteOut();
            EditorApplication.Exit(1);
            return;
        }

        bool running = (bool)_isRunning.Invoke(null, null);
        if (!running) _start.Invoke(null, null);
        _log.AppendLine("IsRunning=" + (bool)_isRunning.Invoke(null, null));
        _log.AppendLine("Port=" + _getPort.Invoke(null, null));
        _log.AppendLine("Address=" + _getAddress.Invoke(null, null));

        var handler = Delegate.CreateDelegate(handlerType,
            typeof(MpeProbe).GetMethod("OnData", BindingFlags.NonPublic | BindingFlags.Static));
        _unsub = (Action)_getOrCreate.Invoke(null, new object[] { ChannelName, handler });
        _log.AppendLine("channel registered: " + ChannelName);

        _startedAt = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
        Debug.Log("[MpeProbe] running; waiting 60s for external clients");
    }

    private static void OnData(int channelId, byte[] data)
    {
        _log.AppendLine("RECV channelId=" + channelId + " payload=" + Encoding.UTF8.GetString(data));
        WriteOut();
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup - _startedAt > 60)
        {
            EditorApplication.update -= Tick;
            _log.AppendLine("TIMEOUT; clientCount=" + ClientCount());
            _unsub?.Invoke();
            _closeChannel.Invoke(null, new object[] { ChannelName });
            WriteOut();
            Debug.Log("[MpeProbe] done");
            EditorApplication.Exit(0);
        }
    }

    private static int ClientCount()
    {
        try
        {
            var list = (Array)_getClientList.Invoke(null, null);
            if (list == null) return 0;
            int count = 0;
            foreach (var info in list)
            {
                var name = _clientNameField?.GetValue(info) as string;
                if (name == ChannelName) count++;
            }
            return count;
        }
        catch (Exception e) { _log.AppendLine("clientlist error: " + e.Message); return -1; }
    }

    private static void WriteOut()
    {
        var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "mpe-probe.txt");
        File.WriteAllText(outPath, _log.ToString());
    }
}