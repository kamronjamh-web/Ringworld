using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Canopy;
using RingworldTechDemo.Vegetation.Understory;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6 stand filler SF-1 (docs/work-orders/terrain-vegetation/T6-stand-filler-design.md): the carriers of UnderstoryCatalog.LoadStandFill(), the small maples and pines (base variants 9, 10, 11, 27, 28, 29). Row order, heights and trunk radii equal the base catalog's;
/// the LOD0, LOD2 and caster (authored LOD3 billboard) carriers hold exactly the source triangles of each variant (padding fully zero) and are padded to the largest variant; the vertices keep the species bark and foliage layers of the saplings of the same
/// species, the caster's vertices are tagged 8 + row; unit radius and bounds equal what the vertex data spans; every carrier's parameter rows 8 to 10 are TreeCrownHullTable.Pack of the source variant's hull (and the hulls equal the table's); the caster
/// has the billboard foliage terms (wind mode 2, cutoff of the billboard material) and a six-layer billboard_albedo array; the load is cached and Reset() clears it. The sapling carriers (data textures, parameter textures, fetch layout, unit radius) hash to the
/// value they had before SF-1 (SaplingHashBefore). Prints STAND_FILL_CARRIER=PASS. Headless: tools/run_tests.ps1 -Tests stand_fill_carrier_test.
/// </summary>
public partial class StandFillCarrierTest : RefCounted
{
    /// <summary>SHA-256 of the sapling carriers' data (see <see cref="SaplingHash"/>) at HEAD before SF-1 (bfe705e plus SF-2).</summary>
    private const string SaplingHashBefore = "45EB1FC893DD44B1213EBF6E5A48566DF150810BBB2E83AE09F457387BC678D7";
    private static readonly string[] Channels = { "vertex_data", "normal_data", "tangent_data", "uv_data", "attachment_data" };

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("STAND_FILL_CARRIER_DETAIL " + text);

    public int Run()
    {
        try
        {
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            TreeCrownHull[] hulls = TreeCrownHullTable.Get();
            UnderstoryStandFill fill = UnderstoryCatalog.LoadStandFill();
            Check(ReferenceEquals(fill, UnderstoryCatalog.LoadStandFill()), "LoadStandFill is not cached");
            int[] sources = UnderstoryCatalog.StandFillSources;
            Check(sources.SequenceEqual(new[] { 9, 10, 11, 27, 28, 29 }), "StandFillSources order");
            Check(fill.HeightMeters.Length == sources.Length && fill.TrunkRadiusMeters.Length == sources.Length && fill.Hulls.Length == sources.Length, "per-row array lengths");
            for (int row = 0; row < sources.Length; row++)
            {
                ImportedTreeVariant v = variants[sources[row]];
                Check(UnderstoryCatalog.StandFillRowOf(sources[row]) == row, $"StandFillRowOf({sources[row]})");
                Check(fill.HeightMeters[row] == v.HeightMeters, $"row {row} height");
                Check(Math.Abs(fill.TrunkRadiusMeters[row] - v.TrunkRadiusMeters / v.HeightMeters) < 1e-7f && fill.TrunkRadiusMeters[row] > 0f, $"row {row} trunk radius");
                TreeCrownHull lifted = fill.Hulls[row];
                Check(lifted.Variant == sources[row] && lifted.Species == hulls[sources[row]].Species && lifted.Porosity == TreeCrownHullTable.PorosityOf(sources[row], v.Species) && lifted.LeafInclination == hulls[sources[row]].LeafInclination && lifted.HeightMeters == v.HeightMeters, $"row {row} lifted hull identity, porosity or leaf inclination");
                Check(fill.CutHeights[row] > 0f && lifted.Base >= fill.CutHeights[row] - 1e-5f && hulls[sources[row]].Base < fill.CutHeights[row] && lifted.Top <= hulls[sources[row]].Top + 1e-5f, $"row {row} hull base {lifted.Base} vs cut {fill.CutHeights[row]} (base variant {hulls[sources[row]].Base})");
                Check(Math.Abs(fill.CutHeights[row] * v.HeightMeters * 0.85f - (1.9f + 0.03f * v.HeightMeters * 0.85f)) < 1e-3f, $"row {row} cut {fill.CutHeights[row]} does not put the lowest leaf of a Size 0.85 filler 1.9 m above its root");
            }
            Carrier("LOD0", fill.Lod0, 0, sources, variants, fill, false);
            Carrier("LOD2", fill.Lod2, 2, sources, variants, fill, false);
            Carrier("Caster", fill.Caster, 3, sources, variants, fill, true);
            Check(fill.Lod0.Material.GetShaderParameter("stand_fill_head_clear").AsBool() && !fill.Lod2.Material.GetShaderParameter("stand_fill_head_clear").AsBool() && !fill.Caster.Material.GetShaderParameter("stand_fill_head_clear").AsBool(), "stand_fill_head_clear must be set on the stand filler L0 carrier only (SF-7b)");
            Check(!UnderstoryCatalog.Load().Lods.Any(l => l.Material.GetShaderParameter("stand_fill_head_clear").AsBool()), "a sapling carrier has stand_fill_head_clear set");
            for (int row = 0; row < sources.Length; row++) Detail($"row {row} (variant {sources[row]}) cut {fill.CutHeights[row]:F4} ({fill.CutHeights[row] * fill.HeightMeters[row]:F2} m authored), hull base {fill.Hulls[row].Base:F4} (was {hulls[sources[row]].Base:F4}), foliage triangles LOD0 {fill.Lod0FoliageBefore[row]} -> {fill.Lod0FoliageAfter[row]}, LOD2 {fill.Lod2FoliageBefore[row]} -> {fill.Lod2FoliageAfter[row]}, LOD0 foliage area kept {fill.Lod0FoliageAreaKept[row]:P1}");

            var billboards = fill.Caster.Material.GetShaderParameter("billboard_albedo").As<Texture2DArray>();
            Check(billboards != null && billboards.GetLayers() == sources.Length && billboards.GetWidth() == 512 && billboards.GetHeight() == 512, "billboard_albedo is not a 6 x 512 array");
            Check(fill.Lod0.Material.GetShaderParameter("billboard_albedo").VariantType == Variant.Type.Nil && fill.Lod2.Material.GetShaderParameter("billboard_albedo").VariantType == Variant.Type.Nil, "billboard_albedo set on a non-caster carrier");

            long data = 0;
            foreach (UnderstoryLodCarrier c in new[] { fill.Lod0, fill.Lod2, fill.Caster }) data += DataBytes(c);
            Detail($"triangles LOD0 {fill.Lod0.TriangleCount} LOD2 {fill.Lod2.TriangleCount} caster {fill.Caster.TriangleCount}; data textures {data} bytes ({data / 1048576.0:0.00} MiB); billboard textures {fill.BillboardTextureBytes} bytes ({fill.BillboardTextureBytes / 1048576.0:0.00} MiB)");

            UnderstoryCatalog saplings = UnderstoryCatalog.Load();
            string hash = SaplingHash(saplings);
            Check(hash == SaplingHashBefore, $"sapling carriers changed: hash {hash}");

            UnderstoryCatalog.Reset();
            Check(!ReferenceEquals(fill, UnderstoryCatalog.LoadStandFill()), "Reset() did not clear the stand filler cache");

            if (_failures.Count > 0)
            {
                foreach (string failure in _failures) Console.WriteLine("STAND_FILL_CARRIER_FAILURE " + failure);
                Console.WriteLine("STAND_FILL_CARRIER=FAIL");
                return 1;
            }
            Console.WriteLine("STAND_FILL_CARRIER=PASS");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("STAND_FILL_CARRIER=FAIL " + error);
            return 1;
        }
    }

