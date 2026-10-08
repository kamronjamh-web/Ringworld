using System;
using System.Reflection;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Tools;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

public partial class TerrainCloudSunVisibilityStateProbe : RefCounted
{
    private Node? _world;
    private RingSunshineCloudBridge? _bridge;
    private DirectionalLight3D? _sun;
    private Basis _originalSun;
    private ShaderMaterial? _standaloneReceiver;
    private ulong _standaloneGeneration;
    public Godot.Collections.Dictionary Prepare(Node world,string directory)
    {
        try {
            _world=world;_bridge=world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge");
            var isolation=new MinimapWindContractTest().ExecuteFixture("isolation",world,directory);
            if(!isolation["pass"].AsBool())return isolation;
            typeof(RingSunshineCloudBridge).GetField("<GameplayCloudDefaults>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic)!
                .SetValue(_bridge,new GameplayCloudDefaults(directory+"/clouds.tres"));
            _bridge.Enabled=true;_bridge.CloudRenderingEnabled=true;
            _bridge.TerrainCloudShadowEnabled=true;_bridge.TerrainCloudShadowGlobalAroundResolution=256;
            _bridge.TerrainCloudShadowGlobalAcrossResolution=8;_bridge.TerrainCloudShadowAltitudeSlices=2;
            _bridge.TerrainCloudShadowRegionalResolution=32;_bridge.TerrainCloudShadowRegionalExtentMeters=8000;
            _bridge.TerrainCloudShadowTraceVisitBudget=32;_bridge.TerrainCloudShadowTexelsPerFrame=4096;
            _bridge.TerrainCloudShadowRefreshSeconds=10;_bridge.TerrainCloudShadowSunValidityDegrees=2;
            return new(){["pass"]=true};
        }catch(Exception error){return new(){["pass"]=false,["detail"]=error.ToString()};}
    }
    public void FreezePoseAndSun()
    {
        if(_world==null)return;
        foreach(Node node in _world.FindChildren("*","",true,false)){
            string path=(node.GetScript().AsGodotObject() as Script)?.ResourcePath??"";
            if(node is RingDayNightCycle||path.EndsWith("day_side_atmosphere.gd")||path.EndsWith("floating_origin.gd"))node.SetProcess(false);
        }
        _world.GetNode<RingVoxelExplorerController>("Explorer").SetPhysicsProcess(false);
        _sun=_world.GetNode<DirectionalLight3D>("StarLight");_originalSun=_sun.GlobalBasis;
    }
    public Godot.Collections.Dictionary State()
    {
        try {
            var service=RingTerrainCloudSunVisibility.Instance;
            if(service==null){
                if(_standaloneReceiver!=null){
                    var neutral=_standaloneReceiver.GetShaderParameter("terrain_cloud_sun_profile").AsGodotArray();
                    if(neutral.Count!=5||neutral[3].AsVector4().Y!=0)throw new Exception("standalone receiver retained readiness after producer release");
                }
                return new(){["pass"]=true,["ready"]=false,["generation"]=0};
            }
            var snapshot=service.CurrentSnapshot;
            if(snapshot.Ready&&_standaloneReceiver==null){
                _standaloneReceiver=new ShaderMaterial{Shader=new Shader{Code="shader_type spatial; uniform sampler2D terrain_cloud_sun_global_texture; uniform sampler2D terrain_cloud_sun_regional_texture; uniform vec4 terrain_cloud_sun_profile[5]; void fragment(){ ALBEDO=vec3(terrain_cloud_sun_profile[3].y); }"}};
                service.BindToMaterial(_standaloneReceiver); // Deliberately never rebind from this owner.
                _standaloneGeneration=snapshot.Generation;
            }
            if(_standaloneReceiver!=null&&snapshot.Ready&&snapshot.Generation>_standaloneGeneration){
                var standalone=_standaloneReceiver.GetShaderParameter("terrain_cloud_sun_global_texture").AsGodotObject() as Texture2Drd;
                if(snapshot.GlobalTexture is not Texture2Drd expected||standalone==null||standalone.TextureRdRid!=expected.TextureRdRid)
                    throw new Exception("paused standalone receiver did not receive newly published atlas RID");
            }
            var clouds=typeof(RingSunshineCloudBridge).GetField("_clouds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_bridge) as GodotObject;
            bool resourceReady=false,matches=false;
            if(clouds!=null){
                var profile=clouds.Get("terrain_cloud_sun_profile").AsGodotArray();
                resourceReady=profile.Count==5&&profile[3].AsVector4().Y>.5f;
                var texture=clouds.Get("terrain_cloud_sun_global_texture").AsGodotObject() as Texture2Drd;
                matches=snapshot.GlobalTexture is Texture2Drd published&&texture!=null&&published.TextureRdRid==texture.TextureRdRid;
            }
            return new(){["pass"]=true,["ready"]=snapshot.Ready,["resourceReady"]=resourceReady,["matches"]=matches,
                ["temporal"]=clouds!=null?clouds.Call("get_temporal_history_diagnostics").AsGodotDictionary():new Godot.Collections.Dictionary(),
                ["generation"]=(long)snapshot.Generation,["status"]=snapshot.Status,["diagnostics"]=service.GetDiagnostics()};
        }catch(Exception error){return new(){["pass"]=false,["detail"]=error.ToString()};}
    }
    public Godot.Collections.Dictionary CheckFrame(bool rebase)
    {
        try {
            var grid=_world!.GetNode<VoxelChunkGridManager>("ChunkGridManager");
            var explorer=_world.GetNode<RingVoxelExplorerController>("Explorer");
            if(rebase){grid.EnableCanonicalFloatingOriginAnchor=true;var position=explorer.CanonicalPosition;
                grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(position.AroundMeters+2048,position.VerticalMeters,position.AcrossMeters));}
            var service=RingTerrainCloudSunVisibility.Instance!;var binding=service.CaptureShaderBinding();
            var frame=binding["canonical_frame"].AsGodotArray();var geometry=grid.CaptureLodGeometrySnapshot();
            var a=frame[0].AsVector4();var b=frame[1].AsVector4();var c=frame[2].AsVector4();
            double maximum=0;
            foreach(double around in new[]{250.0,geometry.CircumferenceMeters-250.0,explorer.CanonicalPosition.AroundMeters}){
                const double height=2500,across=5000;Vector3 world=geometry.CanonicalToWorld(around,height,across);
                var cart=new Vector3(a.X*world.X+a.Y*world.Y+a.Z*world.Z+a.W,b.X*world.X+b.Y*world.Y+b.Z*world.Z+b.W,c.X*world.X+c.Y*world.Y+c.Z*world.Z+c.W);
                double angle=Math.Atan2(cart.Z,cart.X);if(angle<0)angle+=Math.Tau;
                double observedAround=angle*geometry.RingRadiusMeters;
                double delta=Math.Abs(observedAround-around)%geometry.CircumferenceMeters;delta=Math.Min(delta,geometry.CircumferenceMeters-delta);
                maximum=Math.Max(maximum,Math.Max(delta,Math.Max(Math.Abs(geometry.RingRadiusMeters-Math.Sqrt((double)cart.X*cart.X+(double)cart.Z*cart.Z)-geometry.RingVerticalBaseOffsetMeters-height),Math.Abs(cart.Y-geometry.RingAcrossBaseOffsetMeters-across))));
            }
            return new(){["pass"]=maximum<3.0,["maximumErrorMeters"]=maximum};
        }catch(Exception error){return new(){["pass"]=false,["detail"]=error.ToString()};}
    }
    public void FlipSun(){if(_sun!=null)_sun.GlobalBasis=_originalSun.Rotated(Vector3.Up,Mathf.Pi);}
    public void Disable(){_bridge!.TerrainCloudShadowEnabled=false;var p=RingTerrainCloudSunVisibility.Instance!;p.Configure(p.Settings with{Enabled=false});}
    public void Enable(){_bridge!.TerrainCloudShadowEnabled=true;var p=RingTerrainCloudSunVisibility.Instance!;p.Configure(p.Settings with{Enabled=true});}
    public void Release(){
        var service=RingTerrainCloudSunVisibility.Instance;
        if(service!=null){
            // Synchronous exit exercises invalidation of a receiver whose owner never refreshes it.
            service.GetParent().RemoveChild(service);
            var state=State();
            if(!state["pass"].AsBool())throw new Exception(state.ToString());
            service.QueueFree();
        }
    }
    public Godot.Collections.Dictionary SourceContract()
    {
        try {
            var snapshot=CloudSunTerrainSnapshotBuilder.Build(10000,2000,7,(a,z)=>(float)(a<500?100:0),32,8);
            if(snapshot.EditVersion!=7||snapshot.MaximumHeight!=100||snapshot.MinimumHeight!=0||snapshot.Levels.Count<2)throw new Exception("global sampled export incomplete");
            var copy=snapshot.Levels[0].Ranges.ToArray();copy[0]=999;
            if(snapshot.Levels[0].Ranges.Span[0]==999)throw new Exception("snapshot export aliased mutable copy");
            return new(){["pass"]=true};
        }catch(Exception error){return new(){["pass"]=false,["detail"]=error.ToString()};}
    }
}
