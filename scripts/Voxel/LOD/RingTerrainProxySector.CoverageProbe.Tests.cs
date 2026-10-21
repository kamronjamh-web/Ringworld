using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only read access to installed sector footprints for live coverage checks.</summary>
public partial class RingTerrainProxySector
{
    /// <summary>True when a mesh is installed for the sector containing the canonical position.</summary>
    public bool HasInstalledSectorAt(double aroundMeters, double acrossMeters)
    {
        int size = Mathf.Max(32, SectorSizeMeters);
        long aroundIndex = WrapAroundSector(
            (long)Math.Floor(aroundMeters / size), ResolveAroundSectorLength(size));
        int acrossIndex = (int)Math.Floor(acrossMeters / size);
        return _active.ContainsKey(new RingTerrainSectorCoordinate(aroundIndex, acrossIndex));
    }

    /// <summary>Installed mesh statistics: sectors, terrain/ocean triangles and ocean-dominated sectors.</summary>
    public Godot.Collections.Dictionary GetDrawStats()
    {
        long terrain = 0, ocean = 0;
        int oceanDominated = 0, withOcean = 0, sectors = 0;
        foreach ((RingTerrainSectorCoordinate _, MeshInstance3D node) in _active)
        {
            if (node.Mesh is not ArrayMesh mesh) continue;
            sectors++;
            long t = 0, o = 0;
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                long tris = mesh.SurfaceGetArrayIndexLen(i) / 3;
                if (_oceanMaterial != null && mesh.SurfaceGetMaterial(i) == _oceanMaterial) o += tris; else t += tris;
            }
            terrain += t;
            ocean += o;
            if (o > 0) withOcean++;
            if (o > 0 && o * 10 >= t * 9) oceanDominated++;
        }
        return new Godot.Collections.Dictionary
        {
            ["sectors"] = sectors, ["terrain_tris"] = terrain, ["ocean_tris"] = ocean,
            ["sectors_with_ocean"] = withOcean, ["ocean_dominated"] = oceanDominated,
        };
    }
}
