using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Cloud raymarch cost controls (docs/cloud-perf-methods-plan.md, Method 2). Every setting defaults off; with all of them off the
/// Sunshine resource renders exactly as before. The values are pushed into the Sunshine resource from ApplyTuningToResource, so the
/// grave-key menu, presets and persisted overrides reach it through ApplyTemporalQualityTuning like the other temporal settings.
/// </summary>
public partial class RingSunshineCloudBridge
{
    [ExportGroup("Cloud performance")]
    /// <summary>4-phase +-0.25 pixel ray jitter that anti-aliases the Half / Quarter cloud resolutions through the temporal history.</summary>
    [Export] public bool CloudResolutionJitter { get; set; } = false;
    /// <summary>0 off, 1 = 2x2, 2 = 4x4: march one pixel of every block per frame and reconstruct the rest.</summary>
    [Export(PropertyHint.Range, "0,2,1")] public int CloudInterleave { get; set; } = 1;
    /// <summary>Marched pixels keep history decay^(n*n*value); higher values refresh interleaved pixels faster.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudInterleaveHistoryCompensation { get; set; } = .5f;
    /// <summary>Variance-clip width (sigma) of held history against the neighbouring current samples.</summary>
    [Export(PropertyHint.Range, "0.5,6,0.05")] public float CloudInterleaveHistoryClamp { get; set; } = 2;
    /// <summary>Camera reprojection motion (cloud raster pixels) above which a frame marches every pixel.</summary>
    [Export(PropertyHint.Range, "1,200,0.5")] public float CloudInterleaveMaxPixelMotion { get; set; } = 24;

    /// <summary>
    /// Runtime override (not persisted with the cloud defaults; the render-resolution settings own it): while the viewport upscales
    /// with FSR2, Sunshine gets CloudInterleave 0 and CloudInterleave itself keeps the user's value for when FSR2 is off again.
    /// </summary>
    public bool DisableCloudInterleaveUnderFsr { get; set; } = true;

    /// <summary>Runtime flag owned by the render-resolution settings: resample the centred cloud / far-depth textures at the FSR2 / TAA jitter offset.</summary>
    public bool CompositorJitterCompensation { get; set; } = true;

    private void ApplyCloudPerformanceTuning()
    {
        CloudInterleave = Mathf.Clamp(CloudInterleave, 0, 2);
        CloudInterleaveHistoryCompensation = SanitizeFinite(CloudInterleaveHistoryCompensation, .5f, 0, 1);
        CloudInterleaveHistoryClamp = SanitizeFinite(CloudInterleaveHistoryClamp, 2, .5f, 6);
        CloudInterleaveMaxPixelMotion = SanitizeFinite(CloudInterleaveMaxPixelMotion, 24, 1, 200);
        if (_clouds == null) return;
        _clouds.Set("resolution_jitter_enabled", CloudResolutionJitter);
        _clouds.Set("engine_jitter_compensation", CompositorJitterCompensation);
        bool fsrActive = DisableCloudInterleaveUnderFsr && GetViewport().Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr2;
        _clouds.Set("cloud_interleave", fsrActive ? 0 : CloudInterleave);
        _clouds.Set("cloud_interleave_history_compensation", CloudInterleaveHistoryCompensation);
        _clouds.Set("cloud_interleave_history_clamp", CloudInterleaveHistoryClamp);
        _clouds.Set("cloud_interleave_max_pixel_motion", CloudInterleaveMaxPixelMotion);
    }
}
