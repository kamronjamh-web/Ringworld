using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    private sealed class GpuTreeShadowProxy
    {
        public required MultiMeshInstance3D Renderer;
        public bool Ready;
    }

    private readonly Dictionary<int, GpuTreeShadowProxy> _gpuTreeShadowProxies = new();
    private ShaderMaterial? _sharedFarTreeMaterial;

    private bool HasGpuTreeShadowProxy(GpuTreeOutput output)
        => _gpuTreeShadowProxies.TryGetValue(output.BatchId, out GpuTreeShadowProxy? proxy)
           && GodotObject.IsInstanceValid(proxy.Renderer)
           && proxy.Renderer.Visible
           && proxy.Renderer.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off;

    // Called by the GPU-output owner immediately after it chooses the template material.
    // Authored LOD2 uses a cloned per-surface mesh, while the one-surface impostor can
    // safely use the material selected by that owner.
    private void ConfigureGpuTreeOutputMaterial(GpuOutputKey key, GpuTreeGroup group, ref Material? material)
    {
        if (material is ShaderMaterial shader && key.Lod >= 3)
        {
            if (group.SharedImpostor) _sharedFarTreeMaterial = shader;
            shader.SetShaderParameter("simplified_tree_shading", SimplifyDistantTreeMaterials);
        }
    }

    // The authored LOD2 clone carries one material per mesh surface, so it has
    // no MaterialOverride to pass through ConfigureGpuTreeOutputMaterial.
    // Register it at output creation as well as during policy refresh.
    private void ConfigureGpuTreeOutputMeshMaterials(GpuOutputKey key, Mesh mesh)
    {
        if (key.Lod < 2) return;
        foreach (ShaderMaterial material in ImportedTreeRenderMaterials.ShaderMaterials(mesh))
        {
            material.SetShaderParameter("simplified_tree_shading", SimplifyDistantTreeMaterials);
            RegisterTreeLightingMaterial(material);
        }
    }

    private void ReleaseGpuTreeOutputMaterial(GpuTreeOutput output) => ReleaseGpuTreeShadowProxy(output);

    private void ApplyGpuTreeSunShadowChoice(GpuOutputKey key, GpuTreeOutput output, bool selected)
    {
        if (!selected)
        {
            ReleaseGpuTreeShadowProxy(output);
            SetCastShadow(output.Renderer, GeometryInstance3D.ShadowCastingSetting.Off);
            return;
        }

        // Visual LOD0/1 use authored LOD2 shadows; visual LOD2 uses its authored
        // per-variant billboard. Generic/procedural fixtures do not have these
        // catalog replacements and retain their normal shadow geometry.
        if (!UseLowDetailTreeShadows || key.Lod is < 0 or > 2 || _lod3AuthoredTemplates == null || _lod4AuthoredTemplates == null
            || _lod3AuthoredTemplates.Length != ImportedTreeCatalog.VariantCount || _lod4AuthoredTemplates.Length != ImportedTreeCatalog.VariantCount
            || key.Group.Variant < 0 || key.Group.Variant >= _lod3AuthoredTemplates.Length || key.Group.Variant >= _lod4AuthoredTemplates.Length)
        {
            ReleaseGpuTreeShadowProxy(output);
            SetCastShadow(output.Renderer, GeometryInstance3D.ShadowCastingSetting.On);
            return;
        }

        GpuTreeShadowProxy? proxy = GetOrCreateGpuTreeShadowProxy(key, output);
        if (proxy == null)
        {
            SetCastShadow(output.Renderer, GeometryInstance3D.ShadowCastingSetting.On);
            return;
        }

        if (!proxy.Ready && _gpuTreeCompute?.IsOutputMirrorReady(output.BatchId) == true)
        {
            proxy.Ready = true;
            proxy.Renderer.Visible = true;
        }
        // Keep the primary shadow until the GPU-side mirror has received a valid
        // indirect command and instance buffer. This prevents a visible gap.
        SetCastShadow(output.Renderer, proxy.Ready
            ? GeometryInstance3D.ShadowCastingSetting.Off
            : GeometryInstance3D.ShadowCastingSetting.On);
    }

    private GpuTreeShadowProxy? GetOrCreateGpuTreeShadowProxy(GpuOutputKey key, GpuTreeOutput output)
    {
        if (_gpuTreeShadowProxies.TryGetValue(output.BatchId, out GpuTreeShadowProxy? existing)
            && GodotObject.IsInstanceValid(existing.Renderer)) return existing;
        if (_lod3AuthoredTemplates == null || _lod4AuthoredTemplates == null) return null;
        MultiMesh? source = output.Renderer.Multimesh;
        Mesh? shadowSource = (key.Lod == 2 ? _lod4AuthoredTemplates : _lod3AuthoredTemplates)[key.Group.Variant].Multimesh?.Mesh;
        if (source == null || shadowSource == null || _gpuTreeCompute == null) return null;
        Mesh shadowMesh = ImportedTreeRenderMaterials.GetShadowMesh(shadowSource);
        foreach (ShaderMaterial material in ImportedTreeRenderMaterials.ShaderMaterials(shadowMesh))
        {
            RegisterTreeLightingMaterial(material);
        }
        var shadowMultiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = source.UseCustomData,
            Mesh = shadowMesh
        };
        var renderer = new MultiMeshInstance3D
        {
            Name = $"{output.Renderer.Name}_ShadowLod{(key.Lod == 2 ? 3 : 2)}",
            Multimesh = shadowMultiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
            Visible = false,
            ExtraCullMargin = output.Renderer.ExtraCullMargin
        };
        output.Renderer.AddChild(renderer);
        if (!_gpuTreeCompute.AttachOutputMirror(output.BatchId, shadowMultiMesh, shadowMesh))
        {
            renderer.QueueFree();
            return null;
        }
        renderer.SetInstanceShaderParameter("vegetation_haze_slot", _gpuTreeCompute.GetHazeSlot(output.BatchId));
        var proxy = new GpuTreeShadowProxy { Renderer = renderer };
        _gpuTreeShadowProxies[output.BatchId] = proxy;
        return proxy;
    }

    private void ReleaseGpuTreeShadowProxy(GpuTreeOutput output)
    {
        if (!_gpuTreeShadowProxies.Remove(output.BatchId, out GpuTreeShadowProxy? proxy)) return;
        _gpuTreeCompute?.DetachOutputMirror(output.BatchId);
        if (GodotObject.IsInstanceValid(proxy.Renderer)) proxy.Renderer.QueueFree();
    }

    // Kept separate from the output service so stationary players can promote a
    // just-copied mirror even when culling is cached.
    private void ServiceGpuTreeShadowMirrors()
    {
        if (_gpuTreeShadowProxies.Count == 0 || _gpuTreeCompute == null) return;
        foreach ((int batchId, GpuTreeShadowProxy proxy) in _gpuTreeShadowProxies)
        {
            if (proxy.Ready || !GodotObject.IsInstanceValid(proxy.Renderer)
                || !_gpuTreeCompute.IsOutputMirrorReady(batchId)) continue;
            proxy.Ready = true;
            proxy.Renderer.Visible = true;
            foreach (GpuTreeOutput output in _gpuTreeOutputs.Values)
                if (output.BatchId == batchId)
                {
                    SetCastShadow(output.Renderer, GeometryInstance3D.ShadowCastingSetting.Off);
                    break;
                }
        }
    }
}
