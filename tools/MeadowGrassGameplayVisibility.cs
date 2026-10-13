using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Weather;

/// <summary>Fixture-only source-backed camera, independent of indirect GPU output readback.</summary>
public partial class MeadowGrassGameplayVisibility : RefCounted
{
    public Camera3D CreateSourceCamera(Node game)
    {
        var layer = game.GetNode<RingStandingGrassLayer>("StandingGrass");
        var grid = game.GetNode<VoxelChunkGridManager>("ChunkGridManager");
        var focus = game.GetNode<RingVoxelExplorerController>("Explorer").CanonicalPosition;
        var geometry = grid.CaptureLodGeometrySnapshot();
        foreach (var data in layer.CaptureTileDataForTests())
        {
            if (focus.AroundMeters < data.Bounds.AroundMin || focus.AroundMeters >= data.Bounds.AroundMax
                || focus.AcrossMeters < data.Bounds.AcrossMin || focus.AcrossMeters >= data.Bounds.AcrossMax || data.InstanceCount == 0) continue;
            Transform3D tile = RingGrassCoordinates.CreateTileWorldTransform(data.Bounds, data.AnchorHeight, geometry);
            Transform3D localFocus = RingGrassCoordinates.CreateBladeLocalTransform(data.Bounds, data.AnchorHeight,
                focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters, 1, 1, 0, geometry);
            int best = 0;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < data.Buffer.Length; i += RingGrassTileData.FloatsPerInstance)
            {
                float dx = data.Buffer[i + 3] - localFocus.Origin.X, dz = data.Buffer[i + 11] - localFocus.Origin.Z;
                float distance = dx * dx + dz * dz;
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }
            float[] b = data.Buffer;
            Basis basis = new(new Vector3(b[best], b[best + 4], b[best + 8]),
                new Vector3(b[best + 1], b[best + 5], b[best + 9]), new Vector3(b[best + 2], b[best + 6], b[best + 10]));
            Transform3D blade = tile * new Transform3D(basis, new Vector3(b[best + 3], b[best + 7], b[best + 11]));
            Vector3 up = blade.Basis.Y.Normalized(), tangent = blade.Basis.Z.Normalized();
            var camera = new Camera3D { Name = "MeadowVisibilityCamera", Far = 100_000, Fov = 70 };
            game.AddChild(camera);
            camera.GlobalPosition = blade.Origin + up * 1.5f + tangent * 4f;
            camera.LookAt(blade.Origin + up * .04f, up);
            camera.MakeCurrent();
            return camera;
        }
        throw new InvalidOperationException("Focused grassland has no canonical CPU grass source.");
    }

    public void Configure(Node game, bool clumps)
    {
        var layer = game.GetNode<RingStandingGrassLayer>("StandingGrass");
        using var quality = RingGrassQualityProfile.For(RingTerrainLodCoordinator.QualityPreset.High);
        quality.ApplyTo(layer);
        layer.UseGrassClumps = clumps;
        layer.EnableMeadowTufts = true;
        layer.BladesPerSquareMeter = 16;
        layer.GrassMinimumHeightMeters = .10f;
        layer.GrassMaximumHeightMeters = .20f;
        layer.EnableGrassAppearance = true;
        layer.EnableCoordinatedGrassHandoff = true;
        layer.GrassTerrainBlendStartPercent = 0;
        layer.GrassTerrainBlendEndPercent = 1;
        layer.GrassTerrainHandoffStrength = 1;
        layer.GrasslandBiomeHeight = layer.ForestBiomeHeight = 1;
        layer.UseReducedNearGrassGeometry = true;
        layer.EnableGrassGustTexture = false;
        layer.GrassWindStrengthMultiplier = 0;
        layer.GrassWindGustMultiplier = 0;
        layer.EnableGrassInteraction = false;
        layer.EnableGrassModifiers = false;
        layer.EnableGrassCameraFacing = false;
        RingWindSettings.Current.Enabled = false;
    }

    public Godot.Collections.Dictionary Snapshot(Node game)
    {
        var layer = game.GetNode<RingStandingGrassLayer>("StandingGrass");
        return new() { ["resident_sources"] = layer.ResidentGrassInstanceCount, ["resident_tiles"] = layer.ResidentGrassTileCount,
            ["rendered_tiles"] = layer.RenderedGrassTileCount, ["installed_sources"] = layer.InstalledGrassClumpCount,
            ["effective_cutoff"] = layer.EffectiveGrassDrawDistance(), ["appearance_start_percent"] = layer.GrassTerrainBlendStartPercent,
            ["appearance_end_percent"] = layer.GrassTerrainBlendEndPercent, ["gpu_status"] = layer.GpuCullingStatus,
            ["minimum_height"] = layer.GrassMinimumHeightMeters, ["maximum_height"] = layer.GrassMaximumHeightMeters,
            ["clumps"] = layer.UseGrassClumps, ["near_blades_per_source"] = layer.GrassBladesPerSource };
    }
}
