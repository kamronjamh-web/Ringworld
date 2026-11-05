using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>Height-shadow failed-init latch and typed global-array startup/retirement, including storm-map owner replacement, without generating a world.</summary>
public partial class HeightShadowFailureGpuTest : Node
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public string Result { get; private set; } = "RUNNING";

    public override async void _Ready()
    {
        Node3D? fixture = null;
        RingVoxelExplorerController? focus = null;
        RingTerrainHeightShadow? shadow = null;
        try
        {
            Require(RenderingServer.GetRenderingDevice() != null, "requires Vulkan/Forward+");
            fixture = new Node3D { Name = "HeightShadowFailureFixture", ProcessMode = ProcessModeEnum.Disabled };
            AddChild(fixture);
            var grid = new VoxelChunkGridManager {
                Name = "ChunkGridManager", BuildOnReady = false, DormantEditableBackend = true,
                EnableChunkStreaming = false
            };
            fixture.AddChild(grid);
            var sampler = EmitterSiteTestWorlds.CreateGen17Landform(20260419);
            Set(grid, "_terrainSampler", sampler);
            var sun = new DirectionalLight3D { Name = "StarLight" };
            fixture.AddChild(sun);
            focus = new RingVoxelExplorerController();
            shadow = new RingTerrainHeightShadow { Name = "TerrainHeightShadow" };
            fixture.AddChild(shadow);
            Set(shadow, "_focus", focus);
            shadow.Configure(new RingHeightShadowSettings { Enabled = true });

            // A global may still use its typed engine default, but must never bind an RIDless wrapper.
            Require(!(bool)Get(shadow, "_globalArraysRegistered")! || ArraysHaveRids(shadow), "startup published RIDless arrays");
            await NextFrame();
            await NextFrame();
            Invoke(shadow, "RegisterGlobalArrays");
            Require((bool)Get(shadow, "_globalArraysRegistered")!, "placeholder arrays never became publishable");
            await RequireTypedArrays(shadow);
            Rid placeholder = (Rid)Get(shadow, "_placeholderArray")!;

            // Inject exactly the completion state of a render-thread initialization failure. No production debug setting.
            Type linkType = typeof(RingTerrainHeightShadow).GetNestedType("GpuLink", BindingFlags.NonPublic)!;
            object failedLink = Activator.CreateInstance(linkType, true)!;
            linkType.GetField("Failed")!.SetValue(failedLink, true);
            linkType.GetField("Error")!.SetValue(failedLink, "injected initialization failure");
            Set(shadow, "_link", failedLink);
            Set(shadow, "_phase", HeightShadowPhase.Idle);
            Invoke(shadow, "Step", 0.016f);
            Require(shadow.Phase == HeightShadowPhase.Failed, "polling failure fell through into idle/reinitialization");
            string failure = shadow.Status;
            for (int i = 0; i < 32; i++) Invoke(shadow, "Step", 0.016f);
            Require(shadow.Phase == HeightShadowPhase.Failed && shadow.Status == failure, "initialization failure did not stay latched");
            Require(Get(shadow, "_link") == null && shadow.FillsCompleted == 0 && shadow.PublishCount == 0, "failed session restarted work");
            await NextFrame();
            await RequireTypedArrays(shadow);
            Require((Rid)Get(shadow, "_placeholderArray")! == placeholder, "failure replaced its placeholder unnecessarily");

            shadow.Configure(shadow.Settings with { Enabled = false });
            Invoke(shadow, "Step", 0.016f);
            Require(shadow.Phase == HeightShadowPhase.Disabled, "explicit disable did not clear failed latch");
            await RequireTypedArrays(shadow);
            shadow.UndergroundHold = true;
            shadow.Configure(shadow.Settings with { Enabled = true });
            Invoke(shadow, "Step", 0.016f);
            Require(shadow.Phase == HeightShadowPhase.Held, "explicit re-enable did not resume the scheduler");
            shadow.Configure(shadow.Settings with { Enabled = false });
            Invoke(shadow, "Step", 0.016f);
            await RequireTypedArrays(shadow);
            fixture.RemoveChild(shadow);
            Require(!(bool)Get(shadow, "_globalArraysRegistered")!, "exit left wrapper globals bound");
            await OnRenderThread(rd => Require(!rd.TextureIsValid(placeholder), "exit retained placeholder after unbinding globals"));
            shadow.Free();
            await VerifyStormMapRetirement();
            Result = "PASS";
            GD.Print("HEIGHT_SHADOW_FAILURE=PASS");
        }
        catch (Exception error)
        {
            Result = "FAIL " + error.Message;
            GD.PushError("HEIGHT_SHADOW_FAILURE=" + Result + "\n" + error.StackTrace);
        }
        finally
        {
            if (shadow != null && GodotObject.IsInstanceValid(shadow)) shadow.Free();
            focus?.Free();
            fixture?.Free();
        }
    }

    private async Task RequireTypedArrays(RingTerrainHeightShadow shadow)
    {
        var arrays = (Texture2DArrayRD?[])Get(shadow, "_arrays")!;
        await OnRenderThread(rd =>
        {
            foreach (Texture2DArrayRD? array in arrays)
            {
                Rid rid = array!.TextureRdRid;
                Require(rid.IsValid && rd.TextureIsValid(rid), "bound wrapper has no live texture");
                RDTextureFormat format = rd.TextureGetFormat(rid);
                Require(format.TextureType == RenderingDevice.TextureType.Type2DArray && format.ArrayLayers == 3, "global placeholder is not a three-layer texture array");
            }
        });
    }

    private async Task VerifyStormMapRetirement()
    {
        var first = new RingSunshineCloudBridge();
        var second = new RingSunshineCloudBridge();
        var unused = new RingSunshineCloudBridge();
        var pending = new RingSunshineCloudBridge();
        var interrupted = new RingSunshineCloudBridge();
        var receiver = new ColorRect {
            Size = new Vector2(32, 32),
            Material = new ShaderMaterial {
                Shader = new Shader { Code = "shader_type canvas_item; global uniform sampler2DArray ringworld_storm_map; void fragment() { COLOR = vec4(vec3(texture(ringworld_storm_map, vec3(0.5, 0.5, 0.0)).r), 1.0); }" }
            }
        };
        AddChild(receiver);
        try
        {
            Invoke(first, "EnsureStormMapGlobal");
            await NextFrame();
            await NextFrame();
            Invoke(first, "EnsureStormMapGlobal");
            Require(ReferenceEquals(StormOwner(), first), "first storm owner did not bind");

            Invoke(second, "EnsureStormMapGlobal");
            await NextFrame();
            await NextFrame();
            Invoke(second, "EnsureStormMapGlobal");
            Require(ReferenceEquals(StormOwner(), second), "replacement storm owner did not bind");
            Rid secondPlaceholder = (Rid)Get(second, "_stormMapPlaceholder")!;
            Require(await ReceiverRed() < 0.1f, "replacement storm placeholder was not sampled as black");

            Invoke(interrupted, "EnsureStormMapGlobal");
            await NextFrame();
            await NextFrame();
            Invoke(interrupted, "EnsureStormMapGlobal");
            Require(ReferenceEquals(StormOwner(), interrupted), "interrupted replacement did not bind");
            Invoke(interrupted, "ReleaseStormLight");
            Require(StormOwner() == null, "interrupted replacement did not release ownership");
            Invoke(second, "PublishStormLight", false);
            Require(ReferenceEquals(StormOwner(), second), "live previous owner did not reclaim vacant global");
            Require(await ReceiverRed() < 0.1f, "reclaimed global did not sample its typed placeholder");

            // Runtime provider deactivation remains reversible and must not retire the map resource.
            Invoke(second, "PublishStormLight", true);
            Require(!(bool)Get(second, "_stormLightReleased")! && ReferenceEquals(StormOwner(), second), "provider disable retired the storm map");
            Invoke(second, "EnsureStormMapGlobal");
            Invoke(first, "ReleaseStormLight");
            Invoke(first, "EnsureStormMapGlobal");
            Invoke(first, "PublishStormLight", false);
            Require(ReferenceEquals(StormOwner(), second), "obsolete bridge release stole replacement global");
            Require(await ReceiverRed() < 0.1f, "obsolete bridge release invalidated replacement receiver");
            await OnRenderThread(rd => Require(rd.TextureIsValid(secondPlaceholder), "obsolete bridge freed replacement placeholder"));

            Invoke(unused, "ReleaseStormLight");
            Require(Get(unused, "_stormMapRd") == null && ReferenceEquals(StormOwner(), second), "unused bridge release allocated or rebound a global");
            Invoke(pending, "EnsureStormMapGlobal");
            Invoke(pending, "ReleaseStormLight");
            await NextFrame();
            await NextFrame();
            Require(!(bool)Get(pending, "_stormMapRidReady")! && !((Rid)Get(pending, "_stormMapPlaceholder")!).IsValid, "pending initializer resurrected retired placeholder");
            Require(ReferenceEquals(StormOwner(), second), "pending initializer rebound the global after release");

            Invoke(second, "ReleaseStormLight");
            Require(StormOwner() == null, "last bridge left global ownership behind");
            Require(await ReceiverRed() > 0.9f, "live receiver did not select typed array default after release");
            await OnRenderThread(rd => Require(!rd.TextureIsValid(secondPlaceholder), "last storm placeholder was not freed"));
        }
        finally
        {
            foreach (RingSunshineCloudBridge bridge in new[] { first, second, unused, pending, interrupted })
            {
                Invoke(bridge, "ReleaseStormLight");
                bridge.Free();
            }
            receiver.Free();
        }
    }

    private async Task<float> ReceiverRed()
    {
        await NextFrame();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return GetViewport().GetTexture().GetImage().GetPixel(8, 8).R;
    }

    private static object? StormOwner() => typeof(RingSunshineCloudBridge).GetField("_stormMapGlobalOwner", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    private async Task OnRenderThread(Action<RenderingDevice> check)
    {
        Exception? failure = null;
        int completed = 0;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try { check(RenderingServer.GetRenderingDevice()!); }
            catch (Exception error) { failure = error; }
            finally { Volatile.Write(ref completed, 1); }
        }));
        while (Volatile.Read(ref completed) == 0) await NextFrame();
        if (failure != null) throw failure;
    }

    private async Task NextFrame() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static bool ArraysHaveRids(RingTerrainHeightShadow shadow)
    {
        foreach (Texture2DArrayRD? array in (Texture2DArrayRD?[])Get(shadow, "_arrays")!)
            if (array == null || !array.TextureRdRid.IsValid) return false;
        return true;
    }
    private static object? Get(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
