using System;

namespace RingworldTechDemo.Voxel.Integration;

/// <summary>Shared fail-closed gate for persisted underground recovery.</summary>
public static class UndergroundCheckpointSafetyPolicy
{
    public static bool IsWithinStructuralBounds(
        double across,
        double vertical,
        double bodyHalfWidth,
        double ringAcrossWidth,
        double lowerHullTop,
        double structuralWallTop) =>
        double.IsFinite(across)
        && double.IsFinite(vertical)
        && double.IsFinite(bodyHalfWidth)
        && double.IsFinite(ringAcrossWidth)
        && double.IsFinite(lowerHullTop)
        && double.IsFinite(structuralWallTop)
        && bodyHalfWidth >= 0.0
        && ringAcrossWidth >= bodyHalfWidth * 2.0
        && lowerHullTop < structuralWallTop
        && across >= bodyHalfWidth
        && across <= ringAcrossWidth - bodyHalfWidth
        && vertical > lowerHullTop
        && vertical < structuralWallTop;

    public static bool CanResume(bool cavesEnabled, bool withinStructuralBounds, bool bodyClear, bool movementResident) =>
        cavesEnabled && withinStructuralBounds && bodyClear && movementResident;

    public static bool RunSelfTest(out string summary)
    {
        try
        {
            if (CanResume(false, true, true, true)) throw new InvalidOperationException("disabled caves accepted checkpoint");
            if (CanResume(true, false, true, true)) throw new InvalidOperationException("exterior checkpoint accepted");
            if (CanResume(true, true, false, true)) throw new InvalidOperationException("solid/body-overlap checkpoint accepted");
            if (CanResume(true, true, true, false)) throw new InvalidOperationException("unresident checkpoint resumed");
            if (!CanResume(true, true, true, true)) throw new InvalidOperationException("safe resident checkpoint rejected");
            if (!IsWithinStructuralBounds(50.0, 12.0, 0.5, 100.0, -100.0, 40.0))
                throw new InvalidOperationException("valid above-zero underground structural position rejected");
            if (IsWithinStructuralBounds(0.25, -20.0, 0.5, 100.0, -100.0, 40.0)
                || IsWithinStructuralBounds(50.0, -100.0, 0.5, 100.0, -100.0, 40.0)
                || IsWithinStructuralBounds(50.0, 40.0, 0.5, 100.0, -100.0, 40.0))
                throw new InvalidOperationException("structural boundary position accepted");
            summary = "disabled/exterior/solid/unresident rejection, full structural bounds, and safe resident resume passed";
            return true;
        }
        catch (Exception error) { summary = error.Message; return false; }
    }
}
