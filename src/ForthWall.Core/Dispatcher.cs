using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using ForthWall.Protocol;
using Google.Protobuf;
using Code = ForthWall.Protocol.Error.Types.Code;

namespace ForthWall.Core
{
    public sealed class Dispatcher : IDisposable
    {
        public sealed class Client : IDisposable
        {
            internal readonly Dispatcher Owner;
            internal readonly Action<Response> Send;
            internal readonly LinkedList<Work> Pending = new LinkedList<Work>();
            internal readonly Dictionary<ulong, Work> Ids = new Dictionary<ulong, Work>();
            internal volatile bool Closed;
            public bool IsClosed => Closed;
            internal Client(Dispatcher owner, Action<Response> send) { Owner = owner; Send = send; }
            public void Submit(Envelope envelope) => Owner.Submit(this, envelope);
            public void Dispose() => Owner.Disconnect(this);
        }
        internal sealed class Work
        {
            internal Client Client = null!;
            internal Request Request = null!;
            internal long Deadline;
            internal int Bytes;
            internal LinkedListNode<Work>? Node;
            internal bool Finished, Executing;
            internal volatile bool MutationStarted, MutationApplied;
            internal IReadOnlyList<long>? DiscoveryIds;
            internal int DiscoveryIndex;
            internal Blocks? Discovery;
        }

        private readonly object gate = new object();
        private readonly List<Client> clients = new List<Client>();
        private readonly IClock clock;
        private readonly Timer? timer;
        private readonly Action<string>? log;
        private readonly SourceHashes hashes = new SourceHashes();
        private string session = "";
        private int count, bytes, cursor;
        private bool disposed;
        public string SessionId => Volatile.Read(ref session);
        // Notifications contain no game objects. Invoked by Pump on the game thread.
        public event Action<Request, Response>? Completed;

        public Dispatcher(IClock? clock = null, bool startTimer = true, Action<string>? log = null)
        {
            this.clock = clock ?? new MonotonicClock();
            this.log = log;
            if (startTimer) timer = new Timer(_ => Expire(), null, 50, 50);
        }
        public Client Connect(Action<Response> send)
        {
            lock (gate)
            {
                if (disposed || clients.Count >= Limits.Connections) throw new InvalidOperationException("Connection capacity reached.");
                var client = new Client(this, send);
                clients.Add(client);
                return client;
            }
        }
        public int PendingCount { get { lock (gate) return count; } }
        public int ClientCount { get { lock (gate) return clients.Count; } }

