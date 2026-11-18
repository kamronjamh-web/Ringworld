using System;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Cloud data for reflections (docs/work-orders/reflections/R3a-design.md, R3a-1). Publishes the Sunshine colour and data images of the main view
/// as two persistent Texture2Drd globals whose RID is swapped on the render thread (the contact-shadow pattern), plus a direction-to-clip matrix and
/// two parameter vectors. One cached callable per frame reads the effect's snapshot (SunshineClouds.gd get_cloud_reflection_publication), applies the
/// valid rules and writes the globals. It records no GPU command, and no material reads the globals in this order, so the cloud image is unchanged.
/// </summary>
public partial class RingSunshineCloudBridge
{
    [ExportGroup("Cloud reflections")]
    /// <summary>Test hook, in no menu: off stops the effect's snapshot and leaves the globals untouched (the byte-identity test compares off against on).</summary>
    [Export] public bool CloudReflectionPublicationEnabled { get; set; } = true;

    private const double CloudReflectionRampSeconds = 0.25;
    private const long CloudReflectionStaleFrames = 2;
    private static readonly StringName CloudReflectionColorGlobal = "ringworld_cloud_reflection_color";
    private static readonly StringName CloudReflectionDataGlobal = "ringworld_cloud_reflection_data";
    private static readonly StringName CloudReflectionMatrixGlobal = "ringworld_cloud_reflection_dir_to_clip";
    private static readonly StringName CloudReflectionParamsGlobal = "ringworld_cloud_reflection_params";
    private static readonly StringName CloudReflectionParams2Global = "ringworld_cloud_reflection_params2";
    private static readonly StringName CloudReflectionPublicationName = "get_cloud_reflection_publication";
    private static readonly StringName CloudReflectionFallbackName = "get_cloud_reflection_fallback_rids";
    private static readonly StringName CloudReflectionMainViewName = "set_cloud_reflection_main_view";
    private static readonly StringName CloudReflectionEnabledName = "cloud_reflection_publication_enabled";

    // Main-thread facts and the last gameplay frame arguments, handed to the render-thread callable under _cloudReflectionLock.
    private struct CloudReflectionInputs
    {
        public Resource? Clouds;
        public bool Enabled, RenderOn, Xr, BackdropActive, HasFrame;
        public Transform3D Frame;
        public double Around, Across, Radius; // doubles: the effect compares the radius exactly and the around position needs metres at 30,000 km
    }

    private struct CloudReflectionStatusData
    {
        public float Weight;
        public string Reason;
        public long Serial, Generation, FramesDrawn, OtherViewFrame;
        public Vector2I Raster;
        public int Divisor;
        public bool Valid;
        public Rid BoundColor;
        public Vector4 Params;
    }

    private readonly object _cloudReflectionLock = new();
    private CloudReflectionInputs _cloudReflectionInputs;
    private CloudReflectionStatusData _cloudReflectionStatus = new() { Reason = "unstarted" };
    private double _cloudReflectionClock;
    private Texture2Drd? _cloudReflectionColorTexture;
    private Texture2Drd? _cloudReflectionDataTexture;
    private Callable _cloudReflectionCallable;
    private bool _cloudReflectionCallableReady;
    private bool _cloudReflectionEnabledApplied = true;

    // Render-thread state (only the callables below touch it).
    private Rid _cloudReflectionBoundColor, _cloudReflectionBoundData;
    private Rid _cloudReflectionFallbackColor, _cloudReflectionFallbackData;
    private Projection _cloudReflectionWrittenMatrix = Projection.Identity;
    private Vector4 _cloudReflectionWrittenParams = Vector4.Zero;
    private Vector4 _cloudReflectionWrittenParams2 = Vector4.Zero;
    private double _cloudReflectionValidSince = -1.0;

    /// <summary>Weight, reason, content serial, generation, raster size and divisor of the last publication (test and HUD use).</summary>
    public Godot.Collections.Dictionary CloudReflectionStatus
    {
        get
        {
            CloudReflectionStatusData status;
            lock (_cloudReflectionLock) status = _cloudReflectionStatus;
            return new Godot.Collections.Dictionary
            {
                { "weight", status.Weight }, { "reason", status.Reason }, { "valid", status.Valid },
                { "serial", status.Serial }, { "generation", status.Generation }, { "frames_drawn", status.FramesDrawn },
                { "other_view_frame", status.OtherViewFrame }, { "raster", status.Raster }, { "divisor", status.Divisor },
                { "bound_color", status.BoundColor }, { "params", status.Params },
            };
        }
    }

    /// <summary>The gameplay frame handed to the compositor this _Process; the render callable remaps the snapshot camera into it.</summary>
    private void RememberCloudReflectionFrame(Transform3D frame, double around, double across, double radius)
    {
        lock (_cloudReflectionLock)
        {
            _cloudReflectionInputs.HasFrame = true;
            _cloudReflectionInputs.Frame = frame;
            _cloudReflectionInputs.Around = around;
            _cloudReflectionInputs.Across = across;
            _cloudReflectionInputs.Radius = radius;
        }
    }

