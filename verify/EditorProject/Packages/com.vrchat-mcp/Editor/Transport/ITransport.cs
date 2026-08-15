using System;

namespace VrcMcp.Core
{
    /// <summary>
    /// Transport abstraction. Events are always raised on the Unity main thread.
    /// Implementations must marshal callbacks via MainThreadDispatcher.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Raw JSON-RPC message received from a client (main thread).</summary>
        event Action<string> MessageReceived;

        /// <summary>Fired when all connected clients have disconnected (main thread).</summary>
        event Action ClientDisconnected;

        void Start();
        void Stop();
        bool IsRunning { get; }
        void Send(string json);
        string Name { get; }
        /// <summary>Loopback port the transport listens on, or -1 when inactive.</summary>
        int Port { get; }
        /// <summary>Human-readable health info for get_project_info.</summary>
        string Status { get; }
    }
}