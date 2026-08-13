namespace Silt.Core.Collections;

/// <summary>
/// A compact concurrent set of 64-bit file ids, used to count hardlinked content once.
/// </summary>
/// <remarks>
/// <para>
/// A <c>HashSet&lt;long&gt;</c> would work but costs roughly 40 bytes per entry once the
/// bucket and entry arrays are counted. At a few million files that is well over 100 MB on a
/// machine this tool exists to relieve. Open addressing over a flat <c>long[]</c> is 8 bytes
/// per slot, so even at a 0.5 load factor it is about a quarter of the cost.
/// </para>
/// <para>
/// Sharded by the low bits of the hash so workers rarely contend. Growth is per shard, under
/// that shard's lock.
/// </para>
/// </remarks>
internal sealed class ConcurrentFileIdSet
{
    private const int ShardCount = 64;
    private const int ShardMask = ShardCount - 1;

    /// <summary>
    /// Sentinel for an empty slot. Zero is not a valid NTFS file id, so it can mark
    /// emptiness without a parallel occupancy bitmap.
    /// </summary>
    private const long Empty = 0;

    private readonly Shard[] _shards;

    internal ConcurrentFileIdSet(int expectedCapacity = 1 << 16)
    {
        int perShard = Math.Max(64, NextPowerOfTwo(expectedCapacity / ShardCount));
        _shards = new Shard[ShardCount];
        for (int i = 0; i < ShardCount; i++)
        {
            _shards[i] = new Shard(perShard);
        }
    }

    /// <summary>
    /// Adds the id. Returns true if it was newly added, false if already present — i.e.
    /// false means "this is an additional hardlink; do not count its bytes again".
    /// </summary>
    internal bool Add(long fileId)
    {
        // A file id of 0 should not occur; if it ever does, count the file rather than
        // silently collapsing every such entry into one.
        if (fileId == Empty)
        {
            return true;
        }

        ulong h = Mix((ulong)fileId);
        Shard shard = _shards[(int)(h & ShardMask)];
        lock (shard.SyncRoot)
        {
            return shard.Add(fileId, h);
        }
    }

    /// <summary>Fowler-style finalizer; NTFS file ids cluster heavily in their low bits.</summary>
    private static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return x;
    }

    private static int NextPowerOfTwo(int v)
    {
        int n = 64;
        while (n < v && n < (1 << 30))
        {
            n <<= 1;
        }
        return n;
    }

    private sealed class Shard(int capacity)
    {
        internal readonly object SyncRoot = new();
        private long[] _slots = new long[capacity];
        private int _count;

        internal bool Add(long fileId, ulong hash)
        {
            if ((_count + 1) * 2 >= _slots.Length)
            {
                Grow();
            }

            long[] slots = _slots;
            int mask = slots.Length - 1;
            int i = (int)(hash >> 6) & mask;

            while (true)
            {
                long existing = slots[i];
                if (existing == Empty)
                {
                    slots[i] = fileId;
                    _count++;
                    return true;
                }
                if (existing == fileId)
                {
                    return false;
                }
                i = (i + 1) & mask;
            }
        }

        private void Grow()
        {
            long[] old = _slots;
            var grown = new long[old.Length * 2];
            int mask = grown.Length - 1;

            foreach (long v in old)
            {
                if (v == Empty)
                {
                    continue;
                }
                int i = (int)(Mix((ulong)v) >> 6) & mask;
                while (grown[i] != Empty)
                {
                    i = (i + 1) & mask;
                }
                grown[i] = v;
            }

            _slots = grown;
        }
    }
}
