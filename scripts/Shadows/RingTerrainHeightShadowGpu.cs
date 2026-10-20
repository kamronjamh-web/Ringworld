using System;
using Godot;
using RingworldTechDemo.Atmosphere;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// Render-thread GPU half of the S4 height-shadow producer, S4a part (docs/shadow-revamp-plan.md S4): per level a storage buffer float[N^2] of the toroidal
/// slot-space heights (the CPU master mirror) plus a packed max-mip buffer (mips 1..5, layout RingHeightShadowLattice.MipOffset). Heights arrive through
/// scatter passes fed by one &lt;= 256 KB staging buffer; the mip chain is rebuilt after any upload of a level. The compute shaders compile at runtime from
/// RDShaderSource (RingAtmosphereLuts pattern, no .glsl import). All methods must run on the thread that owns <see cref="RenderingDevice"/>
/// (the render thread in production; tests pass a local device and Submit/Sync themselves). The march / build pass and the published texture layers are S4b/S4c.
/// </summary>
public sealed class RingTerrainHeightShadowGpu : IDisposable
{
    private const string ScatterShaderPath = "res://materials/shadows/ring_height_shadow_scatter.comp";
    private const string MipsShaderPath = "res://materials/shadows/ring_height_shadow_mips.comp";
    private readonly RenderingDevice _rd;
    private readonly int _levels;
    private Rid _scatterShader, _scatterPipeline, _mipsShader, _mipsPipeline, _staging;
    private readonly Rid[] _heightBuffers, _mipBuffers, _scatterSets, _mipSets;
    private byte[]? _chunkBytes;
    private bool _initialized;

    public string Error { get; private set; } = "";
    public bool Ready => _initialized && Error.Length == 0;

    public RingTerrainHeightShadowGpu(RenderingDevice rd, int levels = RingHeightShadowClipmap.LevelCount)
    {
        _rd = rd; _levels = levels;
        _heightBuffers = new Rid[levels]; _mipBuffers = new Rid[levels]; _scatterSets = new Rid[levels]; _mipSets = new Rid[levels];
    }

    public Rid HeightBuffer(int level) => _heightBuffers[level];
    public Rid MipBuffer(int level) => _mipBuffers[level];

    /// <summary>Compiles both shaders and allocates the buffers (about 4.0 + 1.3 MB per level plus 256 KB staging). Returns false and sets <see cref="Error"/> on failure.</summary>
    public bool Initialize()
    {
        if (_initialized) return Ready;
        _initialized = true;
        if (!Compile(ScatterShaderPath, out _scatterShader, out _scatterPipeline) || !Compile(MipsShaderPath, out _mipsShader, out _mipsPipeline)) return false;
        int n = RingHeightShadowLattice.TextureSize;
        _staging = _rd.StorageBufferCreate((uint)(RingHeightShadowLattice.ScatterChunkFloats * 4));
        for (int l = 0; l < _levels; l++)
        {
            _heightBuffers[l] = _rd.StorageBufferCreate((uint)(n * n * 4), new byte[n * n * 4]);
            _mipBuffers[l] = _rd.StorageBufferCreate((uint)(RingHeightShadowLattice.MipBufferFloats * 4), new byte[RingHeightShadowLattice.MipBufferFloats * 4]);
            if (!_heightBuffers[l].IsValid || !_mipBuffers[l].IsValid) return Fail("height shadow buffer allocation failed");
            _scatterSets[l] = _rd.UniformSetCreate(Buffers(_heightBuffers[l], _staging), _scatterShader, 0);
            _mipSets[l] = _rd.UniformSetCreate(Buffers(_heightBuffers[l], _mipBuffers[l]), _mipsShader, 0);
            if (!_scatterSets[l].IsValid || !_mipSets[l].IsValid) return Fail("height shadow uniform set creation failed");
        }
        return _staging.IsValid || Fail("height shadow staging allocation failed");
    }

