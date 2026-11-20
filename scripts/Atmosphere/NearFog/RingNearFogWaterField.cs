using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Atmosphere.NearFog;

/// <summary>
/// The near-water field of the steam fog (NF-1 design N1, N2): 64 x 64 texels at 8 m around the camera (+-256 m). Channel R = 1 where the sampler reports a River or Lake with a finite exact level above the bed, else 0;
/// channel G = that water surface's canonical height (ExactWaterLevel), 0 where there is no water. Texel (i, j) is sampled exactly at canonical (origin around + 8 i, origin across + 8 j), origin = snapped centre - 256 m
/// (so the centre is a lattice point); the texture holds i along x and j along y, and the shader reads it bilinearly with texel centres at (i + 0.5) / 64 (ring_near_fog_water in ring_near_fog_fields.gdshaderinc).
/// A build runs on a worker from a terrain sampler delegate once the camera is more than <see cref="RebuildDistanceMeters"/> from where the last build was requested; the finished field is uploaded on the main thread.
/// Main thread: <see cref="Update"/> every frame (cheap: a completed-flag test), then <see cref="Publish"/> every frame (cached: it only talks to the RenderingServer when a value changed).
/// Globals: ringworld_near_fog_water (sampler2D) and ringworld_near_fog_water_frame (vec4: texel-0 centre around and across relative to the height-shadow anchor, texel metres, size in texels; size 0 while no field is built).
/// </summary>
public sealed class RingNearFogWaterField : IDisposable
{
    public const int Size = 64;
    public const float TexelMeters = 8f;
    /// <summary>Camera move, metres, from the last build request that starts a new build.</summary>
    public const float RebuildDistanceMeters = 32f;
    public const string WaterGlobal = "ringworld_near_fog_water";
    public const string FrameGlobal = "ringworld_near_fog_water_frame";

    private const int Channels = 2;

    private readonly Func<long, int, TerrainSample> _sampler;
    private readonly double _circumferenceMeters;
    private readonly int _acrossMax;
    private float[] _front = new float[Size * Size * Channels];
    private float[] _back = new float[Size * Size * Channels];
    private readonly byte[] _upload = new byte[Size * Size * Channels * sizeof(float)];
    private Image? _image;
    private ImageTexture? _texture;
    private ImageTexture? _zero;

    private Task? _task;
    private int _generation;
    private double _pendingOriginAround, _pendingOriginAcross; // owned by the worker while _task runs
    private volatile bool _taskDone;
    private volatile bool _taskFailed;

    private bool _hasRequest;
    private double _requestAround, _requestAcross;
    private double _originAround, _originAcross; // canonical, texel 0 centre, of the front field
    private bool _hasField;

    private Texture2D? _publishedTexture;
    /// <summary>The texture the water global currently holds (the fog-end compute binds its RD texture).</summary>
    public Texture2D? PublishedTexture => _publishedTexture;
    private Vector4 _publishedFrame = new(float.NaN, 0, 0, 0);

