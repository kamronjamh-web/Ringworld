using System;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-2: TEST-ONLY hook behind the landscape harness's `--feature-ab=<feature>` cost case (and the look runs' `--feature-off=<feature>`) `feature_off` (tools/landscape_baseline_common.gd). It republishes the integer shader global
/// `ringworld_world_features` with one bit cleared and restores it afterwards, so a same-boot A-B-A pair measures what a shader-side world feature costs.
/// It never touches `WorldGeneration.Current`, the recipe, a save or a setting, so C# readers (tile builders, the grass builder, proxy sectors) keep seeing the world's real features.
/// Only the landscape tools call it, and Apply refuses unless `--feature-ab=` or `--feature-off=` is on the command line, so no menu, setting or normal game start can reach it.
/// EXTENSION POINT for placement features (T5 scatter, T6 vegetation): their builders read the C# mask, so a placement A-B also needs a tile rebuild and a streaming-idle wait
/// (landscape_baseline_common.gd _apply_extra "feature_off" is where to add it); the shader-side part here stays the same.
/// </summary>
public partial class LandscapeFeatureAbBridge : RefCounted
{
    static bool IsFlag(string arg) => arg.StartsWith("--feature-ab=", StringComparison.Ordinal) || arg.StartsWith("--feature-off=", StringComparison.Ordinal);

    static bool Armed()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (IsFlag(arg)) return true;
        foreach (string word in (System.Environment.GetEnvironmentVariable("LANDSCAPE_HARNESS_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (IsFlag(word)) return true;
        return false;
    }

    /// <summary>Resolves `<feature id>` (a production ledger ID) or `bit:<n>` (a temporary test bit, 0-30). Returns {ok, id, bit, message}.</summary>
    public Godot.Collections.Dictionary Resolve(string spec)
    {
        var result = new Godot.Collections.Dictionary { ["ok"] = false, ["id"] = spec, ["bit"] = -1, ["message"] = "" };
        if (spec.StartsWith("bit:", StringComparison.Ordinal))
        {
            if (int.TryParse(spec[4..], out int raw) && raw is >= 0 and <= 30) { result["ok"] = true; result["bit"] = raw; }
            else result["message"] = $"'{spec}' is not bit:<0-30>";
            return result;
        }
        var info = WorldGenerationLedger.Production.Features.FirstOrDefault(f => f.Id == spec);
        if (info == null)
        {
            result["message"] = $"feature '{spec}' is not in the production ledger (known: {string.Join(", ", WorldGenerationLedger.Production.Features.Select(f => f.Id))}); use bit:<n> for a temporary test bit";
            return result;
        }
        result["ok"] = true;
        result["bit"] = info.Bit;
        return result;
    }

    /// <summary>The mask the world published (what builders see).</summary>
    public int WorldMask() => WorldGeneration.Current.FeatureMask;

    static int _published = -1;

    /// <summary>The mask this hook last published to the shader global (the world mask until it first publishes). The engine's GlobalShaderParameterGet is editor-only (it logs an error outside the editor), so there is no real readback.</summary>
    public int ShaderMask() => _published >= 0 ? _published : WorldMask();

    /// <summary>Sets the shader global to the world mask with `bit` forced to `on`. Returns the mask now readable by shaders, or -1 when the hook is not armed.</summary>
    public int Apply(int bit, bool on)
    {
        if (!Armed()) { GD.PushError("LandscapeFeatureAbBridge is test-only and needs --feature-ab= or --feature-off="); return -1; }
        int mask = WorldMask();
        mask = on ? mask | (1 << bit) : mask & ~(1 << bit);
        RenderingServer.GlobalShaderParameterSet(WorldGeneration.ShaderGlobalName, mask);
        _published = mask;
        return mask;
    }

    /// <summary>Puts the shader global back to the world mask.</summary>
    public int Restore()
    {
        int mask = WorldMask();
        RenderingServer.GlobalShaderParameterSet(WorldGeneration.ShaderGlobalName, mask);
        _published = mask;
        return mask;
    }
}
