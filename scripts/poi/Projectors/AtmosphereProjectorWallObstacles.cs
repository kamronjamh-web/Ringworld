using System;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Wall-top walk collision for the projector towers (<see cref="IRimWallTopObstacles"/>): the walk is analytic, so each tower's slab footprint
/// (<see cref="AtmosphereProjectorFootprint"/>) blocks the body at or below the slab top. Sites are ~249 km apart, so one tower (the one nearest the
/// end point) is enough for a single step. Pure.
/// </summary>
public sealed class AtmosphereProjectorWallObstacles : IRimWallTopObstacles
{
    private readonly AtmosphereProjectorSites _sites;
    private readonly AtmosphereProjectorFootprint _footprint;

    public AtmosphereProjectorSites Sites => _sites;

    public AtmosphereProjectorWallObstacles(AtmosphereProjectorSites sites, AtmosphereProjectorFootprint footprint)
    {
        _sites = sites;
        _footprint = footprint;
    }

    public void Resolve(int side, double around0, double across0, ref double around1, ref double across1, double vertical, double wallTop,
        double halfBodyWidth, out bool blockedAround, out bool blockedAcross)
    {
        blockedAround = false;
        blockedAcross = false;
        if (vertical - wallTop > _footprint.SlabTopMeters) return;

        int index = _sites.NearestOnWall(side, around1, across1, out double x1, out double z1);
        RimWallTopAnchor site = _sites[index];
        double c = _sites.CircumferenceMeters;
        double z0 = EmitterPoiStreamerCore.SignedWrap(around0 - site.AroundMeters, c);
        double x0 = across0 - site.AcrossMeters;

        double step = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        double reachZ = _footprint.MaxAbsZ + halfBodyWidth + step;
        double reachX = _footprint.MaxAbsX + halfBodyWidth + step;
        if ((Math.Abs(z0) > reachZ && Math.Abs(z1) > reachZ) || (Math.Abs(x0) > reachX && Math.Abs(x1) > reachX)) return;

        (double rx, double rz) = _footprint.ResolveMove(x0, z0, x1, z1, halfBodyWidth, out bool blockedX, out bool blockedZ);
        blockedAcross = blockedX;
        blockedAround = blockedZ;
        if (rx == x1 && rz == z1) return;

        double around = site.AroundMeters + rz;
        around %= c;
        if (around < 0d) around += c;
        around1 = around;
        across1 = site.AcrossMeters + rx;
    }
}
