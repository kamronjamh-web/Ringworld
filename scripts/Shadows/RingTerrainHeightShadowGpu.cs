using System;
using Godot;
using RingworldTechDemo.Atmosphere;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// Render-thread GPU half of the S4 height-shadow producer, S4a part (docs/shadow-revamp-plan.md S4): per level a storage buffer float[N^2] of the toroidal
/// slot-space heights (the CPU master mirror) plus a packed max-mip buffer (mips 1..5, layout RingHeightShadowLattice.MipOffset). Heights arrive through
/// scatter passes fed by one &lt;= 256 KB staging buffer; the mip chain is rebuilt after any upload of a level. The compute shaders compile at runtime from
/// RDShaderSource (RingAtmosphereLuts pattern, no .glsl import). All methods must run on the thread that owns <see cref="RenderingDevice"/>
/// (the render thread in production; tests pass a local device and Submit/Sync themselves). S4b adds the march / build pass (ring_height_shadow_build.comp,
/// whole-level or row-range dispatch) writing a plain RG16F texture per level; slicing, the 3-layer array publish and the globals are S4c.
/// </summary>
public sealed class RingTerrainHeightShadowGpu : IDisposable
{
    private const string ScatterShaderPath = "res://materials/shadows/ring_height_shadow_scatter.comp";
    private const string MipsShaderPath = "res://materials/shadows/ring_height_shadow_mips.comp";
    private const string BuildShaderPath = "res://materials/shadows/ring_height_shadow_build.comp";
    private const int BuildParamBytes = 96;
    private readonly RenderingDevice _rd;
    private readonly int _levels;
    private Rid _scatterShader, _scatterPipeline, _mipsShader, _mipsPipeline, _staging, _buildShader, _buildPipeline, _buildParams;
    private readonly Rid[] _heightBuffers, _mipBuffers, _scatterSets, _mipSets, _outputTextures, _buildSets;
    private byte[]? _chunkBytes;
    private bool _initialized;

    public string Error { get; private set; } = "";
    public bool Ready => _initialized && Error.Length == 0;

    public RingTerrainHeightShadowGpu(RenderingDevice rd, int levels = RingHeightShadowClipmap.LevelCount)
    {
        _rd = rd; _levels = levels;
        _heightBuffers = new Rid[levels]; _mipBuffers = new Rid[levels]; _scatterSets = new Rid[levels]; _mipSets = new Rid[levels];
        _outputTextures = new Rid[levels]; _buildSets = new Rid[levels];
    }

    public Rid HeightBuffer(int level) => _heightBuffers[level];
    public Rid MipBuffer(int level) => _mipBuffers[level];
    /// <summary>RG16F slot-space output of the last build of a level: R = shadow-line height H - T0, G = blocker distance (0 none, negative unresolved).</summary>
    public Rid OutputTexture(int level) => _outputTextures[level];

    /// <summary>Compiles both shaders and allocates the buffers (about 4.0 + 1.3 MB per level plus 256 KB staging). Returns false and sets <see cref="Error"/> on failure.</summary>
    public bool Initialize()
    {
        if (_initialized) return Ready;
        _initialized = true;
        if (!Compile(ScatterShaderPath, out _scatterShader, out _scatterPipeline) || !Compile(MipsShaderPath, out _mipsShader, out _mipsPipeline)
            || !Compile(BuildShaderPath, out _buildShader, out _buildPipeline)) return false;
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
        if (!_staging.IsValid) return Fail("height shadow staging allocation failed");
        return _levels != RingHeightShadowClipmap.LevelCount || InitializeBuild();
    }

    private bool InitializeBuild()
    {
        int n = RingHeightShadowLattice.TextureSize;
        _buildParams = _rd.StorageBufferCreate(BuildParamBytes, new byte[BuildParamBytes]);
        if (!_buildParams.IsValid) return Fail("height shadow build params allocation failed");
        for (int l = 0; l < _levels; l++)
        {
            var format = new RDTextureFormat
            {
                Width = (uint)n, Height = (uint)n, Depth = 1, ArrayLayers = 1, Mipmaps = 1, TextureType = RenderingDevice.TextureType.Type2D,
                Format = RenderingDevice.DataFormat.R16G16Sfloat,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
                            | RenderingDevice.TextureUsageBits.CanUpdateBit,
            };
            _outputTextures[l] = _rd.TextureCreate(format, new RDTextureView());
            if (!_outputTextures[l].IsValid) return Fail("height shadow output texture allocation failed");
            var uniforms = new Godot.Collections.Array<RDUniform>();
            int binding = 0;
            foreach (Rid rid in new[] { _heightBuffers[0], _mipBuffers[0], _heightBuffers[1], _mipBuffers[1], _buildParams })
            {
                var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding++ };
                uniform.AddId(rid);
                uniforms.Add(uniform);
            }
            var image = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
            image.AddId(_outputTextures[l]);
            uniforms.Add(image);
            _buildSets[l] = _rd.UniformSetCreate(uniforms, _buildShader, 0);
            if (!_buildSets[l].IsValid) return Fail("height shadow build uniform set creation failed");
        }
        return true;
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

    /// <summary>Highest master height of a level (the early-stop bound of the march; one pass over 1M floats).</summary>
    public static float LevelMaxHeight(RingHeightShadowClipmap clipmap, int level)
    {
        float max = float.NegativeInfinity;
        foreach (float h in clipmap.Levels[level].Heights) if (h > max) max = h;
        return max;
    }

