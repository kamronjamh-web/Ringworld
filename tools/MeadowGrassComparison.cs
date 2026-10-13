using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Bounded production scheduler fixture; no world startup or gameplay nodes.</summary>
public partial class MeadowGrassComparison : Node3D
{
    public bool ReadyForCapture { get; private set; }
    public string Failure { get; private set; } = "";
    public RingStandingGrassLayer? Grass { get; private set; }
    private const long Circumference = 981748L * 32L;
    private RingTerrainSampler? _sampler;
    private SmoothTerrainVolumetricDensitySnapshot? _field;
    private RingLodSurfaceEditSnapshot? _edits;
    private RingGrassExclusionSnapshot? _exclusions;
    private RingCanonicalPosition _focus;
    private RingLodGeometrySnapshot _geometry;
    private float _siteMinimumGrassWeight;

    public override void _Ready()
    {
        try { InitializeFixture(); }
        catch(Exception e) { Failure=e.ToString(); GD.PushError(Failure); }
    }

    private void InitializeFixture()
    {
        var settings = (VoxelTerrainSettings)GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres").Duplicate(true);
        _sampler = new RingTerrainSampler(settings, true, Circumference, 5000000d, 318000, 8000);
        _focus = FindGrasslandFocus(_sampler);
        long around=(long)_focus.AroundMeters;
        int across=(int)_focus.AcrossMeters;
        double angle = _focus.AroundMeters / 5000000d;
        var source = new Basis(new Vector3((float)-Math.Cos(angle), 0, (float)-Math.Sin(angle)), Vector3.Up, new Vector3((float)-Math.Sin(angle), 0, (float)Math.Cos(angle)));
        var target = new Basis(Vector3.Up, Vector3.Right, Vector3.Back);
        _geometry = new RingLodGeometrySnapshot(new Transform3D(target * source.Transposed(), Vector3.Zero), Vector3.Zero, Vector3.Zero, 5000000d, 0, 0, Circumference, -1000, 1000, _focus);
        var hints = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long a = around-80; a <= around+80; a++)
        for (int b = across-80; b <= across+80; b++) hints[new RingTerrainColumnCoordinate(a, b)] = 42;
        _edits = new RingLodSurfaceEditSnapshot(1, Circumference, hints);
        _field = new SmoothTerrainVolumetricDensitySnapshot(new PlaneBaseline(), new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 1, Circumference));
        _exclusions = new RingGrassExclusionSnapshot(1, Circumference, new[] {
            new RingGrassExclusionBox(around-200,around-48,0,100,across-200,across+200),
            new RingGrassExclusionBox(around+48,around+200,0,100,across-200,across+200),
            new RingGrassExclusionBox(around-48,around+48,0,100,across-200,across-48),
            new RingGrassExclusionBox(around-48,around+48,0,100,across+48,across+200) });
        RingTerrainVisuals.PreloadTexturePalette();
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://tools/meadow_grass_ground.gdshader") };
        var sample = _sampler.GetTerrainSample(around,across);
        var weights = _sampler.GetBiomeTextureWeights(sample);
        var palette = RingTerrainVisuals.CaptureTexturePalette();
        float grass = Mathf.Max(0,1-weights.Desert-weights.Forest-weights.Tundra-weights.Alpine);
        material.SetShaderParameter("terrain_color", palette.Grass*grass + palette.Desert*weights.Desert + palette.Forest*weights.Forest + palette.Tundra*weights.Tundra + palette.Alpine*weights.Alpine);
        material.SetShaderParameter("eligibility", grass + weights.Forest*.65f);
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(160,160), Material = material } });
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_enabled",1f);
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_start_meters",20f);
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_end_meters",32f);
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_strength",.6f);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled",0f);
    }

    public async void BuildCase(string label, bool lod)
    {
        if(_edits==null) { GD.PushError("MEADOW_COMPARISON initialization failed: "+Failure); return; }
        ReadyForCapture = false; Failure = "";
        try
        {
            if (Grass != null) { Grass.QueueFree(); await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame); }
            bool authored = label == "meadow_short";
            Grass = new RingStandingGrassLayer {
                EnableMeadowTufts = authored, UseGrassClumps = true, MeadowTuftVariant = 0,
                UseReducedNearGrassGeometry=true,
                UseSingleRibbonIntermediateGrass=true,
                EnableGrassCameraFacing=true, GrassCameraFacingStrength=1,
                // Same untouched gameplay policy: fully face before the near handoff.
                GrassCameraFacingStartMeters=3.5f, GrassCameraFacingEndMeters=4.5f,
                GrassMinimumHeightMeters=label=="legacy_current"?.42f:.10f,
                GrassMaximumHeightMeters=label=="legacy_current"?.92f:.20f,
                BladesPerSquareMeter=24, VisibilityRadiusMeters=40, SpatialTileMeters=16,
                MaximumInstancesPerTile=4096, MaximumInstances=250000, MaximumResidentTiles=64,
                MaximumTileBuildsInFlight=2, MaximumTileUploadsPerFrame=4,
                EnableDistanceLod=lod, EnableHierarchicalCoverage=lod,
                LodNearDistanceMeters=8, LodMidDistanceMeters=18, LodFarDistanceMeters=36,
                LodTransitionWidthMeters=6, EnableGrassAppearance=true,
                GrasslandBiomeDensity=1, ForestBiomeDensity=1, SparseBiomeDensity=1,
                GrasslandBiomeHeight=1, ForestBiomeHeight=1, SparseBiomeHeight=1,
                EnableCoordinatedGrassHandoff=true, EnableGrassGustTexture=false,
                UseGpuCulling=true, CullGrassTiles=false, GpuFrustumCulling=false
            };
            AddChild(Grass); Grass.InitializeForTests();
            ulong deadline = Time.GetTicksMsec()+20000;
            int settledFrames = 0;
            while(Time.GetTicksMsec()<deadline)
            {
                Grass.StepForTests(_sampler!,_edits!,_focus,_geometry,field:_field,exclusions:_exclusions);
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                settledFrames = Grass.PendingGrassTileBuildCount==0 && Grass.PendingGrassUploadCount==0 && Grass.ResidentGrassInstanceCount>0 ? settledFrames+1 : 0;
                if(settledFrames<6) continue;
                ReadyForCapture=true;
                return;
            }
            throw new TimeoutException("bounded production fixture did not settle");
        }
        catch(Exception e) { Failure=e.ToString(); GD.PushError(Failure); }
    }

    public void RefreshPresentation() => Grass?.RefreshLodPresentationForTests(_focus,_geometry,Circumference);
    public Godot.Collections.Dictionary Snapshot()
    {
        var result = Grass?.CaptureGrassStreamingDiagnostics() ?? new Godot.Collections.Dictionary();
        result["anchor_signature"] = AnchorSignature();
        result["effective_draw_distance"] = Grass?.EffectiveGrassDrawDistance() ?? 0;
        result["handoff_band"] = Grass?.CoordinatedGrassHandoffBand() ?? Vector2.Zero;
        result["wind"] = "calm; simulation fixed";
        result["site_around"] = _focus.AroundMeters;
        result["site_across"] = _focus.AcrossMeters;
        result["site_minimum_grass_weight"] = _siteMinimumGrassWeight;
        result["camera_facing_enabled"] = Grass?.EnableGrassCameraFacing ?? false;
        result["single_intermediate_ribbon_active"] = Grass?.IsSingleRibbonIntermediateGrassActive ?? false;
        result["camera_facing_start_m"] = Grass?.GrassCameraFacingStartMeters ?? 0;
        result["camera_facing_end_m"] = Grass?.GrassCameraFacingEndMeters ?? 0;
        result["anchor_scope"] = "sorted tile/tier plus local root origins; excludes geometry and height";
        var tiles = Grass?.CaptureTileDataForTests() ?? Array.Empty<RingGrassTileData>();
        result["source_count"] = tiles.Sum(t=>t.InstanceCount);
        float minimum=float.PositiveInfinity,maximum=0;
        foreach(var tile in tiles)
        for(int i=0;i<tile.Buffer.Length;i+=20)
        {
            float height=new Vector3(tile.Buffer[i+1],tile.Buffer[i+5],tile.Buffer[i+9]).Length();
            minimum=Mathf.Min(minimum,height); maximum=Mathf.Max(maximum,height);
        }
        result["height_min_m"] = float.IsFinite(minimum)?minimum:0;
        result["height_max_m"] = maximum;
        result["mesh_triangles_per_source"] = new Godot.Collections.Dictionary {
            ["near"]=Grass?.UseReducedNearGrassGeometry==true?16:32,
            ["intermediate"]=Grass?.EnableMeadowTufts==true?12:Grass?.IsSingleRibbonIntermediateGrassActive==true?8:16,
            ["far"]=Grass?.EnableMeadowTufts==true?8:16 };
        return result;
    }

    private string AnchorSignature()
    {
        ulong hash=14695981039346656037UL;
        foreach(var tile in (Grass?.CaptureTileDataForTests() ?? Array.Empty<RingGrassTileData>()).OrderBy(t=>t.Key.Around).ThenBy(t=>t.Key.Across))
        {
            hash=unchecked((hash^(ulong)tile.Key.Around)*1099511628211UL);
            hash=unchecked((hash^(ulong)tile.Key.Across)*1099511628211UL);
            hash=unchecked((hash^tile.SourceTier)*1099511628211UL);
            for(int i=0;i<tile.Buffer.Length;i+=20)
            foreach(int component in new[]{3,7,11}) hash=unchecked((hash^(uint)BitConverter.SingleToInt32Bits(tile.Buffer[i+component]))*1099511628211UL);
        }
        return hash.ToString("X16");
    }

    public override void _ExitTree() { _sampler?.Dispose(); _field?.DisposePooledWorkerContexts(); }

    private RingCanonicalPosition FindGrasslandFocus(RingTerrainSampler sampler)
    {
        // Use actual biome classification at a dry, uniformly grassy 96m patch.
        // Search local region first, then a bounded coarse ring grid; reject desert
        // rather than silently repainting it. Nine probes include patch corners.
        float best=-1;
        long bestAround=0;
        int bestAcross=0;
        for(int pass=0;pass<2;pass++)
        for(int a=0;a<64;a++)
        for(int b=0;b<32;b++)
        {
            long around=pass==0 ? 184000+a*128L : Circumference*a/64;
            int across=pass==0 ? 139000+b*128 : 1000+(316000*b/31);
            float minimum=1;
            for(int da=-48;da<=48;da+=48)
            for(int db=-48;db<=48;db+=48)
            {
                TerrainSample sample=sampler.GetTerrainSample(around+da,across+db);
                var weights=sampler.GetBiomeTextureWeights(sample);
                float grass=sample.HasSurfaceWater?0:Mathf.Max(0,1-weights.Desert-weights.Forest-weights.Tundra-weights.Alpine);
                minimum=Mathf.Min(minimum,grass);
            }
            if(minimum<=best) continue;
            best=minimum; bestAround=around; bestAcross=across;
            if(best<.9f) continue;
            _siteMinimumGrassWeight=best;
            GD.Print($"MEADOW_COMPARISON_SITE around={bestAround} across={bestAcross} min_grass_weight={best:F3}");
            return new RingCanonicalPosition(bestAround,42.25,bestAcross);
        }
        throw new InvalidOperationException($"No representative grassland patch found; best minimum grass weight {best:F3} at {bestAround}/{bestAcross}");
    }
    private sealed class PlaneBaseline : ISmoothTerrainBaseline
    {
        public long AroundLength=>Circumference;
        public int AcrossWidth=>318000;
        public ulong Revision=>1401;
        public SmoothTerrainSample Sample(long around,int vertical,int across)=>new(vertical-42.25f,(int)(vertical<40.25f?SmoothTerrainMaterial.Rock:SmoothTerrainMaterial.Soil),Revision);
    }
}
