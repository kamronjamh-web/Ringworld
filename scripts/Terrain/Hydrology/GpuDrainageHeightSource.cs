using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// GPU drainage height source (materials/terrain_gpu/ring_drainage_sample.glsl): the same height model and regional climate as the proxy kernel K1, with a
/// lean output (3 floats per grid cell, 1 per point). Runs on the <see cref="RenderingDevice"/> it is given; a local device created on a worker thread works
/// (checked by drainage_height_source_gpu_test). See <see cref="IDrainageHeightSource"/> for the natural-height contract.
/// Not thread-safe: one caller at a time.
/// </summary>
public sealed class GpuDrainageHeightSource : IDrainageHeightSource, IDisposable
{
    /// <summary>Columns per dispatch. A chunk spans about 4 Mm (limit 2^23 m from the anchor) and reads back about 16 MB (limit 64 MB).</summary>
    public const int ChunkColumns = 4096;
    private const int PointChunk = 1 << 20;
    private const string ShaderDirectory = "res://materials/terrain_gpu/";

    private readonly RenderingDevice _rd;
    private readonly long _circumference;
    private Rid _shader, _pipeline, _recipeBuffer, _inputBuffer, _outputBuffer, _uniformSet;
    private int _inputCapacity, _outputCapacity;

    public string Name => "gpu";
    /// <summary>Seconds spent in GPU dispatch plus readback in the last <see cref="SampleGrid"/> (not counting host packing).</summary>
    public double LastDeviceSeconds { get; private set; }

    public static bool IsSupported(TerrainGenerationParameters recipe, out string reason) => ProxyGpuHeightKernel.IsSupported(recipe, out reason);

