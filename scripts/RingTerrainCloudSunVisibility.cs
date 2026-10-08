using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

public sealed record RingTerrainCloudSunVisibilitySettings
{
    public bool Enabled { get; init; } = true;
    public float Strength { get; init; } = 1;
    public int GlobalAroundResolution { get; init; } = 1024;
    public int GlobalAcrossResolution { get; init; } = 32;
    public int AltitudeSlices { get; init; } = 8;
    public int RegionalResolution { get; init; } = 64;
    public float RegionalExtentMeters { get; init; } = 32000;
    public float RefreshSeconds { get; init; } = .5f;
    public int TraceVisitBudget { get; init; } = 32;
    public int TexelsPerFrame { get; init; } = 32768;
    public float SunValidityDegrees { get; init; } = .25f;
    public RingTerrainCloudSunVisibilitySettings Normalized() => this with {
        Strength = Mathf.Clamp(float.IsFinite(Strength) ? Strength : 1,0,1),
        GlobalAroundResolution = Power(GlobalAroundResolution,256,4096),
        GlobalAcrossResolution = Power(GlobalAcrossResolution,8,128),
        AltitudeSlices = Math.Clamp(AltitudeSlices,2,16), RegionalResolution = Power(RegionalResolution,32,256),
        RegionalExtentMeters = Mathf.Clamp(float.IsFinite(RegionalExtentMeters) ? RegionalExtentMeters : 32000,8000,128000),
        RefreshSeconds = Mathf.Clamp(float.IsFinite(RefreshSeconds) ? RefreshSeconds : .5f,.1f,10),
        TraceVisitBudget = Math.Clamp(TraceVisitBudget,8,128), TexelsPerFrame = Math.Clamp(TexelsPerFrame,4096,131072),
        SunValidityDegrees = Mathf.Clamp(float.IsFinite(SunValidityDegrees) ? SunValidityDegrees : .25f,.01f,2) };
    private static int Power(int value,int min,int max) { int n=min; while(n<value && n<max)n*=2; return n; }
}

public sealed record RingTerrainCloudSunVisibilitySnapshot
{
    public Texture2D? GlobalTexture { get; init; }
    public Texture2D? RegionalTexture { get; init; }
    private Godot.Collections.Array<Vector4> _profile;
    public Godot.Collections.Array<Vector4> Profile { get => new(_profile); init => _profile=new(value); }
    public RingLodGeometrySnapshot Geometry { get; init; }
    public ulong Generation { get; init; }
    public ulong EditVersion { get; init; }
    public bool Ready { get; init; }
    public string Status { get; init; }
    public RingTerrainCloudSunVisibilitySnapshot(Texture2D? global,Texture2D? regional,Godot.Collections.Array<Vector4> profile,
        RingLodGeometrySnapshot geometry,ulong generation,ulong editVersion,bool ready,string status)
    {GlobalTexture=global;RegionalTexture=regional;_profile=new(profile);Geometry=geometry;Generation=generation;EditVersion=editVersion;Ready=ready;Status=status;}
}

