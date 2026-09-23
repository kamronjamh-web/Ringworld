using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    // The compact profile deliberately leaves authored LOD1 unused.  It retains
    // the high-detail mesh only around the player and uses the substantially
    // cheaper authored LOD2 for the working range.
    [ExportGroup("Tree render policy")]
    [Export] public bool UseCompactTreeLodProfile { get; set; } = true;
    [Export(PropertyHint.Range, "0,200,1")] public float TreeNearDetailFadeStartMeters { get; set; } = 25;
    [Export(PropertyHint.Range, "0,200,1")] public float TreeNearDetailFadeEndMeters { get; set; } = 40;
    [Export(PropertyHint.Range, "0,2000,1")] public float TreeBillboardFadeStartMeters { get; set; } = 300;
    [Export(PropertyHint.Range, "0,2000,1")] public float TreeBillboardFadeEndMeters { get; set; } = 350;
    [Export] public bool MergeMidGpuTreeBatches { get; set; } = true;
    [Export(PropertyHint.Range, "1,4,1")] public int MidTreeBatchTileSpan { get; set; } = 2;
    [Export] public bool PruneEmptyGpuTreeBatches { get; set; } = true;
    [Export] public bool SimplifyDistantTreeMaterials { get; set; } = true;
    [Export] public bool UseLowDetailTreeShadows { get; set; } = true;

    // Test-only escape hatch: synthetic four-mesh fixtures do not carry the
    // imported catalog's thirty variants, but need to exercise the same GPU
    // selection and dither ABI.
    internal bool ForceCompactTreeLodProfileForTests { get; set; }

    private bool EffectiveCompactTreeLodProfile
        => UseCompactTreeLodProfile && (ForceCompactTreeLodProfileForTests || (!_gpuTreeFixture
            && _lod1AuthoredTemplates?.Length == 30
            && _lod2AuthoredTemplates?.Length == 30
            && _lod3AuthoredTemplates?.Length == 30
            && _lod4AuthoredTemplates?.Length == 30));

    private int EffectiveMidTreeBatchTileSpan => Mathf.Clamp(MidTreeBatchTileSpan, 1, 4);

    private void CompactTreeFadeBounds(out float nearStart, out float nearEnd, out float billboardStart, out float billboardEnd)
    {
        nearStart = float.IsFinite(TreeNearDetailFadeStartMeters) ? Mathf.Clamp(TreeNearDetailFadeStartMeters, 0, 200) : 25;
        nearEnd = float.IsFinite(TreeNearDetailFadeEndMeters) ? Mathf.Clamp(TreeNearDetailFadeEndMeters, nearStart, 200) : Math.Max(nearStart, 40);
        billboardStart = float.IsFinite(TreeBillboardFadeStartMeters) ? Mathf.Clamp(TreeBillboardFadeStartMeters, nearEnd, 2000) : Math.Max(nearEnd, 300);
        billboardEnd = float.IsFinite(TreeBillboardFadeEndMeters) ? Mathf.Clamp(TreeBillboardFadeEndMeters, billboardStart, 2000) : Math.Max(billboardStart, 350);
    }

    private bool UsesTreeDitherData(int lod) => EffectiveCompactTreeLodProfile && lod is 0 or 2 or 3;

    private bool IsGpuTreeLodEnabled(int lod) => !EffectiveCompactTreeLodProfile || lod != 1;

    private int GpuTreeRenderPolicySignature()
    {
        CompactTreeFadeBounds(out float a, out float b, out float c, out float d);
        return HashCode.Combine(EffectiveCompactTreeLodProfile, MergeMidGpuTreeBatches, EffectiveMidTreeBatchTileSpan,
            PruneEmptyGpuTreeBatches, a, b, c, d);
    }

    private void GpuTreeLodInterval(int lod, float[] edges, out float near, out float far, out float fadeInStart, out float fadeInEnd, out float fadeOutStart, out float fadeOutEnd)
    {
        near = edges[lod]; far = edges[lod + 1]; fadeInStart = fadeInEnd = fadeOutStart = fadeOutEnd = -1;
        if (!EffectiveCompactTreeLodProfile) return;
        CompactTreeFadeBounds(out float nearStart, out float nearEnd, out float farStart, out float farEnd);
        switch (lod)
        {
            case 0: near = 0; far = nearEnd; fadeOutStart = nearStart; fadeOutEnd = nearEnd; break;
            case 1: near = far = nearEnd; break;
            case 2: near = nearStart; far = farEnd; fadeInStart = nearStart; fadeInEnd = nearEnd; fadeOutStart = farStart; fadeOutEnd = farEnd; break;
            case 3: near = farStart; far = edges[4]; fadeInStart = farStart; fadeInEnd = farEnd; break;
        }
    }
}
