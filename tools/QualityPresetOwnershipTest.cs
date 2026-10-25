using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.LOD;
namespace RingworldTechDemo.Tools;

/// <summary>
/// The LOD profile owns terrain worker counts (docs/quality-presets-plan.md O13): applying a menu preset sets
/// MaxConcurrentWorkerBuilds to the profile's ConcurrentWorkers, and GetProfileConcurrentWorkers reports the same value.
/// Run: Godot --headless --path . --script res://tools/quality_preset_ownership_test.gd. Prints QP_OWNERSHIP=PASS.
/// </summary>
public partial class QualityPresetOwnershipTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        RingTerrainLodCoordinator? coordinator = null;
        try
        {
            string[] files = { "low", "medium", "high", "very_high", "ultra_video", "cinematic_5090" };
            var profiles = files.Select(f => ResourceLoader.Load<RingLodQualityProfile>($"res://resources/voxel/ring_lod_{f}.tres")).ToArray();
            for (int i = 0; i < profiles.Length; i++) Check(profiles[i] != null, $"profile {files[i]} failed to load");
            if (failures.Count == 0)
            {
                coordinator = new RingTerrainLodCoordinator
                {
                    LowProfile = profiles[0],
                    MediumProfile = profiles[1],
                    HighProfile = profiles[2],
                    VeryHighProfile = profiles[3],
                    UltraVideoProfile = profiles[4],
                    Cinematic5090Profile = profiles[5]
                };
                (Engine.GetMainLoop() as SceneTree)!.Root.AddChild(coordinator);
                for (int p = 0; p <= 4; p++)
                {
                    var preset = (RingTerrainLodCoordinator.QualityPreset)p;
                    int expected = Math.Clamp(profiles[p].ConcurrentWorkers, 1, 32);
                    int reported = coordinator.GetProfileConcurrentWorkers(preset);
                    coordinator.ApplyUserQualityPreset(preset);
                    Check(reported == expected, $"preset {p}: GetProfileConcurrentWorkers={reported} want {expected}");
                    Check(coordinator.MaxConcurrentWorkerBuilds == expected, $"preset {p}: MaxConcurrentWorkerBuilds={coordinator.MaxConcurrentWorkerBuilds} want {expected}");
                    Check(coordinator.CurrentPreset == preset, $"preset {p}: CurrentPreset={coordinator.CurrentPreset}");
                }
                coordinator.ApplyUserQualityPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh);
                Check(coordinator.CurrentPresetName == "Very High", $"VeryHigh CurrentPresetName='{coordinator.CurrentPresetName}' want 'Very High'");
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        finally { if (coordinator != null && GodotObject.IsInstanceValid(coordinator)) { coordinator.GetParent()?.RemoveChild(coordinator); coordinator.Free(); } }
        if (failures.Count != 0) { Console.Error.WriteLine("QP_OWNERSHIP=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("QP_OWNERSHIP=PASS");
        return 0;
    }
}
