using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Verifies that layer teardown cancels and joins a live local-RD prototype session.</summary>
public partial class GpuGrassGenerationShutdownTest : Node
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        try
        {
            await Run();
            Result = "PASS prototype session cancelled and joined during layer exit";
        }
        catch (Exception error)
        {
            Result = "FAIL " + error;
        }
    }

    private async Task Run()
    {
        var layer = new RingStandingGrassLayer { Name = "GpuGrassGenerationShutdownLayer" };
        layer.SetProcess(false);
        AddChild(layer);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.Run(() =>
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var session = new RingGrassGpuGenerationPrototype.Session();
                ready.TrySetResult(true);
                cancellation.Token.WaitHandle.WaitOne();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                throw;
            }
        });

        SetPrivate(layer, "_gpuGenerationPrototypeCancellation", cancellation);
        SetPrivate(layer, "_gpuGenerationPrototypeTask", worker);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Stopwatch exitTime = Stopwatch.StartNew();
            layer.Free(); // Production _ExitTree must finish local-RD cleanup before returning.
            bool completedAtExit = worker.IsCompleted;
            exitTime.Stop();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            if (!completedAtExit) throw new InvalidOperationException("layer exit returned before the local-RD worker finished");
            if (exitTime.Elapsed.TotalSeconds > 2) throw new TimeoutException("prototype shutdown took more than two seconds");
            if (layer.GpuGenerationPrototypeRunning) throw new InvalidOperationException("layer still reports a prototype worker after exit");
            GD.Print($"GPU_GRASS_GENERATION_SHUTDOWN_MS={exitTime.Elapsed.TotalMilliseconds:F2}");
        }
        finally
        {
            cancellation.Cancel();
            if (GodotObject.IsInstanceValid(layer)) layer.Free();
        }
    }

    private static void SetPrivate(RingStandingGrassLayer layer, string name, object value)
    {
        FieldInfo? field = typeof(RingStandingGrassLayer).GetField(name, PrivateInstance);
        if (field == null) throw new MissingFieldException(typeof(RingStandingGrassLayer).FullName, name);
        field.SetValue(layer, value);
    }
}
