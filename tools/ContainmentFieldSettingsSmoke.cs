using System;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the containment field preferences (defaults, round-trip, clamps, reset, invalid path, Apply).</summary>
public partial class ContainmentFieldSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/containment_field_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static bool IsDefault(GameplayContainmentFieldSettings s) =>
        Mathf.IsEqualApprox(s.ShimmerStrength, 1) && Mathf.IsEqualApprox(s.ShimmerRangeKm, 3) && Mathf.IsEqualApprox(s.SheenStrength, 1) && Mathf.IsEqualApprox(s.LatticeStrength, 1) && Mathf.IsEqualApprox(s.RippleStrength, 1);

    private static void Run(string path)
    {
        var first = new GameplayContainmentFieldDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing containment field preferences did not use defaults");
        Require(first.Save(), "clean defaults should save as a no-op");
        Require(first.SetShimmerStrength(.4f) && !first.SetShimmerStrength(.4f), "Set did not report the changed flag");
        first.SetShimmerRangeKm(7); first.SetSheenStrength(1.5f); first.SetRippleStrength(.7f);
        Require(first.Dirty && first.Save() && !first.Dirty, "containment field preferences did not save");
        var second = new GameplayContainmentFieldDefaults(path); second.Load();
        var s = second.Settings;
        Require(Mathf.IsEqualApprox(s.ShimmerStrength, .4f) && Mathf.IsEqualApprox(s.ShimmerRangeKm, 7) && Mathf.IsEqualApprox(s.SheenStrength, 1.5f) && Mathf.IsEqualApprox(s.RippleStrength, .7f), "preferences did not round-trip");

        // Setter clamps and NaN fallback.
        second.SetShimmerStrength(99); second.SetShimmerRangeKm(0); second.SetSheenStrength(float.NaN); second.SetRippleStrength(50);
        s = second.Settings;
        var (sMin, sMax) = GameplayContainmentFieldDefaults.RangeOf(nameof(GameplayContainmentFieldSettings.ShimmerStrength));
        var (rMin, rMax) = GameplayContainmentFieldDefaults.RangeOf(nameof(GameplayContainmentFieldSettings.ShimmerRangeKm));
        var (hMin, hMax) = GameplayContainmentFieldDefaults.RangeOf(nameof(GameplayContainmentFieldSettings.SheenStrength));
        Require(Mathf.IsEqualApprox(sMin, 0) && Mathf.IsEqualApprox(sMax, 2) && Mathf.IsEqualApprox(rMin, .5f) && Mathf.IsEqualApprox(rMax, 10)
            && Mathf.IsEqualApprox(hMin, 0) && Mathf.IsEqualApprox(hMax, 2), "RangeOf did not match the design ranges");
        Require(Mathf.IsEqualApprox(s.ShimmerStrength, sMax) && Mathf.IsEqualApprox(s.ShimmerRangeKm, rMin), "setter bounds were not enforced");
        Require(Mathf.IsEqualApprox(s.SheenStrength, 1), "NaN did not fall back to the default");
        Require(Mathf.IsEqualApprox(s.RippleStrength, GameplayContainmentFieldDefaults.RangeOf(nameof(GameplayContainmentFieldSettings.RippleStrength)).Max), "ripple strength was not clamped");

        using var dirty = new GameplayContainmentFieldSettings { ShimmerStrength = -5, ShimmerRangeKm = float.PositiveInfinity, SheenStrength = 1e9f };
        using var clean = GameplayContainmentFieldDefaults.Sanitize(dirty);
        Require(Mathf.IsEqualApprox(clean.ShimmerStrength, 0) && Mathf.IsEqualApprox(clean.ShimmerRangeKm, 3) && Mathf.IsEqualApprox(clean.SheenStrength, 2), "Sanitize bounds were not enforced");

        second.Reset();
        Require(second.Save(), "reset did not save");
        var reset = new GameplayContainmentFieldDefaults(path); reset.Load();
        Require(IsDefault(reset.Settings), "reset did not restore defaults");

        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayContainmentFieldSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayContainmentFieldDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetShimmerStrength(.5f);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid preference path did not fail safely");
        bool threw = false;
        try { invalid.SetFloat("NoSuchField", 1); } catch (ArgumentException) { threw = true; }
        Require(threw, "unknown field did not throw");

        // An older save lacks a field: strip its line and the load must give that default while keeping the rest.
        var old = new GameplayContainmentFieldDefaults(path); old.Load();
        old.SetShimmerStrength(.6f); old.SetShimmerRangeKm(8); old.SetSheenStrength(.2f); old.SetLatticeStrength(1.5f); old.SetRippleStrength(.3f);
        Require(old.Save(), "old-save fixture did not save");
        string full = ProjectSettings.GlobalizePath(path);
        string[] lines = System.IO.File.ReadAllLines(full);
        var kept = new System.Collections.Generic.List<string>();
        foreach (string line in lines) if (!line.StartsWith("ShimmerRangeKm =", StringComparison.Ordinal) && !line.StartsWith("LatticeStrength =", StringComparison.Ordinal) && !line.StartsWith("RippleStrength =", StringComparison.Ordinal)) kept.Add(line);
        Require(kept.Count < lines.Length, "old-save fixture had no ShimmerRangeKm line to remove");
        System.IO.File.WriteAllLines(full, kept);
        var legacy = new GameplayContainmentFieldDefaults(path); legacy.Load();
        Require(Mathf.IsEqualApprox(legacy.Settings.ShimmerStrength, .6f) && Mathf.IsEqualApprox(legacy.Settings.ShimmerRangeKm, 3) && Mathf.IsEqualApprox(legacy.Settings.SheenStrength, .2f) && Mathf.IsEqualApprox(legacy.Settings.LatticeStrength, 1) && Mathf.IsEqualApprox(legacy.Settings.RippleStrength, 1),
            "an older save did not load the missing field default");

        // Defaults equal the node constants, and Apply pushes the three properties onto a real node.
        Require(Mathf.IsEqualApprox(new GameplayContainmentFieldSettings().ShimmerStrength, RingContainmentField.DefaultShimmerStrength)
            && Mathf.IsEqualApprox(new GameplayContainmentFieldSettings().ShimmerRangeKm, RingContainmentField.DefaultShimmerRangeKm)
            && Mathf.IsEqualApprox(new GameplayContainmentFieldSettings().SheenStrength, RingContainmentField.DefaultSheenStrength) && Mathf.IsEqualApprox(new GameplayContainmentFieldSettings().LatticeStrength, RingContainmentField.DefaultLatticeStrength)
            && Mathf.IsEqualApprox(new GameplayContainmentFieldSettings().RippleStrength, RingContainmentField.DefaultRippleStrength), "defaults differ from RingContainmentField.Default*");
        var node = new RingContainmentField { LoadSavedDefaults = false };
        try
        {
            legacy.Apply(node);
            Require(Mathf.IsEqualApprox(node.ShimmerStrength, .6f) && Mathf.IsEqualApprox(node.ShimmerRangeKm, 3) && Mathf.IsEqualApprox(node.SheenStrength, .2f) && Mathf.IsEqualApprox(node.LatticeStrength, 1) && Mathf.IsEqualApprox(node.RippleStrength, 1), "Apply did not set the field properties");
            legacy.Apply(new Node()); // other node types are ignored
        }
        finally { node.Free(); }
        old.Reset(); old.Save();
    }
}
