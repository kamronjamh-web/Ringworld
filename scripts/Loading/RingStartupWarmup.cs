using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Loading;

/// <summary>
/// Background warm-up run while the loader prepares resources, so the world attach does not pay first-use costs on the main thread.
///  - Shader compile: Shader.GetRid() on one worker. With a RenderingDevice backend the engine runs the shader parse and compile on the
///    calling thread, so a later ShaderMaterial.Shader assignment on the main thread finds the RID ready.
///  - Pre-JIT: RuntimeHelpers.PrepareMethod for the game assembly's non-generic methods (once per process).
/// </summary>
public static class RingStartupWarmup
{
    private static readonly List<Shader> RetainedShaders = new();
    private static readonly ConcurrentQueue<string> Log = new();
    private static int _pending;
    private static bool _jitStarted;

    public static bool IsComplete => Volatile.Read(ref _pending) == 0;
    /// <summary>True while background shader compiles may hold the scene-shader mutex (main thread should not poll pipeline monitors).</summary>
    public static bool IsWarming => Volatile.Read(ref _pending) > 0;

    /// <summary>Starts the shader-compile worker and (once per process) the pre-JIT. Keeps the shaders alive until <see cref="ReleaseShaders"/>:
    /// ResourceLoader returns the same compiled instance only while a reference exists.</summary>
    public static void Start(IReadOnlyList<Shader> shaders)
    {
        RetainedShaders.Clear();
        RetainedShaders.AddRange(shaders);
        var list = new List<Shader>(shaders);
        Interlocked.Increment(ref _pending);
        // One worker, one shader at a time: the engine serialises ShaderCompiler on SceneShaderForwardClustered's singleton mutex anyway,
        // and the short sleep lets main-thread users of that mutex (material parameter updates) in between.
        var thread = new Thread(() =>
        {
            var total = Stopwatch.StartNew();
            foreach (Shader captured in list)
            {
                try { _ = captured.GetRid(); }
                catch (Exception error) { Log.Enqueue("WARMUP shader failed " + captured.ResourcePath + ": " + error.Message); }
                Thread.Sleep(2);
            }
            Log.Enqueue($"WARMUP shaders done: {list.Count} in {total.Elapsed.TotalMilliseconds:0} ms");
            Interlocked.Decrement(ref _pending);
        }) { IsBackground = true, Name = "RingShaderWarmup", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        if (!_jitStarted)
        {
            _jitStarted = true;
            Interlocked.Increment(ref _pending);
            Task.Run(PreJit);
        }
    }

    /// <summary>Drops the retained shader references (world exit or cancelled load).</summary>
    public static void ReleaseShaders() => RetainedShaders.Clear();

    /// <summary>Main thread: forwards worker log lines to the console.</summary>
    public static void Flush()
    {
        while (Log.TryDequeue(out string? line)) GD.Print(line);
    }

    private static void PreJit()
    {
        var watch = Stopwatch.StartNew();
        int prepared = 0, failed = 0;
        try
        {
            Type[] types;
            try { types = typeof(RingStartupWarmup).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException partial) { types = Array.FindAll(partial.Types, t => t != null)!; }
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 4, 2, 6) };
            Parallel.ForEach(types, options, type =>
            {
                if (type.ContainsGenericParameters) return;
                string? ns = type.Namespace;
                if (ns != null && (ns.Contains(".Tools", StringComparison.Ordinal) || ns.EndsWith(".Tests", StringComparison.Ordinal))) return;
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MethodBase[] methods;
                try { methods = type.GetMethods(all); }
                catch { return; }
                foreach (MethodBase method in methods) Prepare(method, ref prepared, ref failed);
                try
                {
                    foreach (ConstructorInfo ctor in type.GetConstructors(all))
                        if (!ctor.IsStatic) Prepare(ctor, ref prepared, ref failed);
                }
                catch { }
            });
        }
        catch (Exception error) { Log.Enqueue("WARMUP prejit error: " + error.Message); }
        Log.Enqueue($"WARMUP prejit prepared={prepared} failed={failed} in {watch.Elapsed.TotalMilliseconds:0} ms");
        Interlocked.Decrement(ref _pending);
    }

    private static void Prepare(MethodBase method, ref int prepared, ref int failed)
    {
        if (method.IsAbstract || method.ContainsGenericParameters) return;
        if ((method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0) return;
        try { RuntimeHelpers.PrepareMethod(method.MethodHandle); Interlocked.Increment(ref prepared); }
        catch { Interlocked.Increment(ref failed); }
    }
}
