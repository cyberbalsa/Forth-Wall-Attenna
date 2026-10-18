using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using ForthWall.Core;
using ForthWall.Protocol;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using VRage.Game.Entity;
using VRageMath;
using Code = ForthWall.Protocol.Error.Types.Code;
using ModPB = Sandbox.ModAPI.IMyProgrammableBlock;
using GameRC = Sandbox.ModAPI.Ingame.IMyRemoteControl;

namespace ForthWall.Plugin
{
    internal sealed class GameWorld : IWorld, IDisposable
    {
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Action<string> log;
        private readonly Dictionary<long, MyTerminalBlock> blocks = new Dictionary<long, MyTerminalBlock>();
        private readonly Dictionary<long, MyCubeGrid> grids = new Dictionary<long, MyCubeGrid>();
        private readonly Queue<MyCubeGrid> seed = new Queue<MyCubeGrid>();
        private IEnumerator<MyTerminalBlock>? scan;
        private MyCubeGrid? scanningGrid;
        private readonly HashSet<MyDataBroadcaster> broadcasters = new HashSet<MyDataBroadcaster>();
        private sealed class Mission { internal long Block, Hub, Owner; }
        private readonly List<Mission> missions = new List<Mission>();
        private int missionCursor;
        private long[] blockIds = Array.Empty<long>(), hubIds = Array.Empty<long>();
        private bool dirty, hubsDirty;
        private static readonly Type IgcType = typeof(MyProgrammableBlock).Assembly.GetType("Sandbox.Game.SessionComponents.MyIGCSystemSessionComponent", true)!;
        private static readonly PropertyInfo IgcStatic = IgcType.GetProperty("Static", BindingFlags.Static | BindingFlags.Public)!;
        private static readonly MethodInfo ContextFor = IgcType.GetMethod("GetOrMakeContextFor", BindingFlags.Instance | BindingFlags.Public)!;
        private static readonly FieldInfo AssemblyField = Field("m_currentAssembly");
        private static readonly FieldInfo InstanceField = Field("m_instance");
        private static readonly FieldInfo TerminationField = Field("m_terminationReason");
        private static readonly FieldInfo DiagnosticsField = Field("m_compilerErrors");
        private static FieldInfo Field(string name) => typeof(MyProgrammableBlock).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new NotSupportedException("Game PB layout changed: " + name);
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public bool Paused => MySandboxGame.IsPaused;
        public bool ScriptsEnabled => MySession.Static.EnableIngameScripts;
        public long LocalOwner => Sync.IsServer && !MyAPIGateway.Multiplayer.MultiplayerActive ? MySession.Static.LocalPlayerId : 0;
        public IReadOnlyList<long> BlockIds { get { Snapshot(); return blockIds; } }
        public IReadOnlyList<long> HubIds
        {
            get
            {
                CheckThread();
                if (hubsDirty) { hubIds = blocks.Values.Where(IsHub).Select(p => p.EntityId).ToArray(); hubsDirty = false; }
                return hubIds;
            }
        }

