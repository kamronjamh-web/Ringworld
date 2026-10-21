using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Pure planning math for the terrain proxy chain: screen-space bands, the
/// projected-error invariant, terminal-tier selection and altitude activation.
/// Contains no scene or node state so it can be exercised headlessly.
/// </summary>
/// <remarks>
/// Invariant for each rendered tier T (spacing s, projection scale P, pixel
/// budget e): the tier is visible on the slant interval [previous far handoff,
/// own far handoff]; it is needed while its far handoff exceeds the viewer
/// altitude h; its projected error is s * P / max(near, h).
/// Proxy tiers must satisfy error &lt;= 2.5 e, the global/far backdrop &lt;= 5 e.
/// </remarks>
internal static class RingTerrainLodChainPlanner
{
    public const float ProxyErrorFactor = 2.5f;
    public const float BackdropErrorFactor = 5.0f;
    // A tier is pre-warmed when the viewer descends below 1.25x its far handoff
    // and released only after climbing above 1.40x, so altitude noise cannot flicker it.
    public const float ActivateAltitudeRatio = 1.25f;
    public const float DeactivateAltitudeRatio = 1.40f;
    // Highest terrain surface (m above sea level) assumed when trimming windows
    // by altitude; keeps tall peaks beside a high viewer inside the window.
    public const float TerrainCeilingMeters = 8000.0f;

    public static float ProjectionScale(float viewportHeight, float fovDegrees)
    {
        float fovRadians = Mathf.DegToRad(fovDegrees);
        return viewportHeight / (2.0f * Mathf.Tan(fovRadians * 0.5f));
    }

    /// <summary>Screen-space fade band (start, end) of one tier; end = spacing x scale / pixel error.</summary>
    public static Vector2 ScreenSpaceBand(
        float spacingMeters,
        float projectionScale,
        float targetPixelError,
        float transitionFraction)
    {
        float pixelError = Mathf.Max(0.5f, targetPixelError);
        float fraction = Mathf.Clamp(transitionFraction, 0.05f, 0.4f);
        float geometricError = Mathf.Max(1.0f, spacingMeters);
        float fadeEnd = Mathf.Max(geometricError * 4.0f, geometricError * projectionScale / pixelError);
        float fadeStart = fadeEnd * (1.0f - fraction);
        return new Vector2(fadeStart, fadeEnd);
    }

    /// <summary>Projected size in pixels of one cell edge seen from the near end of its slant interval.</summary>
    public static float ProjectedErrorPixels(
        float spacingMeters,
        float projectionScale,
        float nearHandoffMeters,
        float altitudeMeters)
        => spacingMeters * projectionScale / Mathf.Max(1.0f, Mathf.Max(nearHandoffMeters, altitudeMeters));

    /// <summary>Edge spacing of the global backdrop mesh.</summary>
    public static float GlobalEdgeSpacing(
        double circumferenceMeters,
        double widthMeters,
        int aroundSegments,
        int acrossSegments)
        => (float)Math.Max(
            circumferenceMeters / Math.Max(1, aroundSegments),
            widthMeters / Math.Max(1, acrossSegments));

    /// <summary>
    /// Minimum global backdrop resolution: cells no larger than ~7.7 km around and
    /// ~5 km across (4096 x 64 on the Certified ring), never above the mesh caps.
    /// </summary>
    public static (int Around, int Across) GlobalResolutionFloor(double circumferenceMeters, double widthMeters)
    {
        if (circumferenceMeters <= 0.0 || widthMeters <= 0.0) return (4096, 64);
        int around = (int)Math.Ceiling(circumferenceMeters / 7700.0 / 64.0) * 64;
        int across = (int)Math.Ceiling(widthMeters / 5000.0);
        return (Math.Clamp(around, 64, 4096), Math.Clamp(across, 4, 64));
    }

    /// <summary>
    /// Raises a configured level count until the last enabled level hands off no
    /// closer than <paramref name="minimumHandoffMeters"/>.
    /// </summary>
    public static int ExtendCountForMinimumHandoff(
        int configuredCount,
        ReadOnlySpan<float> farHandoffs,
        ReadOnlySpan<bool> enabled,
        float minimumHandoffMeters)
    {
        while (configuredCount < farHandoffs.Length
            && farHandoffs[LastEnabledIndex(enabled, configuredCount)] < minimumHandoffMeters)
        {
            configuredCount++;
        }
        return configuredCount;
    }

    /// <summary>
    /// Raises a configured level count until the global backdrop, seen from the
    /// last enabled level's far handoff, is within the backdrop error budget.
    /// </summary>
    public static int ExtendCountForBackdropError(
        int configuredCount,
        ReadOnlySpan<float> farHandoffs,
        ReadOnlySpan<bool> enabled,
        float backdropSpacingMeters,
        float projectionScale,
        float targetPixelError)
    {
        float limit = BackdropErrorFactor * Mathf.Max(0.5f, targetPixelError);
        while (configuredCount < farHandoffs.Length
            && ProjectedErrorPixels(
                backdropSpacingMeters,
                projectionScale,
                farHandoffs[LastEnabledIndex(enabled, configuredCount)],
                0.0f) > limit)
        {
            configuredCount++;
        }
        return configuredCount;
    }

