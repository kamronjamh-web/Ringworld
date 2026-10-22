using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    [Export] public bool UseStagedStartup { get; set; }
    private IEnumerator<bool>? _startupResources;
    private bool _startupResourcesComplete;
    private string _startupResourceError = string.Empty;
    private string _startupResourcePhase = "tree assets";
    public double LastStartupWorkMilliseconds { get; private set; }
    public double MaximumStartupWorkMilliseconds { get; private set; }
    public bool StartupComplete => !Enabled || !UseStagedStartup || _startupResourcesComplete;
    public Godot.Collections.Dictionary GetLoadingStatus() => new()
    {
        ["ready"] = StartupComplete,
        ["phase"] = _startupResourcePhase,
        ["error"] = _startupResourceError,
        ["last_work_ms"] = LastStartupWorkMilliseconds,
        ["maximum_work_ms"] = MaximumStartupWorkMilliseconds
    };

    private void ServiceStartupResources()
    {
        if (_startupResources == null) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            for (int units = 0; units < 2; units++)
            {
                if (!_startupResources.MoveNext())
                {
                    DisposeStartupResources();
                    _startupResourcesComplete = true;
                    break;
                }
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
            }
        }
        catch (Exception exception)
        {
            _startupResourceError = exception.Message;
            DisposeStartupResources();
            GD.PushError($"Tree startup failed: {exception.Message}");
        }
        finally
        {
            LastStartupWorkMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            MaximumStartupWorkMilliseconds = Math.Max(MaximumStartupWorkMilliseconds, LastStartupWorkMilliseconds);
        }
    }

    private IEnumerable<bool> PrepareStartupResources()
    {
        if (!UseRegionalImportedForest)
        {
            InitializeStartupResources();
            yield break;
        }
        UsePersistentVegetationTiles = true;
        foreach (bool unit in ImportedTreeCatalog.PrepareCooperatively()) yield return unit;
        IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
        _startupResourcePhase = "tree templates";
        var templates = new MultiMeshInstance3D[4][];
        for (int lod = 0; lod < templates.Length; lod++)
        {
            templates[lod] = new MultiMeshInstance3D[variants.Count];
            for (int i = 0; i < variants.Count; i++)
            {
                ImportedTreeVariant variant = variants[i];
                templates[lod][i] = CreateInstances($"ImportedForestLod{lod}_{variant.Index}", variant.Lods[lod], null, lod < 3, lod < 3 ? lod : -1);
                foreach (ShaderMaterial material in variant.Materials) RegisterTreeLightingMaterial(material);
                yield return true;
            }
        }
        _lod1AuthoredTemplates = templates[0];
        _lod2AuthoredTemplates = templates[1];
        _lod3AuthoredTemplates = templates[2];
        _lod4AuthoredTemplates = templates[3];
        _lod4Trees = CreateInstances("BiomeTreesLod4", new QuadMesh { Size = Vector2.One }, CreateBillboardMaterial(new Color(.08f, .27f, .10f, 1)), false);
        yield return true;
        _startupResourcePhase = "tree impostors";
        foreach (bool unit in ImportedTreeImpostorCatalog.PrepareCooperatively()) yield return unit;
        _lod5Trees = CreateInstances("BiomeTreesLod5", new QuadMesh { Size = Vector2.One }, CreateBillboardMaterial(new Color(.08f, .27f, .10f, 1)), false);
        _startupResourcePhase = "tree streaming";
    }

    private void DisposeStartupResources()
    {
        _startupResources?.Dispose();
        _startupResources = null;
    }
}
