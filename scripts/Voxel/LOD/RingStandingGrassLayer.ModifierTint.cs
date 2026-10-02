using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private Texture2DArray? _grassModifierAtlas;
    private readonly Dictionary<RingGrassTileKey, int> _grassModifierSlots = new();
    private readonly Stack<int> _freeGrassModifierSlots = new();
    private int _grassModifierCapacity;

    internal void ConfigureGrassModifierMaterial(ShaderMaterial? material)
    {
        if (material == null) return;
        if (_grassModifierAtlas != null) material.SetShaderParameter("grass_modifier_atlas", _grassModifierAtlas);
        material.SetShaderParameter("grass_modifier_enabled", EnableGrassModifiers && GrassModifierTintStrength > 0 && _grassModifierAtlas != null);
        material.SetShaderParameter("grass_modifier_tint_strength", Mathf.Clamp(GrassModifierTintStrength, 0, 1));
    }
    internal void ConfigureGrassModifierRenderer(MultiMeshInstance3D? renderer, RingGrassTileData data, RingLodGeometrySnapshot geometry)
    {
        if (renderer == null) return;
        int layer = _grassModifierSlots.TryGetValue(data.Key, out int slot) ? slot : -1;
        if (!CalculateGrassGroundCovectors(geometry, data.AnchorHeight, data.Bounds.AroundCenter,
                out Vector3 aroundCovector, out Vector3 acrossCovector)) layer = -1;
        renderer.SetInstanceShaderParameter("grass_modifier_layer", layer);
        renderer.SetInstanceShaderParameter("grass_modifier_origin_world", renderer.GlobalTransform.Origin);
        float aroundInverseSpan = (float)(1d / Math.Max(.001, data.Bounds.AroundMax - data.Bounds.AroundMin));
        float acrossInverseSpan = (float)(1d / Math.Max(.001, data.Bounds.AcrossMax - data.Bounds.AcrossMin));
        renderer.SetInstanceShaderParameter("grass_modifier_around_world",
            new Vector4(aroundCovector.X, aroundCovector.Y, aroundCovector.Z, aroundInverseSpan));
        renderer.SetInstanceShaderParameter("grass_modifier_across_world",
            new Vector4(acrossCovector.X, acrossCovector.Y, acrossCovector.Z, acrossInverseSpan));
    }
    private void InstallGrassModifierTint(RingGrassTileKey key, RingGrassTileData data)
    {
        if (data.ModifierTintBytes == null) { ReleaseGrassModifierTint(key); return; }
        EnsureGrassModifierAtlas(); if (_grassModifierAtlas == null) return;
        if (!_grassModifierSlots.TryGetValue(key, out int slot)) { if (_freeGrassModifierSlots.Count == 0) return; slot=_freeGrassModifierSlots.Pop(); _grassModifierSlots[key]=slot; }
        using Image image=Image.CreateFromData(32,32,false,Image.Format.Rgba8,data.ModifierTintBytes);
        _grassModifierAtlas.UpdateLayer(image,slot);
        ConfigureGrassModifierMaterial(_material); ConfigureGrassModifierMaterial(_intermediateMaterial);
    }
    private void ReleaseGrassModifierTint(RingGrassTileKey key)
    { if (_grassModifierSlots.Remove(key,out int slot)) _freeGrassModifierSlots.Push(slot); }
    private void EnsureGrassModifierAtlas()
    {
        int capacity=Math.Clamp(MaximumResidentTiles,16,1024); if (_grassModifierAtlas != null && _grassModifierCapacity==capacity) return;
        var images=new Godot.Collections.Array<Image>();
        Image transparent=Image.CreateFromData(32,32,false,Image.Format.Rgba8,new byte[32*32*4]);
        for(int i=0;i<capacity;i++) images.Add(transparent);
        Texture2DArray replacement=new();
        Error error=replacement.CreateFromImages(images);
        transparent.Dispose();
        if(error!=Error.Ok) { replacement.Dispose(); return; }
        _grassModifierAtlas?.Dispose(); _grassModifierAtlas=replacement; _grassModifierCapacity=capacity;
        _freeGrassModifierSlots.Clear(); _grassModifierSlots.Clear();
        // Reupload all installed residents after a capacity change; never retain
        // a stale slot assignment against a newly allocated array.
        int slot=0;
        foreach(var pair in _tiles) if(pair.Value.Data.ModifierTintBytes != null && slot<capacity)
        { _grassModifierSlots[pair.Key]=slot; using Image image=Image.CreateFromData(32,32,false,Image.Format.Rgba8,pair.Value.Data.ModifierTintBytes); _grassModifierAtlas.UpdateLayer(image,slot++); }
        for(int i=capacity-1;i>=slot;i--)_freeGrassModifierSlots.Push(i);
        ConfigureGrassModifierMaterial(_material); ConfigureGrassModifierMaterial(_intermediateMaterial);
    }
    private void DisposeGrassModifierTint() { _grassModifierSlots.Clear(); _freeGrassModifierSlots.Clear(); _grassModifierAtlas?.Dispose(); _grassModifierAtlas=null; _grassModifierCapacity=0; }
}
