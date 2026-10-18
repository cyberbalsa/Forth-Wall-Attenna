using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ForthWall.Core
{
    internal sealed class PreparationPending : Exception { }

    // Reference keys avoid traversing a complete PB source on each game tick.
    // Strings are immutable; hashing a captured source needs no game API access.
    public sealed class SourceHashes
    {
        private sealed class ReferenceComparer : IEqualityComparer<string>
        {
            public bool Equals(string? a, string? b) => ReferenceEquals(a, b);
            public int GetHashCode(string value) => RuntimeHelpers.GetHashCode(value);
        }
        private readonly Dictionary<string, Task<string>> cache = new Dictionary<string, Task<string>>(new ReferenceComparer());
        private readonly Queue<string> order = new Queue<string>();

        public static string Compute(string source)
        {
            var bytes = Encoding.UTF8.GetBytes(source);
            if (bytes.Length > Limits.SourceBytes)
                throw new GatewayException(Protocol.Error.Types.Code.InvalidRequest, "Program exceeds the 256 KiB source limit.");
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public string Get(string source)
        {
            if (source.Length > Limits.SourceBytes)
                throw new GatewayException(Protocol.Error.Types.Code.InvalidRequest, "Program exceeds the source limit.");
            if (!cache.TryGetValue(source, out var task))
            {
                if (cache.Count >= 32)
                {
                    // A bounded number of source snapshots/tasks can exist at once.
                    var oldest = order.Peek();
                    if (!cache[oldest].IsCompleted) throw new PreparationPending();
                    order.Dequeue();
                    cache.Remove(oldest);
                }
                task = Task.Run(() => Compute(source));
                cache.Add(source, task);
                order.Enqueue(source);
            }
            if (!task.IsCompleted) throw new PreparationPending();
            return task.GetAwaiter().GetResult();
        }

        public void Clear() { cache.Clear(); order.Clear(); }
    }
}