    public static int LastEnabledIndex(ReadOnlySpan<bool> enabled, int exclusiveCount)
    {
        for (int i = Math.Min(exclusiveCount, enabled.Length) - 1; i >= 0; i--)
        {
            if (enabled[i]) return i;
        }
        return 0;
    }

    /// <summary>Altitude activation with hysteresis: a tier is needed while the viewer is within its far handoff.</summary>
    public static bool IsLevelActiveAtAltitude(float farHandoffMeters, float altitudeMeters, bool currentlyActive)
        => currentlyActive
            ? altitudeMeters <= farHandoffMeters * DeactivateAltitudeRatio
            : altitudeMeters < farHandoffMeters * ActivateAltitudeRatio;

    /// <summary>
    /// Horizontal reach of a tier whose slant reach is <paramref name="farHandoffMeters"/>
    /// for a viewer <paramref name="altitudeMeters"/> above sea level, assuming terrain
    /// never rises above <see cref="TerrainCeilingMeters"/>.
    /// </summary>
    public static float TrimmedHorizontalReach(float farHandoffMeters, float viewerHeightMeters)
    {
        float clearance = Mathf.Max(0.0f, viewerHeightMeters - TerrainCeilingMeters);
        if (clearance <= 0.0f) return farHandoffMeters;
        float squared = farHandoffMeters * farHandoffMeters - clearance * clearance;
        return squared <= 0.0f ? 0.0f : Mathf.Sqrt(squared);
    }

    internal readonly record struct ChainTier(string Label, float SpacingMeters, float FarHandoffMeters);

    /// <summary>
    /// Evaluates the projected-error invariant for a configured tier chain and its
    /// backdrop at each altitude. Returns one message per violation.
    /// </summary>
    public static List<string> EvaluateContract(
        IReadOnlyList<ChainTier> tiers,
        float firstNearMeters,
        float backdropSpacingMeters,
        float projectionScale,
        float targetPixelError,
        ReadOnlySpan<float> altitudes,
        out string summary)
    {
        var violations = new List<string>();
        float e = Mathf.Max(0.5f, targetPixelError);
        float proxyLimit = ProxyErrorFactor * e * 1.001f;
        float backdropLimit = BackdropErrorFactor * e * 1.001f;
        float previousFar = 0.0f;
        for (int i = 0; i < tiers.Count; i++)
        {
            if (tiers[i].FarHandoffMeters <= previousFar)
                violations.Add($"tier {tiers[i].Label} far handoff {tiers[i].FarHandoffMeters:0} is not beyond {previousFar:0}");
            previousFar = tiers[i].FarHandoffMeters;
        }

        var text = new StringBuilder();
        foreach (float altitude in altitudes)
        {
            float worstProxy = 0.0f;
            string worstLabel = "-";
            int activeCount = 0;
            float near = firstNearMeters;
            float lastFar = firstNearMeters;
            for (int i = 0; i < tiers.Count; i++)
            {
                ChainTier tier = tiers[i];
                if (tier.FarHandoffMeters > altitude)
                {
                    activeCount++;
                    float error = ProjectedErrorPixels(tier.SpacingMeters, projectionScale, near, altitude);
                    if (error > worstProxy)
                    {
                        worstProxy = error;
                        worstLabel = tier.Label;
                    }
                    if (error > proxyLimit)
                    {
                        violations.Add(string.Format(CultureInfo.InvariantCulture,
                            "h={0:0}m tier {1}: error {2:0.00}px > {3:0.00}px", altitude, tier.Label, error, proxyLimit));
                    }
                }
                near = tier.FarHandoffMeters;
                lastFar = tier.FarHandoffMeters;
            }
            float backdropError = ProjectedErrorPixels(backdropSpacingMeters, projectionScale, lastFar, altitude);
            if (backdropError > backdropLimit)
            {
                violations.Add(string.Format(CultureInfo.InvariantCulture,
                    "h={0:0}m backdrop (edge {1:0}m, near {2:0}m): error {3:0.00}px > {4:0.00}px",
                    altitude, backdropSpacingMeters, lastFar, backdropError, backdropLimit));
            }
            text.Append(string.Format(CultureInfo.InvariantCulture,
                " [h={0:0} active={1} worstProxy={2:0.0}@{3} backdrop={4:0.0}]",
                altitude, activeCount, worstProxy, worstLabel, backdropError));
        }
        summary = text.ToString();
        return violations;
    }
}
