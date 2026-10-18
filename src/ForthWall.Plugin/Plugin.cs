using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using ForthWall.Core;
using ForthWall.Transport;
using VRage.FileSystem;
using VRage.Game.Components;
using VRage.Plugins;

namespace ForthWall.Plugin
{
    public sealed class GatewayConfig
    {
        public int Port { get; set; } = 18751;
    }

    public sealed class Plugin : IPlugin
    {
        internal static Plugin? Instance;
        internal readonly Dispatcher Dispatcher;
        public Plugin() { Dispatcher = new Dispatcher(log: Log); }
        private readonly ConcurrentQueue<string> log = new ConcurrentQueue<string>();
        private readonly object gate = new object();
        private WebSocketServer? server;
        private Timer? logTimer;
        private string directory = "";
        private bool disposed;
        private int flushing;
        private volatile string listenerStatus = "Starting";
        private int listenerPort = 18751;
        internal string ListenerStatus => listenerStatus;
        internal int ListenerPort => Volatile.Read(ref listenerPort);
        public void Init(object gameInstance)
        {
            Instance = this;
            directory = Path.Combine(MyFileSystem.UserDataPath, "Storage", "ForthWallAttenna");
            // Capture the path here; all filesystem/network startup happens on a worker.
            Task.Run((Action)Start);
        }
        private void Start()
        {
            try
            {
                Directory.CreateDirectory(directory);
                var configPath = Path.Combine(directory, "gateway.xml");
                var serializer = new XmlSerializer(typeof(GatewayConfig));
                GatewayConfig config;
                if (File.Exists(configPath))
                { using (var file = File.OpenRead(configPath)) config = (GatewayConfig)serializer.Deserialize(file); }
                else
                {
                    config = new GatewayConfig();
                    using (var file = File.Create(configPath)) serializer.Serialize(file, config);
                }
                if (config.Port < 1024 || config.Port > 65535) throw new InvalidOperationException("Invalid configured port.");
                var tokenPath = Path.Combine(directory, "gateway.token");
                if (!File.Exists(tokenPath))
                {
                    var data = new byte[32];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(data);
                    using (var file = new FileStream(tokenPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var value = Encoding.ASCII.GetBytes(BitConverter.ToString(data).Replace("-", "").ToLowerInvariant());
                        file.Write(value, 0, value.Length);
                    }
                }
                string token = File.ReadAllText(tokenPath).Trim();
                lock (gate)
                {
                    if (disposed) return;
                    server = new WebSocketServer(Dispatcher, token, config.Port);
                    Volatile.Write(ref listenerPort, config.Port);
                    listenerStatus = "Listening";
                    logTimer = new Timer(_ => FlushLog(), null, 1000, 1000);
                }
                Log("Forth Wall Attenna 0.2.0 listening on loopback port " + config.Port + ".");
                FlushLog();
            }
            catch (Exception e)
            {
                listenerStatus = "Unavailable";
                // Exception messages can include configuration values; record only type.
                Log("Startup failed: " + e.GetType().Name + ". Check gateway.xml, token file permissions, and port availability.");
                FlushLog();
            }
        }
        internal void Log(string message)
        {
            if (log.Count < 128) log.Enqueue(DateTime.UtcNow.ToString("O") + " " + message);
        }
        private void FlushLog()
        {
            if (Interlocked.Exchange(ref flushing, 1) != 0) return;
            try
            {
                if (log.IsEmpty) return;
                var path = Path.Combine(directory, "gateway.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                {
                    var previous = path + ".previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                using (var writer = new StreamWriter(path, true))
                    while (log.TryDequeue(out var entry)) writer.WriteLine(entry);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Volatile.Write(ref flushing, 0); }
        }
        public void Update() { /* Simulation work belongs to GatewaySession only. */ }
        public void Dispose()
        {
            listenerStatus = "Offline";
            Instance = null;
            lock (gate) { disposed = true; server?.Dispose(); logTimer?.Dispose(); }
            Dispatcher.Dispose();
            Task.Run((Action)FlushLog);
        }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public sealed class GatewaySession : MySessionComponentBase
    {
        private GameWorld? world;
        private HubPresentation? presentation;
        private bool failed;
        public override void BeforeStart()
        {
            if (Plugin.Instance == null) return;
            try
            {
                world = new GameWorld(Plugin.Instance.Log);
                Plugin.Instance.Dispatcher.SetSession(world.SessionId);
                try { presentation = new HubPresentation(world, Plugin.Instance); }
                catch (Exception e) { Plugin.Instance.Log("Hub display initialization failed: " + e.GetType().Name + ". Gateway remains active."); }
            }
            catch (Exception e) { Fail(e); }
        }
        public override void UpdateBeforeSimulation()
        {
            if (world == null || failed || Plugin.Instance == null) return;
            try
            {
                world.UpdateIndex();
                world.UpdateMissions();
                // Idle frames do not scan grids, hash source, serialize, or touch sockets.
                if (Plugin.Instance.Dispatcher.PendingCount != 0) Plugin.Instance.Dispatcher.Pump(world);
            }
            catch (Exception e) { Fail(e); }
            if (!failed && presentation != null)
            {
                try { presentation.Update(); }
                catch (Exception e)
                {
                    Plugin.Instance?.Log("Hub displays disabled for this session: " + e.GetType().Name + ". Gateway remains active.");
                    presentation.Dispose(); presentation = null;
                }
            }
        }
        private void Fail(Exception e)
        {
            failed = true;
            Plugin.Instance?.Log("Game adapter disabled for this session: " + e.GetType().Name + ". Check game version compatibility.");
            Plugin.Instance?.Dispatcher.SetSession("");
        }
        protected override void UnloadData()
        {
            presentation?.Dispose(); presentation = null;
            world?.Dispose(); world = null;
            Plugin.Instance?.Dispatcher.SetSession("");
        }
    }
}
