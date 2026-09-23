using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class ImportedForestCollisionRegressionTest : RefCounted
{
	public bool Run()
	{
		var layer = new RingBiomeDecorationLayer { Enabled = false };
		string result = layer.RunImportedForestCollisionRegression();
		if (!result.StartsWith("PASS:")) GD.PushError("IMPORTED_FOREST_COLLISION_REGRESSION=" + result);
		else GD.Print("IMPORTED_FOREST_COLLISION_REGRESSION=" + result);
		layer.Free();
		return result.StartsWith("PASS:");
	}
}
