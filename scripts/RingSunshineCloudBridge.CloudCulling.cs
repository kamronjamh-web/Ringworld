using Godot;

namespace RingworldTechDemo;

public partial class RingSunshineCloudBridge
{
    [Export] public bool SkipEmptyCloudMaskSamples { get; set; } = true;
    [Export] public bool FarCloudPatchCullingEnabled { get; set; } = true;

    private void ApplyCloudCullingTuning()
    {
        _clouds?.Set("skip_empty_cloud_mask_samples", SkipEmptyCloudMaskSamples);
        _scaledFarViewport?.ConfigureFarCloudPatchCulling(FarCloudPatchCullingEnabled);
    }
}
