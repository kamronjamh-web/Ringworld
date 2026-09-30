using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Guarded synchronous path used only for the M6 travel comparison fixture.</summary>
public partial class RingTerrain3DGameplay
{
    private const string BenchmarkWorldDirectoryPrefix = "res://.godot/terrain-generation-tests/m6-";

    public bool EnableSynchronousBenchmarkMode()
    {
        if (IsInsideTree() || IsNodeReady() || _field != null || _surface != null) return false;
        string directory = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        if (!directory.StartsWith(BenchmarkWorldDirectoryPrefix, StringComparison.Ordinal)) return false;
        _synchronousBenchmark = true;
        return true;
    }

    private void ProcessSynchronousBenchmark(RingCanonicalPosition position)
    {
        if (_surface == null || _explorer == null) return;
        EnsureSynchronousBenchmarkCoverage(position);
        _surface.SetWorldFrame(_lastWorldFrame);
        _surface.RefreshEditedSurface();
    }

    private void EnsureSynchronousBenchmarkCoverage(RingCanonicalPosition position)
    {
        if (_surface == null || _field == null) return;
        double around = _frame.SignedAroundDeltaMeters(position.AroundMeters);
        double across = position.AcrossMeters - _frame.AnchorAcrossMeters;
        if (Math.Abs(around) < 128.0 && Math.Abs(across) < 128.0) return;

        _frame = BuildSynchronousBenchmarkFrame(position);
        _lastWorldFrame = BuildWorldFrame(_frame);
        _surface.RebuildWindow(_frame);
        _surface.SetWorldFrame(_lastWorldFrame);
        _surface.SetHandoffDistance(_handoffDistanceMeters);
        _surface.SetMeshLods(_meshLods);
        _surface.SetMeshSize(_meshSize);
    }

    private RingSurfaceFrame BuildSynchronousBenchmarkFrame(RingCanonicalPosition position)
    {
        long around = (long)Math.Round(position.AroundMeters / 2.0) * 2;
        int across = Mathf.Clamp((int)Math.Round(position.AcrossMeters / 2.0) * 2,
            0, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters) - 2);
        return new RingSurfaceFrame(around, across, _field!.SampleHeight(around, across),
            _field.CircumferenceMeters, _grid.CaptureLodGeometrySnapshot().RingRadiusMeters,
            _field.SpacingMeters, -1);
    }
}
