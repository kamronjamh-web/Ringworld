using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>One absolute surface-height override at a canonical lattice coordinate.</summary>
public readonly record struct RingSurfaceHeightEdit(long AroundMeters, int AcrossMeters, float Height);

/// <summary>Portable, versioned sparse edit data for the surface-only terrain prototype.</summary>
public sealed record RingSurfaceEditSnapshot(
    int FormatVersion,
    ulong TerrainDefinitionHash,
    long CircumferenceMeters,
    int WidthMeters,
    float SpacingMeters,
    float MinimumHeight,
    float MaximumHeight,
    ulong Revision,
    RingSurfaceHeightEdit[] Edits);

public readonly record struct RingSurfaceEditStoreResult(bool Success, string Error)
{
    public static RingSurfaceEditStoreResult Ok => new(true, string.Empty);
}

/// <summary>JSON persistence for sparse surface heights. It has no knowledge of underground terrain data.</summary>
public static class RingSurfaceEditStore
{
    public const int FormatVersion = 1;
    public const string DefaultPath = "surface_terrain3d_edits_v1.json";

    public static RingSurfaceEditStoreResult Save(string path, RingSurfaceEditSnapshot snapshot)
    {
        string? temporaryPath = null;
        try
        {
            Validate(snapshot);
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Save directory is missing.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
            File.Move(temporaryPath, fullPath, true);
            temporaryPath = null;
            return RingSurfaceEditStoreResult.Ok;
        }
        catch (Exception exception) { return new(false, exception.Message); }
        finally { if (temporaryPath != null) TryDelete(temporaryPath); }
    }

    public static RingSurfaceEditStoreResult Load(string path, out RingSurfaceEditSnapshot snapshot)
    {
        snapshot = default!;
        try
        {
            RingSurfaceEditSnapshot? loaded = JsonSerializer.Deserialize<RingSurfaceEditSnapshot>(File.ReadAllText(path));
            if (loaded == null) throw new InvalidDataException("Surface edit save is empty.");
            Validate(loaded);
            snapshot = loaded;
            return RingSurfaceEditStoreResult.Ok;
        }
        catch (Exception exception) { return new(false, exception.Message); }
    }

    public static void Validate(RingSurfaceEditSnapshot snapshot)
    {
        if (snapshot.FormatVersion != FormatVersion || snapshot.TerrainDefinitionHash == 0 ||
            snapshot.CircumferenceMeters <= 0 || snapshot.WidthMeters <= 0 ||
            !float.IsFinite(snapshot.SpacingMeters) || snapshot.SpacingMeters <= 0 || snapshot.SpacingMeters != MathF.Round(snapshot.SpacingMeters) || snapshot.SpacingMeters > int.MaxValue ||
            !float.IsFinite(snapshot.MinimumHeight) || !float.IsFinite(snapshot.MaximumHeight) ||
            snapshot.MinimumHeight > snapshot.MaximumHeight || snapshot.Edits == null)
            throw new InvalidDataException("Surface edit snapshot identity is invalid.");
        int spacing = checked((int)snapshot.SpacingMeters);
        if (snapshot.CircumferenceMeters % spacing != 0 || snapshot.WidthMeters % spacing != 0)
            throw new InvalidDataException("Surface edit dimensions are not aligned to spacing.");
        var seen = new HashSet<(long, int)>();
        foreach (RingSurfaceHeightEdit edit in snapshot.Edits)
            if (edit.AroundMeters < 0 || edit.AroundMeters >= snapshot.CircumferenceMeters || edit.AcrossMeters < 0 || edit.AcrossMeters >= snapshot.WidthMeters ||
                edit.AroundMeters % spacing != 0 || edit.AcrossMeters % spacing != 0 ||
                !float.IsFinite(edit.Height) || edit.Height < snapshot.MinimumHeight || edit.Height > snapshot.MaximumHeight || !seen.Add((edit.AroundMeters, edit.AcrossMeters)))
                throw new InvalidDataException("Surface edit snapshot contains an invalid edit.");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
