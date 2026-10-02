using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

public partial class GrassGpuGeometryTest : RefCounted
{
    public bool Run()
    {
        try
        {
            Validate(RingGrassMeshFactory.CreateClump(), RingGrassMeshFactory.CreateClump(2), 32, 16);
            Validate(RingGrassMeshFactory.CreateIntermediateClump(), RingGrassMeshFactory.CreateIntermediateClump(true), 16, 8);
            ValidateGates();
            ValidateGameplayDefaults();
            return true;
        }
        catch (Exception error) { GD.PushError("GRASS_GPU_GEOMETRY=FAIL " + error.Message); return false; }
    }

    private static void Validate(Mesh baseline, Mesh candidate, int baselineTriangles, int candidateTriangles)
    {
        using (baseline)
        using (candidate)
        {
            var source = baseline.SurfaceGetArrays(0);
            var reduced = candidate.SurfaceGetArrays(0);
            Vector3[] original = source[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] vertices = reduced[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] originalNormals = source[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector3[] normals = reduced[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector2[] originalUvs = source[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            Vector2[] uvs = reduced[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            float[] originalMetadata = source[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            float[] metadata = reduced[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            Require(original.Length == baselineTriangles * 3 && vertices.Length == candidateTriangles * 3, "triangle budget changed");
            Require(normals.Length == vertices.Length && uvs.Length == vertices.Length && metadata.Length == vertices.Length * 4, "missing geometry attributes");
            int roots = 0, tips = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                Require(float.IsFinite(vertices[i].X) && float.IsFinite(vertices[i].Y) && float.IsFinite(vertices[i].Z), "non-finite vertex");
                Require(Mathf.IsEqualApprox(normals[i].Length(), 1f), "invalid normal");
                Require(vertices[i].Y >= 0f && vertices[i].Y <= 1f, "normalized blade height changed");
                if (vertices[i].Y == 0f) roots++;
                if (vertices[i].Y == 1f) tips++;
                bool preserved = false;
                for (int j = 0; j < original.Length && !preserved; j++)
                {
                    if (vertices[i].DistanceSquaredTo(original[j]) > 1e-10f || normals[i].DistanceSquaredTo(originalNormals[j]) > 1e-10f || uvs[i].DistanceSquaredTo(originalUvs[j]) > 1e-10f) continue;
                    preserved = true;
                    for (int component = 0; component < 4; component++)
                        preserved &= metadata[i * 4 + component] == originalMetadata[j * 4 + component];
                }
                Require(preserved, "reduced geometry changed an authored root, section, normal, UV or camera-facing anchor");
                Vector3 relative = vertices[i] - candidate.GetAabb().Position;
                Vector3 extent = candidate.GetAabb().Size;
                Require(relative.X >= -.00001f && relative.Y >= -.00001f && relative.Z >= -.00001f
                    && relative.X <= extent.X + .00001f && relative.Y <= extent.Y + .00001f && relative.Z <= extent.Z + .00001f, "mesh bounds exclude a vertex");
            }
            Require(roots > 0 && tips > 0, "pinned roots or normalized tips missing");
            for (int i = 0; i < vertices.Length; i += 3)
            {
                Vector3 cross = (vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]);
                Require(cross.Length() > .000001f, "degenerate reduced triangle");
                Require((-cross.Normalized()).Dot(normals[i]) > .85f, "clockwise winding changed");
            }
        }
    }

    private static void ValidateGates()
    {
        var layer = new RingStandingGrassLayer();
        try
        {
            Require(!layer.UseReducedNearGrassGeometry && !layer.UseSingleRibbonIntermediateGrass, "experimental defaults enabled");
            layer.UseSingleRibbonIntermediateGrass = true;
            Require(!layer.IsSingleRibbonIntermediateGrassActive, "single ribbons allowed without full camera facing");
            layer.EnableDistanceLod = true;
            layer.EnableGrassCameraFacing = true;
            layer.GrassCameraFacingStrength = 1f;
            layer.GrassCameraFacingStartMeters = 0f;
            layer.GrassCameraFacingEndMeters = 1f;
            layer.LodNearDistanceMeters = 48f;
            layer.LodTransitionWidthMeters = 8f;
            Require(layer.IsSingleRibbonIntermediateGrassActive, "valid single ribbon settings rejected");
            layer.GrassCameraFacingStrength = .5f;
            Require(!layer.IsSingleRibbonIntermediateGrassActive, "partial facing allowed single ribbons");
            layer.GrassCameraFacingStrength = 1f;
            layer.GrassCameraFacingEndMeters = 48f;
            Require(!layer.IsSingleRibbonIntermediateGrassActive, "facing after handoff allowed single ribbons");
            layer.GrassCameraFacingEndMeters = 1f;
            layer.EnableDistanceLod = false;
            Require(!layer.IsSingleRibbonIntermediateGrassActive, "disabled LOD allowed single ribbons");
        }
        finally { layer.Free(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ValidateGameplayDefaults()
    {
        // Inject only the scene lookup for an isolated controller; exercise its
        // public preset/override APIs without terrain streaming or user files.
        var layer = new RingStandingGrassLayer();
        var controller = new RingGraphicsQualityController();
        Variant originalTier = ProjectSettings.GetSetting("ringworld/runtime/hardware_tier", "desktop_mid_high");
        try
        {
            typeof(RingGraphicsQualityController).GetField("_grass", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, layer);
            foreach (string tier in new[] { "desktop_mid_high", "laptop_140v_baseline" })
            {
                ProjectSettings.SetSetting("ringworld/runtime/hardware_tier", tier);
                foreach (var preset in Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
                {
                    controller.ApplyPreset(preset, applyTerrainPreset: false);
                    controller.SetGrassTuningOverrides(new Godot.Collections.Dictionary());
                    Require(layer.UseReducedNearGrassGeometry && layer.UseSingleRibbonIntermediateGrass
                        && !layer.UseSimplifiedIntermediateGrassShading, "gameplay geometry defaults incorrect");
                    Require(layer.IsSingleRibbonIntermediateGrassActive, $"single ribbons inactive for {tier}/{preset}");
                    Require(layer.GrassCameraFacingStartMeters < layer.GrassCameraFacingEndMeters,
                        "preset camera-facing range unordered");
                    using var profile = RingGrassQualityProfile.For(preset, tier);
                    Require(layer.BladesPerSquareMeter == profile.NearDensity
                        && layer.MaximumInstances == profile.MaximumInstances, "geometry defaults changed placement budgets");
                }
            }
            controller.SetGrassTuningOverrides(new Godot.Collections.Dictionary
            {
                ["LodNearDistanceMeters"] = 18f, ["LodTransitionWidthMeters"] = 12f
            });
            Require(layer.IsSingleRibbonIntermediateGrassActive, "untouched facing failed to follow LOD overrides");
            controller.SetGrassTuningOverrides(new Godot.Collections.Dictionary
            {
                ["LodNearDistanceMeters"] = 18f, ["GrassCameraFacingEndMeters"] = 80f
            });
            Require(layer.GrassCameraFacingEndMeters == 80f && !layer.IsSingleRibbonIntermediateGrassActive,
                "explicit late facing lost or unsafe ribbons selected");
            controller.SetGrassTuningOverrides(new Godot.Collections.Dictionary { ["GrassCameraFacingStrength"] = .5f });
            Require(layer.GrassCameraFacingStrength == .5f && !layer.IsSingleRibbonIntermediateGrassActive,
                "explicit partial facing lost or unsafe ribbons selected");
            controller.SetGrassTuningOverrides(new Godot.Collections.Dictionary
            {
                ["UseReducedNearGrassGeometry"] = false, ["UseSingleRibbonIntermediateGrass"] = false,
                ["EnableGrassCameraFacing"] = false, ["UseSimplifiedIntermediateGrassShading"] = true
            });
            controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Low, applyTerrainPreset: false);
            Require(!layer.UseReducedNearGrassGeometry && !layer.UseSingleRibbonIntermediateGrass
                && !layer.EnableGrassCameraFacing && layer.UseSimplifiedIntermediateGrassShading,
                "explicit detail choices did not survive preset change");
            controller.SetGrassTuningOverrides(new Godot.Collections.Dictionary());
            Require(layer.IsSingleRibbonIntermediateGrassActive && layer.UseReducedNearGrassGeometry
                && !layer.UseSimplifiedIntermediateGrassShading, "reset did not restore recommended geometry");
        }
        finally
        {
            ProjectSettings.SetSetting("ringworld/runtime/hardware_tier", originalTier);
            controller.Free();
            layer.Free();
        }
    }
}