    private void EnsureCloudReflectionTextures()
    {
        if (_cloudReflectionColorTexture != null) return;
        _cloudReflectionColorTexture = new Texture2Drd();
        _cloudReflectionDataTexture = new Texture2Drd();
        RenderingServer.GlobalShaderParameterSet(CloudReflectionColorGlobal, _cloudReflectionColorTexture);
        RenderingServer.GlobalShaderParameterSet(CloudReflectionDataGlobal, _cloudReflectionDataTexture);
    }

    /// <summary>End of _Process: hand the main view to the effect, record the main-thread facts and enqueue the cached render-thread callable.</summary>
    private void QueueCloudReflectionPublication(double delta)
    {
        Resource? clouds = _clouds;
        if (clouds == null || !GodotObject.IsInstanceValid(clouds)) return;
        EnsureCloudReflectionTextures();
        bool enabled = CloudReflectionPublicationEnabled;
        if (enabled != _cloudReflectionEnabledApplied)
        {
            clouds.Set(CloudReflectionEnabledName, enabled);
            _cloudReflectionEnabledApplied = enabled;
        }
        Viewport viewport = GetViewport();
        Camera3D? camera = viewport.GetCamera3D();
        Vector2I target = viewport switch
        {
            Window window => window.Size,
            SubViewport sub => sub.Size,
            _ => Vector2I.Zero,
        };
        clouds.Call(CloudReflectionMainViewName, camera != null ? target : Vector2I.Zero, camera != null ? camera.GlobalTransform : Transform3D.Identity);
        bool backdropActive = _scaledFarViewport != null && _scaledFarViewport.ExactSunshineBackdropActive;
        lock (_cloudReflectionLock)
        {
            _cloudReflectionInputs.Clouds = clouds;
            _cloudReflectionInputs.Enabled = enabled;
            _cloudReflectionInputs.RenderOn = _lastRenderOn;
            _cloudReflectionInputs.Xr = viewport.UseXR;
            _cloudReflectionInputs.BackdropActive = backdropActive;
            // Unscaled real time: the ramp must finish under a time scale of 0 (capture mode stills) and in slow motion.
            _cloudReflectionClock = Time.GetTicksUsec() * 1e-6;
        }
        if (!_cloudReflectionCallableReady)
        {
            _cloudReflectionCallable = Callable.From(RunCloudReflectionPublication);
            _cloudReflectionCallableReady = true;
        }
        RenderingServer.CallOnRenderThread(_cloudReflectionCallable);
    }

    // Render thread (or the main thread in the single-threaded model): runs after frame N's draw was submitted and before frame N+1's draw.
    private void RunCloudReflectionPublication()
    {
        if (_cloudReflectionColorTexture == null || _cloudReflectionDataTexture == null) return;
        CloudReflectionInputs input;
        double clock;
        lock (_cloudReflectionLock)
        {
            input = _cloudReflectionInputs;
            clock = _cloudReflectionClock;
        }
        Resource? clouds = input.Clouds;
        string reason;
        bool valid = false;
        Godot.Collections.Dictionary? publication = null;
        if (!input.Enabled) reason = "disabled";
        else if (clouds == null || !GodotObject.IsInstanceValid(clouds)) reason = "no_clouds";
        else if (!input.RenderOn) reason = "render_off";
        else if (input.Xr) reason = "xr";
        else if (input.BackdropActive) reason = "far_backdrop";
        else
        {
            publication = clouds.Call(CloudReflectionPublicationName, input.Frame, input.Around, input.Across, input.Radius, input.HasFrame).AsGodotDictionary();
            reason = publication["reason"].AsString();
            valid = publication["valid"].AsBool();
            if (valid && (long)Engine.GetFramesDrawn() - publication["frames_drawn"].AsInt64() > CloudReflectionStaleFrames)
            {
                valid = false;
                reason = "stale";
            }
        }

        float weight = 0.0f;
        if (valid)
        {
            if (_cloudReflectionValidSince < 0.0) _cloudReflectionValidSince = clock;
            weight = (float)Math.Clamp((clock - _cloudReflectionValidSince) / CloudReflectionRampSeconds, 0.0, 1.0);
        }
        else _cloudReflectionValidSince = -1.0;

        CloudReflectionStatusData status = new() { Reason = reason, Valid = valid, Weight = weight };
        if (publication != null)
        {
            Rid color = publication["color"].AsRid(), data = publication["data"].AsRid();
            Vector2I raster = publication["raster"].AsVector2I();
            status.Serial = publication["serial"].AsInt64();
            status.Generation = publication["generation"].AsInt64();
            status.FramesDrawn = publication["frames_drawn"].AsInt64();
            status.OtherViewFrame = publication["last_other_view_frame"].AsInt64();
            status.Raster = raster;
            status.Divisor = (int)publication["divisor"].AsInt64();
            if (!color.IsValid || !data.IsValid)
            {
                // Unpublished (before the first dispatch, or after retirement): never leave a retired RID bound.
                BindCloudReflectionFallback(clouds!);
            }
            else if (valid)
            {
                BindCloudReflection(color, data);
                Vector4 enclosed = publication["enclosed"].AsVector4();
                WriteCloudReflectionMatrix(publication["dir_to_clip"].AsProjection());
                WriteCloudReflectionParams(new Vector4(weight, 1.0f / Math.Max(1, raster.X), 1.0f / Math.Max(1, raster.Y), (float)(status.Serial & 0xFFFFFF)));
                WriteCloudReflectionParams2(new Vector4(enclosed.X, enclosed.Y * enclosed.Z, publication["min_step"].AsSingle(), status.Divisor));
            }
        }
        if (!valid || publication == null)
            WriteCloudReflectionParams(new Vector4(0.0f, _cloudReflectionWrittenParams.Y, _cloudReflectionWrittenParams.Z, _cloudReflectionWrittenParams.W));
        status.BoundColor = _cloudReflectionBoundColor;
        status.Params = _cloudReflectionWrittenParams;
        lock (_cloudReflectionLock) _cloudReflectionStatus = status;
    }

