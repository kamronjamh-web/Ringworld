using System.Collections.Generic;

namespace RingworldTechDemo;

/// <summary>
/// The seam between the megastorm model and the weather field (docs/megastorm-design.md WO3). The weather layer
/// (<see cref="RingVolumetricCloudLayer.MegastormSource"/>) asks it two pure questions at a weather-clock time and never owns megastorm state:
/// the bundle records (section 3.2) and the natural-weather suppression weight S (section 4.3). <see cref="MegastormModel"/> implements it directly;
/// the runtime node (WO2) hands its model (or a wrapper) to the layer. Both calls must be pure functions of their arguments (the layer calls them from
/// the main, physics and worker threads, and rewinds time for the rain history), allocation free, and return 0 / empty when the megastorm is not active.
/// </summary>
public interface IMegastormWeatherSource
{
    /// <summary>Fills <paramref name="list"/> (cleared first) with the bundle records at time t with their across centre at <paramref name="queryAcross"/>; returns the count (0 when inactive).</summary>
    int BuildBundle(double t, double queryAcross, List<MegastormRecord> list);

    /// <summary>Natural-weather suppression S in [0, 1] at a world position; exactly 0 when the megastorm is not active.</summary>
    float SuppressionAt(double around, double across, double t);
}
