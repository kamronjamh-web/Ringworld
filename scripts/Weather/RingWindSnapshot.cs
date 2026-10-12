using Godot;
namespace RingworldTechDemo.Weather;
public readonly record struct RingWindSnapshot(Vector2 MeanWind, Vector2 InstantWind, float StormFactor, double SimulationTime, float GustMultiplier, bool Enabled);
