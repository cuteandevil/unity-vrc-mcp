using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using VrcMcp.Core;

namespace VrcMcp.Transport
{
    /// <summary>
    /// Hand-rolled minimal WebSocket server on loopback TCP. Version-proof:
    /// no dependency on the experimental UnityEditor.MPE API. Supports text
    /// frames, masking (client→server), ping/pong and close handshake.
    /// </summary>
    public class TcpWsTransport : ITransport
    {
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxFrameSize = 64 * 1024 * 1024;

        private TcpListener _listener;
        private Thread _acceptThread;
        private readonly object _clientsLock = new object();
        private readonly List<ClientConnection> _clients = new List<ClientConnection>();
        private volatile bool _running;

        public event Action<string> MessageReceived;
        public event Action ClientDisconnected;

        public string Name => "tcpws";
        public int Port { get; private set; } = -1;
        public string Status => _running ? $"listening on 127.0.0.1:{Port}" : "stopped";
        public bool IsRunning => _running;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "vrcmcp-accept" };
            _acceptThread.Start();
            Debug.Log($"[VrcMcp] TcpWsTransport listening on 127.0.0.1:{Port}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); } catch { }
            List<ClientConnection> snapshot;
            lock (_clientsLock) { snapshot = new List<ClientConnection>(_clients); _clients.Clear(); }
            foreach (var c in snapshot) c.Close();
            Port = -1;
        }

        public void Send(string json)
        {
            List<ClientConnection> snapshot;
            lock (_clientsLock) { snapshot = new List<ClientConnection>(_clients); }
            foreach (var c in snapshot) c.SendText(json);
        }

        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }

                var conn = new ClientConnection(client, OnClientMessage, OnClientClosed);
                lock (_clientsLock) _clients.Add(conn);
                var t = new Thread(conn.Run) { IsBackground = true, Name = "vrcmcp-client" };
                t.Start();
            }
        }

        private void OnClientMessage(string json)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                try { MessageReceived?.Invoke(json); }
                catch (Exception e) { Debug.LogException(e); }
            });
        }

        private void OnClientClosed(ClientConnection conn)
        {
            lock (_clientsLock) _clients.Remove(conn);
            int remaining;
            lock (_clientsLock) remaining = _clients.Count;
            if (remaining == 0)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    try { ClientDisconnected?.Invoke(); }
                    catch (Exception e) { Debug.LogException(e); }
                });
            }
        }

        private sealed class ClientConnection
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly Action<string> _onMessage;
            private readonly Action<ClientConnection> _onClosed;
            private readonly object _writeLock = new object();

            public ClientConnection(TcpClient client, Action<string> onMessage, Action<ClientConnection> onClosed)
            {
                _client = client;
                _stream = client.GetStream();
                _onMessage = onMessage;
                _onClosed = onClosed;
            }

            public void Run()
            {
                try
                {
                    if (!Handshake()) return;
                    ReadLoop();
                }
                catch (Exception) { /* connection dropped */ }
                finally
                {
                    Close();
                    _onClosed?.Invoke(this);
                }
            }

            private bool Handshake()
            {
                var buffer = new byte[8192];
                var req = new MemoryStream();
                string key = null;
                while (true)
                {
                    int n = _stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) return false;
                    req.Write(buffer, 0, n);
                    string text = Encoding.ASCII.GetString(req.ToArray());
                    int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        string headers = text.Substring(0, headerEnd);
                        foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                                key = line.Substring(line.IndexOf(':') + 1).Trim();
                        }
                        break;
                    }
                    if (req.Length > 64 * 1024) return false;
                }
                if (string.IsNullOrEmpty(key)) return false;

                string accept;
                using (var sha1 = SHA1.Create())
                {
                    accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsGuid)));
                }
                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                WriteRaw(Encoding.ASCII.GetBytes(response));
                return true;
            }

            private void ReadLoop()
            {
                while (true)
                {
                    if (!TryReadFrame(out int opcode, out byte[] payload, out bool fin)) return;
                    switch (opcode)
                    {
                        case 0x1: // text
                            if (fin)
                                _onMessage?.Invoke(Encoding.UTF8.GetString(payload));
                            break;
                        case 0x8: // close
                            WriteFrame(0x8, payload);
                            return;
                        case 0x9: // ping
                            WriteFrame(0xA, payload);
                            break;
                        case 0xA: // pong
                            break;
                    }
                }
            }

            private bool TryReadFrame(out int opcode, out byte[] payload, out bool fin)
            {
                opcode = 0;
                payload = null;
                fin = false;

                byte[] header = ReadExactly(2);
                if (header == null) return false;
                fin = (header[0] & 0x80) != 0;
                opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long len = header[1] & 0x7F;

                if (len == 126)
                {
                    var b = ReadExactly(2);
                    if (b == null) return false;
                    len = (b[0] << 8) | b[1];
                }
                else if (len == 127)
                {
                    var b = ReadExactly(8);
                    if (b == null) return false;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | b[i];
                }
                if (len < 0 || len > MaxFrameSize) return false;

                byte[] mask = null;
                if (masked)
                {
                    mask = ReadExactly(4);
                    if (mask == null) return false;
                }

                payload = ReadExactly((int)len);
                if (payload == null) return false;
                if (masked)
                    for (int i = 0; i < payload.Length; i++)
                        payload[i] ^= mask[i % 4];
                return true;
            }

            private byte[] ReadExactly(int count)
            {
                var buf = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = _stream.Read(buf, read, count - read);
                    if (n <= 0) return null;
                    read += n;
                }
                return buf;
            }

            public void SendText(string text) => WriteFrame(0x1, Encoding.UTF8.GetBytes(text));

            private void WriteFrame(int opcode, byte[] payload)
            {
                try
                {
                    lock (_writeLock)
                    {
                        var header = new byte[10];
                        header[0] = (byte)(0x80 | opcode);
                        int headerLen;
                        if (payload.Length < 126)
                        {
                            header[1] = (byte)payload.Length;
                            headerLen = 2;
                        }
                        else if (payload.Length <= 0xFFFF)
                        {
                            header[1] = 126;
                            header[2] = (byte)(payload.Length >> 8);
                            header[3] = (byte)(payload.Length & 0xFF);
                            headerLen = 4;
                        }
                        else
                        {
                            header[1] = 127;
                            ulong l = (ulong)payload.Length;
                            for (int i = 0; i < 8; i++)
                                header[2 + i] = (byte)(l >> (56 - i * 8));
                            headerLen = 10;
                        }
                        _stream.Write(header, 0, headerLen);
                        if (payload.Length > 0) _stream.Write(payload, 0, payload.Length);
                    }
                }
                catch { /* socket gone */ }
            }

            private void WriteRaw(byte[] data)
            {
                lock (_writeLock) _stream.Write(data, 0, data.Length);
            }

            public void Close()
            {
                try { _client.Close(); } catch { }
            }
        }
    }
}