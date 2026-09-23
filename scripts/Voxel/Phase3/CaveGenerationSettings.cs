using System;
using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Versioned input to the designed-cave generator. The world terrain seed is
/// the sole entropy source; this resource contains only cave-generator
/// settings that may evolve independently of the terrain generator version.
/// </summary>
[GlobalClass]
public partial class CaveGenerationSettings : Resource
{
    public const int CurrentVersion = 1;

    [Export(PropertyHint.Range, "1,1,1")]
    public int ContractVersion { get; set; } = CurrentVersion;

    public void Validate()
    {
        if (ContractVersion != CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(ContractVersion),
                $"Unsupported cave-generation contract version {ContractVersion}.");
    }

    /// <summary>Captures this mutable editor resource as an immutable runtime contract.</summary>
    public CaveGenerationContract CaptureContract(int terrainSeed)
    {
        Validate();
        return CaveGenerationContract.Create(terrainSeed, this);
    }
}
