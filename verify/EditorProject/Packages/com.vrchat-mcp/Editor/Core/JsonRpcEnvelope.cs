using System;
using UnityEngine;

namespace VrcMcp.Core
{
    [Serializable]
    public class JsonRpcError
    {
        public int code;
        public string message;
        public string data;
    }

    /// <summary>
    /// JSON-RPC 2.0 envelope for the bridge protocol.
    /// <c>id == 0</c> means a notification (no response expected).
    /// <c>params</c> and <c>result</c> are raw JSON strings to avoid JsonUtility
    /// nested-object limitations. The Python server always serializes params to a string.
    /// </summary>
    [Serializable]
    public class JsonRpcEnvelope
    {
        public string jsonrpc = "2.0";
        public long id;
        public string method;
        public string @params;
        public string result;
        public JsonRpcError error;

        public bool IsNotification => id == 0;
        public bool HasError => error != null && error.code != 0;

        public static JsonRpcEnvelope Parse(string json)
        {
            JsonRpcEnvelope env;
            try
            {
                env = JsonUtility.FromJson<JsonRpcEnvelope>(json);
            }
            catch (Exception e)
            {
                throw new FormatException("Invalid JSON-RPC envelope: " + json + "\n" + e.Message);
            }
            if (env == null)
                throw new FormatException("Invalid JSON-RPC envelope (null): " + json);
            return env;
        }

        public string Serialize()
        {
            // Ensure payload fields are valid JSON even when empty.
            if (string.IsNullOrEmpty(result)) result = null;
            if (string.IsNullOrEmpty(@params)) @params = null;
            return JsonUtility.ToJson(this);
        }
    }
}