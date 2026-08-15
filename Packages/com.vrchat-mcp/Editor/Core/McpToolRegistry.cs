using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VrcMcp.Batch;

namespace VrcMcp.Core
{
    /// <summary>
    /// Discovers [McpTool] static methods and dispatches JSON-RPC requests to them.
    /// Transport-agnostic: only talks to ITransport + JsonRpcEnvelope.
    /// </summary>
    public class McpToolRegistry
    {
        public delegate string ToolExecutor(string argsJson, McpToolContext ctx);

        private sealed class ToolEntry
        {
            public string Name;
            public string Description;
            public ToolExecutor Executor;
        }

        private readonly Dictionary<string, ToolEntry> _tools = new Dictionary<string, ToolEntry>();
        private ITransport _transport;

        public static McpToolRegistry Instance { get; } = new McpToolRegistry();

        public IReadOnlyCollection<string> ToolNames => _tools.Keys;
        public bool HasTransport => _transport != null;

        [InitializeOnLoadMethod]
        private static void Bootstrap()
        {
            Instance.DiscoverTools();
        }

        public void DiscoverTools()
        {
            _tools.Clear();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsMcpAssembly(assembly)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                    if (types == null) continue;
                }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var attr = method.GetCustomAttribute<McpToolAttribute>();
                        if (attr == null) continue;
                        var del = (ToolExecutor)Delegate.CreateDelegate(typeof(ToolExecutor), method);
                        Register(attr.Name, attr.Description, del);
                    }
                }
            }
            Debug.Log($"[VrcMcp] discovered {_tools.Count} tools");
        }

        private static bool IsMcpAssembly(Assembly asm)
        {
            var name = asm.GetName().Name;
            return name != null && name.IndexOf("VrcMcp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void Register(string name, string description, ToolExecutor executor)
        {
            _tools[name] = new ToolEntry { Name = name, Description = description, Executor = executor };
        }

        public void BindTransport(ITransport transport)
        {
            if (_transport != null)
                _transport.MessageReceived -= OnMessage;
            _transport = transport;
            _transport.MessageReceived += OnMessage;
        }

        /// <summary>Called on the main thread with a raw client JSON message.</summary>
        public void OnMessage(string json)
        {
            JsonRpcEnvelope request;
            try
            {
                request = JsonRpcEnvelope.Parse(json);
            }
            catch (Exception e)
            {
                SendError(0, -32700, "Parse error", e.Message);
                return;
            }

            if (string.IsNullOrEmpty(request.method))
            {
                SendError(request.id, -32600, "Invalid Request", "Missing method");
                return;
            }

            if (request.IsNotification)
            {
                if (_tools.ContainsKey(request.method))
                {
                    try
                    {
                        var ctx = new McpToolContext { ToolName = request.method };
                        _tools[request.method].Executor(request.@params ?? "{}", ctx);
                        CloseUndoGroupOutsideBatch();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
                return;
            }

            if (!_tools.TryGetValue(request.method, out var entry))
            {
                SendError(request.id, -32601, "Unknown tool", request.method);
                return;
            }

            var response = new JsonRpcEnvelope { id = request.id };
            try
            {
                var ctx = new McpToolContext { ToolName = request.method };
                var result = entry.Executor(request.@params ?? "{}", ctx);
                response.result = string.IsNullOrEmpty(result) ? "{}" : result;
                CloseUndoGroupOutsideBatch();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                response.error = new JsonRpcError
                {
                    code = -32603,
                    message = e.Message,
                    data = e.ToString()
                };
            }
            Send(response);
        }

        /// <summary>After a successful tool call outside an explicit batch, flush the
        /// programmatic RecordObject changes and open a fresh undo group. This is
        /// what actually implements the documented "one tool call = one undo group"
        /// default semantics: without it every batch-less tool call lands in the
        /// same open group and a single undo rolls back the whole session's edits
        /// (the mouse-up flush point that normally closes groups never fires for
        /// programmatic edits - see DESIGN §33/§0). Inside a batch the group stays
        /// open until BatchStateMachine.End() collapses it.</summary>
        private static void CloseUndoGroupOutsideBatch()
        {
            if (BatchStateMachine.Instance.Phase != BatchPhase.Closed) return;
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
        }

        public void SendError(long id, int code, string message, string data)
        {
            Send(new JsonRpcEnvelope
            {
                id = id,
                error = new JsonRpcError { code = code, message = message, data = data }
            });
        }

        private void Send(JsonRpcEnvelope envelope)
        {
            if (_transport == null || !_transport.IsRunning) return;
            try { _transport.Send(envelope.Serialize()); }
            catch (Exception e) { Debug.LogWarning($"[VrcMcp] send failed: {e.Message}"); }
        }
    }
}