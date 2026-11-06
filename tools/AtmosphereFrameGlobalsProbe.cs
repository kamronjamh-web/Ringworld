using Godot;
using RingworldTechDemo.Atmosphere;

namespace RingworldTechDemo;

/// <summary>GDScript bridge: RenderingServer.global_shader_parameter_get is editor-only, so tests read the managed copy of the ray-frame globals.</summary>
public partial class AtmosphereFrameGlobalsProbe : RefCounted
{
    public Variant GetValue(string name) => RingAtmosphereFrameGlobals.PublishedValue(name);
}
