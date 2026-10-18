using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ForthWall.Core;
using ForthWall.Protocol;
using Google.Protobuf;

namespace ForthWall.Transport
{
    // Small RFC 6455 binary-only server. Managed TCP avoids HTTP.sys under Proton.
    // Authentication precedes upgrade. Header, frame, fragment, message, connection,
    // time, and output bounds are enforced before payload allocations.
    public sealed class WebSocketServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Dispatcher dispatcher;
        private readonly byte[] authorization;
        private readonly ConcurrentDictionary<Peer, byte> peers = new ConcurrentDictionary<Peer, byte>();
        private int stopped;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public WebSocketServer(Dispatcher dispatcher, string token, int port = 18751)
        {
            if (token.Length < 32 || token.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid token configuration.");
            this.dispatcher = dispatcher;
            authorization = Encoding.ASCII.GetBytes("Bearer " + token);
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(16);
            Task.Run((Action)Accept);
        }
        private void Accept()
        {
            try
            {
                while (Volatile.Read(ref stopped) == 0)
                {
                    var socket = listener.AcceptTcpClient();
                    if (peers.Count >= Limits.Connections) { socket.Close(); continue; }
                    var peer = new Peer(this, socket);
                    peers.TryAdd(peer, 0);
                    if (Volatile.Read(ref stopped) != 0) { peer.Close(); break; }
                    Task.Run((Action)peer.Run);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        private bool Authorized(string value)
        {
            var actual = Encoding.ASCII.GetBytes(value);
            int diff = actual.Length ^ authorization.Length;
            for (int i = 0; i < authorization.Length; i++) diff |= authorization[i] ^ (i < actual.Length ? actual[i] : 0);
            return diff == 0;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            listener.Stop();
            foreach (var peer in peers.Keys) peer.Close();
        }
        private sealed class FrameError : Exception
        {
            internal readonly ushort Code;
            internal FrameError(ushort code) { Code = code; }
        }
        private sealed class Peer
        {
            private readonly WebSocketServer server;
            private readonly TcpClient socket;
            private readonly NetworkStream stream;
            private readonly object writeGate = new object();
            // Each response is capped at 1 MiB by the writer; no unbounded slow-reader queue.
            private readonly BlockingCollection<Response> outgoing = new BlockingCollection<Response>(4);
            private readonly Stopwatch time = Stopwatch.StartNew();
            private Dispatcher.Client? client;
            private int closed, closeScheduled;
            internal Peer(WebSocketServer server, TcpClient socket)
            {
                this.server = server; this.socket = socket;
                socket.NoDelay = true;
                stream = socket.GetStream();
                stream.WriteTimeout = 3000;
            }
            internal void Run()
            {
                try
                {
                    if (!Handshake()) return;
                    client = server.dispatcher.Connect(Enqueue);
                    Task.Run((Action)WriteResponses);
                    Receive();
                }
                catch (FrameError e) { TryCloseFrame(e.Code); }
                catch (InvalidProtocolBufferException) { TryCloseFrame(1007); }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally { Close(); }
            }
            private bool Handshake()
            {
                var header = new byte[8192];
                int length = 0;
                long deadline = time.ElapsedMilliseconds + 5000;
                while (length < header.Length)
                {
                    Read(header, length++, 1, deadline);
                    if (length >= 4 && header[length - 4] == 13 && header[length - 3] == 10 && header[length - 2] == 13 && header[length - 1] == 10) break;
                }
                if (length == header.Length) return Reject(431);
                if (header.Take(length).Any(b => b > 127 || b == 0)) return Reject(400);
                var lines = Encoding.ASCII.GetString(header, 0, length).Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines[0] != "GET /forthwall HTTP/1.1") return Reject(400);
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    if (line.Length == 0) continue;
                    int colon = line.IndexOf(':');
                    if (colon < 1 || char.IsWhiteSpace(line[0])) return Reject(400);
                    string key = line.Substring(0, colon);
                    if (key.Any(c => !(char.IsLetterOrDigit(c) || c == '-')) || fields.ContainsKey(key)) return Reject(400);
                    fields.Add(key, line.Substring(colon + 1).Trim());
                }
                string Field(string name) => fields.TryGetValue(name, out var value) ? value : "";
                if (fields.ContainsKey("Origin")) return Reject(403);
                string host = Field("Host");
                if (!string.Equals(host, "127.0.0.1:" + server.Port, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(host, "localhost:" + server.Port, StringComparison.OrdinalIgnoreCase)) return Reject(400);
                if (!server.Authorized(Field("Authorization"))) return Reject(401);
                if (!string.Equals(Field("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase) ||
                    !HasToken(Field("Connection"), "Upgrade") || Field("Sec-WebSocket-Version") != "13" ||
                    !HasToken(Field("Sec-WebSocket-Protocol"), "forthwall.v1", false)) return Reject(400);
                byte[] keyBytes;
                try { keyBytes = Convert.FromBase64String(Field("Sec-WebSocket-Key")); } catch (FormatException) { return Reject(400); }
                if (keyBytes.Length != 16) return Reject(400);
                string accept;
                using (var sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(Field("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                WriteRaw(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Protocol: forthwall.v1\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));
                return true;
            }
            private static bool HasToken(string value, string token, bool insensitive = true) => value.Split(',').Any(s => string.Equals(s.Trim(), token, insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            private bool Reject(int status)
            {
                WriteRaw(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Rejected\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"));
                return false;
            }
            private void Receive()
            {
                using (var message = new MemoryStream())
                {
                    bool fragmented = false;
                    int fragments = 0, framesInWindow = 0;
                    long window = time.ElapsedMilliseconds, messageDeadline = 0;
                    var header = new byte[8];
                    var mask = new byte[4];
                    while (Volatile.Read(ref closed) == 0)
                    {
                        long now = time.ElapsedMilliseconds;
                        if (now - window >= 1000) { window = now; framesInWindow = 0; }
                        if (++framesInWindow > 256) throw new FrameError(1008);
                        long deadline = fragmented ? messageDeadline : now + 30000;
                        Read(header, 0, 2, deadline);
                        bool final = (header[0] & 128) != 0;
                        int opcode = header[0] & 15;
                        int marker = header[1] & 127;
                        bool control = opcode >= 8;
                        if ((header[0] & 112) != 0 || (header[1] & 128) == 0 ||
                            !(opcode == 0 || opcode == 1 || opcode == 2 || opcode == 8 || opcode == 9 || opcode == 10)) throw new FrameError(1002);
                        if (control && (!final || marker > 125)) throw new FrameError(1002);
                        ulong size = (uint)marker;
                        if (marker == 126 || marker == 127)
                        {
                            int width = marker == 126 ? 2 : 8;
                            Read(header, 0, width, deadline);
                            if (width == 8 && (header[0] & 128) != 0) throw new FrameError(1002);
                            size = 0;
                            for (int i = 0; i < width; i++) size = (size << 8) | header[i];
                            if ((marker == 126 && size < 126) || (marker == 127 && size <= 65535)) throw new FrameError(1002);
                        }
                        if (size > Limits.MessageBytes || (!control && size + (ulong)message.Length > Limits.MessageBytes)) throw new FrameError(1009);
                        if (!control && ((opcode == 0) != fragmented)) throw new FrameError(1002);
                        if (opcode == 1) throw new FrameError(1003);
                        Read(mask, 0, 4, deadline);
                        var payload = new byte[(int)size];
                        Read(payload, 0, payload.Length, deadline);
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                        if (opcode == 8)
                        {
                            ValidateClose(payload);
                            SendFrame(8, payload);
                            return;
                        }
                        if (opcode == 9) { SendFrame(10, payload); continue; }
                        if (opcode == 10) continue;
                        if (++fragments > 1024) throw new FrameError(1008);
                        message.Write(payload, 0, payload.Length);
                        if (!fragmented) messageDeadline = time.ElapsedMilliseconds + 30000;
                        fragmented = !final;
                        if (!final) continue;
                        var envelope = Envelope.Parser.ParseFrom(message.GetBuffer(), 0, (int)message.Length);
                        message.SetLength(0); fragments = 0;
                        client!.Submit(envelope);
                        if (client.IsClosed) return;
                    }
                }
            }
            private static void ValidateClose(byte[] payload)
            {
                if (payload.Length == 1) throw new FrameError(1002);
                if (payload.Length < 2) return;
                int code = payload[0] * 256 + payload[1];
                if (!(code >= 3000 && code <= 4999) && !(code >= 1000 && code <= 1014 && code != 1004 && code != 1005 && code != 1006)) throw new FrameError(1002);
                try { new UTF8Encoding(false, true).GetString(payload, 2, payload.Length - 2); }
                catch (DecoderFallbackException) { throw new FrameError(1007); }
            }
            private void Enqueue(Response response)
            {
                // Invoked from simulation/timer workers: no serialization or socket I/O here.
                if (Volatile.Read(ref closed) != 0 || !outgoing.TryAdd(response))
                {
                    if (Interlocked.Exchange(ref closeScheduled, 1) == 0) Task.Run((Action)Close);
                    throw new IOException("Response queue is full or closed.");
                }
            }
            private void WriteResponses()
            {
                try
                {
                    foreach (var queued in outgoing.GetConsumingEnumerable())
                    {
                        var response = queued;
                        if (response.SessionId != server.dispatcher.SessionId)
                            response = new Response { Id = response.Id, SessionId = server.dispatcher.SessionId, MutationOutcome = response.MutationOutcome,
                                Error = new Error { Code = Error.Types.Code.WorldUnloaded, Message = "Session ended before response delivery." } };
                        var envelope = new Envelope { ProtocolVersion = 1, Response = response };
                        if (envelope.CalculateSize() > Limits.MessageBytes) throw new IOException("Response exceeds limit.");
                        SendFrame(2, envelope.ToByteArray());
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                finally { Close(); }
            }
            private void Read(byte[] buffer, int offset, int count, long deadline)
            {
                while (count > 0)
                {
                    long remaining = deadline - time.ElapsedMilliseconds;
                    if (remaining <= 0) throw new IOException("Read deadline elapsed.");
                    stream.ReadTimeout = (int)Math.Min(remaining, 30000);
                    int read = stream.Read(buffer, offset, count);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read; count -= read;
                }
            }
            private void SendFrame(byte opcode, byte[] payload)
            {
                var header = new byte[10];
                header[0] = (byte)(128 | opcode);
                int length = 2;
                if (payload.Length < 126) header[1] = (byte)payload.Length;
                else if (payload.Length <= 65535)
                {
                    header[1] = 126; header[2] = (byte)(payload.Length >> 8); header[3] = (byte)payload.Length; length = 4;
                }
                else
                {
                    header[1] = 127;
                    for (int i = 0; i < 8; i++) header[9 - i] = (byte)((ulong)payload.Length >> (8 * i));
                    length = 10;
                }
                lock (writeGate)
                {
                    stream.Write(header, 0, length);
                    stream.Write(payload, 0, payload.Length);
                }
            }
            private void WriteRaw(byte[] value) { lock (writeGate) stream.Write(value, 0, value.Length); }
            private void TryCloseFrame(ushort code)
            {
                try { SendFrame(8, new[] { (byte)(code >> 8), (byte)code }); } catch { }
            }
            internal void Close()
            {
                if (Interlocked.Exchange(ref closed, 1) != 0) return;
                client?.Dispose();
                outgoing.CompleteAdding();
                socket.Close();
                server.peers.TryRemove(this, out _);
            }
        }
    }
}
