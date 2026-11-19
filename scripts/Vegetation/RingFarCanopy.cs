using System;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>
/// T6 design B5 (vegetation.far_canopy): the far canopy albedo and the distance where the tree impostors end. Published in worlds with the feature as the shader global
/// <c>ringworld_far_canopy</c> (rgb = canopy albedo, w = far start in metres; (0, 0, 0, 0) otherwise). The proxy ground shader reads it; the far colour code
/// (RingTerrainVisuals.ResolveMacroTextureColor) reads <see cref="TryGetAlbedo"/>.
/// The albedo is the impostor pictures' coverage-weighted mean linear albedo weighted by the species shares of the landscape, times a canopy self-shading factor: a closed canopy reflects about
/// half of what its leaves do (shadowed gaps, lower leaves). The factor is calibrated, not tuned: the tinted proxy forest just past the far start has the mean luminance of the impostor forest just before it.
/// The mean comes from the baked impostor atlas resource itself (the same pictures the impostor catalog binds), so any thread can ask for it at any time, before the tree layer has started.
/// </summary>
public static class RingFarCanopy
{
    public const string GlobalName = "ringworld_far_canopy";
    /// <summary>Canopy self-shading factor (T6-10 calibration, see docs/work-orders/terrain-vegetation/T6.md).</summary>
    public const float SelfShading = 0.55f;

    // Regional species shares of ForestRegionSampler: the maple baseline 0.85 against about 0.11 (fir) and 0.10 (pine) averaged over the pocket fields (pockets cover about 12 % of the ring at
    // a mean smoothstep of 0.5, times 2.45 and the temperature / moisture terms), so maple is about 80 % of the stems and fir and pine about 10 % each.
    private const float MapleShare = 0.80f, FirShare = 0.10f, PineShare = 0.10f;
    // Variant layout of ImportedTreeCatalog (OrderedSpecies maple, fir, pine; 12, 3 and 15 variants).
    private const int MapleCount = 12, FirCount = 3, PineCount = 15;

    private static readonly object Gate = new();
    private static bool _computed;
    private static Vector3? _albedo;
    private static volatile bool _published;
    private static float _farStart;

    /// <summary>The self-shaded linear canopy albedo, or false when the baked atlas is missing. Computed once, thread safe.</summary>
    public static bool TryGetAlbedo(out Vector3 albedo)
    {
        lock (Gate)
        {
            if (!_computed) { _albedo = ComputeAlbedo(); _computed = true; }
            albedo = _albedo ?? Vector3.Zero;
            return _albedo != null;
        }
    }

    public static float FarStartMeters => _published ? _farStart : 0f;

    /// <summary>The coverage-weighted mean linear albedo of the baked atlas (species shares applied), before the self-shading factor.</summary>
    public static Vector3? ComputeMeanAlbedo()
    {
        if (!ResourceLoader.Exists(ImportedTreeImpostorAtlasBake.ResourcePath)) return null;
        PreparedTreeImpostorAtlas? atlas = ResourceLoader.Load<PreparedTreeImpostorAtlas>(ImportedTreeImpostorAtlasBake.ResourcePath);
        int layers = MapleCount + FirCount + PineCount;
        int layerBytes = PreparedTreeImpostorAtlas.AlbedoLayerBytes;
        if (atlas == null || atlas.AlbedoBase.Length < layers * layerBytes) return null;
        double shareTotal = 0d, red = 0d, green = 0d, blue = 0d;
        for (int layer = 0; layer < layers; layer++)
        {
            double share = layer < MapleCount ? MapleShare / MapleCount : layer < MapleCount + FirCount ? FirShare / FirCount : PineShare / PineCount;
            double r = 0d, g = 0d, b = 0d, alpha = 0d;
            int start = layer * layerBytes;
            for (int p = start; p < start + layerBytes; p += 4)
            {
                double a = atlas.AlbedoBase[p + 3] / 255d;
                if (a <= 0d) continue;
                r += SrgbToLinear(atlas.AlbedoBase[p] / 255d) * a; g += SrgbToLinear(atlas.AlbedoBase[p + 1] / 255d) * a; b += SrgbToLinear(atlas.AlbedoBase[p + 2] / 255d) * a; alpha += a;
            }
            if (alpha <= 0d) continue;
            red += share * r / alpha; green += share * g / alpha; blue += share * b / alpha; shareTotal += share;
        }
        return shareTotal <= 0d ? null : new Vector3((float)(red / shareTotal), (float)(green / shareTotal), (float)(blue / shareTotal));
    }

    private static Vector3? ComputeAlbedo() => ComputeMeanAlbedo() is { } mean ? mean * SelfShading : null;

    private static double SrgbToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    /// <summary>Publishes the albedo and the far start in metres. Main thread (RenderingServer global). A missing atlas publishes nothing (the shader keeps w = 0).</summary>
    public static void Publish(float farStartMeters)
    {
        if (!TryGetAlbedo(out Vector3 albedo)) return;
        _farStart = farStartMeters;
        _published = true;
        RenderingServer.GlobalShaderParameterSet(GlobalName, new Vector4(albedo.X, albedo.Y, albedo.Z, farStartMeters));
    }

    public static void Clear()
    {
        _published = false;
        RenderingServer.GlobalShaderParameterSet(GlobalName, new Vector4(0, 0, 0, 0));
    }
}
