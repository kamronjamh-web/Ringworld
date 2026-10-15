using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Emitter bore apertures (M1-8): the on/off setting, the replacement-window revisions and the single authority
/// (<see cref="SurfaceApertureConsumersActive"/>) that the bore layer, the collision-height semantics and the underground
/// probe all read. There is never a hole without a floor: when switched off the floor stays until the lid is committed,
/// and when switched on the floor appears before the hole opens.
/// </summary>
public partial class RingTerrain3DGameplay
{
    private bool _aperturesEnabled = true;
    private long _apertureRevision;
    private long _pendingApertureRevision;
    private long _committedApertureRevision;

    public bool SurfaceAperturesEnabled => _aperturesEnabled;
    public int CommittedHoleVertexCount => _surface?.CommittedHoleVertexCount ?? 0;
    public bool ApertureReplacementPending => _apertureRevision != _committedApertureRevision;

    /// <summary>True while a bore floor must exist: the setting is on, or a committed window still has hole vertices.</summary>
    public bool SurfaceApertureConsumersActive
        => _sampler?.EmitterSites != null && (_aperturesEnabled || CommittedHoleVertexCount > 0);

    /// <summary>Turns the surface holes on or off. The new mask applies with the next window swap (the window is replaced in place).</summary>
    public void SetSurfaceAperturesEnabled(bool enabled)
    {
        if (_aperturesEnabled == enabled) return;
        _aperturesEnabled = enabled;
        ConfigureApertureFlags();
        _apertureRevision++;
        if (!_startupPending && _explorer != null && _stream != null && _surface != null)
            RequestCoverage(_explorer.CanonicalPosition, true);
    }

    private void ConfigureApertureFlags()
    {
        if (_stream != null) _stream.AperturesEnabled = _aperturesEnabled;
        if (_surface != null) _surface.SurfaceAperturesEnabled = _aperturesEnabled;
    }

    /// <summary>The plug-top height when the point is inside a bore liner and bore consumers are active.</summary>
    public bool TryGetBoreFloorHeight(double around, double across, out float height)
    {
        height = 0f;
        return SurfaceApertureConsumersActive && EmitterSiteApertures.TryGetBoreFloorAt(_sampler?.EmitterSites, around, across, out height);
    }

    /// <summary>Height used to lift the capsule when a window activates: the bore floor inside a liner, else the committed height.</summary>
    private float SampleActivationHeight(double around, double across)
        => TryGetBoreFloorHeight(around, across, out float floor) ? floor : _surface!.SampleCommittedHeight(around, across);
}