    public RingNearFogWaterField(Func<long, int, TerrainSample> sampler, double circumferenceMeters, int acrossMaxMeters)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _circumferenceMeters = circumferenceMeters > 0.0 ? circumferenceMeters : double.PositiveInfinity;
        _acrossMax = Math.Max(0, acrossMaxMeters);
    }

    /// <summary>True once a field is built and published (size > 0).</summary>
    public bool HasField => _hasField;
    public bool Building => _task != null;
    /// <summary>Builds started since construction (<see cref="Invalidate"/> does not reset it).</summary>
    public int BuildsStarted { get; private set; }
    public int BuildsCompleted { get; private set; }
    public int BuildsFailed { get; private set; }
    /// <summary>Wall time of the last completed build on the worker, ms.</summary>
    public double LastBuildMilliseconds { get; private set; }
    public double OriginAroundMeters => _originAround;
    public double OriginAcrossMeters => _originAcross;
    public double CenterAroundMeters => _originAround + Size / 2 * TexelMeters;
    public double CenterAcrossMeters => _originAcross + Size / 2 * TexelMeters;
    /// <summary>The front field, R then G per texel, row-major (index = (j * Size + i) * 2). Main thread; valid while <see cref="HasField"/>.</summary>
    public ReadOnlySpan<float> Data => _front;
    /// <summary>Texels of the front field whose mask channel is above 0.5 (0 while no field is built). Cached when a build is adopted.</summary>
    public int WaterTexelCount { get; private set; }

    /// <summary>Mask (R) and surface height (G) of texel (i, j) of the front field.</summary>
    public (float Mask, float Height) Texel(int i, int j)
    {
        int index = (j * Size + i) * Channels;
        return (_front[index], _front[index + 1]);
    }

    /// <summary>Drops the field (teleport, world change, tests): size reads 0 until the next build lands. A running build is abandoned.</summary>
    public void Invalidate()
    {
        _generation++;
        _hasField = false;
        _hasRequest = false;
        WaterTexelCount = 0;
    }

    /// <summary>Main thread, every frame. Uploads a finished build and starts the next one when the camera is more than <see cref="RebuildDistanceMeters"/> from the last request (or none was made).</summary>
    public void Update(double cameraAround, double cameraAcross)
    {
        if (_task != null && _taskDone)
        {
            _task = null;
            int generation = _pendingGeneration;
            if (!_taskFailed && generation == _generation) Adopt();
            else if (_taskFailed) BuildsFailed++;
        }
        if (_task != null) return;
        if (_hasRequest)
        {
            double da = WrapDelta(cameraAround - _requestAround);
            double dz = cameraAcross - _requestAcross;
            if (da * da + dz * dz <= (double)RebuildDistanceMeters * RebuildDistanceMeters) return;
        }
        Begin(cameraAround, cameraAcross);
    }

    private int _pendingGeneration;

    private void Begin(double cameraAround, double cameraAcross)
    {
        _hasRequest = true;
        _requestAround = cameraAround;
        _requestAcross = cameraAcross;
        _pendingOriginAround = Math.Round(cameraAround / TexelMeters) * TexelMeters - (Size / 2) * TexelMeters;
        _pendingOriginAcross = Math.Round(cameraAcross / TexelMeters) * TexelMeters - (Size / 2) * TexelMeters;
        _pendingGeneration = _generation;
        _taskDone = false;
        _taskFailed = false;
        BuildsStarted++;
        double originAround = _pendingOriginAround, originAcross = _pendingOriginAcross;
        float[] target = _back;
        _task = Task.Run(() => Build(target, originAround, originAcross));
    }

    private void Build(float[] target, double originAround, double originAcross)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            long baseAround = (long)Math.Round(originAround);
            long baseAcross = (long)Math.Round(originAcross);
            int step = (int)TexelMeters;
            for (int j = 0; j < Size; j++)
            {
                long across = baseAcross + (long)j * step;
                int index = j * Size * Channels;
                if (across < 0 || across > _acrossMax)
                {
                    for (int i = 0; i < Size; i++, index += Channels) { target[index] = 0f; target[index + 1] = 0f; }
                    continue;
                }
                for (int i = 0; i < Size; i++, index += Channels)
                {
                    TerrainSample sample = _sampler(baseAround + (long)i * step, (int)across);
                    bool water = (sample.WaterKind == SurfaceWaterKind.River || sample.WaterKind == SurfaceWaterKind.Lake)
                        && sample.HasExactWaterLevel && float.IsFinite(sample.ExactWaterLevel)
                        && sample.SurfaceHeight < sample.ExactWaterLevel;   // the sampler's kind covers banks too; water only where the bed is below the level (RingLodSurfaceEditSnapshot's rule)
                    target[index] = water ? 1f : 0f;
                    target[index + 1] = water ? sample.ExactWaterLevel : 0f;
                }
            }
            LastBuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        catch (Exception error)
        {
            _taskFailed = true;
            GD.PushError("near fog water field build failed: " + error.Message);
        }
        finally
        {
            _taskDone = true;
        }
    }

    private void Adopt()
    {
        (_front, _back) = (_back, _front);
        int waterTexels = 0;
        for (int k = 0; k < _front.Length; k += Channels) if (_front[k] > 0.5f) waterTexels++;
        WaterTexelCount = waterTexels;
        _originAround = _pendingOriginAround;
        _originAcross = _pendingOriginAcross;
        Buffer.BlockCopy(_front, 0, _upload, 0, _upload.Length);
        if (_image == null || _texture == null)
        {
            _image = Image.CreateFromData(Size, Size, false, Image.Format.Rgf, _upload);
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(Size, Size, false, Image.Format.Rgf, _upload);
            _texture.Update(_image);
        }
        _hasField = true;
        BuildsCompleted++;
    }

    /// <summary>Main thread, every frame: writes the two globals (only on change). <paramref name="anchorAround"/> and <paramref name="anchorAcross"/> are the height-shadow anchor (RingTerrainHeightShadow.PublishedAnchor).</summary>
    public void Publish(long anchorAround, long anchorAcross)
    {
        Vector4 frame = FrameFor(anchorAround, anchorAcross);
        Texture2D texture = _hasField && _texture != null ? _texture : ZeroTexture();
        if (!ReferenceEquals(texture, _publishedTexture))
        {
            _publishedTexture = texture;
            RenderingServer.GlobalShaderParameterSet(WaterGlobal, texture.GetRid());
        }
        if (frame != _publishedFrame)
        {
            _publishedFrame = frame;
            RenderingServer.GlobalShaderParameterSet(FrameGlobal, frame);
        }
    }

    /// <summary>The frame global for an anchor: texel-0 centre around (shortest signed distance around the circumference, which is what the receivers' p.x = R * angle is) and across relative to the anchor, texel metres, size (0 while no field).</summary>
    public Vector4 FrameFor(long anchorAround, long anchorAcross)
    {
        if (!_hasField) return new Vector4(0f, 0f, TexelMeters, 0f);
        return new Vector4((float)WrapDelta(_originAround - anchorAround), (float)(_originAcross - anchorAcross), TexelMeters, Size);
    }

    /// <summary>The anchor as the (around, across) vector of the order; anchors are multiples of 4096, exact in a float to 2^36 m.</summary>
    public void Publish(Vector2 anchorAroundAcross) => Publish((long)Math.Round(anchorAroundAcross.X), (long)Math.Round(anchorAroundAcross.Y));

    private Texture2D ZeroTexture()
    {
        if (_zero == null)
        {
            var image = Image.CreateFromData(1, 1, false, Image.Format.Rgf, new byte[2 * sizeof(float)]);
            _zero = ImageTexture.CreateFromImage(image);
        }
        return _zero;
    }

    private double WrapDelta(double delta)
    {
        if (!double.IsFinite(_circumferenceMeters)) return delta;
        double wrapped = delta % _circumferenceMeters;
        if (wrapped > _circumferenceMeters * 0.5) wrapped -= _circumferenceMeters;
        else if (wrapped < -_circumferenceMeters * 0.5) wrapped += _circumferenceMeters;
        return wrapped;
    }

    public void Dispose()
    {
        _generation++;
        try { _task?.Wait(2000); } catch (Exception) { /* a failed build is already reported */ }
        _task = null;
        _texture?.Dispose();
        _zero?.Dispose();
        _image?.Dispose();
        _texture = null;
        _zero = null;
        _image = null;
        _publishedTexture = null;
        _publishedFrame = new Vector4(float.NaN, 0, 0, 0);
        _hasField = false;
    }
}