    public GpuDrainageHeightSource(RenderingDevice rd, TerrainGenerationParameters recipe, DrainageClimateInputs climate)
    {
        _rd = rd;
        _circumference = checked((long)recipe.CircumferenceM);
        if (!IsSupported(recipe, out string reason)) throw new NotSupportedException("GPU drainage source: " + reason);
        string headerText = Read("ring_height_model.glslinc") + "\n" + Read("ring_regional_climate.glslinc");
        uint[] words = ProxyGpuHeightKernel.PackRecipe(recipe, climate.Gpu, ProxyGpuHeightKernel.ParseLayout(headerText));
        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        _recipeBuffer = rd.StorageBufferCreate((uint)bytes.Length, bytes);

        var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = ProxyGpuHeightKernel.ExpandIncludes(Read("ring_drainage_sample.glsl")) };
        RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(src);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException("drainage sample compile: " + spirv.CompileErrorCompute);
        _shader = rd.ShaderCreateFromSpirV(spirv, "ring_drainage_sample");
        if (!_shader.IsValid) throw new InvalidOperationException("drainage sample shader_create_from_spirv failed");
        _pipeline = rd.ComputePipelineCreate(_shader);
        if (!_pipeline.IsValid) throw new InvalidOperationException("drainage sample compute_pipeline_create failed");
    }

    public void Dispose()
    {
        Free(ref _uniformSet);
        Free(ref _inputBuffer);
        Free(ref _outputBuffer);
        Free(ref _recipeBuffer);
        Free(ref _pipeline);
        Free(ref _shader);
    }

    private void Free(ref Rid rid) { if (rid.IsValid) _rd.FreeRid(rid); rid = default; }

    private static string Read(string file) => Godot.FileAccess.GetFileAsString(ShaderDirectory + file).Replace("\r\n", "\n");

    private void EnsureBuffers(int inputInts, int outputFloats)
    {
        bool rebuild = !_uniformSet.IsValid;
        if (inputInts > _inputCapacity || outputFloats > _outputCapacity) { Free(ref _uniformSet); rebuild = true; }
        if (inputInts > _inputCapacity)
        {
            Free(ref _inputBuffer);
            _inputCapacity = Math.Max(inputInts, 64);
            _inputBuffer = _rd.StorageBufferCreate((uint)((long)_inputCapacity * 4));
            rebuild = true;
        }
        if (outputFloats > _outputCapacity)
        {
            Free(ref _outputBuffer);
            _outputCapacity = Math.Max(outputFloats, 64);
            _outputBuffer = _rd.StorageBufferCreate((uint)((long)_outputCapacity * 4));
            rebuild = true;
        }
        if (!rebuild) return;
        Free(ref _uniformSet);
        var recipe = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; recipe.AddId(_recipeBuffer);
        var output = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; output.AddId(_outputBuffer);
        var input = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 }; input.AddId(_inputBuffer);
        _uniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { recipe, output, input }, _shader, 0);
    }

    private static byte[] Push(int mode, int count, int cols, int rows, int anchor, (float CosHi, float CosLo, float SinHi, float SinLo) trig)
    {
        var b = new byte[48];
        Buffer.BlockCopy(new[] { mode, count, cols, rows, anchor, 0, 0, 0 }, 0, b, 0, 32);
        Buffer.BlockCopy(new[] { trig.CosHi, trig.CosLo, trig.SinHi, trig.SinLo }, 0, b, 32, 16);
        return b;
    }

    private void Dispatch(byte[] push, int count)
    {
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, _uniformSet, 0);
        _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
        _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();
    }

    public DrainageGrid SampleGrid(DrainageGridSpec spec, IProgress<double>? progress, CancellationToken cancellation)
    {
        var grid = new DrainageGrid(spec);
        int rows = spec.Rows;
        double device = 0;
        var clock = new System.Diagnostics.Stopwatch();
        for (int c0 = 0; c0 < spec.Columns; c0 += ChunkColumns)
        {
            cancellation.ThrowIfCancellationRequested();
            int cols = Math.Min(ChunkColumns, spec.Columns - c0);
            long anchor = spec.AroundSamples[c0 * DrainageGridSpec.Stride];
            int inputInts = cols * DrainageGridSpec.Stride + rows * DrainageGridSpec.Stride;
            var input = new int[inputInts];
            for (int c = 0; c < cols; c++)
            for (int k = 0; k < DrainageGridSpec.Stride; k++)
            {
                long d = spec.AroundSamples[(c0 + c) * DrainageGridSpec.Stride + k] - anchor;
                if (d < -_circumference / 2) d += _circumference;
                else if (d > _circumference / 2) d -= _circumference;
                input[c * DrainageGridSpec.Stride + k] = checked((int)d);
            }
            Array.Copy(spec.AcrossSamples, 0, input, cols * DrainageGridSpec.Stride, rows * DrainageGridSpec.Stride);
            int cells = cols * rows;
            EnsureBuffers(inputInts, cells * 3);
            var bytes = new byte[inputInts * 4];
            Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);
            clock.Restart();
            _rd.BufferUpdate(_inputBuffer, 0, (uint)bytes.Length, bytes);
            Dispatch(Push(0, cells, cols, rows, (int)anchor, ProxyGpuHeightKernel.AnchorTrig(anchor, _circumference)), cells);
            byte[] data = _rd.BufferGetData(_outputBuffer, 0, (uint)((long)cells * 12));
            device += clock.Elapsed.TotalSeconds;
            var f = new float[cells * 3];
            Buffer.BlockCopy(data, 0, f, 0, data.Length);
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int src = (r * cols + c) * 3, dst = r * spec.Columns + c0 + c;
                grid.Lowest[dst] = f[src];
                grid.LowestIndex[dst] = (byte)(int)f[src + 1];
                grid.Moisture[dst] = f[src + 2];
            }
            progress?.Report((double)(c0 + cols) / spec.Columns);
        }
        LastDeviceSeconds = device;
        return grid;
    }

    public void SamplePoints(ReadOnlySpan<DrainagePoint> positions, Span<float> heights)
    {
        if (heights.Length < positions.Length) throw new ArgumentException("heights is shorter than positions");
        for (int p0 = 0; p0 < positions.Length; p0 += PointChunk)
        {
            int n = Math.Min(PointChunk, positions.Length - p0);
            var words = new int[n * 8];
            for (int i = 0; i < n; i++)
            {
                DrainagePoint pt = positions[p0 + i];
                var trig = ProxyGpuHeightKernel.AnchorTrig(pt.Around, _circumference);
                int b = i * 8;
                words[b] = checked((int)pt.Around);
                words[b + 1] = 0;
                words[b + 2] = pt.Across;
                words[b + 3] = 0;
                words[b + 4] = BitConverter.SingleToInt32Bits(trig.CosHi);
                words[b + 5] = BitConverter.SingleToInt32Bits(trig.CosLo);
                words[b + 6] = BitConverter.SingleToInt32Bits(trig.SinHi);
                words[b + 7] = BitConverter.SingleToInt32Bits(trig.SinLo);
            }
            EnsureBuffers(words.Length, n);
            var bytes = new byte[words.Length * 4];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            _rd.BufferUpdate(_inputBuffer, 0, (uint)bytes.Length, bytes);
            Dispatch(Push(1, n, 0, 0, 0, default), n);
            byte[] data = _rd.BufferGetData(_outputBuffer, 0, (uint)(n * 4));
            var f = new float[n];
            Buffer.BlockCopy(data, 0, f, 0, data.Length);
            f.AsSpan().CopyTo(heights.Slice(p0, n));
        }
    }
}
