using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>
/// WO-S0 contract (docs/gpu-offload-plan.md section 7): real worker sectors are packed into vertex-table slots,
/// uploaded through the render thread, read back with TextureGetDataAsync and compared with SectorData.
/// Also checks that the flag-off state reserves nothing and reports the VRAM a slot pool costs.
/// </summary>
public partial class ProxyVertexTableContractTest : Node
{
    private const int Cells = 64;
    private const int SectorsPerLevel = 20;
    private static readonly int[] Spacings = { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };

    public string Result { get; private set; } = "RUNNING";
    private readonly ConcurrentDictionary<(int Texture, int Slot), byte[]> _readbacks = new();

    public async void Start()
    {
        string result;
        try { result = await Run(); }
        catch (Exception error) { result = "FAIL " + error; }
        Result = result;
    }

    private async Task<string> Run()
    {
        RenderingDevice? rd = RenderingServer.GetRenderingDevice();
        if (rd == null) return "FAIL RenderingDevice required";
        Require(RingTerrainProxySector.VertexTableReservedBytes == 0, "flag-off state reserved vertex-table bytes before any pool existed");
        Require(RingTerrainProxySector.VertexTableInstallEnabled, "install flag must default to on");
        Require(RingTerrainProxySector.VertexTableColorQuantisationMatchesMesh(out string colorDetail), colorDetail);
        GD.Print("PROXY_VERTEX_TABLE flag_off_reserved_bytes=0 install_flag=off " + colorDetail);

        TerrainGenerationParameters recipe = TerrainGenerationParameters.Default();
        using var runtimeSettings = new VoxelTerrainSettings
        {
            SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
            BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
            EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
        };
        using var runtime = new RingTerrainSampler(runtimeSettings, true, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, 8400);
        using var legacySettings = new VoxelTerrainSettings();
        using var legacy = new RingTerrainSampler(legacySettings, true, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, 8000);

        int totalSectors = 0, totalSkirtVertices = 0;
        double maxNormalDegrees = 0.0;
        for (int level = 0; level < Spacings.Length; level++)
        {
            (int sectors, int skirts, double normalDegrees, string line) = await RunLevel(rd, runtime, Spacings[level], level, "runtime");
            totalSectors += sectors; totalSkirtVertices += skirts; maxNormalDegrees = Math.Max(maxNormalDegrees, normalDegrees);
            GD.Print(line);
        }
        // Legacy (non runtime-chord) sampler: no chord texture, legacy diagonal.
        (int legacySectors, int legacySkirts, double legacyDegrees, string legacyLine) = await RunLevel(rd, legacy, 2, 0, "legacy-no-chord");
        totalSectors += legacySectors; totalSkirtVertices += legacySkirts; maxNormalDegrees = Math.Max(maxNormalDegrees, legacyDegrees);
        GD.Print(legacyLine);

        // Reserved bytes per level for a pool sized like a level (radius around/across, warm sectors).
        (int width, int height, long bytesPerSlot) = RingTerrainProxySector.DescribeVertexTableSlot(Cells, hasChord: true);
        (_, _, long noChordSlot) = RingTerrainProxySector.DescribeVertexTableSlot(Cells, hasChord: false);
        foreach ((int radius, int warm) in new[] { (2, 48), (3, 48), (4, 96), (6, 96) })
        {
            int capacity = RingTerrainProxySector.VertexTableCapacity(radius, radius, warm);
            GD.Print($"PROXY_VERTEX_TABLE_VRAM radius={radius} warm={warm} capacity={capacity} bytes_per_slot={bytesPerSlot} "
                + $"with_chord_mb={capacity * bytesPerSlot / 1048576.0:0.0} no_chord_mb={capacity * noChordSlot / 1048576.0:0.0}");
        }
        GD.Print($"PROXY_VERTEX_TABLE_SLOT texels={width}x{height} bytes_with_chord={bytesPerSlot} bytes_without_chord={noChordSlot}");
        Require(RingTerrainProxySector.VertexTableReservedBytes == 0, "pools leaked reserved bytes");
        return $"PASS levels={Spacings.Length} sectors={totalSectors} skirt_vertices={totalSkirtVertices} max_normal_deg={maxNormalDegrees:0.00000}";
    }

