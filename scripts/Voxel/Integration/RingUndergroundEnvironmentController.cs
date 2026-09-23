using System;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.Integration;

/// <summary>
/// Applies the underground lighting contract from canonical coordinates.
/// A position below the terrain-bottom is only a candidate: sky light is
/// blocked by an authoritative density-column test to the local terrain surface.
/// This deliberately has no physics/global raycast dependency. Results are
/// cached by canonical column, edit revision, and vertical-domain identity.
/// </summary>
public partial class RingUndergroundEnvironmentController : Node
{
    private const int ColumnCacheSubdivisionsPerMeter = 4;
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export(PropertyHint.Range, "1,256,1")] public float OpeningTransitionDepthMeters { get; set; } = 48.0f;

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smoothTerrain;
    private bool _smoke;
    private int _smokeFrames;
    private readonly System.Collections.Generic.Dictionary<ColumnKey, bool> _openSkyColumns = new();
    private ulong _cachedEditRevision = ulong.MaxValue;
    private RingVerticalDomainConfiguration _cachedDomain;

    /// <summary>0 means no verified path to sky; 1 means a verified access shaft at the roof boundary.</summary>
    public float CurrentSkyAccess { get; private set; } = 1.0f;
    public bool IsUnderground { get; private set; }

    public override void _Ready()
    {
        // Publish state before DaySideAtmosphere composes its single-owner
        // lighting output for this frame.
        ProcessPriority = -100;
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _smoothTerrain = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _smoke = Array.Exists(OS.GetCmdlineUserArgs(), a => a.Equals("--underground-environment-smoke-test", StringComparison.OrdinalIgnoreCase));
    }

    public override void _Process(double delta)
    {
        if (_explorer == null || _grid == null) return;
        RingVerticalDomainConfiguration domain = _grid.GetVerticalDomainConfiguration();
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        double vertical = position.VerticalMeters;
        // The structural shell is only meaningful inside the physical ring
        // volume. Do not allow its underground-lighting state to darken the
        // exterior beyond either across-wall or below the lower hull, even if
        // an out-of-range terrain density query reports solid material.
        IsUnderground = IsWithinStructuralShellLightingDomain(
            position.AcrossMeters, _grid.RingAcrossWidthMeters, vertical, domain);
        if (!IsUnderground) CurrentSkyAccess = 1.0f;
        else
        {
            bool openSky = HasOpenSkyColumn(domain);
            CurrentSkyAccess = openSky
                ? OpeningAccessForDepth(domain, vertical, OpeningTransitionDepthMeters)
                : 0.0f;
        }

        if (_smoke && ++_smokeFrames >= 3)
        {
            try
            {
                RunAutomatedTests();
                GD.Print("UNDERGROUND_ENVIRONMENT_SMOKE_TEST=PASS");
                GetTree().Quit(0);
            }
            catch (Exception exception)
            {
                GD.PushError($"UNDERGROUND_ENVIRONMENT_SMOKE_TEST=FAIL: {exception.Message}");
                GetTree().Quit(1);
            }
        }
    }

    private bool HasOpenSkyColumn(RingVerticalDomainConfiguration domain)
    {
        if (_smoothTerrain == null || _explorer == null || _grid?.TerrainSampler == null) return false;
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        var key = new ColumnKey(
            (long)Math.Floor(position.AroundMeters * ColumnCacheSubdivisionsPerMeter),
            (int)Math.Floor(position.AcrossMeters * ColumnCacheSubdivisionsPerMeter));
        if (_cachedEditRevision != _smoothTerrain.EditRevision || !_cachedDomain.Equals(domain))
        {
            _openSkyColumns.Clear();
            _cachedEditRevision = _smoothTerrain.EditRevision;
            _cachedDomain = domain;
        }
        if (_openSkyColumns.TryGetValue(key, out bool cached)) return cached;
        // Evaluate the centre of the cached quarter-metre cell so a cached
        // answer is never silently reused for a different probe position.
        double sampleAround = (key.AroundQuarter + 0.5) / ColumnCacheSubdivisionsPerMeter;
        double sampleAcross = (key.AcrossQuarter + 0.5) / ColumnCacheSubdivisionsPerMeter;
        long around0 = (long)Math.Floor(sampleAround);
        int across0 = (int)Math.Floor(sampleAcross);
        float smoothSurface = Math.Max(
            Math.Max(
                _grid.TerrainSampler.GetSmoothSurfaceHeight(around0, across0),
                _grid.TerrainSampler.GetSmoothSurfaceHeight(around0 + 1, across0)),
            Math.Max(
                _grid.TerrainSampler.GetSmoothSurfaceHeight(around0, across0 + 1),
                _grid.TerrainSampler.GetSmoothSurfaceHeight(around0 + 1, across0 + 1)));
        int surface = Mathf.CeilToInt(smoothSurface);
        bool open = HasOpenSkyColumn(domain, surface, y =>
            _smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(
                sampleAround, y, sampleAcross, out float density) ? density : -1.0f);
        // Bound memory while retaining the deliberate future cache key shape.
        if (_openSkyColumns.Count >= 256) _openSkyColumns.Clear();
        _openSkyColumns[key] = open;
        return open;
    }

    public static bool HasOpenSkyColumn(
        RingVerticalDomainConfiguration domain,
        int localSurfaceHeight,
        Func<int, float> densityAtColumn)
    {
        // The 1 m lattice is intentionally exhaustive. A 16 m sign probe can
        // miss a thin intact plug and create a sunlight leak. This runs only
        // when a cached column/revision is new, never as a global raycast.
        int ceiling = Math.Max(domain.ProtectedRoofTopGlobalY, localSurfaceHeight + 1);
        for (int y = domain.TerrainBottomGlobalY - 1; y <= ceiling; y++)
            if (densityAtColumn(y) <= 0.0f) return false;
        return true;
    }

    public static float OpeningAccessForDepth(
        RingVerticalDomainConfiguration domain,
        double verticalMeters,
        float openingTransitionDepthMeters = 48.0f)
    {
        if (verticalMeters >= domain.TerrainBottomGlobalY) return 1.0f;
        float transition = Math.Max(1.0f, openingTransitionDepthMeters);
        return Mathf.Clamp((float)((verticalMeters - (domain.TerrainBottomGlobalY - transition)) / transition), 0.0f, 1.0f);
    }

    /// <summary>
    /// Returns whether a canonical across coordinate is on the ring ribbon,
    /// including the inner wall plane but excluding the outer wall plane. The
    /// canonical habitable interval is [0, ring width), so exact ring width is
    /// exterior. This is the single authoritative guard for structural-shell
    /// lighting; ring width always comes from the grid.
    /// </summary>
    public static bool IsWithinRingAcrossBounds(double acrossMeters, float ringAcrossWidthMeters)
    {
        return ringAcrossWidthMeters >= 0.0f
            && acrossMeters >= 0.0
            && acrossMeters < ringAcrossWidthMeters;
    }

    /// <summary>
    /// True only within the finite structural shell: the canonical interval is
    /// [structural bottom, terrain bottom) vertically and [0, ring width)
    /// across. The exact lower-hull and left-wall planes remain part of the
    /// shell; the upper terrain plane, right wall, and points below the hull
    /// are exterior.
    /// </summary>
    public static bool IsWithinStructuralShellLightingDomain(
        double acrossMeters,
        float ringAcrossWidthMeters,
        double verticalMeters,
        RingVerticalDomainConfiguration domain)
    {
        return IsWithinRingAcrossBounds(acrossMeters, ringAcrossWidthMeters)
            && verticalMeters >= domain.StructuralBottomGlobalY
            && verticalMeters < domain.TerrainBottomGlobalY;
    }

    private readonly record struct ColumnKey(long AroundQuarter, int AcrossQuarter);

    public static void RunAutomatedTests()
    {
        var domain = new RingVerticalDomainConfiguration(-8192, 512, -22304, 800);
        if (!domain.Validate(out string error)) throw new InvalidOperationException(error);
        if (HasOpenSkyColumn(domain, 32, _ => -1.0f))
            throw new InvalidOperationException("intact roof leaked direct sky light");
        if (!HasOpenSkyColumn(domain, 32, _ => 1.0f))
            throw new InvalidOperationException("fully excavated access shaft was not recognized");
        float openingNearRoof = OpeningAccessForDepth(domain, -8193);
        float openingDeep = OpeningAccessForDepth(domain, -8300);
        if (openingNearRoof < .95f || openingDeep != 0.0f)
            throw new InvalidOperationException("roof-opening transition band is incorrect");
        if (HasOpenSkyColumn(domain, 32, y => y == -8000 ? -0.01f : 1.0f))
            throw new InvalidOperationException("one-metre roof plug leaked sky light");
        if (HasOpenSkyColumn(domain, 32, y => y == 0 ? -0.01f : 1.0f))
            throw new InvalidOperationException("intact terrain above a breached roof leaked sky light");
        const float ringWidth = 128.0f;
        if (!IsWithinRingAcrossBounds(0.0, ringWidth)
            || !IsWithinRingAcrossBounds(ringWidth - 0.001, ringWidth)
            || IsWithinRingAcrossBounds(-0.001, ringWidth)
            || IsWithinRingAcrossBounds(ringWidth, ringWidth)
            || IsWithinRingAcrossBounds(ringWidth + 0.001, ringWidth))
            throw new InvalidOperationException("ring-wall lighting bounds are incorrect");
        if (!IsWithinStructuralShellLightingDomain(64.0, ringWidth, -8193.0, domain))
            throw new InvalidOperationException("interior structural-shell lighting was rejected");
        if (!IsWithinStructuralShellLightingDomain(
                64.0, ringWidth, domain.StructuralBottomGlobalY, domain)
            || !IsWithinStructuralShellLightingDomain(
                64.0, ringWidth, domain.StructuralBottomGlobalY + 1.0, domain))
            throw new InvalidOperationException("structural-bottom interior lighting bounds are incorrect");
        if (IsWithinStructuralShellLightingDomain(
                64.0, ringWidth, domain.StructuralBottomGlobalY - 0.001, domain)
            || IsWithinStructuralShellLightingDomain(
                64.0, ringWidth, domain.StructuralBottomGlobalY - 1000.0, domain))
            throw new InvalidOperationException("structural-shell lighting expanded below the lower hull");
        if (IsWithinStructuralShellLightingDomain(-0.001, ringWidth, -9000.0, domain)
            || IsWithinStructuralShellLightingDomain(ringWidth, ringWidth, -9000.0, domain)
            || IsWithinStructuralShellLightingDomain(ringWidth + 0.001, ringWidth, -9000.0, domain)
            || IsWithinStructuralShellLightingDomain(-1000.0, ringWidth, -9000.0, domain)
            || IsWithinStructuralShellLightingDomain(ringWidth + 1000.0, ringWidth, -9000.0, domain))
            throw new InvalidOperationException("structural-shell lighting expanded beyond a ring wall");
        if (IsWithinStructuralShellLightingDomain(64.0, ringWidth, domain.TerrainBottomGlobalY, domain))
            throw new InvalidOperationException("surface position was treated as structural-shell lighting");
        if (IsWithinStructuralShellLightingDomain(-0.001, ringWidth, domain.StructuralBottomGlobalY, domain)
            || IsWithinStructuralShellLightingDomain(ringWidth, ringWidth, domain.StructuralBottomGlobalY, domain))
            throw new InvalidOperationException("combined lower-hull and wall bounds are incorrect");
    }
}