    private void Carrier(string label, UnderstoryLodCarrier carrier, int sourceLod, int[] sources, IReadOnlyList<ImportedTreeVariant> variants, UnderstoryStandFill fill, bool caster)
    {
        Check(carrier.SourceLod == sourceLod, $"{label} source LOD {carrier.SourceLod}");
        var expected = new int[sources.Length];
        for (int row = 0; row < sources.Length; row++)
        {
            Mesh mesh = variants[sources[row]].Lods[sourceLod];
            for (int s = 0; s < mesh.GetSurfaceCount(); s++) expected[row] += mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;
            if (!caster) expected[row] -= (sourceLod == 0 ? fill.Lod0FoliageBefore[row] - fill.Lod0FoliageAfter[row] : fill.Lod2FoliageBefore[row] - fill.Lod2FoliageAfter[row]); // SF-7: the dropped foliage cards
            if (caster) Check(mesh.GetSurfaceCount() == 1, $"{label} variant {sources[row]} authored LOD3 has {mesh.GetSurfaceCount()} surfaces");
        }
        int largest = expected.Max();
        Check(carrier.TriangleCount == largest && carrier.VertexCount == largest * 3, $"{label} padded to {carrier.TriangleCount} triangles, expected {largest}");
        Check(carrier.Carrier.GetSurfaceCount() == 1 && carrier.Carrier.CustomAabb == carrier.Bounds, $"{label} carrier mesh / bounds");
        Check(carrier.Carrier.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length == carrier.VertexCount, $"{label} carrier vertex count");

        int width = (int)carrier.Material.GetShaderParameter("fetch_width"), rowsPerVariant = (int)carrier.Material.GetShaderParameter("fetch_rows_per_variant");
        Check(width == Math.Min(carrier.VertexCount, UnderstoryCatalog.MaximumTextureWidth) && rowsPerVariant == (carrier.VertexCount + width - 1) / width, $"{label} fetch layout");
        Dictionary<string, Image> images = Channels.ToDictionary(name => name, name => carrier.Material.GetShaderParameter(name).As<Texture2D>().GetImage());
        foreach (Image image in images.Values) Check(image.GetWidth() == width && image.GetHeight() == sources.Length * rowsPerVariant, $"{label} data texture size");
        Image vertexData = images["vertex_data"];
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
        float radius = 0f;
        for (int row = 0; row < sources.Length; row++)
        {
            int real = expected[row] * 3;
            var species = new HashSet<float>();
            for (int i = 0; i < carrier.VertexCount; i++)
            {
                Color texel = vertexData.GetPixel(i % width, row * rowsPerVariant + i / width);
                if (i >= real)
                {
                    if (texel.R != 0f || texel.G != 0f || texel.B != 0f || texel.A != 0f) { Check(false, $"{label} row {row} padding vertex {i} is not zero"); break; }
                    continue;
                }
                var p = new Vector3(texel.R, texel.G, texel.B);
                if (!caster && texel.A == (int)variants[sources[row]].Species + 4f) Check(p.Y >= fill.CutHeights[row] - 1e-6f, $"{label} row {row} foliage vertex {i} at y {p.Y} below the cut {fill.CutHeights[row]}");
                min = min.Min(p); max = max.Max(p); radius = Math.Max(radius, p.Length());
                species.Add(texel.A);
            }
            int layer = (int)variants[sources[row]].Species;
            HashSet<float> allowed = caster ? new() { UnderstoryCatalog.BillboardLayerBase + row } : new() { layer, layer + 4f };
            Check(species.IsSubsetOf(allowed) && species.Count > 0, $"{label} row {row} vertex layers {{{string.Join(",", species)}}}, expected within {{{string.Join(",", allowed)}}}");
            if (!caster) Check(species.Contains(layer + 4f), $"{label} row {row} has no foliage vertex");
            if (!caster) Check(species.Contains(layer), $"{label} row {row} has no bark vertex");
        }
        Check(MathF.Abs(radius - carrier.UnitRadius) < 1e-6f, $"{label} unit radius {carrier.UnitRadius} vs data {radius}");
        Check(carrier.Bounds.Position.IsEqualApprox(min) && carrier.Bounds.End.IsEqualApprox(max), $"{label} bounds {carrier.Bounds} vs data {min} .. {max}");
        Check(carrier.UnitRadius > 0.1f && carrier.UnitRadius < 2f, $"{label} unit radius {carrier.UnitRadius} outside a normalised tree");

        Image parameters = carrier.Material.GetShaderParameter("variant_parameters").As<Texture2D>().GetImage();
        Check(parameters.GetWidth() == sources.Length && parameters.GetHeight() == UnderstoryCatalog.ParameterRowCount, $"{label} parameter texture size");
        for (int row = 0; row < sources.Length; row++)
        {
            TreeCrownHullTable.Pack(fill.Hulls[row], out Vector4 hull, out Vector4 extra, out Vector4 lad);
            Check(parameters.GetPixel(row, 3).A == fill.CutHeights[row], $"{label} row {row} parameter row 3 w {parameters.GetPixel(row, 3).A} is not the cut {fill.CutHeights[row]}");
            Check(Same(parameters.GetPixel(row, 8), hull) && Same(parameters.GetPixel(row, 9), extra) && Same(parameters.GetPixel(row, 10), lad), $"{label} row {row} hull rows 8 to 10 differ from the lifted hull");
            if (caster)
            {
                var billboard = (ShaderMaterial)variants[sources[row]].Lods[3].SurfaceGetMaterial(0);
                Color wind = parameters.GetPixel(row, 4), scale = parameters.GetPixel(row, 3);
                Check((int)wind.B == 2 && wind.A == 0f, $"{label} row {row} wind terms {wind}");
                Check(MathF.Abs(scale.B - billboard.GetShaderParameter("alpha_cutoff").AsSingle()) < 1e-7f, $"{label} row {row} cutoff {scale.B}");
            }
        }
    }

