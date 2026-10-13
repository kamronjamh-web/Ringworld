using System;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Integration;

public partial class RingVoxelExplorerController
{
    /// <summary>True while spawn terrain settle is holding the body; read-only for observers.</summary>
    public bool IsSpawnTerrainSettling => _spawnTerrainSettling;

    /// <summary>Positions initial streaming without recovering against terrain edits still being loaded.</summary>
    public void RestoreWorldLoadingFocus(in RingCanonicalPosition position)
    {
        if (_grid == null) return;
        _canonicalPosition = position.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        RecenterCollisionOrigin(force: true);
        _cameraBasisInitialized = false;
        ApplyWorldTransform();
        ApplyCameraRotation();
        _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
    }

    /// <summary>Advance only spawn settlement while the loading overlay owns player input.</summary>
    public Dictionary AdvanceWorldLoadingSpawn(double delta)
    {
        if (_grid?.TerrainSampler == null)
            return new Dictionary { ["ready"] = false, ["detail"] = "Preparing terrain coordinates." };

        bool covered;
        if (_noclip)
        {
            ApplyWorldTransform();
            covered = true;
        }
        else
        {
            // A loading screen must wait for real coverage. The normal gameplay
            // settle timeout is not a reason to uncover an unsupported player.
            _spawnTerrainSettleRemaining = Math.Max(1.0, _spawnTerrainSettleRemaining);
            if (!_spawnTerrainSettling) BeginSpawnTerrainSettle();
            bool settling = ProcessSpawnTerrainSettle(Math.Clamp((float)delta, 0f, .05f));
            covered = !settling;
        }

        return new Dictionary
        {
            ["ready"] = covered,
            ["detail"] = covered ? "Your location is ready." : "Preparing solid ground around your location.",
            ["around_m"] = _canonicalPosition.AroundMeters,
            ["across_m"] = _canonicalPosition.AcrossMeters,
            ["height_m"] = _canonicalPosition.VerticalMeters,
            ["flying"] = _noclip
        };
    }
}
