using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Debug A/B for tools/atmosphere_lid_capture.gd (containment field WO9): switches the physical-haze lid off (the pre-WO9 box: 20 km cap,
/// inner-face side planes, old camera interior factor) and back on with the profile the bridge published. Sunshine keeps its own GenericData
/// copy (the cloud ceiling and the cloud haze stay on the lid in both states). Never used by production code.
/// </summary>
public partial class AtmosphereLidCaptureProbe : RefCounted
{
    private static ContainmentFieldProfile _saved;

    public bool HasLid() => RingAtmosphereModel.FieldProfile.Exists;

    public bool SetLid(bool on)
    {
        if (RingAtmosphereModel.FieldProfile.Exists) _saved = RingAtmosphereModel.FieldGeometry;
        if (on)
        {
            if (!_saved.IsValid) return false;
            RingAtmosphereModel.SetContainmentField(_saved);
        }
        else RingAtmosphereModel.ClearContainmentField();
        return true;
    }

    /// <summary>(wall top, apex, crown, root0, rootW) of the published or saved profile.</summary>
    public float[] Profile()
    {
        ContainmentFieldProfile p = RingAtmosphereModel.FieldProfile.Exists ? RingAtmosphereModel.FieldGeometry : _saved;
        return new[] { (float)p.WallTopMeters, (float)p.ApexMeters, (float)p.CrownMeters, (float)p.RootAcross0, (float)p.RootAcrossW };
    }
}
