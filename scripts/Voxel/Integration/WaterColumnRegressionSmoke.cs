using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Integration;

public partial class WaterColumnRegressionSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            var water = new TerrainSample(12, TerrainRegion.Lowlands, 0, 0, 0, 0, 0,
                TerrainBiomeId.Lake, SurfaceWaterKind.Lake, 20);
            if (!RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 13.0)
                || !RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 21.0)
                || RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 12.0)
                || RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 11.0)
                || RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 21.01))
                throw new InvalidOperationException("generated water column bounds regression");
            var dry = water with { WaterKind = SurfaceWaterKind.None };
            if (RingVoxelExplorerController.IsWithinGeneratedWaterColumn(dry, 13.0))
                throw new InvalidOperationException("dry terrain was classified as water");
            if (!RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 21.3, 21.5f)
                || RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 21.6, 21.5f)
                || RingVoxelExplorerController.IsWithinGeneratedWaterColumn(water, 13.0, float.NaN))
                throw new InvalidOperationException("offset generated-water surface bounds regression");
            if (RingTerrainProxySector.ShouldRetainProxySurface(true, false)
                || !RingTerrainProxySector.ShouldRetainProxySurface(true, true)
                || !RingTerrainProxySector.ShouldRetainProxySurface(false, false))
                throw new InvalidOperationException("terrain/water handoff readiness regression");
            if (!RingTerrainProxySector.ShouldClipWaterTriangle(true, false, false)
                || !RingTerrainProxySector.ShouldClipWaterTriangle(true, true, false)
                || RingTerrainProxySector.ShouldClipWaterTriangle(false, false, false)
                || RingTerrainProxySector.ShouldClipWaterTriangle(true, true, true))
                throw new InvalidOperationException("shoreline triangle clipping regression");
            foreach (SurfaceWaterKind kind in new[]
                     {
                         SurfaceWaterKind.Ocean,
                         SurfaceWaterKind.Lake,
                         SurfaceWaterKind.River
                     })
            {
                Color encoded = RingWaterModel.EncodeWaterKind(Colors.White, kind);
                if (RingWaterModel.DecodeWaterKind(encoded.A) != kind)
                    throw new InvalidOperationException($"water-kind vertex encoding regression: {kind}");
            }
            GD.Print("WATER_COLUMN_REGRESSION_SMOKE=PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"WATER_COLUMN_REGRESSION_SMOKE=FAIL: {exception.Message}");
            GetTree().Quit(1);
        }
    }
}
