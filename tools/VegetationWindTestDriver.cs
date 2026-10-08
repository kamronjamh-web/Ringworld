using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tests;

/// <summary>Use real production prototypes in disposable GPU wind fixtures.</summary>
public partial class VegetationWindTestDriver : RefCounted
{
    private RingWindField? _productionWindField;
    private readonly RingWindSettings _productionWindSettings = new() { GustScaleMeters=600, GustStrength=1.2f, GustBaseBreeze=.2f };
    public Godot.Collections.Dictionary PublishProductionWindFixture(double time)
    {
        _productionWindField ??= new RingWindField();
        _productionWindField.SetSeed(41031);
        const double radius=5_000_000;
        Vector2 wind=new(-12,0);
        var geometry=new RingLodGeometrySnapshot(new Transform3D(new Basis(Vector3.Down,Vector3.Back,Vector3.Left),Vector3.Zero),
            Vector3.Zero,Vector3.Zero,radius,0,0,System.Math.Tau*radius,-100,100,new RingCanonicalPosition(0,0,0));
        _productionWindField.Configure(geometry.CircumferenceMeters,wind,time,_productionWindSettings);
        _productionWindField.Publish(geometry,new RingCanonicalPosition(0,0,0),wind,wind,_productionWindSettings,true);
        Vector3[] roots=new Vector3[33]; float[] gusts=new float[roots.Length];
        for(int i=0;i<roots.Length;i++) {
            var canonical=new RingCanonicalPosition(-(i-16)*6,0,0);
            roots[i]=geometry.CanonicalToWorld(canonical.AroundMeters,0,0);
            gusts[i]=_productionWindField.Sample(canonical,_productionWindSettings);
        }
        double phase=time*.19;
        Vector4 wave=new((float)System.Math.Sin(phase),(float)System.Math.Cos(phase),
            (float)System.Math.Sin(phase*.47),(float)System.Math.Cos(phase*.47));
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled",1f);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_vector",wind);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_center",new Vector3(0,(float)radius,0));
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_axis",Vector3.Back);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_wave",wave);
        RenderingServer.GlobalShaderParameterSet("ring_wind_phase",wave);
        RenderingServer.GlobalShaderParameterSet("ring_tree_wind_response",new Vector4(2.1f,.36f,2,.6f));
        RenderingServer.GlobalShaderParameterSet("ring_tree_wind_parts",new Vector2(.15f,1));
        RenderingServer.GlobalShaderParameterSet("ring_tree_wind_trunk",wind);
        RenderingServer.GlobalShaderParameterSet("ring_tree_wind_canopy",wind);
        return new Godot.Collections.Dictionary { ["roots"]=roots,["gusts"]=gusts };
    }
    public void DisposeProductionWindFixture() { _productionWindField?.Dispose(); _productionWindField=null; }
    public Godot.Collections.Dictionary GetSharedWindPayload(Node effects)
    {
        var local = (RingLocalWeatherEffects)effects;
        var weather = local.GetNodeOrNull<RingVolumetricCloudLayer>(local.WeatherSourcePath);
        bool active = local.EnableVegetationWind && local.Enabled && weather?.Enabled == true && local.CurrentWindSnapshot.Enabled;
        return new Godot.Collections.Dictionary
        {
            ["shared_enabled"] = active && local.SharedWindFieldReady ? 1.0f : 0.0f,
            ["mean"] = local.CurrentWindSnapshot.MeanWind,
            ["parts"] = SanitizeTreeWindParts(weather == null ? new Vector2(.15f, 1f)
                : new Vector2(weather.WindSettings.TreeTrunkBendStrength, weather.WindSettings.TreeCanopyFlexStrength))
        };
    }
    public Vector2 SetTreeWindParts(Node effects, Vector2 strengths)
    {
        var local = (RingLocalWeatherEffects)effects;
        var weather = local.GetNode<RingVolumetricCloudLayer>(local.WeatherSourcePath);
        weather.WindSettings.TreeTrunkBendStrength = strengths.X;
        weather.WindSettings.TreeCanopyFlexStrength = strengths.Y;
        return SanitizeTreeWindParts(strengths);
    }
    private static Vector2 SanitizeTreeWindParts(Vector2 strengths) => new(
        float.IsFinite(strengths.X) ? Mathf.Clamp(strengths.X, 0, 1) : .15f,
        float.IsFinite(strengths.Y) ? Mathf.Clamp(strengths.Y, 0, 3) : 1f);
    public Godot.Collections.Array<Godot.Collections.Dictionary> LoadSharedWindProductionMeshes()
    {
        var result = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        var variant = ImportedTreeCatalog.Load()[0];
        for (int lod = 0; lod < variant.Lods.Length; lod++)
            result.Add(new Godot.Collections.Dictionary { ["name"] = $"imported_lod{lod}", ["mesh"] = variant.Lods[lod] });
        var packed = ImportedTreeImpostorCatalog.Load();
        result.Add(new Godot.Collections.Dictionary { ["name"] = "packed_lod3", ["mesh"] = packed.Mesh,
            ["material"] = packed.Material.Duplicate(false) });
        RingTreeMeshSet procedural = RingTreeProceduralMeshFactory.Create(41031, 8, 18, true);
        result.Add(new Godot.Collections.Dictionary { ["name"] = "procedural_trunk", ["mesh"] = procedural.Trunk, ["mode"] = 0 });
        result.Add(new Godot.Collections.Dictionary { ["name"] = "procedural_canopy", ["mesh"] = procedural.Canopy, ["mode"] = 1 });
        return result;
    }

    public bool VerifyTreeWindAttachments()
    {
        int islands = 0, attachmentVertices = 0;
        foreach (ImportedTreeVariant variant in ImportedTreeCatalog.Load())
            for (int lod = 0; lod < variant.Lods.Length; lod++)
                for (int surface = 0; surface < variant.Lods[lod].GetSurfaceCount(); surface++)
                {
                    Mesh mesh = variant.Lods[lod];
                    var mat = (ShaderMaterial)mesh.SurfaceGetMaterial(surface);
                    int mode = mat.GetShaderParameter("tree_foliage_mode").AsInt32();
                    if (lod == 3 && mode != 2) throw new System.InvalidOperationException("Combined far card not classified.");
                    if (mode != 1) continue;
                    var arrays = mesh.SurfaceGetArrays(surface);
                    float[] data = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
                    Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (data.Length != vertices.Length * 4 || !mat.GetShaderParameter("tree_foliage_attachments").AsBool())
                        throw new System.InvalidOperationException("Foliage custom attachment channel missing.");
                    ulong format = ((ulong)((ArrayMesh)mesh).SurfaceGetFormat(surface) >> (int)Mesh.ArrayFormat.FormatCustom0Shift) & 7;
                    if (format != (ulong)Mesh.ArrayCustomFormat.RgbaFloat)
                        throw new System.InvalidOperationException("Foliage attachment channel is not lossless float RGBA.");
                    var anchors = new System.Collections.Generic.HashSet<Vector3>();
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 anchor = new(data[i*4], data[i*4+1], data[i*4+2]);
                        float inverse = data[i*4+3];
                        if (!anchor.IsFinite() || !float.IsFinite(inverse) || inverse < 0) throw new System.InvalidOperationException("Invalid attachment.");
                        if (inverse <= 0) continue;
                        anchors.Add(anchor);
                        if (vertices[i] == anchor) attachmentVertices++;
                    }
                    islands += anchors.Count;
                }
        if (islands == 0 || attachmentVertices < islands) throw new System.InvalidOperationException("No exactly pinned production attachments.");
        ImportedTreeImpostorCatalog packed = ImportedTreeImpostorCatalog.Load();
        Image flags = ((Texture2D)packed.Material.GetShaderParameter("variant_parameters").AsGodotObject()).GetImage();
        for (int i = 0; i < packed.VariantCount; i++)
            if (flags.GetPixel(i, 2).G != 2) throw new System.InvalidOperationException("Packed far classification lost.");
        if (packed.Material.GetShaderParameter("attachment_data").AsGodotObject() is not Texture2D)
            throw new System.InvalidOperationException("Packed custom attachment channel lost.");
        GD.Print($"TREE_WIND_ATTACHMENTS=PASS islands={islands} exact_attachment_vertices={attachmentVertices}");
        return true;
    }
    public Mesh LoadGrassMesh() => (Mesh)typeof(RingStandingGrassLayer)
        .GetMethod("CreateBladeMesh", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, null)!;

    public Mesh LoadMeadowMesh() => RingGrassMeshFactory.Create();
    public Mesh LoadIntermediateGrassMesh() => RingGrassMeshFactory.CreateIntermediate();

    public Mesh? LoadAuthoredTreeMesh() => KayKitTreeMeshLoader.LoadNormalizedMesh(
        "res://resources/trees/kaykit_forest/Tree_2_A_Color1.gltf");

    public Godot.Collections.Dictionary LoadProceduralTreeMeshes()
    {
        RingTreeMeshSet meshes = RingTreeProceduralMeshFactory.Create(41031, 8, 18, true);
        return new Godot.Collections.Dictionary { ["trunk"] = meshes.Trunk, ["canopy"] = meshes.Canopy };
    }
}
