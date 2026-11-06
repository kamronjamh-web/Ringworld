namespace RingworldTechDemo.ContainmentField;

public enum ContainmentPermeability { Pass, Block }

/// <summary>Gameplay policy for the field (decision 4, 2026-11-04): everything passes through; contact only ripples (WO4+).</summary>
public static class ContainmentFieldPolicy
{
    public const ContainmentPermeability FlightPermeability = ContainmentPermeability.Pass;
    public const ContainmentPermeability ProjectilePermeability = ContainmentPermeability.Pass;
}