/// <summary>Independent, bounded terrain sunlight producer shared by local and far clouds.</summary>
public partial class RingTerrainCloudSunVisibility : Node
{
    public static RingTerrainCloudSunVisibility? Instance { get; private set; }
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath FocusPath { get; set; } = new("../Explorer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    public RingTerrainCloudSunVisibilitySettings Settings { get; private set; } = new();
    public RingTerrainCloudSunVisibilitySnapshot CurrentSnapshot => _snapshot;
    private RingTerrainCloudSunVisibilitySnapshot _snapshot = new(null,null,new(){Vector4.Zero,Vector4.Zero,Vector4.Zero,Vector4.Zero,Vector4.Zero},default,0,0,false,"uninitialized");
    private VoxelChunkGridManager? _grid;
    private RingTerrainGlobalLod? _global;
    private RingVoxelExplorerController? _focus;
    private DirectionalLight3D? _sun;
    private GodotObject? _gpu;
    private bool _diagnosticEnabled;
    private string _diagnosticBegin="",_diagnosticEnd="";
    private ImageTexture? _neutral;
    private CloudSunTerrainSnapshot? _terrain;
    private int _terrainSourceIdentity;
    private Task<CloudSunTerrainSnapshot>? _globalTask;
    private readonly System.Collections.Generic.Dictionary<string,bool> _receiverDemand=new();
    private Task<RegionalHeights>? _regionalTask;
    private RegionalHeights? _regional;
    private GenerationInputs? _pending;
    private GenerationInputs? _published;
    private ulong _nextGeneration;
    private long _bindingRevision;
    private long _lightingRevision;
    private readonly System.Collections.Generic.Dictionary<ulong,(WeakReference<ShaderMaterial> Material,long Revision)> _materials=new();
    private CloudSunTerrainSnapshot? _packedTerrain;
    private float[] _packedHeights=Array.Empty<float>(),_packedLevels=Array.Empty<float>();
    private double _elapsed=10;
    private float _altitudeMin=0, _altitudeMax=12000;
    private readonly System.Collections.Generic.Dictionary<string,Vector2> _requiredRanges=new();
    private readonly System.Collections.Generic.Dictionary<ulong,(WeakReference<Resource> Resource,long Revision,long LightingRevision,RingLodGeometrySnapshot Geometry)> _resources=new();
    private sealed record RegionalHeights(float[] Heights,int Resolution,double Around,double Across,float Extent,ulong EditVersion,RingLodGeometrySnapshot Geometry,int SourceIdentity);
    private sealed record GenerationInputs(ulong Generation,ulong EditVersion,Vector3 Sun,RingLodGeometrySnapshot Geometry,
        RegionalHeights Regional,RingTerrainCloudSunVisibilitySettings Settings,float MinAltitude,float MaxAltitude);

    public void Configure(RingTerrainCloudSunVisibilitySettings settings)
    {
        var normalized=settings.Normalized();
        if(normalized==Settings)return;
        Settings=normalized; _elapsed=10; _regional=null; Invalidate("settings changed");
    }
    public void SetRequiredAltitudeRange(float minimumMeters,float maximumMeters)=>RegisterRequiredAltitudeRange("sunshine",minimumMeters,maximumMeters);
    public void RegisterRequiredAltitudeRange(string owner,float minimumMeters,float maximumMeters)
    {
        if(string.IsNullOrWhiteSpace(owner)||!float.IsFinite(minimumMeters)||!float.IsFinite(maximumMeters))return;
        if(!_receiverDemand.ContainsKey(owner))_receiverDemand[owner]=true;
        _requiredRanges[owner]=new(Math.Min(minimumMeters,maximumMeters),Math.Max(minimumMeters,maximumMeters));
        ResolveAltitudeRanges();
    }
    public void SetReceiverDemand(string owner,bool active){_receiverDemand[owner]=active;}
    public void UnregisterRequiredAltitudeRange(string owner){_receiverDemand.Remove(owner);if(_requiredRanges.Remove(owner))ResolveAltitudeRanges();}
    private void ResolveAltitudeRanges()
    {
        float lo=_requiredRanges.Count==0?0:_requiredRanges.Values.Min(value=>value.X);
        float hi=_requiredRanges.Count==0?12000:_requiredRanges.Values.Max(value=>value.Y);
        // Quantized coverage avoids rebuilding for every small weather interpolation.
        lo=MathF.Floor(lo/500)*500;hi=Math.Max(lo+500,MathF.Ceiling(hi/500)*500);
        if(lo==_altitudeMin&&hi==_altitudeMax)return;
        _altitudeMin=lo;_altitudeMax=hi;_elapsed=10;Invalidate("altitude range changed");
    }
    public override void _Ready()
    {
        Instance?.Invalidate("owner replaced"); Instance=this;
        _grid=GetNodeOrNull<VoxelChunkGridManager>(GridPath);_global=GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _focus=GetNodeOrNull<RingVoxelExplorerController>(FocusPath);_sun=GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        var image=Image.CreateEmpty(1,1,false,Image.Format.Rgf);image.SetPixel(0,0,new Color(1,0,0,1));_neutral=ImageTexture.CreateFromImage(image);
        var script=GD.Load<GDScript>("res://scripts/render/RingTerrainCloudSunVisibilityGpu.gd");
        _gpu=script.New().AsGodotObject();
        Invalidate("waiting for terrain");
    }
    public override void _ExitTree()
    {
        if(ReferenceEquals(Instance,this))Invalidate("owner exited");_gpu?.Call("release");_gpu=null;
        if(ReferenceEquals(Instance,this))Instance=null;
    }
    private void Invalidate(string status)
    {
        if(!_snapshot.Ready&&_snapshot.Status==status)return;
        if(_snapshot.Ready)_lightingRevision++;
        var profile=_snapshot.Profile;
        if(profile.Count==5)profile[3]=new(profile[3].X,0,0,1);
        _snapshot=_snapshot with {GlobalTexture=_neutral,RegionalTexture=_neutral,Profile=profile,Ready=false,Status=status};
        _bindingRevision++;
        RefreshRegisteredBindings();
    }
    private void RefreshRegisteredBindings()
    {
        foreach(var entry in _materials.ToArray())
            if(entry.Value.Material.TryGetTarget(out var material)&&GodotObject.IsInstanceValid(material))BindToMaterial(material);
            else _materials.Remove(entry.Key);
        foreach(var entry in _resources.ToArray())
            if(entry.Value.Resource.TryGetTarget(out var resource)&&GodotObject.IsInstanceValid(resource))BindToResource(resource);
            else _resources.Remove(entry.Key);
    }
    public override void _Process(double delta)
    {
        if(!ReferenceEquals(Instance,this))return;
        if(!Settings.Enabled||!_receiverDemand.Values.Any(active=>active)){
            Invalidate(Settings.Enabled?"no active cloud receivers":"disabled");
            if(_pending!=null){_gpu?.Call("discard_generation");_pending=null;}return;}
        if(_grid==null||_focus==null||_sun==null||!GodotObject.IsInstanceValid(_grid)||!GodotObject.IsInstanceValid(_sun))
        {Invalidate("source unavailable");return;}
        _elapsed+=delta;
        var geometry=_grid.CaptureLodGeometrySnapshot();
        Vector3 sun=geometry.GridGlobalTransform.Basis.Inverse()*_sun.GlobalBasis.Z.Normalized();sun=sun.Normalized();
        ulong edits=_grid.LodSurfaceEditVersion;
        int sourceIdentity=_global!=null&&GodotObject.IsInstanceValid(_global)?_global.CloudSunSourceIdentity:0;
        int samplerIdentity=_grid.TerrainSampler==null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_grid.TerrainSampler);
        if(sourceIdentity==0)sourceIdentity=samplerIdentity;
        double width=_grid.TerrainSampler?.RingAcrossWidthMeters??_grid.RingAcrossWidthMeters;
        bool valid(GenerationInputs inputs)=>inputs.EditVersion==edits && inputs.Regional.SourceIdentity==sourceIdentity && _terrain?.Width==width && inputs.Settings==Settings
            && inputs.MinAltitude==_altitudeMin&&inputs.MaxAltitude==_altitudeMax
            && SameGeometry(inputs.Geometry,geometry)&&inputs.Sun.Dot(sun)>=Mathf.Cos(Mathf.DegToRad(Settings.SunValidityDegrees));
        _snapshot=_snapshot with {Geometry=geometry};
        if(_published!=null&&!valid(_published))Invalidate("sun, terrain or geometry changed");
        if(_pending!=null&&!valid(_pending)){_gpu?.Call("discard_generation");_pending=null;_elapsed=10;}
        if(_globalTask is {IsCompleted:true}){
            if(_globalTask.IsCompletedSuccessfully){var next=_globalTask.Result;if(next.EditVersion==edits&&next.Circumference==geometry.CircumferenceMeters&&next.Width==width&&next.SourceIdentity==sourceIdentity){_terrain=next;_terrainSourceIdentity=sourceIdentity;_regional=null;}}
            else GD.PushWarning("Cloud terrain global snapshot failed: "+_globalTask.Exception?.GetBaseException().Message);
            _globalTask=null;
        }
        if(_terrain==null||_terrain.EditVersion!=edits||_terrain.Circumference!=geometry.CircumferenceMeters||_terrain.Width!=width||_terrainSourceIdentity!=sourceIdentity)
        {
            var next=_global!=null&&GodotObject.IsInstanceValid(_global)?_global.CaptureCloudSunTerrainSnapshot():null;
            if(next!=null){_terrain=next;_terrainSourceIdentity=sourceIdentity;_regional=null;}
            else{
                if(_globalTask==null){var sampler=_grid.CreateIndependentTerrainSampler();var snapshot=_grid.CaptureLodSurfaceEditSnapshot();
                    if(sampler!=null){double circumference=geometry.CircumferenceMeters;int sourceWidth=(int)width;
                        _globalTask=Task.Run(()=>{using(sampler)return CloudSunTerrainSnapshotBuilder.Build(circumference,sourceWidth,snapshot.Version,
                            (a,z)=>snapshot.GetPresentationSurfaceHeight(sampler,a,z)) with {SourceIdentity=sourceIdentity};});}}
                Invalidate("terrain snapshot pending");return;
            }
        }
        var position=_focus.CanonicalPosition;
        double centerAround=Math.Floor(position.AroundMeters/4000)*4000;
        double centerAcross=Math.Floor(position.AcrossMeters/4000)*4000;
        if(_regionalTask is {IsCompleted:true}){
            if(_regionalTask.IsCompletedSuccessfully){var candidate=_regionalTask.Result;
                if(candidate.EditVersion==edits&&candidate.SourceIdentity==sourceIdentity&&SameGeometry(candidate.Geometry,geometry)
                   &&candidate.Resolution==Settings.RegionalResolution*2&&candidate.Extent==Settings.RegionalExtentMeters
                   &&candidate.Around==centerAround&&candidate.Across==centerAcross)_regional=candidate;}
            else GD.PushWarning("Cloud terrain regional snapshot failed: "+_regionalTask.Exception?.GetBaseException().Message);
            _regionalTask=null;
        }
        if(_regionalTask==null&&(_regional==null||_regional.EditVersion!=edits||_regional.Around!=centerAround||_regional.Across!=centerAcross||_regional.Extent!=Settings.RegionalExtentMeters||_regional.Resolution!=Settings.RegionalResolution*2||_regional.SourceIdentity!=sourceIdentity||!SameGeometry(_regional.Geometry,geometry)))
        {
            var sampler=_grid.CreateIndependentTerrainSampler();
            var editSnapshot=_grid.CaptureLodSurfaceEditSnapshot();
            int resolution=Settings.RegionalResolution*2;float extent=Settings.RegionalExtentMeters;
            double circumference=_terrain.Circumference,regionalWidth=_terrain.Width;
            if(sampler!=null)_regionalTask=Task.Run(()=>{
                using(sampler){var heights=new float[resolution*resolution];
                    for(int y=0;y<resolution;y++)for(int x=0;x<resolution;x++){
                        double around=centerAround+((x+.5)/resolution-.5)*extent;
                        double a=((around%circumference)+circumference)%circumference;
                        int z=(int)Math.Clamp(Math.Floor(centerAcross+((y+.5)/resolution-.5)*extent),0,regionalWidth-1);
                        heights[y*resolution+x]=editSnapshot.GetPresentationSurfaceHeight(sampler,a,z);
                    }
                    return new RegionalHeights(heights,resolution,centerAround,centerAcross,extent,editSnapshot.Version,geometry,sourceIdentity);
                }});
        }
        if(_pending!=null&&_gpu!=null)
        {
            bool diagnostics=RingPerformanceGpu.Enabled;
            string begin=RingPerformanceGpu.TerrainCloudSunVisibilityBeginMarker,end=RingPerformanceGpu.TerrainCloudSunVisibilityEndMarker;
            if(_diagnosticEnabled!=diagnostics||_diagnosticBegin!=begin||_diagnosticEnd!=end){_gpu.Call("configure_diagnostics",diagnostics,begin,end);_diagnosticEnabled=diagnostics;_diagnosticBegin=begin;_diagnosticEnd=end;}
            _gpu.Call("advance",Settings.TexelsPerFrame);
            var result=_gpu.Call("take_completed").AsGodotDictionary();
            if(result.Count>0 && (!result.ContainsKey("generation")||result["generation"].AsInt64()==(long)_pending.Generation)){
                if(result.ContainsKey("error")){Invalidate(result["error"].AsString());_gpu.Call("discard_generation");}
                else if(valid(_pending))Publish(_pending,result,geometry);
                else _gpu.Call("discard_generation");
                _pending=null;
            }
        }
        if(_pending!=null||_regional==null||_regional.EditVersion!=edits||_elapsed<Settings.RefreshSeconds||_gpu==null)return;
        _elapsed=0;
        _pending=new(++_nextGeneration,edits,sun,geometry,_regional,Settings,_altitudeMin,_altitudeMax);
        _gpu.Call("begin_generation",BuildRequest(_pending,_terrain));
    }
    private static bool SameGeometry(RingLodGeometrySnapshot a,RingLodGeometrySnapshot b)=>
        a.RingRadiusMeters==b.RingRadiusMeters&&a.CircumferenceMeters==b.CircumferenceMeters
        &&a.RingAcrossBaseOffsetMeters==b.RingAcrossBaseOffsetMeters&&a.RingVerticalBaseOffsetMeters==b.RingVerticalBaseOffsetMeters&&a.StructuralWallTopGlobalY==b.StructuralWallTopGlobalY
        &&a.StructuralBottomGlobalY==b.StructuralBottomGlobalY;
    private Godot.Collections.Dictionary BuildRequest(GenerationInputs i,CloudSunTerrainSnapshot terrain)
    {
        if(!ReferenceEquals(_packedTerrain,terrain)){
            _packedTerrain=terrain;
            _packedHeights=terrain.Levels.SelectMany(level=>level.Ranges.ToArray().Where((_,index)=>index%2==1)).ToArray();
            _packedLevels=new float[terrain.Levels.Count*4];int offset=0;
            for(int n=0;n<terrain.Levels.Count;n++){var level=terrain.Levels[n];_packedLevels[n*4]=level.Width;_packedLevels[n*4+1]=level.Height;_packedLevels[n*4+2]=offset;_packedLevels[n*4+3]=level.CellSize;offset+=level.Width*level.Height;}
        }
        return new(){["generation"]=(long)i.Generation,["terrain_key"]=$"{terrain.SourceIdentity}:{terrain.EditVersion}:{terrain.Circumference}:{terrain.Width}",["levels"]=_packedLevels,["height"] = _packedHeights,["height_size"]=new Vector2I(terrain.Levels[0].Width,terrain.Levels[0].Height),
            ["regional_height"]=i.Regional.Heights,["regional_height_size"]=i.Regional.Resolution,["regional_maximum"]=i.Regional.Heights.Max(),["regional_minimum"]=i.Regional.Heights.Min(),
            ["circumference"]=(float)terrain.Circumference,["width"]=(float)terrain.Width,["radius"]=(float)i.Geometry.RingRadiusMeters,
            ["vertical_offset"]=(float)i.Geometry.RingVerticalBaseOffsetMeters,["sun"]=i.Sun,
            ["global_size"]=new Vector2I(i.Settings.GlobalAroundResolution,i.Settings.GlobalAcrossResolution),["regional_size"]=i.Settings.RegionalResolution,
            ["slices"]=i.Settings.AltitudeSlices,["altitudes"]=new Vector2(i.MinAltitude,i.MaxAltitude),["regional_center"]=new Vector2((float)i.Regional.Around,(float)i.Regional.Across),
            ["regional_extent"]=i.Regional.Extent,["visits"]=i.Settings.TraceVisitBudget,
            ["maximum_height"]=Math.Max(terrain.MaximumHeight,i.Regional.Heights.Max()),["minimum_height"]=Math.Min(terrain.MinimumHeight,i.Regional.Heights.Min()),
            ["walls"]=_global?.BuildStructuralEdgeWalls==true?new Vector2((float)i.Geometry.StructuralBottomGlobalY,(float)i.Geometry.StructuralWallTopGlobalY):new Vector2(1,0)};
    }
    private void Publish(GenerationInputs i,Godot.Collections.Dictionary result,RingLodGeometrySnapshot geometry)
    {
        var global=new Texture2Drd{TextureRdRid=result["global"].AsRid()};var regional=new Texture2Drd{TextureRdRid=result["regional"].AsRid()};
        var profile=new Godot.Collections.Array<Vector4>{new((float)_terrain!.Circumference,(float)_terrain.Width,i.MinAltitude,i.MaxAltitude),
            new(i.Settings.GlobalAroundResolution,i.Settings.GlobalAcrossResolution,i.Settings.AltitudeSlices,i.Settings.Strength),
            new(i.Settings.RegionalResolution,i.Settings.RegionalResolution,i.Regional.Extent,(float)i.Regional.Around),new((float)i.Regional.Across,1,1,1),
            new((float)geometry.RingRadiusMeters,(float)geometry.RingVerticalBaseOffsetMeters,0,0)};
        // Replacing an equivalent cached field must preserve temporal convergence.
        if(!_snapshot.Ready)_lightingRevision++;
        _snapshot=new(global,regional,profile,geometry,i.Generation,i.EditVersion,true,"ready: sampled heightfield");_published=i;_bindingRevision++;
        // Update even paused receiver owners before retiring their previous atlas RIDs.
        RefreshRegisteredBindings();
        _gpu?.Call("acknowledge_publication",(long)i.Generation);
    }
    private static Variant TextureVariant(Texture2D? texture)=>texture is null?default:Variant.From(texture);
    public Godot.Collections.Dictionary GetDiagnostics()
    {
        var settings=new Godot.Collections.Dictionary{["enabled"]=Settings.Enabled,["strength"]=Settings.Strength,
            ["globalAroundResolution"]=Settings.GlobalAroundResolution,["globalAcrossResolution"]=Settings.GlobalAcrossResolution,
            ["altitudeSlices"]=Settings.AltitudeSlices,["regionalResolution"]=Settings.RegionalResolution,["regionalExtentMeters"]=Settings.RegionalExtentMeters,
            ["refreshSeconds"]=Settings.RefreshSeconds,["traceVisitBudget"]=Settings.TraceVisitBudget,["texelsPerFrame"]=Settings.TexelsPerFrame,["sunValidityDegrees"]=Settings.SunValidityDegrees};
        var demand=new Godot.Collections.Dictionary();foreach(var pair in _receiverDemand)demand[pair.Key]=pair.Value;
        var ranges=new Godot.Collections.Dictionary();foreach(var pair in _requiredRanges)ranges[pair.Key]=pair.Value;
        var gpu=_gpu!=null&&GodotObject.IsInstanceValid(_gpu)?_gpu.Call("get_diagnostics").AsGodotDictionary():new Godot.Collections.Dictionary();
        return new(){["generation"]=(long)_snapshot.Generation,["ready"]=_snapshot.Ready,["status"]=_snapshot.Status,
            ["editVersion"]=(long)_snapshot.EditVersion,["settings"]=settings,["receiverDemand"]=demand,["requiredRanges"]=ranges,
            ["altitudeRange"]=new Vector2(_altitudeMin,_altitudeMax),["globalCpuBuildPending"]=_globalTask!=null&&!_globalTask.IsCompleted,
            ["regionalCpuBuildPending"]=_regionalTask!=null&&!_regionalTask.IsCompleted,["gpuGenerationPending"]=_pending!=null,["gpu"]=gpu,
            ["coverageObserved"]=false,["representation"]="Sampled global/regional heightfields; native overhangs are not represented; unknown traversal is neutral."};
    }
    public Godot.Collections.Dictionary CaptureShaderBinding()
    {
        var geometry=_grid!=null&&GodotObject.IsInstanceValid(_grid)?_grid.CaptureLodGeometrySnapshot():_snapshot.Geometry;
        Basis inverse=geometry.GridGlobalTransform.Basis.Inverse();
        Vector3 origin=geometry.GridGlobalTransform.Origin-geometry.FloatingOriginWorldOffset;
        Vector3 anchor=Vector3.Zero;
        if(geometry.FloatingOriginCanonicalAnchor is {} canonical){var absolute=geometry.CanonicalToAbsoluteLocal(canonical.AroundMeters,canonical.VerticalMeters,canonical.AcrossMeters);anchor=new((float)absolute.X,(float)absolute.Y,(float)absolute.Z);}
        Vector3 shift=inverse*(-origin)+anchor-geometry.RingCenterLocalOffset;
        var frame=new Godot.Collections.Array<Vector4>{new(inverse.Row0.X,inverse.Row0.Y,inverse.Row0.Z,shift.X),new(inverse.Row1.X,inverse.Row1.Y,inverse.Row1.Z,shift.Y),new(inverse.Row2.X,inverse.Row2.Y,inverse.Row2.Z,shift.Z),new((float)geometry.RingRadiusMeters,(float)geometry.RingVerticalBaseOffsetMeters,(float)geometry.RingAcrossBaseOffsetMeters,(float)geometry.CircumferenceMeters)};
        return new(){["global_texture"]=TextureVariant(_snapshot.GlobalTexture??_neutral),["regional_texture"]=TextureVariant(_snapshot.RegionalTexture??_neutral),["profile"]=_snapshot.Profile,["canonical_frame"]=frame,["generation"]=(long)_snapshot.Generation,["ready"]=_snapshot.Ready};
    }
    public void BindToResource(Resource resource)
    {
        if(!GodotObject.IsInstanceValid(resource))return;
        ulong id=resource.GetInstanceId();
        var geometry=_grid!=null&&GodotObject.IsInstanceValid(_grid)?_grid.CaptureLodGeometrySnapshot():_snapshot.Geometry;
        if(_resources.TryGetValue(id,out var binding)&&binding.Revision==_bindingRevision&&binding.Geometry==geometry)return;
        _resources[id]=(new(resource),_bindingRevision,_lightingRevision,geometry);
        var values=CaptureShaderBinding();
        resource.Set("terrain_cloud_sun_global_texture",values["global_texture"]);
        resource.Set("terrain_cloud_sun_regional_texture",values["regional_texture"]);
        resource.Set("terrain_cloud_sun_profile",values["profile"]);
        resource.Set("terrain_cloud_sun_canonical_frame",values["canonical_frame"]);
        if(binding.LightingRevision!=_lightingRevision&&resource.HasMethod("request_history_reset"))resource.Call("request_history_reset");
    }
    public void BindToMaterial(ShaderMaterial material)
    {
        if(!GodotObject.IsInstanceValid(material))return;
        ulong id=material.GetInstanceId();
        if(_materials.TryGetValue(id,out var binding)&&binding.Revision==_bindingRevision)return;
        _materials[id]=(new(material),_bindingRevision);
        material.SetShaderParameter("terrain_cloud_sun_global_texture",TextureVariant(_snapshot.GlobalTexture??_neutral));
        material.SetShaderParameter("terrain_cloud_sun_regional_texture",TextureVariant(_snapshot.RegionalTexture??_neutral));
        material.SetShaderParameter("terrain_cloud_sun_profile",_snapshot.Profile);
    }
}
