using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-2: contract test of the dressing pose fixture (tools/fixtures/landscape_poses_dressing_s1337.json) on the pinned generation 1 world. Run via tools/landscape_dressing_poses_test.gd.
/// Flags (after `--`): --capture (write the fixture, refuses to overwrite), --find (scan and print the differences from the committed fixture), --fixture=&lt;path&gt; (validate another file).
/// Prints LANDSCAPE_DRESSING_POSES=PASS.
/// </summary>
public partial class LandscapeDressingPosesTest : RefCounted
{
    static string Arg(string prefix)
    {
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return "";
    }

    static bool Flag(string flag) => Array.IndexOf(OS.GetCmdlineUserArgs(), flag) >= 0;

    public bool Run()
    {
        var failures = new List<string>();
        try { RunChecks(failures); }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PushError("LANDSCAPE_DRESSING_POSES=FAIL " + failure);
        return failures.Count == 0;
    }

    static void RunChecks(List<string> failures)
    {
        var clock = Stopwatch.StartNew();
        LandscapeWorld.Worker[] workers = LandscapeWorld.CreateWorkers(LandscapeWorld.DefaultWorkerCount);
        try
        {
            string fixtureArg = Arg("--fixture=");
            string fixturePath = fixtureArg.Length > 0 ? fixtureArg : ProjectSettings.GlobalizePath(LandscapeDressingPoses.FixturePath);
            if (Flag("--capture") || Flag("--find"))
            {
                List<LandscapePose> found = LandscapeDressingPoses.Find(workers, s => GD.Print("LANDSCAPE_DRESSING_FIND " + s));
                GD.Print($"LANDSCAPE_DRESSING_FIND finished in {clock.Elapsed.TotalSeconds:F1} s with {found.Count} poses");
                if (Flag("--capture"))
                {
                    if (File.Exists(fixturePath)) failures.Add("--capture refuses to overwrite the existing fixture " + fixturePath);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!); File.WriteAllText(fixturePath, LandscapePoseFinder.ToJson(found)); GD.Print("LANDSCAPE_DRESSING_CAPTURE wrote " + fixturePath); }
                }
                else if (File.Exists(fixturePath))
                {
                    List<string> diffs = LandscapePoseFinder.Diff(found, LandscapePoseFinder.ReadFixture(fixturePath));
                    GD.Print("LANDSCAPE_DRESSING_FIND differences from the committed fixture: " + (diffs.Count == 0 ? "none" : ""));
                    foreach (string diff in diffs) GD.Print("LANDSCAPE_DRESSING_FIND   " + diff);
                }
            }
            if (!File.Exists(fixturePath)) failures.Add("fixture missing: " + fixturePath + " (run with --capture once)");
            else
            {
                LandscapePoseFinder.Fixture fixture = LandscapePoseFinder.ReadFixture(fixturePath);
                foreach (string failure in LandscapeDressingPoses.Validate(fixture, workers[0])) failures.Add(failure);
                GD.Print($"LANDSCAPE_DRESSING_POSES fixture rows={fixture.Poses.Count} hash={fixture.DefinitionHash}");
            }
        }
        finally { foreach (var worker in workers) worker.Dispose(); }
        GD.Print($"LANDSCAPE_DRESSING_POSES finished in {clock.Elapsed.TotalSeconds:F1} s");
    }
}
