using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>A real main-RD compaction fixture. It verifies root ordering, two batches
/// sharing a budget, rebase invariance, metadata updates, and repeated retirement.</summary>
public partial class GpuVegetationDriverTest : Node
{
    RingGpuVegetationCompute? gpu; int a,b; Mesh? mesh; MultiMesh? ma,mb;
    public string Result { get; private set; }="RUNNING";
    public async void Start(){try{await Run();Result="PASS exact prefix, shared source, sliced grass fairness/idle, global cap, metadata, rebase, teardown";}catch(Exception e){Result="FAIL "+e.Message;}}
    async Task Run(){
        for(int cycle=0;cycle<2;cycle++){
            gpu=new RingGpuVegetationCompute(); mesh=MakeMesh(); ma=new MultiMesh();mb=new MultiMesh();
            // Grass near/intermediate registrations use this same immutable array.
            // Prove that two indirect outputs retain one resident source upload.
            float[] shared = Candidates(3);
            int sharedA = gpu.RegisterBatch(new("shared-a", new MultiMesh(), mesh, shared, Metadata(), 130, Bounds(), false));
            int sharedB = gpu.RegisterBatch(new("shared-b", new MultiMesh(), mesh, shared, Metadata(), 65, Bounds(), false));
            Check(sharedA >= 0 && sharedB >= 0, "shared register");
            for(int i=0;!gpu.Ready&&i<180;i++)await Frame(); Check(gpu.Ready,"ready");
            for(int i=0;i<4;i++)await Frame();
            Check(gpu.SharedSourceCount == 1 && gpu.SharedSourceBytes == shared.LongLength * 4L, "shared source upload");
            Check(gpu.OutputBytes > 0 && gpu.EstimatedBytes >= gpu.SharedSourceBytes + gpu.OutputBytes, "shared source diagnostics");
            Check(gpu.RemoveBatch(sharedA) && gpu.RemoveBatch(sharedB), "shared remove");
            for(int i=0;i<4;i++)await Frame();
            Check(gpu.SharedSourceCount == 0 && gpu.SharedSourceBytes == 0, "shared source retire");
            await VerifyGrassSlicing(mesh);
            a=gpu.RegisterBatch(new("test-a",ma,mesh,Candidates(0),Metadata(),130,Bounds(),false));
            b=gpu.RegisterBatch(new("test-b",mb,mesh,Candidates(1),Metadata(),130,Bounds(),false)); Check(a>=0&&b>=0,"register");
            // 43 normal rank-.25 roots plus protected index 129 = 44 per batch.
            await Submit(Vector3.Zero,300,true); var ra=await Read(a);var rb=await Read(b); Exact(ra,44);Exact(rb,44); Prefix(ra.Rows,0,44);Prefix(rb.Rows,1,44);
            long dispatched=gpu.DispatchCount, skipped=gpu.SkippedUnchangedUpdates;
            await Submit(Vector3.Zero,300,true); Check(gpu.DispatchCount==dispatched&&gpu.SkippedUnchangedUpdates==skipped+1,"unchanged skip");
            await Submit(Vector3.Zero,300,true,null,1); Exact(await Read(a),43);Exact(await Read(b),43);
            Check(gpu.UpdateMetadata(a,EmptyMetadata())&&gpu.UpdateMetadata(b,EmptyMetadata()),"clear protected"); await Submit(Vector3.Zero,300,true,null,0,false); dispatched=gpu.DispatchCount; await Submit(Vector3.Zero,300,true,null,0,false); Check(gpu.DispatchCount-dispatched==6,"nonprotected uses ordinary sweep only"); Exact(await Read(a),43);Exact(await Read(b),43);
            Check(gpu.UpdateMetadata(a,Metadata())&&gpu.UpdateMetadata(b,Metadata()),"restore protected dispatch"); await Submit(Vector3.Zero,300,true,null,0,false); dispatched=gpu.DispatchCount; await Submit(Vector3.Zero,300,true,null,0,false); Check(gpu.DispatchCount-dispatched==12,"protected uses both sweeps");
            await Submit(Vector3.Zero,17,true,null,0,false,true); Exact(await Read(b),16);Exact(await Read(a),1);
            // Protected sweep consumes one candidate from each batch first; the normal
            // sweep then gives the first batch the remaining fifteen positions.
            await Submit(Vector3.Zero,17,true);ra=await Read(a);rb=await Read(b);Exact(ra,16);Exact(rb,1); Prefix(ra.Rows,0,16);Prefix(rb.Rows,1,1);
            await Submit(new Vector3(10000,0,0),300,true);ra=await Read(a);rb=await Read(b);Exact(ra,1);Exact(rb,1);
            Check(gpu.UpdateMetadata(a,EmptyMetadata())&&gpu.UpdateMetadata(b,EmptyMetadata()),"metadata update"); await Submit(new Vector3(10000,0,0),300,true);Exact(await Read(a),0);Exact(await Read(b),0);
            Check(gpu.UpdateMetadata(a,Metadata())&&gpu.UpdateMetadata(b,Metadata()),"metadata restore"); await Submit(Vector3.Zero,300,true);ra=await Read(a);rb=await Read(b);Exact(ra,44);Exact(rb,44);
            await Submit(new Vector3(100,0,0),300,true,new Vector3(100,0,0));var shifted=await Read(a);Exact(shifted,44);Prefix(shifted.Rows,0,44);
            var replacement=new MultiMesh(); Check(gpu.UpdateBatch(a,new("test-a-replaced",replacement,mesh,Candidates(0),Metadata(),130,Bounds(),false)),"replace source"); long replaced=gpu.CompletedUpdates; await Submit(new Vector3(100,0,0),300,true,new Vector3(100,0,0)); Check(gpu.CompletedUpdates==replaced+1&&gpu.IsBatchReady(a),"replacement invalidates unchanged cache"); Exact(await Read(a),44);
            int c=gpu.RegisterBatch(new("test-c",new MultiMesh(),mesh,Candidates(2),Metadata(),130,Bounds(),false)); Check(c>=0,"source addition"); for(int i=0;i<4;i++)await Frame(); var t=Transform3D.Identity; var pa=new RingGpuVegetationCompute.BatchFrameParameters(a,t,false,0,0,20,2,1,1,true,.5f,true,null,0);var pb=new RingGpuVegetationCompute.BatchFrameParameters(b,t,false,0,0,20,2,1,1,true,.5f,true,null,0);var pc=new RingGpuVegetationCompute.BatchFrameParameters(c,t,false,0,0,20,2,1,1,true,.5f,true,null,0);Check(gpu.RequestFrame(new RingGpuVegetationCompute.FrameRequest(Vector3.Zero,new uint[]{17,300,300,300,300},new[]{pa,pb,pc},false)),"added budget");for(int i=0;i<4;i++)await Frame();Exact(await Read(a),15);Exact(await Read(b),1);Exact(await Read(c),1);Check(gpu.RemoveBatch(b),"remove middle source");Check(gpu.RequestFrame(new RingGpuVegetationCompute.FrameRequest(Vector3.Zero,new uint[]{17,300,300,300,300},new[]{pa,pc},false)),"removed budget");for(int i=0;i<4;i++)await Frame();Exact(await Read(a),16);Exact(await Read(c),1);
            Check(gpu.RemoveBatch(a)&&gpu.RemoveBatch(c),"remove");for(int i=0;i<4;i++)await Frame();Check(gpu.BatchCount==0&&gpu.EstimatedBytes==0,"retire");gpu.Dispose();for(int i=0;i<3;i++)await Frame();
        }
    }
    async Task Submit(Vector3 camera,uint budget,bool protectedMode,Vector3? origin=null,int lodPass=0,bool skip=true,bool reverse=false){var t=Transform3D.Identity;t.Origin=origin??Vector3.Zero;var p0=new RingGpuVegetationCompute.BatchFrameParameters(a,t,false,lodPass,0,20,2,1,1,true,.5f,protectedMode,null,0);var p1=new RingGpuVegetationCompute.BatchFrameParameters(b,t,false,lodPass,0,20,2,1,1,true,.5f,protectedMode,null,0);Check(gpu!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera,new[]{budget,300u,300u,300u,300u},reverse?new[]{p1,p0}:new[]{p0,p1},skip)),"request");for(int i=0;i<4;i++)await Frame();}
    async Task VerifyGrassSlicing(Mesh grassMesh){
        int ga=gpu!.RegisterBatch(new("grass-a",new MultiMesh(),grassMesh,GrassCandidates(0),null,3,Bounds(),true));
        int gb=gpu.RegisterBatch(new("grass-b",new MultiMesh(),grassMesh,GrassCandidates(1),null,3,Bounds(),true));
        int gc=gpu.RegisterBatch(new("grass-c",new MultiMesh(),grassMesh,GrassCandidates(2),null,3,Bounds(),true));
        Check(ga>=0&&gb>=0&&gc>=0,"grass sliced register");for(int i=0;i<4;i++)await Frame();
        var t=Transform3D.Identity;
        var pa=new RingGpuVegetationCompute.BatchFrameParameters(ga,t,true,0,0,1000,10,1,1,false,1,false,null,8);
        var pb=new RingGpuVegetationCompute.BatchFrameParameters(gb,t,true,0,0,1000,10,1,1,false,1,false,null,8);
        var pc=new RingGpuVegetationCompute.BatchFrameParameters(gc,t,true,0,0,1000,10,1,1,false,1,false,null,8);
        var request=new RingGpuVegetationCompute.FrameRequest(Vector3.Zero,Array.Empty<uint>(),new[]{pa,pb,pc},true,1);
        for(int step=1;step<=3;step++){
            Check(gpu.RequestFrame(request),"grass sliced request");for(int i=0;i<4;i++)await Frame();
            int ready=(gpu.IsBatchReady(ga)?1:0)+(gpu.IsBatchReady(gb)?1:0)+(gpu.IsBatchReady(gc)?1:0);
            Check(ready==step&&gpu.LastFrameRequestedBatchCount==3&&gpu.LastFrameDispatchedBatchCount==1&&gpu.DeferredBatchCount==2,"grass sliced fairness");
        }
        long dispatched=gpu.DispatchCount, skipped=gpu.SkippedUnchangedUpdates;
        Check(gpu.RequestFrame(request),"grass stationary request");for(int i=0;i<4;i++)await Frame();
        Check(gpu.DispatchCount==dispatched&&gpu.SkippedUnchangedUpdates==skipped+1,"grass stationary catch-up skip");
        Check(gpu.RemoveBatch(ga)&&gpu.RemoveBatch(gb)&&gpu.RemoveBatch(gc),"grass sliced remove");for(int i=0;i<4;i++)await Frame();
    }
    async Task<(uint[] Commands,float[] Rows)> Read(int id){var t=new TaskCompletionSource<(uint[],float[])>();gpu!.RequestReadbackForTests(id,(c,r)=>t.TrySetResult((c,r)));return await t.Task;}
    async Task Frame(){await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
    static void Exact((uint[] Commands,float[] Rows) r,uint n){Check(r.Commands.Length>=10&&r.Commands[0]==3&&r.Commands[5]==3&&r.Commands[1]==n&&r.Commands[6]==n,"indirect exact count");}
    static void Prefix(float[] rows,int tile,int count){Check(rows.Length>=count*12,"rows");float protectedX=15f+tile*.01f;Check(MathF.Abs(rows[3]-protectedX)<.02f,"protected first");for(int k=1;k<count;k++){int source=(k-1)*3;float x=-15f+source*30f/129f+tile*.01f;Check(MathF.Abs(rows[k*12+3]-x)<.02f,"compact prefix "+k);}}
    public override void _ExitTree() => gpu?.Dispose();
    static void Check(bool ok,string what){if(!ok)throw new InvalidOperationException(what);}
    static Aabb Bounds()=>new(new Vector3(-20,-4,-4),new Vector3(40,8,8));
    static Vector4[] Metadata(){var r=new Vector4[130];for(int i=0;i<130;i++)r[i]=new Vector4(i==129?.75f:(i%3==0?.25f:.75f),i==129?1:0,0,0);return r;} static Vector4[] EmptyMetadata(){var r=Metadata();r[129].Y=0;return r;}
    static float[] Candidates(int tile){var r=new float[130*12];for(int i=0;i<130;i++){int q=i*12;r[q]=r[q+5]=r[q+10]=1;r[q+3]=-15+i*30f/129f+tile*.01f;}return r;}
    static float[] GrassCandidates(int tile){var r=new float[3*RingGpuVegetationCompute.GrassFloats];for(int i=0;i<3;i++){int q=i*RingGpuVegetationCompute.GrassFloats;r[q]=r[q+5]=r[q+10]=1;r[q+3]=tile*2+i*.25f;r[q+15]=1;r[q+16]=r[q+17]=.5f;}return r;}
    static ArrayMesh MakeMesh(){var m=new ArrayMesh();var v=new[]{new Vector3(-.1f,0,0),new Vector3(.1f,0,0),new Vector3(0,.2f,0)};var i=new[]{0,1,2};for(int q=0;q<2;q++){var x=new Godot.Collections.Array();x.Resize((int)Mesh.ArrayType.Max);x[(int)Mesh.ArrayType.Vertex]=v;x[(int)Mesh.ArrayType.Index]=i;m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,x);}return m;}
}
