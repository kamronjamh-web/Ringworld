using System;
using System.Diagnostics.CodeAnalysis;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Public-API bridge for the rendered FFT swimming fixture.</summary>
public partial class WaterFftSwimmingProbe : RefCounted
{
    /// <summary>Keeps the observer on the selected ocean column while its bounded GPU query warms.</summary>
    public bool PrimeOceanFftQuery(Node game, Godot.Collections.Dictionary ocean)
    {
        if (!TryResolve(game, ocean, out RingVoxelExplorerController? explorer,
                out RingWaterFftController? fft, out RingCanonicalPosition position, out _))
            return false;
        explorer.SetBenchmarkCanonicalPosition(position, 0.0f, simulatedNoclip: false);
        return fft.TryGetOceanSurfaceOffset(explorer.CanonicalPosition, out _);
    }

    /// <summary>Starts actual physics two metres below the gameplay water top.</summary>
    public Godot.Collections.Dictionary BeginSwimming(Node game, Godot.Collections.Dictionary ocean)
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        if (!TryResolve(game, ocean, out RingVoxelExplorerController? explorer,
                out _, out RingCanonicalPosition top, out float waterTop))
            return result;

        RingCanonicalPosition start = top with { VerticalMeters = waterTop - 2.0d };
        explorer.SetBenchmarkCanonicalPosition(start, 0.0f, simulatedNoclip: false);
        explorer.ClearBenchmarkOverride();
        result["valid"] = true;
        result["start_vertical"] = start.VerticalMeters;
        result["base_surface"] = waterTop;
        return result;
    }

    public Godot.Collections.Dictionary Capture(Node game)
    {
        var result = new Godot.Collections.Dictionary
        {
            ["valid"] = false,
            ["query_ready"] = false,
        };
        RingVoxelExplorerController? explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        RingWaterFftController? fft = game.GetNodeOrNull<RingWaterFftController>("WaterFftController");
        if (explorer is null || fft is null) return result;

        // Capture the sample consumed by UpdateWaterState, not a newer async
        // readback which may have arrived after its physics tick.
        bool queryReady = explorer.ConsumedOceanFftQueryReady;
        bool freshQueryReady = fft.TryGetOceanSurfaceOffset(
            queryReady ? explorer.ConsumedOceanFftQueryPosition : explorer.CanonicalPosition, out float freshOffset);
        bool parityReady = queryReady && freshQueryReady
            && explorer.ConsumedOceanFftQuerySerial == fft.SurfaceQuerySampleSerial;
        result["parity_ready"] = parityReady;
        result["fresh_query_ready"] = freshQueryReady;
        result["fresh_offset"] = freshOffset;
        result["consumed_query_serial"] = explorer.ConsumedOceanFftQuerySerial;
        result["consumed_query_time"] = explorer.ConsumedOceanFftQueryTimeSeconds;
        result["fresh_query_serial"] = fft.SurfaceQuerySampleSerial;
        result["valid"] = double.IsFinite(explorer.CanonicalPosition.VerticalMeters)
            && float.IsFinite(explorer.CurrentWaterSurfaceHeightMeters)
            && float.IsFinite(explorer.CurrentWaterDepthMeters);
        result["query_ready"] = queryReady;
        result["offset"] = freshOffset;
        result["vertical"] = explorer.CanonicalPosition.VerticalMeters;
        result["surface"] = explorer.CurrentWaterSurfaceHeightMeters;
        result["depth"] = explorer.CurrentWaterDepthMeters;
        result["swimming"] = explorer.IsSwimming;
        result["underwater"] = explorer.IsCameraUnderwater;
        return result;
    }

    private static bool TryResolve(Node game, Godot.Collections.Dictionary ocean,
        [NotNullWhen(true)] out RingVoxelExplorerController? explorer, [NotNullWhen(true)] out RingWaterFftController? fft,
        out RingCanonicalPosition top, out float waterTop)
    {
        explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        fft = game.GetNodeOrNull<RingWaterFftController>("WaterFftController");
        top = default;
        waterTop = 0.0f;
        if (explorer is null || fft is null
            || !ocean.TryGetValue("valid", out Variant valid) || !valid.AsBool()
            || !ocean.TryGetValue("water_kind", out Variant kind) || kind.AsInt32() != 1
            || !ocean.TryGetValue("around", out Variant around)
            || !ocean.TryGetValue("across", out Variant across)
            || !ocean.TryGetValue("surface", out Variant surface)) return false;
        // FindOcean publishes TerrainSample.WaterSurfaceHeight through a
        // Dictionary Variant. Match its established double conversion before
        // narrowing to the controller's float water-surface API.
        waterTop = (float)surface.AsDouble() + 1.0f;
        if (!float.IsFinite(waterTop)) return false;
        top = new RingCanonicalPosition(around.AsDouble(), waterTop, across.AsDouble());
        return true;
    }
}
