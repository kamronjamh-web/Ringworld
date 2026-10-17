namespace RingworldTechDemo;

/// <summary>
/// Lets another system own part of the primary (star) light's direction without a second writer: <see cref="RingDayNightCycle"/> stays the single
/// writer of the StarLight basis and blends its real-sun direction toward the player's local zenith by <see cref="PrimaryLightWeight"/>
/// (docs/eclipse-false-sun-spec.md C.1 and C.2). The eclipse director implements it so the hub sun lights every point from its own zenith.
/// </summary>
public interface IPrimaryLightOverride
{
    /// <summary>0 = the real sun direction, 1 = the local zenith. Values in between blend the two directions.</summary>
    float PrimaryLightWeight { get; }
}
