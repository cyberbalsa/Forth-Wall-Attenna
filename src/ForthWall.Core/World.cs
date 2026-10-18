using System;
using System.Collections.Generic;
using ForthWall.Protocol;

namespace ForthWall.Core
{
    // Every member of this interface is called only by the simulation thread.
    public interface IWorld
    {
        string SessionId { get; }
        bool Paused { get; }
        bool ScriptsEnabled { get; }
        long LocalOwner { get; }
        IReadOnlyList<long> BlockIds { get; }
        IReadOnlyList<long> HubIds { get; }
        WorldBlock? Find(long id);
        bool Reachable(long source, long destination);
        string ReadSource(long id);
        BlockStatus Status(long id);
        void AssignSource(long id, string source);
        void Run(long id, string argument);
        ControlStatus ControlStatus(long id);
        void SetWaypoints(long id, SetWaypoints command);
        void SetAutopilot(long id, long hub, SetAutopilot command);
    }

    public sealed class WorldBlock
    {
        public Block Info { get; set; } = new Block();
        public long Owner { get; set; }
        public bool Accessible { get; set; }
    }

    public sealed class GatewayException : Exception
    {
        public Error.Types.Code Code { get; }
        public GatewayException(Error.Types.Code code, string message) : base(message) { Code = code; }
    }

    public interface IClock { long Milliseconds { get; } }
    public sealed class MonotonicClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        public long Milliseconds => watch.ElapsedMilliseconds;
    }

    public static class Limits
    {
        public const int Connections = 8;
        public const int PendingPerClient = 16;
        public const int PendingTotal = 64;
        public const int PendingBytes = 8 * 1024 * 1024;
        public const int MessageBytes = 1024 * 1024;
        public const int SourceBytes = 256 * 1024;
        public const int ArgumentBytes = 4096;
        public const int Blocks = 512;
        public const int DefaultTimeoutMs = 10000;
        public const int MaxTimeoutMs = 30000;
    }
}
