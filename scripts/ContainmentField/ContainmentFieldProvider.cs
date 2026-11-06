using System;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// The one field-height source (docs/containment-field-design.md 4.2 "Shared field-strike pieces"). <see cref="Install"/> assigns the megastorm seam
/// (<see cref="MegastormFieldCoupling.FieldUndersideProvider"/> and the real <see cref="MegastormFieldCoupling.CeilingFromUnderside"/>, replacing the WO8 stub), so the
/// megastorm's ceiling, contact band, field strikes, the ripples and the cloud ceiling all derive from the same H. Natural lightning reads <see cref="Underside"/>.
/// </summary>
public static class ContainmentFieldProvider
{
    private static ContainmentFieldProfile? _profile;
    private static Func<double, double>? _installedProvider;

    /// <summary>The installed profile, or null when no field is installed.</summary>
    public static ContainmentFieldProfile? Profile => _profile;
    public static bool IsInstalled => _profile != null;

    /// <summary>H(across) in metres; NaN outside the roots or with no field.</summary>
    public static double Underside(double acrossMeters) => _profile is ContainmentFieldProfile p ? ContainmentFieldGeometry.HeightAt(p, acrossMeters) : double.NaN;

    /// <summary>The cloud ceiling at <paramref name="acrossMeters"/> (18 km with no field): the same rule as CloudCeilingFromUnderside(Underside(across)).</summary>
    public static double CloudCeilingAt(double acrossMeters) => _profile is ContainmentFieldProfile p ? ContainmentFieldGeometry.CloudCeilingAt(p, acrossMeters) : ContainmentFieldGeometry.CloudCeilingMeters;

    /// <summary>Installs the field for <paramref name="profile"/> (replaces a previous install). The profile must be valid.</summary>
    public static void Install(in ContainmentFieldProfile profile)
    {
        if (!profile.IsValid) throw new ArgumentException("invalid containment field profile", nameof(profile));
        ContainmentFieldProfile p = profile;
        _profile = p;
        Func<double, double> provider = across => ContainmentFieldGeometry.HeightAt(p, across);
        _installedProvider = provider;
        MegastormFieldCoupling.FieldUndersideProvider = provider;
        MegastormFieldCoupling.CeilingFromUnderside = ContainmentFieldGeometry.CloudCeilingFromUnderside;
    }

    /// <summary>
    /// Removes the field. The megastorm seam is only cleared when it still holds the provider this class installed, so a test or another owner that replaced it
    /// is left alone.
    /// </summary>
    public static void Uninstall()
    {
        if (_installedProvider != null && ReferenceEquals(MegastormFieldCoupling.FieldUndersideProvider, _installedProvider))
        {
            MegastormFieldCoupling.FieldUndersideProvider = null;
            MegastormFieldCoupling.CeilingFromUnderside = ContainmentFieldGeometry.CloudCeilingFromUnderside;
        }
        _installedProvider = null;
        _profile = null;
    }
}