    private static Godot.Collections.Array<RDUniform> Buffers(Rid first, Rid second)
    {
        var uniforms = new Godot.Collections.Array<RDUniform>();
        int binding = 0;
        foreach (Rid rid in new[] { first, second })
        {
            var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding++ };
            uniform.AddId(rid);
            uniforms.Add(uniform);
        }
        return uniforms;
    }

    private bool Compile(string path, out Rid shader, out Rid pipeline)
    {
        shader = default; pipeline = default;
        string source = RingAtmosphereLuts.ExpandIncludes(path);
        if (string.IsNullOrEmpty(source)) return Fail("height shadow shader missing: " + path);
        using var shaderSource = new RDShaderSource { SourceCompute = source };
        using RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(shaderSource);
        if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) return Fail($"height shadow shader compile error in {path}: {spirv.CompileErrorCompute}");
        shader = _rd.ShaderCreateFromSpirV(spirv);
        if (!shader.IsValid) return Fail("height shadow shader creation failed: " + path);
        pipeline = _rd.ComputePipelineCreate(shader);
        return pipeline.IsValid || Fail("height shadow pipeline creation failed: " + path);
    }

    private bool Fail(string message)
    {
        Error = message;
        GD.PushError(message);
        return false;
    }

    /// <summary>Uploads one slot rect of the CPU master into the level's height buffer through the staging buffer (several chunks when &gt; 256 KB).</summary>
    public void UploadSlotRect(int level, RingHeightShadowClipmap clipmap, HeightShadowSlotRect rect)
    {
        if (!Ready || rect.Width <= 0 || rect.Height <= 0) return;
        int rowsPerChunk = Math.Max(1, RingHeightShadowLattice.ScatterChunkFloats / rect.Width);
        var floats = new float[rowsPerChunk * rect.Width];
        for (int row = 0; row < rect.Height; row += rowsPerChunk)
        {
            int rows = Math.Min(rowsPerChunk, rect.Height - row);
            var chunk = new HeightShadowSlotRect(rect.X, rect.Y + row, rect.Width, rows);
            int count = rows * rect.Width;
            clipmap.CopySlotRect(level, chunk, floats.AsSpan(0, count));
            if (_chunkBytes == null || _chunkBytes.Length != count * 4) _chunkBytes = new byte[count * 4];
            Buffer.BlockCopy(floats, 0, _chunkBytes, 0, count * 4);
            _rd.BufferUpdate(_staging, 0, (uint)(count * 4), _chunkBytes);
            var push = new byte[16];
            BitConverter.GetBytes((uint)chunk.X).CopyTo(push, 0);
            BitConverter.GetBytes((uint)chunk.Y).CopyTo(push, 4);
            BitConverter.GetBytes((uint)chunk.Width).CopyTo(push, 8);
            BitConverter.GetBytes((uint)count).CopyTo(push, 12);
            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _scatterPipeline);
            _rd.ComputeListBindUniformSet(list, _scatterSets[level], 0);
            _rd.ComputeListSetPushConstant(list, push, 16);
            _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
            _rd.ComputeListEnd();
        }
    }

    /// <summary>Uploads an unwrapped-index rect (split into up to four slot rects).</summary>
    public void UploadIndexRect(int level, RingHeightShadowClipmap clipmap, HeightShadowIndexRect rect)
    {
        Span<HeightShadowSlotRect> slots = stackalloc HeightShadowSlotRect[4];
        int count = RingHeightShadowLattice.SplitToSlots(rect, slots);
        for (int i = 0; i < count; i++) UploadSlotRect(level, clipmap, slots[i]);
    }

    /// <summary>Uploads the whole N x N master of a level.</summary>
    public void UploadAll(int level, RingHeightShadowClipmap clipmap)
        => UploadSlotRect(level, clipmap, new HeightShadowSlotRect(0, 0, RingHeightShadowLattice.TextureSize, RingHeightShadowLattice.TextureSize));

    /// <summary>Rebuilds mips 1..5 of a level from its height buffer (5 dependent dispatches in one compute list).</summary>
    public void BuildMips(int level)
    {
        if (!Ready) return;
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _mipsPipeline);
        _rd.ComputeListBindUniformSet(list, _mipSets[level], 0);
        for (int mip = 1; mip <= RingHeightShadowLattice.MipCount; mip++)
        {
            int dst = RingHeightShadowLattice.MipSize(mip);
            var push = new byte[32];
            BitConverter.GetBytes((uint)(mip == 1 ? 0 : RingHeightShadowLattice.MipOffset(mip - 1))).CopyTo(push, 0);
            BitConverter.GetBytes((uint)(mip == 1 ? RingHeightShadowLattice.TextureSize : RingHeightShadowLattice.MipSize(mip - 1))).CopyTo(push, 4);
            BitConverter.GetBytes((uint)RingHeightShadowLattice.MipOffset(mip)).CopyTo(push, 8);
            BitConverter.GetBytes((uint)dst).CopyTo(push, 12);
            BitConverter.GetBytes(mip == 1 ? 0u : 1u).CopyTo(push, 16);
            if (mip > 1) _rd.ComputeListAddBarrier(list);
            _rd.ComputeListSetPushConstant(list, push, 32);
            _rd.ComputeListDispatch(list, (uint)((dst + 7) / 8), (uint)((dst + 7) / 8), 1);
        }
        _rd.ComputeListEnd();
    }

    /// <summary>Blocking readback of a level's height buffer (tests; on a local device Submit and Sync first).</summary>
    public float[] ReadHeights(int level) => ToFloats(_rd.BufferGetData(_heightBuffers[level]));
    public float[] ReadMips(int level) => ToFloats(_rd.BufferGetData(_mipBuffers[level]));

    private static float[] ToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public void Dispose()
    {
        foreach (Rid[] group in new[] { _scatterSets, _mipSets, _heightBuffers, _mipBuffers })
            for (int i = 0; i < group.Length; i++)
            {
                if (group[i].IsValid) _rd.FreeRid(group[i]);
                group[i] = default;
            }
        foreach (Rid rid in new[] { _staging, _scatterPipeline, _scatterShader, _mipsPipeline, _mipsShader })
            if (rid.IsValid) _rd.FreeRid(rid);
        _staging = default; _scatterPipeline = default; _scatterShader = default; _mipsPipeline = default; _mipsShader = default;
        _initialized = false;
    }
}
