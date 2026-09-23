using System;
using System.Diagnostics;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Bounded menu-only whole-ring terrain tier, sampled from the flyover cache sampler.</summary>
public sealed class MenuFlyoverFarTerrain : IDisposable
{
    public const int AroundSamples = 2048;
    public const int AcrossSamples = 65;
    private readonly MenuFlyoverTerrainCache _cache;
    private readonly Image _heightImage;
    private readonly Image _colorImage;
    private int _cursor;
    private bool _disposed;
    public ImageTexture HeightTexture { get; }
    public ImageTexture ColorTexture { get; }
    public MeshInstance3D Instance { get; }
    public bool Ready { get; private set; }
    public int SampleCount => _cursor;
    public int AroundSampleCount { get; }
    public int AcrossSampleCount { get; }
    public int TriangleCount => AroundSampleCount * (AcrossSampleCount - 1) * 2;

    public MenuFlyoverFarTerrain(MenuFlyoverTerrainCache cache, int detail = 2)
    {
        _cache = cache;
        (AroundSampleCount, AcrossSampleCount) = detail switch {
            0 => (512, 17), 1 => (1024, 33), 2 => (2048, 65),
            _ => throw new ArgumentOutOfRangeException(nameof(detail), "Far terrain detail must be 0, 1, or 2.")
        };
        _heightImage = Image.CreateEmpty(AroundSampleCount, AcrossSampleCount, false, Image.Format.Rf);
        _colorImage = Image.CreateEmpty(AroundSampleCount, AcrossSampleCount, false, Image.Format.Rgba8);
        HeightTexture = ImageTexture.CreateFromImage(_heightImage);
        ColorTexture = ImageTexture.CreateFromImage(_colorImage);
        Instance = new MeshInstance3D { Name = "MenuFlyoverFarTerrain", Mesh = BuildMesh(), MaterialOverride = CreateMaterial(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true, CustomAabb = new Aabb(new Vector3(-159f, -10f, -(float)(_cache.RadiusMeters*.001)-10f), new Vector3(318f, (float)(_cache.RadiusMeters*.002)+20f, (float)(_cache.RadiusMeters*.002)+20f)) };
    }

    public void BudgetedUpdate()
    {
        if (Ready) return;
        long started = Stopwatch.GetTimestamp();
        int worked = 0;
        while (_cursor < AroundSampleCount * AcrossSampleCount && worked++ < 512 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2.0)
        {
            int x = _cursor % AroundSampleCount, y = _cursor / AroundSampleCount;
            _cache.ResolveBackdropSample((long)Math.Round(x / (double)AroundSampleCount * _cache.CircumferenceMeters), y * _cache.RingWidthMeters / (AcrossSampleCount - 1), out float h, out Color c);
            _heightImage.SetPixel(x, y, new Color(h, 0, 0, 1));
            _colorImage.SetPixel(x, y, c.LinearToSrgb());
            _cursor++;
        }
        if (_cursor < AroundSampleCount * AcrossSampleCount) return;
        HeightTexture.Update(_heightImage); ColorTexture.Update(_colorImage); Ready = true; Instance.Visible = true;
    }

    private Mesh BuildMesh()
    {
        var arrays = new Godot.Collections.Array();
        var vertices = new Vector3[(AroundSampleCount + 1) * AcrossSampleCount];
        var uvs = new Vector2[vertices.Length];
        var indices = new int[AroundSampleCount * (AcrossSampleCount - 1) * 6];
        int vertex = 0;
        for (int y = 0; y < AcrossSampleCount; y++) for (int x = 0; x <= AroundSampleCount; x++) { vertices[vertex] = Vector3.Zero; uvs[vertex++] = new Vector2(x / (float)AroundSampleCount, y / (float)(AcrossSampleCount - 1)); }
        int index = 0, row = AroundSampleCount + 1;
        for (int y = 0; y < AcrossSampleCount - 1; y++) for (int x = 0; x < AroundSampleCount; x++) { int a=y*row+x,b=a+1,c=a+row,d=c+1; indices[index++]=a;indices[index++]=b;indices[index++]=c;indices[index++]=b;indices[index++]=d;indices[index++]=c; }
        arrays.Resize((int)ArrayMesh.ArrayType.Max); arrays[(int)ArrayMesh.ArrayType.Vertex]=vertices; arrays[(int)ArrayMesh.ArrayType.TexUV]=uvs; arrays[(int)ArrayMesh.ArrayType.Index]=indices;
        var mesh=new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays); return mesh;
    }

    private ShaderMaterial CreateMaterial()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/menu_flyover_far_surface.gdshader") };
        material.SetShaderParameter("height_texture", HeightTexture); material.SetShaderParameter("color_texture", ColorTexture); material.SetShaderParameter("radius_km", (float)(_cache.RadiusMeters * .001)); material.SetShaderParameter("ring_width_km", _cache.RingWidthMeters * .001f); return material;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Instance.Visible = false;
        Material? material = Instance.MaterialOverride;
        Mesh? mesh = Instance.Mesh;
        Instance.MaterialOverride = null;
        Instance.Mesh = null;
        material?.Dispose();
        mesh?.Dispose();
        HeightTexture.Dispose();
        ColorTexture.Dispose();
        _heightImage.Dispose();
        _colorImage.Dispose();
        Instance.QueueFree();
    }
}
