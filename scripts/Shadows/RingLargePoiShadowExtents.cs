using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// Registry of large POIs whose mounted detail wants the sun shadow map to reach further than the near map (docs/shadow-revamp-plan.md, POI sun map
/// extension). Each provider returns the POI's current world-space AABB and is queried on every use, so a floating-origin rebase needs no bookkeeping.
/// Providers register while their detail is mounted and unregister when it is released, disabled or leaves the tree.
/// </summary>
public static class RingLargePoiShadowExtents
{
    /// <summary>Writes the POI's current world AABB; false when the POI has nothing mounted right now.</summary>
    public delegate bool BoundsProvider(out Aabb worldBounds);

    private static readonly List<BoundsProvider> Providers = new();

    public static int Count => Providers.Count;

    public static void Register(BoundsProvider provider)
    {
        if (!Providers.Contains(provider)) Providers.Add(provider);
    }

    public static void Unregister(BoundsProvider provider) => Providers.Remove(provider);

    /// <summary>Tests: drops every provider.</summary>
    public static void Clear() => Providers.Clear();

    /// <summary>
    /// Distance from <paramref name="camera"/> to the farthest corner of any registered AABB that the shadow map can still reach (its nearest point is closer than
    /// <paramref name="reachMeters"/>; a POI whose detail stays mounted far away gets no extension). False when no provider qualifies.
    /// </summary>
    public static bool TryGetFarthestCornerDistance(Vector3 camera, float reachMeters, out float distance)
    {
        distance = 0.0f;
        bool any = false;
        foreach (BoundsProvider provider in Providers)
        {
            if (!provider(out Aabb box) || box.Size == Vector3.Zero) continue;
            Vector3 nearest = camera.Clamp(box.Position, box.End);
            if ((nearest - camera).Length() >= reachMeters) continue;
            for (int corner = 0; corner < 8; corner++)
            {
                float d = (box.GetEndpoint(corner) - camera).Length();
                if (float.IsFinite(d)) { distance = Math.Max(distance, d); any = true; }
            }
        }
        return any;
    }
}
