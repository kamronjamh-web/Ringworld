using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Reads and writes POI architecture material parameters for both the POI ShaderMaterial (materials/ring_poi_architecture*.gdshader,
/// what every converted assets/poi .tres is) and a plain StandardMaterial3D (fallbacks, old scenes). The .tres saver omits parameters
/// equal to the shader default and <c>get_shader_parameter</c> then returns null, so reads fall back to the shader default.
/// </summary>
public static class PoiMaterialParams
{
    public const string ShaderPath = "res://materials/ring_poi_architecture.gdshader";
    public const string DoubleSidedShaderPath = "res://materials/ring_poi_architecture_double_sided.gdshader";
    public static readonly StringName EnabledName = "poi_haze_enabled";
    public static readonly StringName CloudShadowName = "poi_cloud_shadow_enabled";
    private static readonly StringName AlbedoName = "albedo_color";
    private static readonly StringName MetallicName = "metallic_value";
    private static readonly StringName RoughnessName = "roughness_value";
    private static readonly StringName EmissionColorName = "emission_color";
    private static readonly StringName EmissionEnergyName = "emission_energy";

    public static bool IsPoiShader(Material? material)
        => material is ShaderMaterial { Shader: { } shader }
            && (shader.ResourcePath == ShaderPath || shader.ResourcePath == DoubleSidedShaderPath);

    /// <summary>Albedo, metallic, roughness and the emissive colour (black when the surface does not glow; energy is not applied).</summary>
    public static bool TryGetSurface(Material? material, out Color albedo, out float metallic, out float roughness, out Color emission)
    {
        switch (material)
        {
            case BaseMaterial3D standard:
                albedo = standard.AlbedoColor; metallic = standard.Metallic; roughness = standard.Roughness;
                emission = standard.EmissionEnabled ? standard.Emission : new Color(0f, 0f, 0f);
                return true;
            case ShaderMaterial shaderMaterial when IsPoiShader(shaderMaterial):
                albedo = GetColor(shaderMaterial, AlbedoName, Colors.White);
                metallic = GetFloat(shaderMaterial, MetallicName, 0f);
                roughness = GetFloat(shaderMaterial, RoughnessName, 1f);
                emission = GetColor(shaderMaterial, EmissionColorName, new Color(0f, 0f, 0f));
                return true;
            default:
                albedo = Colors.White; metallic = 0f; roughness = 1f; emission = new Color(0f, 0f, 0f);
                return false;
        }
    }

    public static float GetEmissionEnergy(Material? material) => material switch
    {
        BaseMaterial3D standard => standard.EmissionEnergyMultiplier,
        ShaderMaterial shaderMaterial when IsPoiShader(shaderMaterial) => GetFloat(shaderMaterial, EmissionEnergyName, 1f),
        _ => 0f
    };

    public static void SetEmissionEnergy(Material? material, float energy)
    {
        switch (material)
        {
            case BaseMaterial3D standard: standard.EmissionEnergyMultiplier = energy; break;
            case ShaderMaterial shaderMaterial when IsPoiShader(shaderMaterial): shaderMaterial.SetShaderParameter(EmissionEnergyName, energy); break;
        }
    }

    /// <summary>Turns a POI ShaderMaterial duplicate into the double-sided variant (bore liner and inlay are seen from both sides).</summary>
    public static void MakeDoubleSided(ShaderMaterial material)
    {
        if (material.Shader?.ResourcePath == DoubleSidedShaderPath) return;
        if (ResourceLoader.Load<Shader>(DoubleSidedShaderPath) is { } shader) material.Shader = shader;
    }

    private static float GetFloat(ShaderMaterial material, StringName name, float fallback)
    {
        Variant value = material.GetShaderParameter(name);
        if (value.VariantType == Variant.Type.Nil && material.Shader != null)
            value = RenderingServer.ShaderGetParameterDefault(material.Shader.GetRid(), name);
        return value.VariantType == Variant.Type.Nil ? fallback : (float)value.AsDouble();
    }

    private static Color GetColor(ShaderMaterial material, StringName name, Color fallback)
    {
        Variant value = material.GetShaderParameter(name);
        if (value.VariantType == Variant.Type.Nil && material.Shader != null)
            value = RenderingServer.ShaderGetParameterDefault(material.Shader.GetRid(), name);
        return value.VariantType == Variant.Type.Nil ? fallback : value.AsColor();
    }
}
