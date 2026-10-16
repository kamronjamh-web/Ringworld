using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>A material (or test double) that mirrors a subset of the Terrain3D surface's shader parameters.</summary>
public interface IRingSurfaceFollower
{
    /// <summary>False once the underlying object is freed; the registry then drops the follower.</summary>
    bool IsAlive { get; }
    void SetSurfaceParameter(StringName name, Variant value);
}

/// <summary>Adapts a <see cref="ShaderMaterial"/> (for example a grass material) to <see cref="IRingSurfaceFollower"/>.</summary>
public sealed class ShaderMaterialSurfaceFollower : IRingSurfaceFollower
{
    public ShaderMaterialSurfaceFollower(ShaderMaterial material) => Material = material ?? throw new ArgumentNullException(nameof(material));
    public ShaderMaterial Material { get; }
    public bool IsAlive => GodotObject.IsInstanceValid(Material);
    public void SetSurfaceParameter(StringName name, Variant value) => Material.SetShaderParameter(name, value);
}

/// <summary>
/// Forwards the Terrain3D surface's shader parameters to follower materials. Each follower subscribes to a set of
/// parameter names and only receives a value when it differs from the last one written to it, so a stationary frame
/// performs no writes. Main thread only.
/// </summary>
public sealed class RingSurfaceFollowerRegistry
{
    private sealed class Entry
    {
        public IRingSurfaceFollower Follower = null!;
        public HashSet<StringName> Names = null!;
        public readonly Dictionary<StringName, Variant> Written = new();
    }

    private readonly List<Entry> _entries = new();

    public int Count => _entries.Count;
    /// <summary>Total parameter writes issued to followers (diagnostic; unchanged values never increment it).</summary>
    public long WriteCount { get; private set; }

    public void Register(IRingSurfaceFollower follower, IEnumerable<StringName> names, IReadOnlyDictionary<StringName, Variant> current)
    {
        ArgumentNullException.ThrowIfNull(follower);
        ArgumentNullException.ThrowIfNull(names);
        Unregister(follower);
        var entry = new Entry { Follower = follower, Names = new HashSet<StringName>(names) };
        _entries.Add(entry);
        // Replay the last known values so a follower registered mid-session starts coherent.
        foreach (StringName name in entry.Names)
            if (current.TryGetValue(name, out Variant value)) Write(entry, name, value);
    }

    public bool Unregister(IRingSurfaceFollower follower) => _entries.RemoveAll(e => ReferenceEquals(e.Follower, follower)) > 0;

    public bool Unregister(ShaderMaterial material) => _entries.RemoveAll(e => e.Follower is ShaderMaterialSurfaceFollower adapter && ReferenceEquals(adapter.Material, material)) > 0;

    public void Forward(StringName name, Variant value)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry entry = _entries[i];
            if (!entry.Follower.IsAlive) { _entries.RemoveAt(i); continue; }
            if (entry.Names.Contains(name)) Write(entry, name, value);
        }
    }

    /// <summary>Re-sends every subscribed value from <paramref name="current"/>; unchanged values are skipped.</summary>
    public void Replay(IReadOnlyDictionary<StringName, Variant> current)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry entry = _entries[i];
            if (!entry.Follower.IsAlive) { _entries.RemoveAt(i); continue; }
            foreach (StringName name in entry.Names)
                if (current.TryGetValue(name, out Variant value)) Write(entry, name, value);
        }
    }

    private void Write(Entry entry, StringName name, Variant value)
    {
        if (entry.Written.TryGetValue(name, out Variant last) && last.Equals(value)) return;
        entry.Follower.SetSurfaceParameter(name, value);
        entry.Written[name] = value;
        WriteCount++;
    }
}
