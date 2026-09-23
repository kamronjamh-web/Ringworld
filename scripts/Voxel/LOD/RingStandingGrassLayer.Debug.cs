using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    public enum GrassDebugView { Off, Lod, Density, Wind, Interaction, Tiles }
    [Export] public GrassDebugView GrassDebugMode { get; set; }
    [Export] public bool ShowGrassTileBounds { get; set; }
    private ImmediateMesh? _grassDebugBoundsMesh;
    private MeshInstance3D? _grassDebugBoundsRenderer;
    private void ConfigureGrassDebugMaterial(MultiMeshInstance3D renderer, RingGrassTileKey key)
    {
        ulong h = unchecked((ulong)key.Around * 0x9E3779B185EBCA87UL ^ (ulong)key.Across);
        renderer.SetInstanceShaderParameter("grass_debug_tile_color", new Color(((h >> 0) & 255) / 255f, ((h >> 8) & 255) / 255f, ((h >> 16) & 255) / 255f));
    }
    private void UpdateGrassDebugBounds()
    {
        if (!ShowGrassTileBounds)
        {
            if (_grassDebugBoundsRenderer != null) _grassDebugBoundsRenderer.Visible = false;
            return;
        }
        _grassDebugBoundsMesh ??= new ImmediateMesh();
        if (_grassDebugBoundsRenderer == null)
        {
            var shader = new Shader { Code = "shader_type spatial; render_mode unshaded, cull_disabled, depth_draw_always; void fragment(){ ALBEDO=COLOR.rgb; EMISSION=COLOR.rgb; }" };
            _grassDebugBoundsRenderer = new MeshInstance3D { Name = "GrassTileBoundsDebug", Mesh = _grassDebugBoundsMesh, MaterialOverride = new ShaderMaterial { Shader = shader }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 8, Visible = true };
            AddChild(_grassDebugBoundsRenderer);
        }
        _grassDebugBoundsMesh.ClearSurfaces();
        bool began = false;
        foreach (var pair in _tiles)
        {
            if (!_wanted.Contains(pair.Key) || pair.Value.Renderer?.Multimesh == null) continue;
            if (!began) { _grassDebugBoundsMesh.SurfaceBegin(Mesh.PrimitiveType.Lines); began = true; }
            Aabb aabb = pair.Value.Renderer.Transform * pair.Value.Renderer.Multimesh.CustomAabb;
            Vector3 p = aabb.Position, e = aabb.End;
            Vector3[] v = { new(p.X,p.Y,p.Z), new(e.X,p.Y,p.Z), new(e.X,e.Y,p.Z), new(p.X,e.Y,p.Z), new(p.X,p.Y,e.Z), new(e.X,p.Y,e.Z), new(e.X,e.Y,e.Z), new(p.X,e.Y,e.Z) };
            int[] edges = { 0,1,1,2,2,3,3,0,4,5,5,6,6,7,7,4,0,4,1,5,2,6,3,7 };
            _grassDebugBoundsMesh.SurfaceSetColor(new Color(1,.75f,.05f));
            foreach (int index in edges) _grassDebugBoundsMesh.SurfaceAddVertex(v[index]);
        }
        if (began) _grassDebugBoundsMesh.SurfaceEnd();
        _grassDebugBoundsRenderer.Visible = began;
    }
}
