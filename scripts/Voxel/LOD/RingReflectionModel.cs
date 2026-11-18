using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Transitional switch for the reflection model (docs/reflections-plan.md 10.2). Model 0 keeps today's black main
/// reflections; model 1 makes the main Environment reflect the Sky. Mirrored to the shader global
/// ringworld_reflection_model. RefCounted so GDScript can reach it; the instance methods forward to the statics.
/// </summary>
public partial class RingReflectionModel : RefCounted
{
	private const string GlobalName = "ringworld_reflection_model";
	private static float _model;
	private static Godot.Environment? _mainEnvironment;

	public static float Model => _model;

	/// <summary>The reflected source the main Environment uses at the given model value.</summary>
	public static Godot.Environment.ReflectionSource SourceFor(float model)
		=> Sanitize(model) >= 0.5f ? Godot.Environment.ReflectionSource.Sky : Godot.Environment.ReflectionSource.Bg;

	public static void RegisterMain(Godot.Environment? mainEnvironment)
	{
		_mainEnvironment = mainEnvironment;
		Apply();
	}

	public static void SetModelStatic(float model)
	{
		_model = Sanitize(model);
		RenderingServer.GlobalShaderParameterSet(GlobalName, _model);
		Apply();
	}

	private static float Sanitize(float model) => float.IsFinite(model) && model >= 0.5f ? 1.0f : 0.0f;

	private static void Apply()
	{
		if (_mainEnvironment == null || !GodotObject.IsInstanceValid(_mainEnvironment)) return;
		_mainEnvironment.ReflectedLightSource = SourceFor(_model);
	}

	public float GetModel() => Model;
	public void SetModel(float model) => SetModelStatic(model);
	public void Register(Godot.Environment? mainEnvironment) => RegisterMain(mainEnvironment);
}
