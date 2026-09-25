using System;
using System.IO;
using Godot;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo;

/// <summary>Focused pure-data coverage for sparse, surface-only ring terrain edits.</summary>
public partial class RingSurfaceHeightFieldTest : RefCounted
{
    public bool Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ring_surface_height_field_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            VerifyWrapBrush(); VerifyAcrossEdgeAndInvalidInput(); VerifyBoundsAndInterpolation(); VerifyMegaCoordinates(); VerifyEditWindows();
            VerifyPersistence(directory);
            GD.Print("RING_SURFACE_HEIGHT_FIELD=PASS");
            return true;
        }
        catch (Exception exception) { GD.PushError("RING_SURFACE_HEIGHT_FIELD=FAIL " + exception.Message); return false; }
        finally { TryDeleteTestDirectory(directory); }
    }

    private static RingSurfaceHeightField Create(ulong hash = 123) => new(hash, 16, 6, -2, 10, (around, across) => around + across, 2);

    private static void VerifyWrapBrush()
    {
        RingSurfaceHeightField field = new(1, 16, 6, -2, 10, (_, _) => 0, 2);
        Require(field.ApplyBrush(0, 4, 3, 2), "seam brush was rejected");
        Require(field.GetSample(14, 2) > 0, "seam brush did not affect wrapped sample");
        Require(field.GetSample(-2, 2) == field.GetSample(14, 2), "negative around did not wrap");
    }

    private static void VerifyAcrossEdgeAndInvalidInput()
    {
        RingSurfaceHeightField field = Create();
        Require(field.ApplyBrush(4, 0, 2, 1), "edge brush was rejected");
        Require(field.ChangedSampleCount > 0, "edge brush did not write samples");
        Require(!field.ApplyBrush(4, -0.1, 2, 1) && !field.ApplyBrush(double.NaN, 0, 2, 1) && !field.ApplyBrush(0, 0, 0, 1) && !field.ApplyBrush(0, 0, 2, float.NaN), "invalid brush input was accepted");
        RequireThrows(() => field.GetSample(0, -1));
    }

    private static void VerifyBoundsAndInterpolation()
    {
        RingSurfaceHeightField field = Create();
        Require(field.ApplyBrush(4, 4, 2, 100), "height clamp brush was rejected");
        Require(field.GetSample(4, 4) == 10, "height ceiling was not applied");
        RingSurfaceHeightField plane = new(9, 8, 4, -100, 100, (a, x) => a * 10 + x, 2);
        Require(Math.Abs(plane.SampleHeight(1, 1) - 11f) < .001f, "bilinear interpolation is wrong");
        Require(Math.Abs(plane.SampleHeight(7, 0) - 30f) < .001f, "interpolation did not wrap at seam");
    }

    private static void VerifyMegaCoordinates()
    {
        const long circumference = 471_238_898_048;
        RingSurfaceHeightField certified = new(76, 31_415_936, 318_000, -1, 1, (_, _) => 0);
        Require(certified.CircumferenceSamples == 15_707_968 && certified.WidthSamples == 159_000, "certified metre dimensions were not preserved");
        Require(certified.GetSample(0, 159_000) == 0, "certified width midpoint is not a valid lattice coordinate");
        RingSurfaceHeightField field = new(77, circumference, 2, -1, 1, (_, _) => 0);
        Require(field.ApplyBrush(circumference - 2, 0, 3, 1), "mega-ring brush was rejected");
        Require(field.GetSample(-2, 0) > 0 && field.GetSample(circumference - 2, 0) > 0, ">2^32 around coordinate aliased");
    }

    private static void VerifyPersistence(string directory)
    {
        string path = Path.Combine(directory, RingSurfaceEditStore.DefaultPath);
        RingSurfaceHeightField source = Create(); source.ApplyBrush(4, 4, 2, 2);
        Require(source.Save(path).Success, "save failed");
        RingSurfaceHeightField loaded = Create(); Require(loaded.Load(path).Success && loaded.ChangedSampleCount == source.ChangedSampleCount, "valid reload failed");
        float preserved = loaded.GetSample(4, 4); RingSurfaceHeightField mismatch = Create(999);
        Require(!mismatch.Load(path).Success && mismatch.ChangedSampleCount == 0, "identity mismatch changed state");
        RingSurfaceHeightField dimensionMismatch = new(123, 18, 6, -2, 10, (around, across) => around + across, 2);
        Require(!dimensionMismatch.Load(path).Success && dimensionMismatch.ChangedSampleCount == 0, "dimension mismatch changed state");
        File.WriteAllText(path, "not json");
        Require(!loaded.Load(path).Success && loaded.GetSample(4, 4) == preserved, "corrupt reload changed state");
    }

    private static void VerifyEditWindows()
    {
        RingSurfaceHeightField field = new(3, 16, 6, -2, 10, (_, _) => 0, 2);
        Require(field.ApplyBrush(0, 0, 3, 1), "window setup brush was rejected");
        Require(Count(field.GetEditsInWindow(14, 2, 0, 2)) == field.ChangedSampleCount, "wrapped edit window missed seam edits");
        Require(Count(field.GetEditsInWindow(0, 14, 0, 2)) == field.ChangedSampleCount, "full-circumference edit window missed edits");
    }

    private static void RequireThrows(Action action) { try { action(); } catch (ArgumentOutOfRangeException) { return; } throw new InvalidOperationException("expected bounds exception"); }
    private static int Count(System.Collections.Generic.IEnumerable<RingSurfaceHeightEdit> edits) { int count = 0; foreach (RingSurfaceHeightEdit _ in edits) count++; return count; }
    private static void TryDeleteTestDirectory(string directory)
    {
        try
        {
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullDirectory = Path.GetFullPath(directory);
            if (!fullDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullDirectory).StartsWith("ring_surface_height_field_", StringComparison.Ordinal)) return;
            if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, true);
        }
        catch { }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