    private static bool Same(Color color, Vector4 v) => color.R == v.X && color.G == v.Y && color.B == v.Z && color.A == v.W;

    private static long DataBytes(UnderstoryLodCarrier carrier)
    {
        long bytes = 0;
        foreach (string name in Channels.Append("variant_parameters")) bytes += carrier.Material.GetShaderParameter(name).As<Texture2D>().GetImage().GetData().Length;
        return bytes;
    }

    /// <summary>SHA-256 over every sapling carrier's data textures (as bytes), parameter texture, vertex count, fetch layout and unit radius.</summary>
    internal static string SaplingHash(UnderstoryCatalog catalog)
    {
        var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (UnderstoryLodCarrier lod in catalog.Lods)
        {
            foreach (string name in Channels.Append("variant_parameters"))
                sha.AppendData(lod.Material.GetShaderParameter(name).As<Texture2D>().GetImage().GetData());
            sha.AppendData(BitConverter.GetBytes(lod.VertexCount));
            sha.AppendData(BitConverter.GetBytes((int)lod.Material.GetShaderParameter("fetch_width")));
            sha.AppendData(BitConverter.GetBytes((int)lod.Material.GetShaderParameter("fetch_rows_per_variant")));
            sha.AppendData(BitConverter.GetBytes(lod.UnitRadius));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
