using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>TEST ONLY. Exposes static runtime state to tools/quality_preset_contract_test.gd (GDScript cannot reach C# statics).</summary>
public partial class QualityPresetContractProbe : RefCounted
{
    public bool HazeEnabled() => RingAtmosphereModel.Enabled;
    public int HazeModel() => (int)RingAtmosphereModel.HazeModel;
    public int HazeQuality() => RingAtmosphereModel.HazeQuality;
    public int PoiShaderCount() => RingPoiHaze.RegisteredPoiShaderCount;
    public int PoiRegisteredCount() => RingPoiHaze.RegisteredCount;
    public int LargePoiShadowCount() => RingLargePoiShadowExtents.Count;
}
