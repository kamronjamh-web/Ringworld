using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Follower fan-out: materials registered here (for example grass) mirror a named subset of the Terrain3D surface's shader
/// parameters, so they share its lighting, atmosphere and anchor values without a second per-frame update path. Prepared
/// windows are separate child surfaces; only the facade's active child forwards, and activating a new one replays.
/// </summary>
public partial class RingTerrain3DSurface
{
    private RingSurfaceFollowerRegistry? _followers;
    private RingTerrain3DSurface? _followerHost;
    // Last value passed to SetMaterialParameter on this surface, for replay into late or re-targeted followers.
    private readonly Dictionary<StringName, Variant> _materialParameterCache = new();

    /// <summary>Total writes issued to followers (diagnostic).</summary>
    public long FollowerWriteCount => _followers?.WriteCount ?? 0;
    public int FollowerCount => _followers?.Count ?? 0;

    /// <summary>Registers a material to receive the named surface parameters, replaying their last values now.</summary>
    public void RegisterSurfaceFollower(ShaderMaterial material, IReadOnlySet<StringName> names)
        => RegisterSurfaceFollower(new ShaderMaterialSurfaceFollower(material), names);

    public void RegisterSurfaceFollower(IRingSurfaceFollower follower, IReadOnlySet<StringName> names)
    {
        _followers ??= new RingSurfaceFollowerRegistry();
        _followers.Register(follower, names, (_activePreparedSurface ?? this)._materialParameterCache);
    }

    public bool UnregisterSurfaceFollower(ShaderMaterial material) => _followers?.Unregister(material) ?? false;
    public bool UnregisterSurfaceFollower(IRingSurfaceFollower follower) => _followers?.Unregister(follower) ?? false;

    /// <summary>The facade (or a non-prepared surface itself) that owns the follower registry.</summary>
    private RingTerrain3DSurface FollowerHost
    {
        get
        {
            _followerHost ??= GetParent() as RingTerrain3DSurface;
            return _followerHost ?? this;
        }
    }

    private void ForwardToFollowers(StringName name, Variant value)
    {
        _materialParameterCache[name] = value;
        RingTerrain3DSurface host = FollowerHost;
        RingSurfaceFollowerRegistry? followers = host._followers;
        if (followers == null || followers.Count == 0) return;
        // Staged and idle windows keep their own materials current but must not drive followers.
        if (!ReferenceEquals(host._activePreparedSurface ?? host, this)) return;
        followers.Forward(name, value);
    }

    /// <summary>Re-targets followers after the active native owner changed (values already equal are not rewritten).</summary>
    private void ReplayFollowersFromActive()
        => _followers?.Replay((_activePreparedSurface ?? this)._materialParameterCache);
}
