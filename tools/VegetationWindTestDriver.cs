using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Use real production prototypes in disposable GPU wind fixtures.</summary>
public partial class VegetationWindTestDriver : RefCounted
{
    public Mesh LoadGrassMesh() => (Mesh)typeof(RingStandingGrassLayer)
        .GetMethod("CreateBladeMesh", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, null)!;

    public Mesh LoadMeadowMesh() => RingGrassMeshFactory.Create();
    public Mesh LoadIntermediateGrassMesh() => RingGrassMeshFactory.CreateIntermediate();

    public Mesh? LoadAuthoredTreeMesh() => KayKitTreeMeshLoader.LoadNormalizedMesh(
        "res://resources/trees/kaykit_forest/Tree_2_A_Color1.gltf");

    public Godot.Collections.Dictionary LoadProceduralTreeMeshes()
    {
        RingTreeMeshSet meshes = RingTreeProceduralMeshFactory.Create(41031, 8, 18, true);
        return new Godot.Collections.Dictionary { ["trunk"] = meshes.Trunk, ["canopy"] = meshes.Canopy };
    }
}
