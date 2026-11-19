using System;

namespace RingworldTechDemo.Vegetation;

/// <summary>
/// T6-8 (design B6): the Vegetation tab's "Ground cover share", read by the ground-cover selection (T6-9). Main thread only; <see cref="Revision"/> changes whenever <see cref="Share"/> does,
/// so a reader can tell that a rebuild or a rank gate must be refreshed. Applies in worlds with ground cover; it never enters a cache key.
/// </summary>
public static class RingGroundCoverSettings
{
	private static float _share = 1f;

	/// <summary>Fraction (0-1) of the ground-cover candidates that are kept; 1 = all.</summary>
	public static float Share
	{
		get => _share;
		set
		{
			float clean = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
			if (clean == _share) return;
			_share = clean;
			Revision++;
		}
	}

	/// <summary>Incremented on every change of <see cref="Share"/>.</summary>
	public static int Revision { get; private set; }
}
