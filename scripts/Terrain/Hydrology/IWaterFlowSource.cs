namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The flow-field query (T3.md Spec E5). RingWaterFlow (T3R-4) implements it; floating bodies and tests code against this. A height of NaN means the water surface.
/// Implementations are thread-safe and allocation-free.
/// </summary>
public interface IWaterFlowSource
{
    WaterFlowSample Sample(double around, double across, double height);
}