        private void Submit(Client client, Envelope envelope)
        {
            var request = envelope.Request;
            Response? rejected = null;
            bool duplicate = false;
            try
            {
                if (envelope.ProtocolVersion != 1) throw new GatewayException(Code.Unsupported, "Protocol version must be 1.");
                if (envelope.BodyCase != Envelope.BodyOneofCase.Request || request == null || request.Id == 0)
                    throw new GatewayException(Code.InvalidRequest, "A request with a nonzero ID is required.");
                if (request.OperationCase == Request.OperationOneofCase.None)
                    throw new GatewayException(Code.InvalidRequest, "Missing or unknown operation.");
                if (request.CalculateSize() > Limits.MessageBytes)
                    throw new GatewayException(Code.InvalidRequest, "Request too large.");
                if (request.Deploy != null)
                {
                    if (Encoding.UTF8.GetByteCount(request.Deploy.Source) > Limits.SourceBytes)
                        throw new GatewayException(Code.InvalidRequest, "Program exceeds the 256 KiB source limit.");
                    var expected = request.Deploy.ExpectedSourceSha256;
                    if (expected.Length != 0 && (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c))))
                        throw new GatewayException(Code.InvalidRequest, "Expected source hash must be a SHA-256 hex digest.");
                }
                var argument = request.Deploy?.Argument ?? request.Run?.Argument ?? "";
                if (Encoding.UTF8.GetByteCount(argument) > Limits.ArgumentBytes)
                    throw new GatewayException(Code.InvalidRequest, "Argument exceeds 4096 bytes.");
                if (request.SetWaypoints != null)
                {
                    var route = request.SetWaypoints;
                    if (route.Waypoints.Count > 32 || float.IsNaN(route.SpeedLimit) || route.SpeedLimit <= 0 || route.SpeedLimit > 100 ||
                        (int)route.FlightPattern < 0 || (int)route.FlightPattern > 2 ||
                        route.Waypoints.Any(p => p.Name.Length > 128 || !Coordinate(p.X) || !Coordinate(p.Y) || !Coordinate(p.Z)))
                        throw new GatewayException(Code.InvalidRequest, "Use at most 32 finite waypoints, names up to 128 characters, and a speed in (0, 100] m/s.");
                }
            }
            catch (GatewayException e) { rejected = Failure(request?.Id ?? 0, e.Code, e.Message); }
            if (rejected != null) { Deliver(client, rejected); return; }
            if (SessionId.Length == 0) { Deliver(client, Failure(request!.Id, Code.NoWorld, "Load a local single-player world first.")); return; }
            // Parsing, cloning and size calculation happen on the network worker.
            request = request!.Clone();
            int size = request.CalculateSize();
            lock (gate)
            {
                if (client.Closed || disposed) return;
                if (client.Ids.ContainsKey(request.Id))
                {
                    duplicate = true;
                    rejected = Failure(request.Id, Code.InvalidRequest, "Duplicate pending ID; connection canceled.");
                }
                else if (count >= Limits.PendingTotal || client.Pending.Count >= Limits.PendingPerClient || bytes + size > Limits.PendingBytes)
                    rejected = Failure(request.Id, Code.Busy, "Request queue is full.");
                else
                {
                    int timeout = request.TimeoutMs == 0 ? Limits.DefaultTimeoutMs : (int)Math.Min(request.TimeoutMs, Limits.MaxTimeoutMs);
                    var work = new Work { Client = client, Request = request, Bytes = size, Deadline = clock.Milliseconds + timeout };
                    work.Node = client.Pending.AddLast(work);
                    client.Ids.Add(request.Id, work);
                    count++; bytes += size;
                }
            }
            if (rejected != null) Deliver(client, rejected);
            if (duplicate) Disconnect(client);
        }

        // Called by the session component on the simulation thread only.
        public void SetSession(string newSession)
        {
            var ended = new List<Work>();
            lock (gate)
            {
                if (session == newSession) return;
                Volatile.Write(ref session, newSession);
                foreach (var client in clients)
                    foreach (var work in client.Pending.ToArray()) { Remove(work); ended.Add(work); }
            }
            hashes.Clear();
            foreach (var work in ended) Deliver(work.Client, Failure(work.Request.Id, Code.WorldUnloaded, "The world session ended.", Outcome(work)));
        }

        public void Expire()
        {
            var ended = new List<Work>();
            lock (gate)
                foreach (var client in clients)
                    foreach (var work in client.Pending.ToArray())
                        if (clock.Milliseconds >= work.Deadline) { Remove(work); ended.Add(work); }
            foreach (var work in ended)
                Deliver(work.Client, Failure(work.Request.Id, Code.Timeout,
                    work.MutationStarted ? "Deadline elapsed after execution began; inspect source/status before retrying." : "Deadline elapsed before execution.", Outcome(work)));
        }

        public void Pump(IWorld world, int maxRequests = 4, double budgetMs = 2)
        {
            SetSession(world.SessionId);
            if (world.Paused) return;
            var budget = Stopwatch.StartNew();
            for (int i = 0; i < maxRequests && budget.Elapsed.TotalMilliseconds < budgetMs; i++)
            {
                Work? work = null;
                lock (gate)
                {
                    for (int n = 0; n < clients.Count; n++)
                    {
                        cursor %= clients.Count;
                        var candidate = clients[cursor++].Pending.First?.Value;
                        if (candidate == null || candidate.Executing || candidate.Finished) continue;
                        candidate.Executing = true; work = candidate; break;
                    }
                }
                if (work == null) break;
                Response? response = null;
                try
                {
                    CheckActive(work);
                    response = Execute(world, work);
                }
                catch (PreparationPending) { /* Hash computation continues off the game thread. */ }
                catch (GatewayException e) { response = Failure(work.Request.Id, e.Code, e.Message, Outcome(work)); }
                catch (Exception e)
                {
                    log?.Invoke("Game operation " + work.Request.OperationCase + " failed: " + e.GetType().Name + ".");
                    response = Failure(work.Request.Id, Code.Internal, "Game operation failed; consult the plugin log.", Outcome(work));
                }
                bool deliver = false;
                lock (gate)
                {
                    work.Executing = false;
                    if (!work.Finished && response != null) { Remove(work); deliver = true; }
                }
                if (response != null)
                {
                    try { Completed?.Invoke(work.Request, response); }
                    catch (Exception e) { log?.Invoke("Status observer failed: " + e.GetType().Name + "."); }
                }
                if (deliver) Deliver(work.Client, response!);
                // At most one mutation per simulation update; a compilation cannot be preempted.
                if (work.MutationStarted) break;
            }
        }

        private Response Execute(IWorld world, Work work)
        {
            var r = work.Request;
            if (world.SessionId.Length == 0 || world.LocalOwner == 0) throw new GatewayException(Code.NoWorld, "No supported local single-player world is ready.");
            var response = new Response { Id = r.Id, SessionId = world.SessionId };
            if (r.ListHubs != null)
            {
                var list = new Blocks();
                foreach (var id in world.HubIds)
                {
                    var b = world.Find(id);
                    if (b != null && b.Info.Hub && Owned(world, b)) Add(list, b);
                }
                response.Blocks = list;
                return response;
            }
            if (r.SessionId != world.SessionId) throw new GatewayException(Code.WrongSession, "Refresh hubs after a world change.");
            var hub = Hub(world, r.HubId);
            long target = TargetId(r);
            switch (r.OperationCase)
            {
                case Request.OperationOneofCase.Discover:
                case Request.OperationOneofCase.DiscoverControls:
                    if (work.DiscoveryIds == null) { work.DiscoveryIds = world.BlockIds; work.Discovery = new Blocks(); }
                    int end = Math.Min(work.DiscoveryIndex + 8, work.DiscoveryIds.Count);
                    while (work.DiscoveryIndex < end)
                    {
                        var b = world.Find(work.DiscoveryIds[work.DiscoveryIndex++]);
                        if (b != null && !b.Info.Hub && b.Info.RemoteControl == (r.DiscoverControls != null) && Owned(world, b) && Route(world, hub.Info.EntityId, b.Info.EntityId)) Add(work.Discovery!, b);
                    }
                    if (work.DiscoveryIndex < work.DiscoveryIds.Count) throw new PreparationPending();
                    response.Blocks = work.Discovery;
                    break;
                case Request.OperationOneofCase.ReadProgram:
                    Target(world, hub, target, false);
                    string source = world.ReadSource(target);
                    response.Program = new ProgramSource { Source = source, Sha256 = hashes.Get(source) };
                    break;
                case Request.OperationOneofCase.GetStatus:
                    Target(world, hub, target, false);
                    response.Status = Status(world, target);
                    break;
                case Request.OperationOneofCase.Deploy:
                    Target(world, hub, target, true);
                    Scripts(world);
                    var before = Status(world, target);
                    if (before.Running) throw new GatewayException(Code.Busy, "Target PB is running.");
                    if (r.Deploy.ExpectedSourceSha256.Length != 0 && !string.Equals(before.SourceSha256, r.Deploy.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new GatewayException(Code.SourceConflict, "Source changed since it was read.");
                    // Prepare the new hash before committing so a later retry cannot repeat a write.
                    string newHash = hashes.Get(r.Deploy.Source);
                    BeginMutation(work);
                    world.AssignSource(target, r.Deploy.Source);
                    work.MutationApplied = true;
                    response.Status = world.Status(target);
                    response.Status.SourceSha256 = newHash;
                    if (!response.Status.Compiled || !response.Status.Initialized)
                        response.Error = new Error { Code = response.Status.TerminationReason == "None" ? Code.CompileFailed : Code.ExecutionFailed, Message = "Source was assigned but compilation or initialization failed; inspect status." };
                    else if (r.Deploy.RunAfterCompile)
                    {
                        CheckActive(work);
                        Target(world, Hub(world, r.HubId), target, true);
                        world.Run(target, r.Deploy.Argument);
                        response.Status = world.Status(target);
                        response.Status.SourceSha256 = newHash;
                        response.Status.MainInvoked = true;
                        CheckRun(response);
                    }
                    break;
                case Request.OperationOneofCase.Run:
                    Target(world, hub, target, true);
                    Scripts(world);
                    var status = Status(world, target);
                    if (status.Running) throw new GatewayException(Code.Busy, "Target PB is running.");
                    if (!status.Compiled || !status.Initialized) throw new GatewayException(Code.ExecutionFailed, "Target has no ready program; inspect status.");
                    BeginMutation(work);
                    world.Run(target, r.Run.Argument);
                    work.MutationApplied = true;
                    response.Status = world.Status(target);
                    response.Status.SourceSha256 = status.SourceSha256;
                    response.Status.MainInvoked = true;
                    CheckRun(response);
                    break;
                case Request.OperationOneofCase.GetControlStatus:
                    Target(world, hub, target, false, true);
                    response.ControlStatus = world.ControlStatus(target);
                    break;
                case Request.OperationOneofCase.SetWaypoints:
                case Request.OperationOneofCase.SetAutopilot:
                    Target(world, hub, target, true, true);
                    if (world.ControlStatus(target).UnderControl)
                        throw new GatewayException(Code.Busy, "A player is controlling this grid.");
                    BeginMutation(work);
                    if (r.SetWaypoints != null) world.SetWaypoints(target, r.SetWaypoints);
                    else world.SetAutopilot(target, r.HubId, r.SetAutopilot);
                    work.MutationApplied = true;
                    response.ControlStatus = world.ControlStatus(target);
                    if (r.SetAutopilot != null && response.ControlStatus.AutopilotEnabled != r.SetAutopilot.Enabled)
                        response.Error = new Error { Code = Code.ExecutionFailed, Message = "The game did not accept the requested autopilot state; check the grid and waypoints." };
                    break;
                default:
                    throw new GatewayException(Code.Unsupported, "RPC, events, raw IGC, and live piloting are not yet implemented.");
            }
            // All response authorization checks happen on the game thread before handoff.
            if (target != 0) Target(world, Hub(world, r.HubId), target, false, IsControl(r));
            CheckActive(work);
            response.MutationOutcome = Outcome(work);
            return response;
        }
        private static void CheckRun(Response response)
        {
            if (!response.Status.Initialized || response.Status.TerminationReason != "None")
                response.Error = new Error { Code = Code.ExecutionFailed, Message = "Program execution failed; inspect status." };
        }
        private BlockStatus Status(IWorld world, long id)
        {
            string source = world.ReadSource(id);
            string hash = hashes.Get(source);
            var status = world.Status(id);
            status.SourceSha256 = hash;
            return status;
        }
        private static void Scripts(IWorld world)
        {
            if (!world.ScriptsEnabled) throw new GatewayException(Code.Unsupported, "In-game scripts are disabled in this world.");
        }
        private static bool Owned(IWorld world, WorldBlock b) => b.Owner == world.LocalOwner && b.Owner != 0 && b.Accessible;
        private static WorldBlock Hub(IWorld world, long id)
        {
            var hub = world.Find(id);
            if (hub == null || !hub.Info.Hub) throw new GatewayException(Code.NotFound, "Hub does not exist.");
            if (!Owned(world, hub)) throw new GatewayException(Code.AccessDenied, "Hub must be owned by the local player.");
            if (!hub.Info.Working) throw new GatewayException(Code.NotWorking, "Hub is not powered and operational.");
            return hub;
        }
        private static void Target(IWorld world, WorldBlock hub, long id, bool working, bool remoteControl = false)
        {
            var target = world.Find(id);
            if (target == null || target.Info.Hub || target.Info.RemoteControl != remoteControl)
                throw new GatewayException(Code.NotFound, remoteControl ? "Target must be a Remote Control block." : "Target must be an ordinary Programmable Block.");
            if (!Owned(world, target)) throw new GatewayException(Code.AccessDenied, "Target must be owned by the local player.");
            if (!Route(world, hub.Info.EntityId, id)) throw new GatewayException(Code.Unreachable, "No two-way antenna/construct route exists.");
            if (working && !target.Info.Working) throw new GatewayException(Code.NotWorking, "Target block is not powered and operational.");
        }
        private static bool Route(IWorld world, long hub, long target) => world.Reachable(hub, target) && world.Reachable(target, hub);
        private static long TargetId(Request r) => r.ReadProgram?.BlockId ?? r.GetStatus?.BlockId ?? r.Deploy?.BlockId ?? r.Run?.BlockId ?? r.GetControlStatus?.BlockId ?? r.SetWaypoints?.BlockId ?? r.SetAutopilot?.BlockId ?? 0;
        private static bool IsControl(Request r) => r.GetControlStatus != null || r.SetWaypoints != null || r.SetAutopilot != null;
        private static bool Coordinate(double value) => !double.IsNaN(value) && Math.Abs(value) <= 1e12;
        private static void Add(Blocks list, WorldBlock block)
        {
            if (list.Blocks_.Count >= Limits.Blocks) throw new GatewayException(Code.Busy, "Discovery exceeds the prototype limit of 512 blocks.");
            list.Blocks_.Add(block.Info.Clone());
        }
        private void CheckActive(Work work)
        {
            lock (gate)
                if (work.Finished || work.Client.Closed || clock.Milliseconds >= work.Deadline)
                    throw new GatewayException(Code.Timeout, "Request expired or disconnected.");
        }
        private void BeginMutation(Work work)
        {
            lock (gate) { CheckActive(work); work.MutationStarted = true; }
        }
        private static MutationOutcome Outcome(Work work) => work.MutationApplied ? MutationOutcome.Applied : work.MutationStarted ? MutationOutcome.MayHaveApplied : MutationOutcome.NotStarted;
        private Response Failure(ulong id, Code code, string message, MutationOutcome outcome = MutationOutcome.NotStarted) =>
            new Response { Id = id, SessionId = SessionId, Error = new Error { Code = code, Message = message }, MutationOutcome = outcome };
        private static void Deliver(Client client, Response response)
        {
            if (client.Closed) return;
            try { client.Send(response); } catch { client.Dispose(); }
        }
        private void Remove(Work work)
        {
            if (work.Finished) return;
            work.Finished = true;
            if (work.Node != null) work.Client.Pending.Remove(work.Node);
            work.Client.Ids.Remove(work.Request.Id);
            count--; bytes -= work.Bytes;
        }
        private void Disconnect(Client client)
        {
            lock (gate)
            {
                client.Closed = true;
                foreach (var work in client.Pending.ToArray()) Remove(work);
                clients.Remove(client);
            }
        }
        public void Dispose()
        {
            timer?.Dispose();
            lock (gate)
            {
                disposed = true;
                foreach (var client in clients.ToArray()) Disconnect(client);
            }
        }
    }
}
