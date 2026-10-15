using System;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Poi;

/// <summary>One emitter site's beam: crown-to-hub geometry in the current world frame plus its on/off flag.</summary>
public readonly record struct EmitterBeamState(int SiteIndex, SiteKind Kind, EmitterBeamGeometry Geometry, bool Active);

/// <summary>
/// Eclipse beam API for the emitter sites (docs/emitter-poi-streaming-spec.md). Data only: no visuals. Geometry is
/// computed on demand from the current floating-origin snapshot; active flags default to false.
/// </summary>
public interface IEmitterBeamSource
{
    int Count { get; }
    ulong Identity { get; }
    int MainSiteIndex { get; }
    /// <summary>Increments whenever the world frame is rebuilt, so cached geometry can be invalidated.</summary>
    int GeometryRevision { get; }
    bool TryGetBeam(int site, out EmitterBeamState beam);
    bool IsBeamActive(int site);
    void SetBeamActive(int site, bool active);
    void SetAllBeamsActive(bool active);
    event Action? BeamsChanged;
}