        public GameWorld(Action<string> log)
        {
            this.log = log;
            MyEntities.OnEntityAdd += EntityAdded;
            MyEntities.OnEntityRemove += EntityRemoved;
            // One entity enumeration at session start; PB indexing is incremental.
            foreach (var entity in MyEntities.GetEntities()) EntityAdded(entity);
            log("Session started; incremental PB index attached.");
        }
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Game API called outside simulation thread.");
        }
        private void EntityAdded(MyEntity entity)
        {
            CheckThread();
            if (!(entity is MyCubeGrid grid) || grids.ContainsKey(grid.EntityId)) return;
            grids.Add(grid.EntityId, grid);
            grid.OnBlockAdded += BlockAdded;
            grid.OnBlockRemoved += BlockRemoved;
            seed.Enqueue(grid);
        }
        private void EntityRemoved(MyEntity entity)
        {
            CheckThread();
            if (!(entity is MyCubeGrid grid) || !grids.Remove(grid.EntityId)) return;
            grid.OnBlockAdded -= BlockAdded;
            grid.OnBlockRemoved -= BlockRemoved;
            foreach (var id in blocks.Where(p => p.Value.CubeGrid == grid).Select(p => p.Key).ToArray())
            { if (IsHub(blocks[id])) hubsDirty = true; blocks.Remove(id); }
            dirty = true;
        }
        private void BlockAdded(MySlimBlock block)
        {
            CheckThread();
            if (block.FatBlock is MyTerminalBlock b && Supported(b)) Track(b);
        }
        private void BlockRemoved(MySlimBlock block)
        {
            CheckThread();
            if (block.FatBlock is MyTerminalBlock b && Supported(b))
            { blocks.Remove(b.EntityId); dirty = true; if (IsHub(b)) hubsDirty = true; }
        }
        private void Track(MyTerminalBlock block)
        {
            if (blocks.TryGetValue(block.EntityId, out var previous) && ReferenceEquals(block, previous)) return;
            blocks[block.EntityId] = block;
            dirty = true;
            if (IsHub(block) || (previous != null && IsHub(previous))) hubsDirty = true;
        }
        public void UpdateIndex()
        {
            CheckThread();
            if (scan == null && seed.Count == 0) return;
            var budget = Stopwatch.StartNew();
            for (int n = 0; n < 32 && budget.Elapsed.TotalMilliseconds < 0.5; n++)
            {
                if (scan == null)
                {
                    if (seed.Count == 0) return;
                    var grid = seed.Dequeue();
                    if (grid.Closed || grid.MarkedForClose) continue;
                    scanningGrid = grid;
                    scan = grid.GetFatBlocks<MyTerminalBlock>().GetEnumerator();
                }
                bool next;
                try { next = scan.MoveNext(); }
                catch (InvalidOperationException)
                {
                    // A grid changed during seeding; restart it instead of losing remaining blocks.
                    if (scanningGrid != null && !scanningGrid.Closed) seed.Enqueue(scanningGrid);
                    next = false;
                }
                if (!next) { scan.Dispose(); scan = null; continue; }
                var pb = scan.Current;
                if (Supported(pb) && !pb.Closed && !pb.MarkedForClose) Track(pb);
            }
        }
        private void Snapshot()
        {
            CheckThread();
            if (!dirty) return;
            blockIds = blocks.Keys.ToArray();
            dirty = false;
        }
        private static bool Supported(MyTerminalBlock b) => b is MyProgrammableBlock || b is MyRemoteControl;
        internal static bool IsHub(MyTerminalBlock pb) => pb.BlockDefinition.Id.SubtypeName == "ForthWallAttennaHubSmall" || pb.BlockDefinition.Id.SubtypeName == "ForthWallAttennaHubLarge";
        internal MyProgrammableBlock? HubBlock(long id)
        {
            CheckThread();
            return blocks.TryGetValue(id, out var b) && b is MyProgrammableBlock pb && IsHub(pb) && !pb.Closed && !pb.MarkedForClose && !pb.CubeGrid.IsPreview ? pb : null;
        }
        private MyTerminalBlock Get(long id)
        {
            CheckThread();
            if (!blocks.TryGetValue(id, out var pb) || pb.Closed || pb.MarkedForClose || pb.CubeGrid.IsPreview)
                throw new GatewayException(Code.NotFound, "Block no longer exists.");
            return pb;
        }
        public WorldBlock? Find(long id)
        {
            CheckThread();
            if (!blocks.TryGetValue(id, out var pb) || pb.Closed || pb.MarkedForClose || pb.CubeGrid.IsPreview) return null;
            return new WorldBlock
            {
                Owner = pb.OwnerId,
                Accessible = LocalOwner != 0 && ((Sandbox.ModAPI.IMyTerminalBlock)pb).HasPlayerAccess(LocalOwner),
                Info = new Block { EntityId = id, GridId = pb.CubeGrid.EntityId,
                    Name = Clip(pb.CustomName.ToString(), 128), GridName = Clip(pb.CubeGrid.DisplayName, 128),
                    Hub = IsHub(pb), RemoteControl = pb is MyRemoteControl,
                    Working = pb.IsWorking && pb.IsFunctional && (!(pb is MyFunctionalBlock functional) || functional.Enabled) && pb.CubeGrid.Physics != null }
            };
        }
        public bool Reachable(long source, long destination)
        {
            CheckThread();
            var sourceBlock = Get(source);
            var destinationBlock = Get(destination);
            if (sourceBlock is MyRemoteControl || destinationBlock is MyRemoteControl)
            {
                var a = sourceBlock.CubeGrid;
                var b = destinationBlock.CubeGrid;
                if (a == b || MyCubeGridGroups.Static.Mechanical.HasSameGroup(a, b) || MyCubeGridGroups.Static.Logical.HasSameGroup(a, b)) return true;
                if (MyAntennaSystem.Static == null) return false;
                broadcasters.Clear();
                MyAntennaSystem.GetCubeGridGroupBroadcasters(a, broadcasters, sourceBlock.OwnerId);
                // Same antenna graph used by IGC, including radio, laser, and relay paths.
                foreach (var broadcaster in broadcasters)
                    if (MyAntennaSystem.Static.CheckConnection(destinationBlock, broadcaster, sourceBlock.OwnerId, false)) return true;
                return false;
            }
            object? system = IgcStatic.GetValue(null);
            if (system == null) return false;
            // Construction/recompilation evicts old contexts; retain metadata only.
            var from = (IMyIntergridCommunicationSystem)ContextFor.Invoke(system, new object[] { sourceBlock });
            ContextFor.Invoke(system, new object[] { destinationBlock });
            return from.IsEndpointReachable(destination, TransmissionDistance.AntennaRelay);
        }
        public string ReadSource(long id) => ((ModPB)Get(id)).ProgramData ?? "";
        public BlockStatus Status(long id)
        {
            var pb = Get(id);
            string reason = TerminationField.GetValue(pb)?.ToString() ?? "Unknown";
            var result = new BlockStatus { BlockId = id, Compiled = AssemblyField.GetValue(pb) != null,
                Initialized = InstanceField.GetValue(pb) != null && reason == "None", TerminationReason = reason,
                Working = Find(id)!.Info.Working, Running = ((ModPB)pb).IsRunning,
                Output = Clip(pb.DetailedInfo.ToString(), 8192) };
            var diagnostics = DiagnosticsField.GetValue(pb) as IEnumerable<string>;
            if (diagnostics != null)
                foreach (var diagnostic in diagnostics.Take(16)) result.Diagnostics.Add(Clip(diagnostic, 1024));
            return result;
        }
        public void AssignSource(long id, string source)
        {
            var pb = Get(id);
            var elapsed = Stopwatch.StartNew();
            ((ModPB)pb).ProgramData = source;
            log("Deployment completed for PB " + id + "; synchronous compile/initialization " + elapsed.ElapsedMilliseconds + " ms.");
        }
        public void Run(long id, string argument)
        {
            var elapsed = Stopwatch.StartNew();
            ((ModPB)Get(id)).Run(argument);
            if (elapsed.ElapsedMilliseconds >= 5) log("Explicit PB run " + id + " took " + elapsed.ElapsedMilliseconds + " ms.");
        }
        public ControlStatus ControlStatus(long id)
        {
            var block = (MyRemoteControl)Get(id);
            var rc = (GameRC)block;
            var controller = block.CubeGrid.GridSystems.ControlSystem.GetShipController();
            var point = rc.CurrentWaypoint;
            return new ControlStatus { BlockId = id, AutopilotEnabled = rc.IsAutoPilotEnabled,
                UnderControl = rc.IsUnderControl || (controller != null && ((Sandbox.ModAPI.Ingame.IMyShipController)controller).IsUnderControl),
                Dampeners = rc.DampenersOverride, Speed = rc.GetShipSpeed(), SpeedLimit = rc.SpeedLimit,
                StopOnLinkLoss = missions.Any(m => m.Block == id),
                CurrentWaypoint = new Waypoint { Name = Clip(point.Name, 128), X = point.Coords.X, Y = point.Coords.Y, Z = point.Coords.Z } };
        }
        public void SetWaypoints(long id, SetWaypoints command)
        {
            var rc = (GameRC)Get(id);
            missions.RemoveAll(m => m.Block == id);
            rc.SetAutoPilotEnabled(false);
            rc.ClearWaypoints();
            rc.SpeedLimit = command.SpeedLimit;
            rc.FlightMode = command.FlightPattern == FlightPattern.Patrol ? FlightMode.Patrol : command.FlightPattern == FlightPattern.Circle ? FlightMode.Circle : FlightMode.OneWay;
            rc.Direction = Base6Directions.Direction.Forward;
            rc.SetCollisionAvoidance(!command.DisableCollisionAvoidance);
            rc.SetDockingMode(command.DockingMode);
            foreach (var point in command.Waypoints) rc.AddWaypoint(new Vector3D(point.X, point.Y, point.Z), point.Name);
        }
        public void SetAutopilot(long id, long hub, SetAutopilot command)
        {
            if (command.Enabled && command.StopOnLinkLoss && missions.Count >= 16 && !missions.Any(m => m.Block == id))
                throw new GatewayException(Code.Busy, "At most 16 missions can monitor link loss at once.");
            var rc = (GameRC)Get(id);
            rc.SetAutoPilotEnabled(command.Enabled);
            missions.RemoveAll(m => m.Block == id);
            if (command.Enabled && rc.IsAutoPilotEnabled && command.StopOnLinkLoss)
                missions.Add(new Mission { Block = id, Hub = hub, Owner = LocalOwner });
            if (!command.Enabled && command.DampenersOnStop) rc.DampenersOverride = true;
        }
        public void UpdateMissions()
        {
            CheckThread();
            if (missions.Count == 0) return;
            // Check one active mission per frame; no world enumeration or networking.
            missionCursor %= missions.Count;
            var mission = missions[missionCursor++];
            var target = Find(mission.Block);
            if (target == null || !target.Accessible || target.Owner != mission.Owner || LocalOwner != mission.Owner)
            { missions.Remove(mission); return; }
            var status = ControlStatus(mission.Block);
            if (!status.AutopilotEnabled || status.UnderControl) { missions.Remove(mission); return; }
            var hub = Find(mission.Hub);
            if (target.Info.Working && hub != null && hub.Info.Hub && hub.Info.Working && hub.Accessible && hub.Owner == mission.Owner &&
                Reachable(mission.Hub, mission.Block) && Reachable(mission.Block, mission.Hub)) return;
            var rc = (GameRC)Get(mission.Block);
            rc.SetAutoPilotEnabled(false);
            rc.DampenersOverride = true;
            missions.Remove(mission);
            log("Autopilot stopped after link loss for Remote Control " + mission.Block + ".");
        }
        private static string Clip(string? value, int length) => value == null ? "" : value.Length <= length ? value : value.Substring(0, length);
        public void Dispose()
        {
            CheckThread();
            // Monitored missions do not survive the lifecycle of their link watchdog.
            foreach (var mission in missions)
            {
                var target = Find(mission.Block);
                if (target != null && target.Accessible && target.Owner == mission.Owner && LocalOwner == mission.Owner && !ControlStatus(mission.Block).UnderControl)
                {
                    var rc = (GameRC)Get(mission.Block);
                    rc.SetAutoPilotEnabled(false);
                    rc.DampenersOverride = true;
                }
            }
            missions.Clear();
            MyEntities.OnEntityAdd -= EntityAdded;
            MyEntities.OnEntityRemove -= EntityRemoved;
            foreach (var grid in grids.Values) { grid.OnBlockAdded -= BlockAdded; grid.OnBlockRemoved -= BlockRemoved; }
            scan?.Dispose(); scan = null;
            blocks.Clear(); grids.Clear(); seed.Clear(); broadcasters.Clear(); scanningGrid = null;
            blockIds = hubIds = Array.Empty<long>();
            log("Session ended; PB index detached.");
        }
    }
}
