namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// What the flow field says at one point (T3.md Spec E5): the owner kind, the water level and bed, the velocity on canonical around and across in m/s (already scaled by the current
/// strength), the speed, the span and class of the river, the danger speed, whiteness and the chute head. Kind None carries NaN levels and zero velocity.
/// </summary>
public readonly record struct WaterFlowSample(InlandWaterKind Kind, double Surface, double Bed, double VelocityAround, double VelocityAcross,
    double Speed, int Span, RiverClass Class, double DangerSpeed, double Whitewater, double ChuteHead);
