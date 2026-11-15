using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7c-PV GDScript bridge (tree_leaf_mip_preview): loads the preview mip chains from res://.godot/tr7c/preview and hands out private duplicates of the catalog's materials in four looks
/// (today, stable = preview texture with the tool-local isotropic alpha shader, ship = preview texture with the shipped shader, ref = shipped textures read at mip 0). The catalog's own materials are never touched.</summary>
public partial class TreeLeafMipPreviewDriver : Node
{
    private readonly Dictionary<string, ImageTexture> _chains = new();
    private readonly Dictionary<string, string> _names = new();
    private readonly Dictionary<(ulong, string), ShaderMaterial> _cache = new();
    private readonly Dictionary<Shader, Shader> _iso = new(), _mip0 = new();
    private Vector3 _sun = new(1, 0, 0);

    /// <summary>Loads the chains of one set (suffix "_cl" = closed loop, "" = open loop) and checks each against the manifest (level count and hash of the loaded image data). Returns "" or the error.</summary>
    public string Init(string dir, string suffix)
    {
        string mode = suffix == "_cl" ? "closed" : "open";
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(dir + "/manifest.json")));
        foreach (JsonElement a in doc.RootElement.GetProperty("atlases").EnumerateArray())
        {
            if (a.GetProperty("mode").GetString() != mode) continue;
            string file = a.GetProperty("file").GetString()!, source = a.GetProperty("source").GetString()!;
            var tex = ResourceLoader.Load<ImageTexture>($"{dir}/{file}", null, ResourceLoader.CacheMode.Ignore);
            if (tex == null) return $"cannot load {file}";
            Image image = tex.GetImage();
            if (image.GetMipmapCount() != a.GetProperty("levels").GetInt32()) return $"{file}: {image.GetMipmapCount()} mips, expected {a.GetProperty("levels").GetInt32()}";
            string hash = Convert.ToHexString(SHA256.HashData(image.GetData()));
            if (hash != a.GetProperty("chain_sha256").GetString()) return $"{file}: the loaded chain differs from the built one";
            _chains[source] = tex; _names[source] = a.GetProperty("name").GetString()!;
        }
        return _chains.Count == 3 ? "" : $"expected 3 atlases, loaded {_chains.Count}";
    }

    public Godot.Collections.Array<string> AtlasNames() => new(_names.Values.OrderBy(s => s, StringComparer.Ordinal));

    public void SetSun(Vector3 direction)
    {
        _sun = direction;
        foreach (ShaderMaterial m in _cache.Values) m.SetShaderParameter("sun_direction", direction);
    }

    public void SetModel(MeshInstance3D instance, int variant, string kind, string look)
    {
        ImportedTreeVariant v = ImportedTreeCatalog.Load()[variant];
        Mesh mesh = kind == "lod0" ? v.Lods[0] : kind == "lod2" ? v.Lods[2] : ImportedTreeRenderLodCatalog.GetLod2b(v);
        instance.Mesh = mesh;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            if (mesh.SurfaceGetMaterial(s) is ShaderMaterial orig) instance.SetSurfaceOverrideMaterial(s, Look(orig, look));
    }

    /// <summary>The leaf material of an atlas ("maple", "fir", "pine") in a look, for the level-0 gate.</summary>
    public ShaderMaterial? AtlasMaterial(string atlas, string look)
    {
        foreach (ImportedTreeVariant v in ImportedTreeCatalog.Load())
            for (int s = 0; s < v.Lods[2].GetSurfaceCount(); s++)
                if (v.Lods[2].SurfaceGetMaterial(s) is ShaderMaterial m && Leaf(m, out string path) && _names[path] == atlas) return Look(m, look);
        return null;
    }

    /// <summary>[u0, v0, du, size] of a 64 x 64 texel window of the atlas with about half of its texels covered (a mix of covered and cut-out texels for the level-0 gate).</summary>
    public float[] FindWindow(string atlas)
    {
        foreach (ImportedTreeVariant v in ImportedTreeCatalog.Load())
            for (int s = 0; s < v.Lods[2].GetSurfaceCount(); s++)
                if (v.Lods[2].SurfaceGetMaterial(s) is ShaderMaterial m && Leaf(m, out string path) && _names[path] == atlas)
                {
                    float cutoff = m.GetShaderParameter("alpha_cutoff").AsSingle();
                    Image image = ((Texture2D)m.GetShaderParameter("albedo_texture").AsGodotObject()).GetImage();
                    image.Decompress(); image.ClearMipmaps(); image.Convert(Image.Format.Rgba8);
                    byte[] d = image.GetData(); int size = image.GetWidth();
                    double best = 9; int bx = 0, by = 0;
                    for (int wy = 0; wy + 64 <= size; wy += 64)
                        for (int wx = 0; wx + 64 <= size; wx += 64)
                        {
                            int n = 0;
                            for (int y = 0; y < 64; y += 2) for (int x = 0; x < 64; x += 2) if (d[((wy + y) * size + wx + x) * 4 + 3] / 255f >= cutoff) n++;
                            double miss = Math.Abs(n / 1024.0 - .5);
                            if (miss < best) { best = miss; bx = wx; by = wy; }
                        }
                    return new[] { bx / (float)size, by / (float)size, 64f / size, size };
                }
        return Array.Empty<float>();
    }

    /// <summary>A 64 x 64 m quad in the plane x = 0 facing +x whose UVs cover a 64 texel window (u0, v0, du), for a 1:1 texel to pixel render.</summary>
    public ArrayMesh BuildQuad(float u0, float v0, float du)
    {
        var pos = new Vector3[] { new(0, -32, -32), new(0, 32, -32), new(0, 32, 32), new(0, -32, 32) };
        var uv = new Vector2[] { new(u0, v0 + du), new(u0 + du, v0 + du), new(u0 + du, v0), new(u0, v0) };
        var normal = new Vector3[] { Vector3.Right, Vector3.Right, Vector3.Right, Vector3.Right };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos; arrays[(int)Mesh.ArrayType.Normal] = normal; arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Pixels that differ from the background: [count, mean linear R, G, B, mean linear luminance].</summary>
    public float[] MeasureForeground(Image image, Color background)
    {
        double count = 0, r = 0, g = 0, b = 0;
        for (int y = 0; y < image.GetHeight(); y++)
            for (int x = 0; x < image.GetWidth(); x++)
            {
                Color pixel = image.GetPixel(x, y);
                if (Mathf.Abs(pixel.R - background.R) + Mathf.Abs(pixel.G - background.G) + Mathf.Abs(pixel.B - background.B) <= .03f) continue;
                Color linear = pixel.SrgbToLinear();
                count++; r += linear.R; g += linear.G; b += linear.B;
            }
        if (count == 0) return new float[5];
        return new[] { (float)count, (float)(r / count), (float)(g / count), (float)(b / count), (float)((.2126 * r + .7152 * g + .0722 * b) / count) };
    }

    /// <summary>Level-0 gate numbers for two same-size images: [pixels covered in both, pixels covered in one only, largest channel difference (0..255) over pixels covered in both].</summary>
    public float[] CompareImages(Image a, Image b, Color background)
    {
        int both = 0, one = 0; float worst = 0;
        for (int y = 0; y < a.GetHeight(); y++)
            for (int x = 0; x < a.GetWidth(); x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                bool pa = Mathf.Abs(p.R - background.R) + Mathf.Abs(p.G - background.G) + Mathf.Abs(p.B - background.B) > .03f;
                bool qa = Mathf.Abs(q.R - background.R) + Mathf.Abs(q.G - background.G) + Mathf.Abs(q.B - background.B) > .03f;
                if (pa && qa) { both++; worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(p.R - q.R), Mathf.Max(Mathf.Abs(p.G - q.G), Mathf.Abs(p.B - q.B))) * 255f); }
                else if (pa != qa) one++;
            }
        return new float[] { both, one, worst };
    }

    // ---- materials ---------------------------------------------------------------------------------------------------------------------------------------

    private static bool Leaf(ShaderMaterial m, out string path)
    {
        path = "";
        if (m.GetShaderParameter("tree_foliage_mode").AsInt32() != 1 || !m.GetShaderParameter("use_alpha_cutout").AsBool()) return false;
        if (m.GetShaderParameter("albedo_texture").AsGodotObject() is not Texture2D t) return false;
        path = t.ResourcePath; return true;
    }

    private ShaderMaterial Look(ShaderMaterial orig, string look)
    {
        var key = (orig.GetInstanceId(), look);
        if (_cache.TryGetValue(key, out ShaderMaterial? cached)) return cached;
        var m = (ShaderMaterial)orig.Duplicate();
        Configure(m);
        switch (look)
        {
            case "ref":
                m.Shader = Swap(_mip0, orig.Shader, "texture(albedo_texture, UV)", "textureLod(albedo_texture, UV, 0.0)", null);
                break;
            case "stable":
            case "ship":
                if (Leaf(orig, out string path) && _chains.TryGetValue(path, out ImageTexture? tex))
                {
                    m.SetShaderParameter("albedo_texture", tex);
                    if (look == "stable")
                    {
                        m.Shader = Swap(_iso, orig.Shader, "ALPHA = albedo.a;", "ALPHA = texture(leaf_alpha_texture, UV).a * albedo_tint.a;", "uniform sampler2D leaf_alpha_texture : filter_linear_mipmap, repeat_enable;\n");
                        m.SetShaderParameter("leaf_alpha_texture", tex);
                    }
                }
                break;
        }
        _cache[key] = m;
        return m;
    }

    private static Shader Swap(Dictionary<Shader, Shader> cache, Shader original, string from, string to, string? uniform)
    {
        if (cache.TryGetValue(original, out Shader? s)) return s;
        string code = original.Code;
        if (!code.Contains(from)) throw new InvalidOperationException($"the shader copy did not find '{from}' in {original.ResourcePath}");
        code = code.Replace(from, to);
        if (uniform != null)
        {
            const string anchor = "uniform sampler2D albedo_texture";
            if (!code.Contains(anchor)) throw new InvalidOperationException("no albedo_texture uniform to anchor the alpha sampler");
            code = code.Replace(anchor, uniform + anchor);
        }
        return cache[original] = new Shader { Code = code };
    }

    private void Configure(ShaderMaterial m)
    {
        m.SetShaderParameter("ring_center", new Vector3(0, 0, 1000000)); // surface_up = +Z at the tree
        m.SetShaderParameter("sun_direction", _sun);
        m.SetShaderParameter("vegetation_wind_enabled", false);
        m.SetShaderParameter("simplified_tree_shading", true); // the shipped default
        m.SetShaderParameter("ring_atmosphere_enabled", false);
        m.SetShaderParameter("ring_radius_meters", 5000000f);
        m.SetShaderParameter("atmosphere_haze_model", 1);
        m.SetShaderParameter("ring_haze_sigma_R", Vector3.Zero);
        m.SetShaderParameter("ring_haze_sigma_A", 0f);
        m.SetShaderParameter("ring_haze_sigma_M0", 0f);
        m.SetShaderParameter("atmosphere_boundary_max_opacity", 0f);
        m.SetShaderParameter("atmosphere_upper_max_opacity", 0f);
        m.SetShaderParameter("atmosphere_distance_haze_max_opacity", 0f);
    }
}