    /// <summary>Writes the march parameters (call before the dispatches of a build; the buffer is not double-buffered, so never while a build is in flight).</summary>
    public void SetBuildInputs(HeightShadowBuildInputs inputs)
    {
        if (!Ready) return;
        var bytes = new byte[BuildParamBytes];
        var span = bytes.AsSpan();
        BitConverter.TryWriteBytes(span.Slice(0, 4), (float)inputs.AnchorSun.Up);
        BitConverter.TryWriteBytes(span.Slice(4, 4), (float)inputs.AnchorSun.Around);
        BitConverter.TryWriteBytes(span.Slice(8, 4), (float)inputs.AnchorSun.Across);
        BitConverter.TryWriteBytes(span.Slice(12, 4), (float)inputs.RingRadiusMeters);
        BitConverter.TryWriteBytes(span.Slice(16, 4), inputs.OutsideMaxMeters);
        BitConverter.TryWriteBytes(span.Slice(20, 4), inputs.WallTopMeters);
        BitConverter.TryWriteBytes(span.Slice(24, 4), (float)inputs.WidthMeters);
        BitConverter.TryWriteBytes(span.Slice(28, 4), inputs.Walls ? 1u : 0u);
        for (int l = 0; l < 2; l++)
        {
            Span<byte> s = span.Slice(32 + 32 * l, 32);
            int t = inputs.TexelMeters[l];
            HeightShadowWindow window = inputs.Windows[l] ?? default;
            if (inputs.Windows[l] != null && inputs.AnchorMeters % t != 0) throw new ArgumentException("anchor must be a multiple of the texel size");
            BitConverter.TryWriteBytes(s.Slice(0, 4), (float)t);
            BitConverter.TryWriteBytes(s.Slice(4, 4), (int)(window.I0 - inputs.AnchorMeters / t));
            BitConverter.TryWriteBytes(s.Slice(8, 4), (int)window.J0);
            BitConverter.TryWriteBytes(s.Slice(12, 4), RingHeightShadowLattice.SlotOf(window.I0));
            BitConverter.TryWriteBytes(s.Slice(16, 4), RingHeightShadowLattice.SlotOf(window.J0));
            BitConverter.TryWriteBytes(s.Slice(20, 4), inputs.LevelMax[l]);
            BitConverter.TryWriteBytes(s.Slice(24, 4), inputs.Windows[l] != null ? 1u : 0u);
        }
        _rd.BufferUpdate(_buildParams, 0, BuildParamBytes, bytes);
    }

    /// <summary>Dispatches the march for rows [rowStart, rowStart + rowCount) of a level's window (window-relative rows; the whole level is 0..N).</summary>
    public void DispatchBuild(int level, int rowStart = 0, int rowCount = RingHeightShadowLattice.TextureSize)
    {
        if (!Ready || rowCount <= 0) return;
        var push = new byte[16];
        BitConverter.GetBytes((uint)level).CopyTo(push, 0);
        BitConverter.GetBytes((uint)rowStart).CopyTo(push, 4);
        BitConverter.GetBytes((uint)rowCount).CopyTo(push, 8);
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _buildPipeline);
        _rd.ComputeListBindUniformSet(list, _buildSets[level], 0);
        _rd.ComputeListSetPushConstant(list, push, 16);
        _rd.ComputeListDispatch(list, (uint)(RingHeightShadowLattice.TextureSize / 8), (uint)((rowCount + 7) / 8), 1);
        _rd.ComputeListEnd();
    }

    /// <summary>Blocking readback of a level's build output as (R, G) per slot texel, row-major (tests; on a local device Submit and Sync first).</summary>
    public (float[] R, float[] G) ReadBuildOutput(int level)
    {
        byte[] bytes = _rd.TextureGetData(_outputTextures[level], 0);
        int count = bytes.Length / 4;
        var r = new float[count];
        var g = new float[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = (float)BitConverter.ToHalf(bytes, i * 4);
            g[i] = (float)BitConverter.ToHalf(bytes, i * 4 + 2);
        }
        return (r, g);
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
        foreach (Rid[] group in new[] { _buildSets, _scatterSets, _mipSets, _outputTextures, _heightBuffers, _mipBuffers })
            for (int i = 0; i < group.Length; i++)
            {
                if (group[i].IsValid) _rd.FreeRid(group[i]);
                group[i] = default;
            }
        foreach (Rid rid in new[] { _staging, _buildParams, _scatterPipeline, _scatterShader, _mipsPipeline, _mipsShader, _buildPipeline, _buildShader })
            if (rid.IsValid) _rd.FreeRid(rid);
        _staging = default; _buildParams = default; _scatterPipeline = default; _scatterShader = default; _mipsPipeline = default; _mipsShader = default;
        _buildPipeline = default; _buildShader = default;
        _initialized = false;
    }
}

/// <summary>
/// Inputs of one height-shadow build. The sun is the stabilised light direction projected onto the anchor tangent frame (RingHeightShadowLattice.ProjectToTangentFrame
/// at theta = AnchorMeters / R). <see cref="OutsideMaxMeters"/> bounds the terrain beyond the L1 window (S4c: pyramid QueryRegion max + 200 m when cheap, else the 8000 m default).
/// A null window marks a level invalid (L1 not yet filled).
/// </summary>
public sealed class HeightShadowBuildInputs
{
    public double RingRadiusMeters;
    public double WidthMeters;
    public long AnchorMeters;
    public HeightShadowSunFrame AnchorSun;
    public float OutsideMaxMeters = 8000f;
    public bool Walls;
    public float WallTopMeters;
    public readonly int[] TexelMeters = new int[2];
    public readonly HeightShadowWindow?[] Windows = new HeightShadowWindow?[2];
    public readonly float[] LevelMax = new float[2];
}