    private void BindCloudReflection(Rid color, Rid data)
    {
        if (color != _cloudReflectionBoundColor)
        {
            _cloudReflectionColorTexture!.TextureRdRid = color;
            _cloudReflectionBoundColor = color;
        }
        if (data != _cloudReflectionBoundData)
        {
            _cloudReflectionDataTexture!.TextureRdRid = data;
            _cloudReflectionBoundData = data;
        }
    }

    private void BindCloudReflectionFallback(Resource clouds)
    {
        if (!_cloudReflectionFallbackColor.IsValid || !_cloudReflectionFallbackData.IsValid)
        {
            Godot.Collections.Array rids = clouds.Call(CloudReflectionFallbackName).AsGodotArray();
            if (rids.Count != 2) return;
            _cloudReflectionFallbackColor = rids[0].AsRid();
            _cloudReflectionFallbackData = rids[1].AsRid();
        }
        if (_cloudReflectionFallbackColor.IsValid && _cloudReflectionFallbackData.IsValid)
            BindCloudReflection(_cloudReflectionFallbackColor, _cloudReflectionFallbackData);
    }

    // Only changed values are written, so a run with the publication off never touches the declared zero globals.
    private void WriteCloudReflectionMatrix(Projection value)
    {
        if (value == _cloudReflectionWrittenMatrix) return;
        RenderingServer.GlobalShaderParameterSet(CloudReflectionMatrixGlobal, value);
        _cloudReflectionWrittenMatrix = value;
    }

    private void WriteCloudReflectionParams(Vector4 value)
    {
        if (value == _cloudReflectionWrittenParams) return;
        RenderingServer.GlobalShaderParameterSet(CloudReflectionParamsGlobal, value);
        _cloudReflectionWrittenParams = value;
    }

    private void WriteCloudReflectionParams2(Vector4 value)
    {
        if (value == _cloudReflectionWrittenParams2) return;
        RenderingServer.GlobalShaderParameterSet(CloudReflectionParams2Global, value);
        _cloudReflectionWrittenParams2 = value;
    }

    /// <summary>
    /// Retirement of a Sunshine generation (and shutdown with force): enqueue a render-thread callable that binds the fallback textures and drops the
    /// weight to 0, unless the bound colour is already the current snapshot's (the per-frame callable rebound the new generation). Enqueued before the
    /// acknowledgement's callable, so the old RIDs are never bound when they are freed.
    /// </summary>
    private void ReleaseCloudReflectionReceivers(bool force)
    {
        Resource? clouds = _clouds;
        if (clouds == null || _cloudReflectionColorTexture == null) return;
        RenderingServer.CallOnRenderThread(Callable.From(() => RunCloudReflectionRelease(clouds, force)));
    }

    private void RunCloudReflectionRelease(Resource clouds, bool force)
    {
        if (!GodotObject.IsInstanceValid(clouds)) return;
        if (!force)
        {
            CloudReflectionInputs input;
            lock (_cloudReflectionLock) input = _cloudReflectionInputs;
            Godot.Collections.Dictionary publication = clouds.Call(CloudReflectionPublicationName, input.Frame, input.Around, input.Across, input.Radius, input.HasFrame).AsGodotDictionary();
            Rid current = publication["color"].AsRid();
            if (current.IsValid && current == _cloudReflectionBoundColor) return;
        }
        BindCloudReflectionFallback(clouds);
        _cloudReflectionValidSince = -1.0;
        WriteCloudReflectionParams(new Vector4(0.0f, _cloudReflectionWrittenParams.Y, _cloudReflectionWrittenParams.Z, _cloudReflectionWrittenParams.W));
        lock (_cloudReflectionLock)
        {
            _cloudReflectionStatus.Weight = 0.0f;
            _cloudReflectionStatus.Valid = false;
            _cloudReflectionStatus.Reason = force ? "shutdown" : "retired";
            _cloudReflectionStatus.BoundColor = _cloudReflectionBoundColor;
            _cloudReflectionStatus.Params = _cloudReflectionWrittenParams;
        }
    }
}
