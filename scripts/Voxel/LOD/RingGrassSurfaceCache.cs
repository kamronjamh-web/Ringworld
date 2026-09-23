using System;
using System.Collections.Generic;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Small, layer-owned LRU of authoritative surface-cell locations.  It never
/// stores approximated heights or normals: the resolver still reconstructs the
/// regular-cell triangles for each requested point.  Entries are keyed by the
/// immutable density identity and edit projection version, so stale terrain can
/// only turn a lookup into a miss, never into an accepted placement.
/// </summary>
internal sealed class RingGrassSurfaceCache
{
    // The field identity is deliberately part of each key and is duplicated by
    // the Dictionary and LinkedList node. Include managed-object headers,
    // references, bucket slack, and alignment rather than counting only the
    // payload. 256 B/entry is a conservative retained-memory budget.
    public const int EstimatedBytesPerEntry = 256;
    private readonly object _gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _byteCap;
    private long _hits, _misses, _evictions;

    private readonly record struct Key(SmoothTerrainVolumetricDensityIdentity Field, ulong HintVersion, long Around, int Across);
    private readonly record struct Entry(Key Key, int Vertical);

    public RingGrassSurfaceCache(long byteCap) => Configure(byteCap);
    public void Configure(long byteCap)
    {
        lock (_gate)
        {
            _byteCap = Math.Clamp(byteCap, 0, 16L * 1024 * 1024);
            Trim();
        }
    }
    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _lru.Clear(); }
    }
    public bool TryGet(SmoothTerrainVolumetricDensityIdentity field, ulong hintVersion, long around, int across, out int vertical)
    {
        lock (_gate)
        {
            var key = new Key(field, hintVersion, around, across);
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node)) { _misses++; vertical = 0; return false; }
            _lru.Remove(node); _lru.AddFirst(node); _hits++; vertical = node.Value.Vertical; return true;
        }
    }
    public void Store(SmoothTerrainVolumetricDensityIdentity field, ulong hintVersion, long around, int across, int vertical)
    {
        lock (_gate)
        {
            if (_byteCap < EstimatedBytesPerEntry) return;
            var key = new Key(field, hintVersion, around, across);
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                existing.Value = new Entry(key, vertical); _lru.Remove(existing); _lru.AddFirst(existing); return;
            }
            var node = new LinkedListNode<Entry>(new Entry(key, vertical));
            _lru.AddFirst(node); _entries.Add(key, node); Trim();
        }
    }
    internal Diagnostics CaptureDiagnostics()
    {
        lock (_gate) return new Diagnostics(_byteCap, _entries.Count, (long)_entries.Count * EstimatedBytesPerEntry, _hits, _misses, _evictions);
    }
    private void Trim()
    {
        int maximum = (int)Math.Min(int.MaxValue, _byteCap / EstimatedBytesPerEntry);
        while (_entries.Count > maximum && _lru.Last is LinkedListNode<Entry> node)
        { _entries.Remove(node.Value.Key); _lru.RemoveLast(); _evictions++; }
    }
    internal readonly record struct Diagnostics(long ByteCap, int Entries, long EstimatedBytes, long Hits, long Misses, long Evictions);
}
