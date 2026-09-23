using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    /// <summary>Installs a deterministic, rendered production-tree fixture for the GPU benchmark.</summary>
    internal void ConfigureGpuTreeBenchmarkFixture(IReadOnlyList<ImportedTreeVariant> variants,
        Transform3D[][] transforms, float[][] ranks, bool[][] protectedRoots,
        long[] tileAround, int[] tileAcross, int[] groupVariants)
    {
        if (variants.Count != ImportedTreeCatalog.VariantCount) throw new ArgumentException("The complete imported tree catalog is required.", nameof(variants));
        int groups = transforms.Length;
        if (ranks.Length != groups || protectedRoots.Length != groups || tileAround.Length != groups || tileAcross.Length != groups || groupVariants.Length != groups)
            throw new ArgumentException("Fixture group arrays must have equal lengths.");
        ClearGpuTreeResidentSources("Benchmark fixture reset"); _gpuTreeFixture = true; _gpuTreeGroups.Clear();

        MultiMeshInstance3D[] Make(int lod)
        {
            var result = new MultiMeshInstance3D[variants.Count];
            for (int variant = 0; variant < variants.Count; variant++)
            {
                result[variant] = new MultiMeshInstance3D { Multimesh = new MultiMesh { Mesh = variants[variant].Lods[lod] }, Visible = false,
                    CastShadow = lod < 3 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(result[variant]); RegisterTreeSunShadowRenderer(result[variant], lod < 3 ? lod : -1);
            }
            return result;
        }
        _lod1AuthoredTemplates = Make(0); _lod2AuthoredTemplates = Make(1); _lod3AuthoredTemplates = Make(2); _lod4AuthoredTemplates = Make(3);
        const double circumference = 31_415_936;
        VegetationLayout layout = CaptureVegetationLayout(circumference);
        for (int g = 0; g < groups; g++)
        {
            int count = transforms[g].Length, variant = groupVariants[g];
            if (variant < 0 || variant >= variants.Count || ranks[g].Length != count || protectedRoots[g].Length != count)
                throw new ArgumentException("Fixture groups require a valid variant and one rank/protection value per transform.");
            (double aroundOrigin, double acrossOrigin) = VegetationTileCenter(new VegetationTileKey(tileAround[g], tileAcross[g]), layout);
            long stableAroundOrigin = (long)aroundOrigin; int stableAcrossOrigin = (int)acrossOrigin;
            var rows = new float[count * 12]; var metadata = new Vector4[count]; Aabb bounds = default;
            Vector2 minimum = new(float.PositiveInfinity, float.PositiveInfinity), maximum = -minimum;
            for (int i = 0; i < count; i++)
            {
                Transform3D transform = transforms[g][i]; WriteGpuTreeTransform(rows, i * 12, transform);
                Vector2 canonical = new((float)WrappedVegetationDelta(transform.Origin.X - stableAroundOrigin, circumference), transform.Origin.Z - stableAcrossOrigin);
                metadata[i] = new Vector4(ranks[g][i], protectedRoots[g][i] ? 1 : 0, canonical.X, canonical.Y);
                minimum = minimum.Min(canonical); maximum = maximum.Max(canonical);
                Aabb next = transform * variants[variant].Lods[0].GetAabb();
                for (int lod = 1; lod < 4; lod++) next = next.Merge(transform * variants[variant].Lods[lod].GetAabb());
                bounds = i == 0 ? next : bounds.Merge(next);
            }
            var key = new GpuGroupKey(tileAround[g], tileAcross[g], variant);
            _gpuTreeGroups[key] = new GpuTreeGroup { Key = key, Roots = Array.Empty<CachedTree>(), Rows = rows, Metadata = metadata,
                Bounds = bounds.Grow(24), AroundOrigin = stableAroundOrigin, AcrossOrigin = stableAcrossOrigin,
                CanonicalMin = minimum, CanonicalMax = maximum, HasProtected = Array.Exists(metadata, value => value.Y > .5f) };
        }
        RebuildGpuTreeMergedGroups(circumference); _gpuFixtureLastMerge = MergeFarGpuTreeBatches; InvalidateGpuTreeFrameCache(); GpuTreeSourceEpoch++;
    }

    /// <summary>Reads one indirect batch. Aggregate command buffers repeat counts per mesh surface.</summary>
    internal void GpuTreeFixtureReadbackBatch(int batchId, Action<uint[], float[]> callback)
    {
        if (_gpuTreeCompute is null) callback(Array.Empty<uint>(), Array.Empty<float>());
        else _gpuTreeCompute.RequestReadbackForTests(batchId, callback);
    }

    internal void ResetGpuTreeBenchmarkFrameCache() => InvalidateGpuTreeFrameCache();
    internal long GpuTreeBenchmarkDispatchCount => _gpuTreeCompute?.DispatchCount ?? 0;
    internal long GpuTreeBenchmarkBarrierCount => _gpuTreeCompute?.BarrierCount ?? 0;
    internal void ServiceGpuTreeBenchmarkShadowMirrors() => ServiceGpuTreeShadowMirrors();

    /// <summary>Benchmark-only presentation override; it changes no source, batch, or compute-frame state.</summary>
    internal void SetGpuTreeBenchmarkPresentation(bool treesVisible, int shadowBatchLimit, float shadowDistance)
    {
        Visible = treesVisible; MaximumTreeSunShadowBatches = shadowBatchLimit; TreeSunShadowDistanceMeters = shadowDistance;
        MarkTreeSunShadowPolicyDirty(); ApplyTreeSunShadowState();
    }
}
