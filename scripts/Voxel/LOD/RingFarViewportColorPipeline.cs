using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Colour handoff between the scaled far SubViewports and the main viewport. The far content must reach the main
/// viewport as unclamped LINEAR radiance and be tonemapped exactly once, by the main viewport:
/// far SubViewports use HDR 2D (linear float target) with a LINEAR, unit-exposure tonemap, and a POST_SKY
/// compositor effect writes them into the main 3D HDR colour buffer where main drew no geometry. The root viewport
/// stays LDR 2D (HUD and screenshots unaffected). RefCounted so the
/// harness (tools/haze_consistency_gpu_test.gd) shares exactly this code.
/// </summary>
public partial class RingFarViewportColorPipeline : RefCounted
{
	/// <summary>Linear float target; the main viewport owns the display transform.</summary>
	public void ConfigureFarViewport(SubViewport farViewport)
	{
		farViewport.UseHdr2D = true;
		// Dither only matters for the 8-bit display transform, which no longer happens here.
		farViewport.UseDebanding = false;
	}

	/// <summary>
	/// Drops the display transform from a far environment. Scene-space settings (ambient, sky, reflections) are set
	/// by the caller; display-space ones (tonemap, exposure) are neutral so the main viewport applies them once.
	/// Glow/adjustments/fog/SSAO/SSR stay at their disabled defaults: glow and adjustments are display-space and run
	/// once in main over the composited image; fog and screen-space effects need the far depth the main pass lacks.
	/// </summary>
	public void ConfigureFarEnvironment(Godot.Environment farEnvironment)
	{
		farEnvironment.TonemapMode = Godot.Environment.ToneMapper.Linear;
		farEnvironment.TonemapExposure = 1.0f;
		farEnvironment.TonemapWhite = 1.0f;
		farEnvironment.GlowEnabled = false;
		farEnvironment.AdjustmentEnabled = false;
		farEnvironment.SsrEnabled = false;
		farEnvironment.SsaoEnabled = false;
		farEnvironment.SsilEnabled = false;
		farEnvironment.FogEnabled = false;
	}

	/// <summary>
	/// Main environment draws nothing for its background (black); the far image is injected into the 3D colour
	/// buffer by the backdrop effect, so the root viewport stays LDR 2D. The main sky resource is kept for ambient.
	/// </summary>
	public void ConfigureMainEnvironment(Godot.Environment mainEnvironment)
	{
		mainEnvironment.BackgroundMode = Godot.Environment.BGMode.Color;
		mainEnvironment.BackgroundColor = Colors.Black;
	}

	/// <summary>Adds the far-backdrop compositor effect first in the main compositor.</summary>
	public CompositorEffect? InstallBackdropEffect(WorldEnvironment mainWorld)
	{
		var effect = GD.Load<GDScript>("res://scripts/render/RingFarBackdropComposite.gd")?.New().AsGodotObject() as CompositorEffect;
		if (effect == null)
		{
			GD.PushWarning("Far backdrop compositor effect unavailable; far ring will not be visible.");
			return null;
		}
		mainWorld.Compositor ??= new Compositor();
		var effects = mainWorld.Compositor.CompositorEffects;
		using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
		effects.Insert(0, effect);
		mainWorld.Compositor.CompositorEffects = effects;
		return effect;
	}

	public void RemoveBackdropEffect(WorldEnvironment? mainWorld, CompositorEffect effect)
	{
		if (mainWorld != null && GodotObject.IsInstanceValid(mainWorld) && mainWorld.Compositor != null)
		{
			var effects = mainWorld.Compositor.CompositorEffects;
			using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
			if (effects.Remove(effect)) mainWorld.Compositor.CompositorEffects = effects;
		}
		effect.Dispose();
	}

	public void PublishBackdrop(CompositorEffect effect, Texture2D? far, Texture2D? regional, bool active)
		=> effect.Call("publish", far!, regional!, active);
}