    private async Task<(int Sectors, int Skirts, double NormalDegrees, string Line)> RunLevel(
        RenderingDevice rd, RingTerrainSampler sampler, int spacing, int level, string tag)
    {
        int size = Cells * spacing;
        int ringWidth = sampler.RingAcrossWidthMeters;
        long aroundSectors = (sampler.AroundRingVoxelLength + size - 1) / size;
        int acrossSectors = (ringWidth + size - 1) / size;
        var session = RingTerrainProxySector.CreateVertexTableFixtureSession(EmptyEdits(sampler), sampler, ringWidth);
        var sectors = new List<RingTerrainProxySector.VertexTableFixtureSector>();
        int count = tag == "runtime" ? SectorsPerLevel : 4;
        for (int j = 0; j < count; j++)
        {
            long around = j == 0 ? aroundSectors - 1 : j == 1 ? 0 : (long)(((j * 0.6180339887) % 1.0) * aroundSectors);
            int across = j == 2 ? 0 : j == 3 ? acrossSectors - 1 : j == 4 ? acrossSectors : (j * 7) % Math.Max(1, acrossSectors);
            var coordinate = new RingTerrainSectorCoordinate(around, across);
            var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
                CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0, sampler.AroundRingVoxelLength,
                CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
                new RingCanonicalPosition(around * (long)size, 0.0, across * size));
            RingTerrainProxySector.VertexTableFixtureSector? sector = session.Build(
                coordinate, spacing, Cells, geometry, buildWalls: j % 3 == 0, buildSkirts: j != 5);
            Require(sector != null, $"{tag} spacing={spacing} sector {j} {coordinate} could not be packed");
            sectors.Add(sector!);
            if (j % 5 == 4) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        bool hasChord = sectors[0].UsesRuntimeParentChord;

        ulong memoryBefore = rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures);
        var pool = new RingTerrainProxySector.VertexTablePool(Cells, count, hasChord);
        try
        {
            for (int frame = 0; frame < 300 && !pool.Ready; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(pool.Ready, $"{tag} spacing={spacing} pool never became ready");
            ulong memoryAfter = rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures);
            var slots = new int[count];
            for (int j = 0; j < count; j++)
            {
                Require(pool.TryAcquire(out slots[j]), "slot pool exhausted");
                pool.Upload(slots[j], sectors[j].Blocks);
            }
            Require(!pool.TryAcquire(out _), "pool handed out more slots than its capacity");
            _readbacks.Clear();
            int expectedReads = count * (hasChord ? 3 : 2);
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                for (int j = 0; j < count; j++)
                {
                    int slot = slots[j];
                    rd.TextureGetDataAsync(pool.PosRid, (uint)slot, Callable.From<byte[]>(bytes => _readbacks[(0, slot)] = bytes));
                    rd.TextureGetDataAsync(pool.AttrRid, (uint)slot, Callable.From<byte[]>(bytes => _readbacks[(1, slot)] = bytes));
                    if (hasChord)
                        rd.TextureGetDataAsync(pool.ChordRid, (uint)slot, Callable.From<byte[]>(bytes => _readbacks[(2, slot)] = bytes));
                }
            }));
            for (int frame = 0; frame < 600 && _readbacks.Count < expectedReads; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(_readbacks.Count == expectedReads, $"{tag} spacing={spacing} readbacks {_readbacks.Count}/{expectedReads}");

            int skirtVertices = 0;
            double maxDegrees = 0.0;
            for (int j = 0; j < count; j++)
            {
                int slot = slots[j];
                string? error = sectors[j].VerifyReadback(_readbacks[(0, slot)], hasChord ? _readbacks[(2, slot)] : null,
                    _readbacks[(1, slot)], out string stats);
                Require(error == null, $"{tag} spacing={spacing} sector {j}: {error}");
                skirtVertices += sectors[j].SkirtVertexCount;
                int at = stats.IndexOf("max_normal_deg=", StringComparison.Ordinal);
                if (at >= 0 && double.TryParse(stats.Substring(at + 15).Split(' ')[0], out double degrees))
                    maxDegrees = Math.Max(maxDegrees, degrees);
            }
            string renderNote = "";
            if (spacing is 2 or 512 && tag == "runtime")
                renderNote = " render_check=" + await RenderCheck(pool, sectors, slots, 3);
            string line = $"PROXY_VERTEX_TABLE_LEVEL tag={tag} level={level} spacing={spacing} sectors={count} chord={hasChord} "
                + $"skirt_vertices={skirtVertices} max_normal_deg={maxDegrees:0.00000} pool_bytes={pool.ReservedBytes} "
                + $"rd_texture_bytes_delta={(long)memoryAfter - (long)memoryBefore} bytes_per_slot={pool.BytesPerSlot}{renderNote} result=PASS";
            return (count, skirtVertices, maxDegrees, line);
        }
        finally
        {
            pool.Dispose();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private const string RenderShader = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always;
#include ""res://materials/ring_terrain_proxy_vertex_fetch.gdshaderinc""
uniform int mode = 0;
uniform float morph = 0.0;
varying vec4 v_color;
varying vec2 v_uv;
varying vec2 v_uv2;
varying vec3 v_world;
void vertex() {
	vec3 proxy_chord = CUSTOM0.xyz;
	if (proxy_vertex_table_enabled) proxy_vertex_table_apply(VERTEX_ID, VERTEX, NORMAL, COLOR, UV, UV2, proxy_chord);
	VERTEX += (UV2.y < .5 ? proxy_chord : vec3(0.0, 1.0, 0.0) * UV2.x) * morph;
	v_color = COLOR; v_uv = UV; v_uv2 = UV2;
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}
void fragment() {
	if (mode == 0) ALBEDO = vec3(v_uv, v_uv2.y * 0.5);
	else if (mode == 1) ALBEDO = v_color.rgb;
	else if (mode == 2) ALBEDO = normalize(NORMAL) * 0.5 + 0.5;
	else if (mode == 3) ALBEDO = vec3(fract(v_uv2.x * 0.05), v_uv2.y, 0.25);
	else ALBEDO = fract(v_world * 0.173);
}
";

    /// <summary>
    /// Renders slot-fetched geometry (shared grid, proxy_vertex_table_enabled) and the per-sector ArrayMesh of the same
    /// worker data through one test shader that includes the real fetch file, and compares the pixels.
    /// </summary>
    private async Task<string> RenderCheck(RingTerrainProxySector.VertexTablePool pool,
        List<RingTerrainProxySector.VertexTableFixtureSector> sectors, int[] slots, int sectorCount)
    {
        var shader = new Shader { Code = RenderShader };
        using var referenceMaterial = new ShaderMaterial { Shader = shader };
        using var slotMaterial = new ShaderMaterial { Shader = shader };
        RingTerrainProxySector.ApplyVertexTableUniforms(slotMaterial, pool, true);
        var viewport = new SubViewport
        {
            Size = new Vector2I(256, 256), OwnWorld3D = true, TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Msaa3D = Viewport.Msaa.Disabled,
        };
        AddChild(viewport);
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Current = true };
        viewport.AddChild(camera);
        long differing = 0, total = 0;
        int renders = 0;
        try
        {
            for (int j = 0; j < sectorCount; j++)
            {
                var sector = sectors[j];
                (Vector3 center, Vector3 normal, float extent) = sector.ViewFrame();
                camera.Size = Math.Max(extent, 1.0f);
                camera.Far = extent * 4.0f + 100.0f;
                camera.Near = 0.01f;
                Vector3 up = Math.Abs(normal.Dot(Vector3.Right)) < 0.9f ? Vector3.Right : Vector3.Forward;
                camera.Position = center + normal * (extent * 1.5f + 50.0f);
                camera.LookAt(center, up);
                using ArrayMesh referenceMesh = sector.BuildReferenceTerrainMesh();
                using ArrayMesh gridMesh = RingTerrainProxySector.BuildTerrainGridMesh(Cells, sector.UsesRuntimeParentChord);
                var referenceNode = new MeshInstance3D { Mesh = referenceMesh, MaterialOverride = referenceMaterial };
                var slotNode = new MeshInstance3D { Mesh = gridMesh, MaterialOverride = slotMaterial, CustomAabb = new Aabb(center - Vector3.One * extent * 4.0f, Vector3.One * extent * 8.0f) };
                slotNode.SetInstanceShaderParameter("proxy_slot", slots[j]);
                viewport.AddChild(referenceNode);
                viewport.AddChild(slotNode);
                foreach (float morph in new[] { 0.0f, 0.6f })
                for (int mode = 0; mode < 5; mode++)
                {
                    referenceMaterial.SetShaderParameter("mode", mode); slotMaterial.SetShaderParameter("mode", mode);
                    referenceMaterial.SetShaderParameter("morph", morph); slotMaterial.SetShaderParameter("morph", morph);
                    Image reference = await Capture(viewport, referenceNode, slotNode, showSlot: false);
                    Image fetched = await Capture(viewport, referenceNode, slotNode, showSlot: true);
                    byte[] a = reference.GetData(), b = fetched.GetData();
                    Require(a.Length == b.Length, "render size mismatch");
                    long lit = 0;
                    for (int i = 0; i < a.Length; i += 4)
                    {
                        total++;
                        if (a[i] != 0 || a[i + 1] != 0 || a[i + 2] != 0) lit++;
                        if (Math.Abs(a[i] - b[i]) > 2 || Math.Abs(a[i + 1] - b[i + 1]) > 2 || Math.Abs(a[i + 2] - b[i + 2]) > 2) differing++;
                    }
                    Require(lit > 2000, $"render check sector {j} mode {mode} drew almost nothing ({lit} px)");
                    renders++;
                }
                referenceNode.QueueFree(); slotNode.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        finally { viewport.QueueFree(); }
        Require(differing * 1000 <= total, $"render check: {differing} of {total} pixels differ by more than 2 levels");
        return $"{renders}renders/{differing}px_of_{total}_differ";
    }

    private async Task<Image> Capture(SubViewport viewport, MeshInstance3D reference, MeshInstance3D slot, bool showSlot)
    {
        reference.Visible = !showSlot; slot.Visible = showSlot;
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return viewport.GetTexture().GetImage();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);
}
