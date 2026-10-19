using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    /// <summary>True while underground mode has stopped the far viewports. Nothing is freed and Enabled is never toggled.</summary>
    public bool RenderSuspended { get; private set; }

    /// <summary>
    /// Stops both far SubViewports and hides the backdrop layer. Resume restores UpdateMode.Always and the next
    /// _Process re-syncs camera and lighting; the owned viewports, cameras and textures are retained throughout.
    /// </summary>
    public void SetRenderSuspended(bool suspended)
    {
        if (RenderSuspended == suspended) return;
        RenderSuspended = suspended;
        SubViewport.UpdateMode mode = suspended ? SubViewport.UpdateMode.Disabled : SubViewport.UpdateMode.Always;
        if (_farViewport != null && GodotObject.IsInstanceValid(_farViewport)) _farViewport.RenderTargetUpdateMode = mode;
        if (_regionalFarViewport != null && GodotObject.IsInstanceValid(_regionalFarViewport)) _regionalFarViewport.RenderTargetUpdateMode = mode;
        if (_backdropComposeViewport != null && GodotObject.IsInstanceValid(_backdropComposeViewport)) _backdropComposeViewport.RenderTargetUpdateMode = mode;
        if (_backdropEffect != null) _colorPipeline.PublishBackdrop(_backdropEffect, _backdropTexture, _backdropRegionalTexture, !suspended);
    }
}
