using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Rendered ABI/regression fixture for dithered tree LOD output and mirrors.</summary>
public partial class TreeLodComputeRegression : Node
{
    private RingGpuVegetationCompute? _gpu;
    private ArrayMesh? _one, _two;
    public string Result { get; private set; } = "RUNNING";
    public async void Start() { try { await Run(); Result = "PASS tree LOD dither, protected ranges, and output mirror"; } catch (Exception e) { Result = "FAIL " + e.Message; } }
    private async Task Run()
    {
        _one = Mesh(1); _two = Mesh(2);
        foreach (bool optimized in new[] { true, false }) await RunMode(optimized);
        await RunFarQuadPartition();
    }

    // TR-6 steep-view rule, on the real compute shader. A card (role 1) / quad (role 2) / LOD2 trio shares one root. For every distance across the
    // LOD2-to-card hand-off (290-360 m) and the card-to-quad hand-off (890-1,060 m) and every steepness, the three dither intervals must cover [0,1) exactly
    // once. The negative control offsets the quad's steep fade end by 10 m and the same check must then fail.
    private const float SinLow = 0.34202014f, SinHigh = 0.5f; // sin 20, sin 30
    private async Task RunFarQuadPartition()
    {
        _gpu = new RingGpuVegetationCompute { OptimizeTreeDispatches = true };
        int Reg(string name) => _gpu!.RegisterBatch(new(name, new MultiMesh(), _one!, FarRow(), new[] { new Vector4(.05f, 0, 0, 0) }, 1, Bounds(), false, true, true));
        int lod2 = Reg("far_lod2"), card = Reg("far_card"), quad = Reg("far_quad");
        Check(lod2 >= 0 && card >= 0 && quad >= 0, "register far batches");
        for (int i = 0; i < 180 && !_gpu.Ready; i++) await Frame(); Check(_gpu.Ready, "ready far");
        // Steepness 0, .5, 1 of smoothstep(sin 20, sin 30, sin e): e = 0 deg, the elevation whose sin is the mid point, 60 deg.
        float[] elevations = { 0f, MathF.Asin((SinLow + SinHigh) * .5f), Mathf.DegToRad(60f) };
        var distances = new System.Collections.Generic.List<float>();
        for (float d = 290; d <= 360; d += 5) distances.Add(d);
        for (float d = 890; d <= 1060; d += 10) distances.Add(d);
        int worst = 0; int samples = 0; float widestError = 0;
        foreach (float d in distances) foreach (float e in elevations)
        {
            foreach (bool offset in new[] { false, true })
            {
                var camera = new Vector3(d * MathF.Cos(e), d * MathF.Sin(e), 0);
                // Slot 2: LOD2 (25..350 m) fades out 300..350. Slot 3: cards 300..1,050 m, in 300..350, out 900..1,050. Slot 4: quad 300..5,500 m, in 900..1,050.
                var a = await FarInterval(lod2, 2, camera, 25, 350, 25, 40, 300, 350, 0, 0, 1);
                var b = await FarInterval(card, 3, camera, 300, 1050, 300, 350, 900, 1050, 1, 350, 1);
                var c = await FarInterval(quad, 4, camera, 300, 5500, 900, 1050, 4950, 5500, 2, offset ? 360 : 350, .35f);
                float total = 0; var all = new[] { a, b, c };
                for (int i = 0; i < 3; i++) total += all[i].Length;
                float union = Union(all);
                float error = MathF.Max(MathF.Abs(total - 1), MathF.Abs(union - 1));
                if (!offset) { Check(error < 1e-4f, $"partition d={d} e={Mathf.RadToDeg(e):F0}: total {total:F5} union {union:F5} (lod2 {a.Lo:F4}-{a.Hi:F4}, card {b.Lo:F4}-{b.Hi:F4}, quad {c.Lo:F4}-{c.Hi:F4})"); widestError = MathF.Max(widestError, error); samples++; }
                else if (error > 1e-3f) worst++;
            }
        }
        Check(worst > 0, "negative control: a 10 m offset of the quad's steep fade end must break the partition");
        GD.Print($"TR6 far quad partition: {samples} samples, widest error {widestError:E2}, negative control broke {worst} samples");
        _gpu.Dispose(); _gpu = null;
    }
    private readonly record struct Interval(float Lo, float Hi) { public float Length => MathF.Max(0, Hi - Lo); }
    private static float Union(Interval[] all)
    {
        var sorted = new System.Collections.Generic.List<Interval>(all); sorted.RemoveAll(i => i.Length <= 0); sorted.Sort((x, y) => x.Lo.CompareTo(y.Lo));
        float covered = 0, end = 0;
        foreach (Interval i in sorted) { float lo = MathF.Max(i.Lo, end); if (i.Hi > lo) covered += i.Hi - lo; end = MathF.Max(end, i.Hi); }
        return covered;
    }
    private async Task<Interval> FarInterval(int id, int lod, Vector3 camera, float near, float far, float inS, float inE, float outS, float outE, int role, float fadeEnd, float rankGate)
    {
        long target = _gpu!.CompletedUpdates + 1;
        var f = new RingGpuVegetationCompute.BatchFrameParameters(id, Transform3D.Identity, false, lod, near, far, 1, 1, 1, true, .35f, false, null, 0, null, 0, false,
            lod == 2 ? -1 : inS, lod == 2 ? -1 : inE, outS, outE, null, role, SinLow, SinHigh, fadeEnd, rankGate);
        Check(_gpu.RequestFrame(new(camera, new[] { 10u, 10u, 10u, 10u, 10u }, new[] { f }, false)), "submit far");
        for (int i = 0; i < 180 && _gpu.CompletedUpdates < target; i++) await Frame(); Check(_gpu.CompletedUpdates >= target, "complete far");
        var r = await Read(id);
        if (r.Commands[1] == 0) return new Interval(0, 0);
        float coverage = r.Rows[13], marker = r.Rows[15];
        Check(MathF.Abs(marker - 2) <= .25f, "marker");
        float lo = 0, hi = 1;
        if (coverage >= 0 && coverage < 1) hi = coverage; else if (coverage > -1 && coverage < 0) lo = 1 + coverage;
        lo = MathF.Max(lo, Mathf.Clamp((marker - 2f) * 5f, 0, 1)); // the shader's decode (imported_tree_impostor.gdshader)
        return new Interval(lo, hi);
    }
    private static float[] FarRow() { var r = new float[16]; r[0] = r[5] = r[10] = 1; r[12] = 3; return r; }
    private async Task RunMode(bool optimized)
    {
        _gpu = new RingGpuVegetationCompute { OptimizeTreeDispatches = optimized };
        int plain = _gpu.RegisterBatch(new("plain", new MultiMesh(), _one!, Rows(3, 12), Meta(), 3, Bounds(), false, false, true));
        int custom = _gpu.RegisterBatch(new("custom", new MultiMesh(), _one!, Rows(1, 16), new[] { new Vector4(.25f, 0, 0, 0) }, 1, Bounds(), false, true, true));
        Check(plain >= 0 && custom >= 0, "register");
        for (int i=0;i<180&&!_gpu.Ready;i++) await Frame(); Check(_gpu.Ready,"ready");
        await Submit(plain, 2, 10, 50, 10, 20, -1, -1, true, 10); // protected root x=15 accepts LOD2
        var first=await Read(plain); Check(first.Commands[1]==2,"protected LOD2 and ordinary roots");
        Check(first.Rows.Length >= 48 && first.Rows[15] == 2,"12 to 16 marker");
        // LOD0 outgoing 10..20 and LOD2 incoming 10..20: same root gets complementary signed coverage.
        await Submit(plain, 0, 0, 20, -1, -1, 10, 20, true, 10); var outgoing=await Read(plain);
        await Submit(plain, 2, 10, 50, 10, 20, -1, -1, true, 10); var incoming=await Read(plain);
        float a=Coverage(outgoing.Rows, 15), b=Coverage(incoming.Rows,15);
        Check(a < 0 && b > 0 && MathF.Abs(MathF.Abs(a) + b - 1) < .02f,"complementary signed overlap");
        await Submit(custom, 2, 0, 50, 0, 10, -1, -1, false, 10); var c=await Read(custom);
        Check(c.Commands[1]==1 && MathF.Abs(c.Rows[12]-77)<.001f && c.Rows[15]==2,"16 custom variant preservation");
        await Submit(custom, 2, 0, 50, 5, 10, -1, -1, false, 10); Check(MathF.Abs((await Read(custom)).Rows[13]) < .001f,"fade-in exact start");
        await Submit(custom, 2, 0, 50, -1, -1, 5, 10, false, 10); Check(MathF.Abs((await Read(custom)).Rows[13] + 1) < .001f,"fade-out exact start");
        await Submit(plain, 2, 50, 60, -1, -1, -1, -1, true, 10); Check((await Read(plain)).Commands[1]==0,"outside range rejected/reset");
        await Submit(plain, 0, 50, 60, -1, -1, -1, -1, false, 10); Check((await Read(plain)).Commands[1]==1,"legacy protected LOD0 outside range");
        await Submit(plain, 2, 0, 50, -1, -1, -1, -1, true, 10, new Transform3D(Basis.Identity, new Vector3(100, 0, 0))); Check((await Read(plain)).Commands[1]==0,"world transform distance");
        await Submit(plain, 2, 0, 50, -1, -1, -1, -1, true, 0); Check((await Read(plain)).Commands[1]==0,"zero cap");
        var mirror = new MultiMesh(); Check(_gpu.AttachOutputMirror(plain, mirror, _two!),"attach mirror");
        await Submit(plain, 2, 0, 50, -1, -1, -1, -1, true, 10);
        for(int i=0;i<120&&!_gpu.IsOutputMirrorReady(plain);i++) await Frame(); Check(_gpu.IsOutputMirrorReady(plain),"mirror ready");
        var parent=await Read(plain); uint[] mirrorCommands=await ReadMirror(mirror);
        Check(mirrorCommands.Length==10 && mirrorCommands[1]==parent.Commands[1] && mirrorCommands[6]==parent.Commands[1],"mirror counts different surfaces");
        Check(mirrorCommands[0]==3 && mirrorCommands[2]==0 && mirrorCommands[5]==3 && mirrorCommands[7]==0,"mirror retains target geometry commands");
        await Submit(plain,2,50,60,-1,-1,-1,-1,true,10); mirrorCommands=await ReadMirror(mirror); Check(mirrorCommands[1]==0&&mirrorCommands[6]==0,"mirror zero reset");
        Check(_gpu.DetachOutputMirror(plain),"detach");
        var replacement = new MultiMesh(); Check(_gpu.AttachOutputMirror(plain, replacement, _one!),"reattach after detach");
        await Submit(plain, 2, 0, 50, -1, -1, -1, -1, true, 10);
        for(int i=0;i<120&&!_gpu.IsOutputMirrorReady(plain);i++) await Frame(); Check(_gpu.IsOutputMirrorReady(plain),"replacement mirror ready");
        Check(_gpu.DetachOutputMirror(plain),"replacement detach"); _gpu.Dispose(); _gpu = null;
    }
    private async Task Submit(int id,int lod,float near,float far,float ins,float ine,float outs,float oute,bool protect,uint cap,Transform3D? transform=null)
    { long target=_gpu!.CompletedUpdates+1; var f=new RingGpuVegetationCompute.BatchFrameParameters(id,transform??Transform3D.Identity,false,lod,near,far,1,1,1,true,1,false,null,0,null,0,protect,ins,ine,outs,oute); Check(_gpu.RequestFrame(new(Vector3.Zero,new[]{cap,cap,cap,cap,cap},new[]{f},false)),"submit"); for(int i=0;i<180&&_gpu.CompletedUpdates<target;i++)await Frame(); Check(_gpu.CompletedUpdates>=target,"complete"); }
    private static float Coverage(float[] r,int markerOffset) { for(int i=0;i+15<r.Length;i+=16) if(r[i+15]==2 && MathF.Abs(r[i+3]-15)<.01f) return r[i+13]; throw new Exception("dither row"); }
    private static async Task<(uint[] Commands,float[] Rows)> Read(RingGpuVegetationCompute g,int id){var t=new TaskCompletionSource<(uint[],float[])>();g.RequestReadbackForTests(id,(x,y)=>t.TrySetResult((x,y)));return await t.Task;}
    private Task<(uint[] Commands,float[] Rows)> Read(int id) => Read(_gpu!, id);
    private static Task<uint[]> ReadMirror(MultiMesh m){var t=new TaskCompletionSource<uint[]>();RenderingServer.CallOnRenderThread(Callable.From(()=>{var rd=RenderingServer.GetRenderingDevice()!;byte[] b=rd.BufferGetData(RenderingServer.MultimeshGetCommandBufferRdRid(m.GetRid()));var u=new uint[b.Length/4];Buffer.BlockCopy(b,0,u,0,b.Length);Callable.From(()=>t.TrySetResult(u)).CallDeferred();}));return t.Task;}
    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    public override void _ExitTree(){_gpu?.Dispose();}
    private static void Check(bool ok,string s){if(!ok)throw new Exception(s);} private static Aabb Bounds()=>new(new(-2,-2,-2),new(4,4,4));
    private static Vector4[] Meta()=>new[]{new Vector4(.2f,0,0,0),new Vector4(.3f,0,0,0),new Vector4(.4f,1,0,0)};
    private static float[] Rows(int n,int stride){var r=new float[n*stride];for(int i=0;i<n;i++){int a=i*stride;r[a]=r[a+5]=r[a+10]=1;r[a+3]=i==2?15:5+i*20;if(stride==16)r[a+12]=77;}return r;}
    private static ArrayMesh Mesh(int surfaces){var m=new ArrayMesh();for(int s=0;s<surfaces;s++){var a=new Godot.Collections.Array();a.Resize((int)Godot.Mesh.ArrayType.Max);a[(int)Godot.Mesh.ArrayType.Vertex]=new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(0,1,0)};a[(int)Godot.Mesh.ArrayType.Index]=new[]{0,1,2};m.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles,a);}return m;}
}
