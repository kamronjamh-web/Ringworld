using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Grass Modifiers")]
    [Export] public bool EnableGrassModifiers { get; set; } = true;
    [Export(PropertyHint.Range, "0,1,.01")] public float GrassModifierDensityStrength { get; set; } = 1;
    [Export(PropertyHint.Range, "0,1,.01")] public float GrassModifierTintStrength { get; set; } = 1;
    [Export] public bool GrassModifierPreviewEnabled { get; set; }
    [Export] public float GrassModifierPreviewRadiusMeters { get; set; } = 8;
    [Export] public float GrassModifierPreviewFeatherMeters { get; set; } = 2;
    [Export(PropertyHint.Range, "0,1,.01")] public float GrassModifierPreviewDensity { get; set; } = .35f;
    [Export] public Color GrassModifierPreviewTint { get; set; } = new(.65f,.45f,.18f,1);
    [Export(PropertyHint.Range, "0,1,.01")] public float GrassModifierPreviewTintStrength { get; set; } = .6f;
    private readonly Dictionary<string, RingGrassModifierZone> _gameplayModifiers = new(StringComparer.Ordinal);
    private RingGrassModifierSnapshot? _modifierSnapshot;
    private ulong _modifierRevision;
    private double _nextModifierDiscovery;
    private bool _previewAnchored; private double _previewAround, _previewAcross;
    private float _capturedModifierDensityStrength = float.NaN;

    /// <summary>Sets a caller-owned canonical horizontal modifier. IDs are stable gameplay ownership keys.</summary>
    public void SetGrassModifier(string id, RingGrassModifierZone zone)
    {
        if (string.IsNullOrWhiteSpace(id) || !zone.IsValid) throw new ArgumentException("Modifier ID and finite positive ellipse are required.");
        if (_gameplayModifiers.TryGetValue(id, out var previous) && previous == zone) return;
        if (!_gameplayModifiers.ContainsKey(id) && _gameplayModifiers.Count >= 256)
            throw new InvalidOperationException("Grass modifier limit is 256 caller-owned IDs.");
        _gameplayModifiers[id] = zone; _modifierRevision++;
    }
    public bool RemoveGrassModifier(string id) { if (string.IsNullOrWhiteSpace(id)) return false; if (!_gameplayModifiers.Remove(id)) return false; _modifierRevision++; return true; }
    public bool PlaceGrassModifierPreviewAtViewer()
    {
        if (_explorer == null) return false;
        RingCanonicalPosition p=_explorer.CanonicalPosition; _previewAround=p.AroundMeters;_previewAcross=p.AcrossMeters;_previewAnchored=true; GrassModifierPreviewEnabled=true; _modifierRevision++; return true;
    }
    internal void SetGrassModifierPreviewForTests(double aroundMeters, double acrossMeters)
    {
        _previewAround = aroundMeters;
        _previewAcross = acrossMeters;
        _previewAnchored = true;
        GrassModifierPreviewEnabled = true;
        _modifierRevision++;
    }
    private RingGrassModifierSnapshot? CaptureGrassModifiers(RingTerrainSampler? sampler = null)
    {
        sampler ??= _grid?.TerrainSampler;
        if (sampler == null) return _modifierSnapshot;
        if (!EnableGrassModifiers) { if (_modifierSnapshot != null) { RingGrassModifierSnapshot empty=new(++_modifierRevision,sampler.AroundRingVoxelLength,null); InvalidateModifierRegions(_modifierSnapshot,empty,sampler); _modifierSnapshot=null; } return null; }
        float densityStrength=Mathf.Clamp(GrassModifierDensityStrength,0,1);
        if (!Mathf.IsEqualApprox(_capturedModifierDensityStrength,densityStrength))
        {
            _capturedModifierDensityStrength=densityStrength;
            if (_modifierSnapshot != null)
            {
                RingGrassModifierSnapshot previous=_modifierSnapshot;
                _modifierSnapshot=null;
                _nextModifierDiscovery=0;
                // Treat density-strength changes as a rebuild of each existing zone extent.
                InvalidateModifierRegions(previous,new RingGrassModifierSnapshot(_modifierRevision,sampler.AroundRingVoxelLength,null),sampler, forceOld: true);
            }
        }
        // Group enumeration is throttled; values are immutable before workers retain them.
        if (Time.GetTicksMsec() / 1000.0 >= _nextModifierDiscovery || _modifierSnapshot == null)
        {
            _nextModifierDiscovery = Time.GetTicksMsec() / 1000.0 + .25;
            List<RingGrassModifierSnapshot.Entry> entries = new();
            foreach (var pair in _gameplayModifiers) entries.Add(new(pair.Key, pair.Value));
            if (GrassModifierPreviewEnabled) { if(!_previewAnchored && _explorer!=null){var p=_explorer.CanonicalPosition;_previewAround=p.AroundMeters;_previewAcross=p.AcrossMeters;_previewAnchored=true;} if(_previewAnchored) entries.Add(new("__preview__",new RingGrassModifierZone(_previewAround,_previewAcross,Vector2.One*Mathf.Max(.01f,GrassModifierPreviewRadiusMeters),GrassModifierPreviewFeatherMeters,GrassModifierPreviewDensity,GrassModifierPreviewTint,GrassModifierPreviewTintStrength,true))); }
            if (_grid != null) foreach (Node node in GetTree().GetNodesInGroup(RingGrassModifier3D.GroupName))
                if (node is RingGrassModifier3D authored && authored.Targets(this) && authored.TryCapture(_grid, out var entry)) entries.Add(entry);
            RingGrassModifierSnapshot next = new(_modifierRevision, sampler.AroundRingVoxelLength, entries);
            if (!SameModifiers(_modifierSnapshot, next)) InvalidateModifierRegions(_modifierSnapshot, next, sampler);
            _modifierSnapshot = next;
        }
        return _modifierSnapshot;
    }
    private static bool SameModifiers(RingGrassModifierSnapshot? a, RingGrassModifierSnapshot? b)
    { if (a == null || b == null) return (a?.Entries.Count ?? 0)==0 && (b?.Entries.Count ?? 0)==0; if(a.AroundLength!=b.AroundLength||a.Entries.Count!=b.Entries.Count)return false; for(int i=0;i<a.Entries.Count;i++)if(a.Entries[i]!=b.Entries[i])return false; return true; }
    private void InvalidateModifierRegions(RingGrassModifierSnapshot? old, RingGrassModifierSnapshot next, RingTerrainSampler sampler, bool forceOld = false)
    {
        var changed=new List<RingGrassModifierSnapshot.Entry>(); var prior=new Dictionary<string,RingGrassModifierSnapshot.Entry>(StringComparer.Ordinal); if(old!=null)foreach(var e in old.Entries)prior[e.Id]=e; var current=new Dictionary<string,RingGrassModifierSnapshot.Entry>(StringComparer.Ordinal);foreach(var e in next.Entries)current[e.Id]=e;
        foreach(var pair in prior) if(!current.TryGetValue(pair.Key,out var n)||n!=pair.Value)changed.Add(pair.Value);
        foreach(var pair in current) if(!prior.TryGetValue(pair.Key,out var o)||o!=pair.Value)changed.Add(pair.Value);
        if (forceOld && old != null) foreach (var entry in old.Entries) if (!changed.Contains(entry)) changed.Add(entry);
        _keys.Clear(); var candidates=new HashSet<RingGrassTileKey>(_tiles.Keys); candidates.UnionWith(_jobs.Keys); candidates.UnionWith(_requested); candidates.UnionWith(_wanted);
        foreach(var key in candidates) { RingGrassTileBounds b=RingGrassCoordinates.Bounds(key.Around,key.Across,TileSize,sampler.AroundRingVoxelLength,sampler.RingAcrossWidthMeters); foreach(var e in changed) if(Overlaps(b,e.Zone,sampler.AroundRingVoxelLength)){_keys.Add(key);break;} }
        foreach(var key in _keys) Remove(key);
        foreach(var p in _jobs) { if(_keys.Contains(p.Key)){p.Value.Dirty=true;p.Value.Cts.Cancel();} else p.Value.Modifiers=next; }
    }
    private static bool Overlaps(in RingGrassTileBounds b, in RingGrassModifierZone z,long length)
    { double a=z.HalfSizeMeters.X,bound=z.HalfSizeMeters.Y; if(b.AcrossMin>=z.AcrossMeters+bound||b.AcrossMax<=z.AcrossMeters-bound)return false; double c=RingGrassCoordinates.Canonicalize(z.AroundMeters,length); return b.AroundMin<c+a&&b.AroundMax>c-a || (c<a&&b.AroundMin<c+length+a&&b.AroundMax>c+length-a) || (c>length-a&&b.AroundMin<c-length+a&&b.AroundMax>c-length-a); }
}
