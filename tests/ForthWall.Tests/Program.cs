using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ForthWall.Core;
using ForthWall.Protocol;
using ForthWall.Transport;
using Google.Protobuf;
using Code = ForthWall.Protocol.Error.Types.Code;

internal static class Program
{
    private const string TestToken = "synthetic-test-credential-not-a-real-secret-123456";
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--serve")
        {
            using var fixture = new Fixture();
            using var server = new WebSocketServer(fixture.Dispatcher, File.ReadAllText(args[1]).Trim(), 0);
            File.WriteAllText(args[2], server.Port.ToString());
            var stop = Task.Run(() => Console.ReadLine());
            while (!stop.IsCompleted) { fixture.Tick(); Thread.Sleep(2); }
            return 0;
        }
        var tests = new (string, Action)[] {
            ("PB discovery enforces ownership and both route directions", Discovery),
            ("deploy is guarded, compiles once, and never implicitly runs Main", Deploy),
            ("failed compilation reports an applied write", CompileFailure),
            ("queued timeout and disconnect cannot mutate later", Cancellation),
            ("timeout during compilation reports uncertainty without replay", InFlightTimeout),
            ("world changes cancel old requests", WorldChange),
            ("queue bounds, duplicate IDs, and source limits", Bounds),
            ("route loss and ownership change are checked before writes", Revocation),
            ("Remote Control discovery, waypoints, pilot protection and flags", Controls),
            ("source hashes match SHA-256 and reject oversized UTF-8", Hashes),
            ("real WebSocket: binary Protobuf, fragmentation, and response", SocketRoundTrip),
            ("HTTP authentication and browser-origin rejection precede upgrade", Handshake),
            ("WebSocket rejects unmasked and oversized frames before allocation", InvalidFrames),
            ("ping between fragments preserves the binary request", FragmentPing),
        };
        int failed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
        return failed == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Error(Response response, Code code) => Check(response.Error?.Code == code, "Expected " + code + ", received " + (response.Error?.Code.ToString() ?? "success"));
    private static void Until(Func<bool> done, Action? tick = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException(); tick?.Invoke(); Thread.Sleep(1); }
    }
    private sealed class Clock : IClock { public long Now; public long Milliseconds => Now; }
    private sealed class Fixture : IDisposable
    {
        internal readonly Clock Clock = new Clock();
        internal readonly FakeWorld World = new FakeWorld();
        internal readonly Dispatcher Dispatcher;
        internal readonly Dispatcher.Client Client;
        internal readonly ConcurrentQueue<Response> Responses = new ConcurrentQueue<Response>();
        private ulong next;
        internal Fixture()
        {
            Dispatcher = new Dispatcher(Clock, false);
            Dispatcher.SetSession(World.SessionId);
            Client = Dispatcher.Connect(Responses.Enqueue);
        }
        internal Request Request() => new Request { Id = ++next, HubId = 1, SessionId = World.SessionId };
        internal void Send(Request r) => Client.Submit(new Envelope { ProtocolVersion = 1, Request = r });
        internal void Tick() => Dispatcher.Pump(World, budgetMs: 100);
        internal Response Call(Action<Request> operation)
        {
            var r = Request(); operation(r); Send(r);
            Until(() => !Responses.IsEmpty, Tick);
            Check(Responses.TryDequeue(out var response)); return response!;
        }
        public void Dispose() { Client.Dispose(); Dispatcher.Dispose(); }
    }
    private sealed class FakeWorld : IWorld
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        internal readonly Dictionary<long, WorldBlock> Blocks = new Dictionary<long, WorldBlock>();
        internal readonly HashSet<(long, long)> MissingRoutes = new HashSet<(long, long)>();
        public string SessionId { get; set; } = "test-world";
        public bool Paused { get; set; }
        public bool ScriptsEnabled { get; set; } = true;
        public long LocalOwner => 123;
        public IReadOnlyList<long> BlockIds => Blocks.Keys.ToArray();
        public IReadOnlyList<long> HubIds => new long[] { 1 };
        internal string Source = "old-source";
        internal int Writes, Runs;
        internal bool CompileOk = true;
        internal Action? OnWrite;
        internal ControlStatus Control = new ControlStatus { BlockId = 3 };
        internal SetWaypoints? Route;
        internal FakeWorld()
        {
            for (long id = 1; id <= 3; id++) Blocks.Add(id, new WorldBlock { Owner = 123, Accessible = true,
                Info = new Block { EntityId = id, GridId = id, Hub = id == 1, RemoteControl = id == 3, Working = true } });
        }
        private void ThreadCheck() => Check(thread == Environment.CurrentManagedThreadId, "Game API escaped game thread");
        public WorldBlock? Find(long id) { ThreadCheck(); return Blocks.TryGetValue(id, out var b) ? b : null; }
        public bool Reachable(long source, long destination) { ThreadCheck(); return !MissingRoutes.Contains((source, destination)); }
        public string ReadSource(long id) { ThreadCheck(); return Source; }
        public BlockStatus Status(long id) { ThreadCheck(); return new BlockStatus { BlockId = id, Compiled = CompileOk, Initialized = CompileOk, TerminationReason = "None", Working = true }; }
        public void AssignSource(long id, string source) { ThreadCheck(); Source = source; Writes++; OnWrite?.Invoke(); }
        public void Run(long id, string argument) { ThreadCheck(); Runs++; }
        public ControlStatus ControlStatus(long id) { ThreadCheck(); return Control.Clone(); }
        public void SetWaypoints(long id, SetWaypoints command) { ThreadCheck(); Route = command.Clone(); Control.AutopilotEnabled = false; Writes++; }
        public void SetAutopilot(long id, long hub, SetAutopilot command)
        { ThreadCheck(); Control.AutopilotEnabled = command.Enabled; Control.StopOnLinkLoss = command.StopOnLinkLoss; Writes++; }
    }
    private static void Discovery()
    {
        using var f = new Fixture();
        Check(f.Call(r => r.Discover = new Discover()).Blocks.Blocks_.Single().EntityId == 2);
        f.World.MissingRoutes.Add((2, 1));
        Check(f.Call(r => r.Discover = new Discover()).Blocks.Blocks_.Count == 0);
        f.World.MissingRoutes.Clear(); f.World.Blocks[2].Owner = 456;
        Check(f.Call(r => r.Discover = new Discover()).Blocks.Blocks_.Count == 0);
        Error(f.Call(r => r.ReadProgram = new ReadProgram { BlockId = 2 }), Code.AccessDenied);
    }
    private static void Deploy()
    {
        using var f = new Fixture();
        var source = f.Call(r => r.ReadProgram = new ReadProgram { BlockId = 2 }).Program;
        var result = f.Call(r => r.Deploy = new Deploy { BlockId = 2, Source = "new-source", ExpectedSourceSha256 = source.Sha256 });
        Check(result.Error == null && result.MutationOutcome == MutationOutcome.Applied && f.World.Writes == 1 && f.World.Runs == 0);
        Error(f.Call(r => r.Deploy = new Deploy { BlockId = 2, Source = "conflict", ExpectedSourceSha256 = source.Sha256 }), Code.SourceConflict);
        Check(f.World.Writes == 1);
        result = f.Call(r => r.Run = new Run { BlockId = 2, Argument = "test" });
        Check(result.Status.MainInvoked && f.World.Runs == 1);
        result = f.Call(r => r.Deploy = new Deploy { BlockId = 2, Source = "third", RunAfterCompile = true });
        Check(result.Status.MainInvoked && f.World.Writes == 2 && f.World.Runs == 2);
    }
    private static void CompileFailure()
    {
        using var f = new Fixture(); f.World.CompileOk = false;
        var result = f.Call(r => r.Deploy = new Deploy { BlockId = 2, Source = "bad", RunAfterCompile = true });
        Error(result, Code.CompileFailed); Check(result.MutationOutcome == MutationOutcome.Applied && f.World.Writes == 1 && f.World.Runs == 0);
    }
    private static void Cancellation()
    {
        using var f = new Fixture(); f.World.Paused = true;
        var r = f.Request(); r.TimeoutMs = 1; r.Deploy = new Deploy { BlockId = 2, Source = "late" }; f.Send(r); f.Tick();
        f.Clock.Now = 2; f.Dispatcher.Expire();
        Check(f.Responses.TryDequeue(out var response)); Error(response!, Code.Timeout);
        f.World.Paused = false; f.Tick(); Check(f.World.Writes == 0 && f.Dispatcher.PendingCount == 0);
        r = f.Request(); r.Run = new Run { BlockId = 2 }; f.Send(r); f.Client.Dispose(); f.Tick();
        Check(f.World.Runs == 0 && f.Dispatcher.PendingCount == 0);
    }
    private static void InFlightTimeout()
    {
        using var f = new Fixture();
        f.World.OnWrite = () => { f.Clock.Now = 40000; f.Dispatcher.Expire(); };
        var response = f.Call(r => r.Deploy = new Deploy { BlockId = 2, Source = "slow", RunAfterCompile = true });
        Error(response, Code.Timeout); Check(response.MutationOutcome == MutationOutcome.MayHaveApplied);
        for (int i = 0; i < 10; i++) f.Tick();
        Check(f.World.Writes == 1 && f.World.Runs == 0 && f.Responses.IsEmpty);
    }
    private static void WorldChange()
    {
        using var f = new Fixture();
        var r = f.Request(); r.Run = new Run { BlockId = 2 }; f.Send(r);
        f.World.SessionId = "new-world"; f.Dispatcher.SetSession(f.World.SessionId);
        Check(f.Responses.TryDequeue(out var response)); Error(response!, Code.WorldUnloaded); f.Tick(); Check(f.World.Runs == 0);
        Error(f.Call(q => { q.SessionId = "test-world"; q.Run = new Run { BlockId = 2 }; }), Code.WrongSession);
    }
    private static void Bounds()
    {
        using var f = new Fixture();
        for (int i = 0; i < 17; i++) { var r = f.Request(); r.ListHubs = new ListHubs(); f.Send(r); }
        Check(f.Dispatcher.PendingCount == 16);
        Check(f.Responses.TryDequeue(out var rejected)); Error(rejected!, Code.Busy);
        var duplicate = new Request { Id = 1, ListHubs = new ListHubs() }; f.Send(duplicate);
        Check(f.Client.IsClosed && f.Dispatcher.PendingCount == 0);
        using var other = new Fixture();
        Error(other.Call(r => r.Deploy = new Deploy { BlockId = 2, Source = new string('x', Limits.SourceBytes + 1) }), Code.InvalidRequest);
    }
    private static void Revocation()
    {
        using var f = new Fixture();
        var r = f.Request(); r.Deploy = new Deploy { BlockId = 2, Source = "denied" }; f.Send(r);
        f.World.MissingRoutes.Add((1, 2)); f.Tick();
        Check(f.Responses.TryDequeue(out var response)); Error(response!, Code.Unreachable); Check(f.World.Writes == 0);
        f.World.MissingRoutes.Clear(); f.World.Blocks[1].Owner = 456;
        Error(f.Call(q => q.Run = new Run { BlockId = 2 }), Code.AccessDenied); Check(f.World.Runs == 0);
    }
    private static void Controls()
    {
        using var f = new Fixture();
        Check(f.Call(r => r.DiscoverControls = new DiscoverControls()).Blocks.Blocks_.Single().EntityId == 3);
        var route = new SetWaypoints { BlockId = 3, SpeedLimit = 10, FlightPattern = FlightPattern.OneWay };
        route.Waypoints.Add(new Waypoint { Name = "destination", X = 100 });
        Check(f.Call(r => r.SetWaypoints = route).Error == null && !f.World.Control.AutopilotEnabled && f.World.Route!.Waypoints.Count == 1);
        Check(f.Call(r => r.SetAutopilot = new SetAutopilot { BlockId = 3, Enabled = true, StopOnLinkLoss = true }).ControlStatus.StopOnLinkLoss);
        Check(!f.Call(r => r.SetAutopilot = new SetAutopilot { BlockId = 3, Enabled = true }).ControlStatus.StopOnLinkLoss);
        f.World.Control.UnderControl = true;
        Error(f.Call(r => r.SetAutopilot = new SetAutopilot { BlockId = 3, Enabled = true }), Code.Busy);
        route.Waypoints[0].X = double.NaN;
        Error(f.Call(r => r.SetWaypoints = route), Code.InvalidRequest);
        Error(f.Call(r => r.Run = new Run { BlockId = 3 }), Code.NotFound);
        Check(f.World.Writes == 3);
    }
    private static void Hashes()
    {
        Check(SourceHashes.Compute("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        try { SourceHashes.Compute(new string('\u20ac', Limits.SourceBytes / 2)); throw new Exception("Oversized UTF-8 accepted"); }
        catch (GatewayException e) { Check(e.Code == Code.InvalidRequest); }
    }
    private static void SocketRoundTrip()
    {
        using var f = new Fixture(); using var server = new WebSocketServer(f.Dispatcher, TestToken, 0);
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("forthwall.v1"); ws.Options.SetRequestHeader("Authorization", "Bearer " + TestToken);
        using var cancellation = new CancellationTokenSource(5000);
        ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/forthwall"), cancellation.Token).GetAwaiter().GetResult();
        var envelope = new Envelope { ProtocolVersion = 1, Request = new Request { Id = 42, ListHubs = new ListHubs() } }.ToByteArray();
        ws.SendAsync(new ArraySegment<byte>(envelope, 0, 2), WebSocketMessageType.Binary, false, cancellation.Token).GetAwaiter().GetResult();
        ws.SendAsync(new ArraySegment<byte>(envelope, 2, envelope.Length - 2), WebSocketMessageType.Binary, true, cancellation.Token).GetAwaiter().GetResult();
        var buffer = new byte[4096]; var receive = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
        Until(() => receive.IsCompleted, f.Tick);
        var result = receive.GetAwaiter().GetResult();
        var response = Envelope.Parser.ParseFrom(buffer, 0, result.Count).Response;
        Check(result.EndOfMessage && response.Id == 42 && response.Blocks.Blocks_.Single().Hub);
    }
    private static TcpClient Raw(WebSocketServer server, string? authorization, string extra, out string status)
    {
        var socket = new TcpClient("127.0.0.1", server.Port); var stream = socket.GetStream(); stream.ReadTimeout = 3000; stream.WriteTimeout = 3000;
        string request = $"GET /forthwall HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: MDEyMzQ1Njc4OWFiY2RlZg==\r\nSec-WebSocket-Protocol: forthwall.v1\r\n";
        if (authorization != null) request += "Authorization: Bearer " + authorization + "\r\n";
        stream.Write(Encoding.ASCII.GetBytes(request + extra + "\r\n"));
        var bytes = new List<byte>();
        while (bytes.Count < 8192)
        {
            int value = stream.ReadByte(); if (value < 0) break; bytes.Add((byte)value);
            if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.Skip(bytes.Count - 4).ToArray()) == "\r\n\r\n") break;
        }
        status = Encoding.ASCII.GetString(bytes.ToArray()); return socket;
    }
    private static void Handshake()
    {
        using var f = new Fixture(); using var server = new WebSocketServer(f.Dispatcher, TestToken, 0);
        using var missing = Raw(server, null, "", out var a); Check(a.StartsWith("HTTP/1.1 401"));
        using var wrong = Raw(server, "wrong", "", out var b); Check(b.StartsWith("HTTP/1.1 401"));
        using var origin = Raw(server, TestToken, "Origin: https://example.invalid\r\n", out var c); Check(c.StartsWith("HTTP/1.1 403"));
        Check(f.Dispatcher.PendingCount == 0);
    }
    private static byte[] Frame(byte opcode, byte[] payload, bool final = true)
    {
        Check(payload.Length < 126); var data = new byte[payload.Length + 6];
        data[0] = (byte)(opcode | (final ? 128 : 0)); data[1] = (byte)(128 | payload.Length);
        for (int i = 0; i < 4; i++) data[2 + i] = (byte)(i + 1);
        for (int i = 0; i < payload.Length; i++) data[6 + i] = (byte)(payload[i] ^ data[2 + i % 4]); return data;
    }
    private static (int Opcode, byte[] Data) ReceiveFrame(NetworkStream stream)
    {
        int opcode = stream.ReadByte() & 15, length = stream.ReadByte();
        Check(length >= 0 && length < 126, "Unexpected test response frame length");
        var bytes = new byte[length]; stream.ReadExactly(bytes); return (opcode, bytes);
    }
    private static void InvalidFrames()
    {
        using var f = new Fixture(); using var server = new WebSocketServer(f.Dispatcher, TestToken, 0);
        using var unmasked = Raw(server, TestToken, "", out var a); Check(a.StartsWith("HTTP/1.1 101"));
        unmasked.GetStream().Write(new byte[] { 0x82, 0 }); var close = ReceiveFrame(unmasked.GetStream());
        Check(close.Opcode == 8 && close.Data[0] * 256 + close.Data[1] == 1002);
        using var huge = Raw(server, TestToken, "", out _);
        huge.GetStream().Write(new byte[] { 0x82, 0xff, 0, 0, 0, 0, 0, 0x10, 0, 1 });
        close = ReceiveFrame(huge.GetStream()); Check(close.Opcode == 8 && close.Data[0] * 256 + close.Data[1] == 1009);
        Check(f.Dispatcher.PendingCount == 0);
    }
    private static void FragmentPing()
    {
        using var f = new Fixture(); using var server = new WebSocketServer(f.Dispatcher, TestToken, 0);
        using var raw = Raw(server, TestToken, "", out _); var stream = raw.GetStream();
        var data = new Envelope { ProtocolVersion = 1, Request = new Request { Id = 7, ListHubs = new ListHubs() } }.ToByteArray();
        stream.Write(Frame(2, data.Take(2).ToArray(), false)); stream.Write(Frame(9, new byte[] { 42 }));
        var pong = ReceiveFrame(stream); Check(pong.Opcode == 10 && pong.Data.Single() == 42);
        stream.Write(Frame(0, data.Skip(2).ToArray())); Until(() => f.Dispatcher.PendingCount != 0); f.Tick();
        var response = ReceiveFrame(stream); Check(response.Opcode == 2 && Envelope.Parser.ParseFrom(response.Data).Response.Id == 7);
    }
}
