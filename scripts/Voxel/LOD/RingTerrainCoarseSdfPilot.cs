using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Experimental 2m volumetric LOD1 presenter. It intentionally remains
/// independent from the production heightfield until its transition boundaries
/// are installed and visually certified.
/// </summary>
public partial class RingTerrainCoarseSdfPilot : Node3D
{
	private readonly record struct CoarseTileBuildResult(
		RingTerrainSampleCoordinate Origin,
		MeshBuildData Build,
		bool HasGeometry,
		int Generation);
    [Export] public bool Enabled { get; set; }
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath FocusPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export] public NodePath Lod1ProxyPath { get; set; } = new("../Lod1Terrain2m");
    [Export(PropertyHint.Range, "0,2,1")] public int RadiusInCoarseChunks { get; set; } = 1;
    [Export] public bool HideLod1ProxyWhileEnabled { get; set; } = true;

    private readonly Dictionary<RingTerrainSampleCoordinate, MeshInstance3D> _tiles = new();
    private readonly HashSet<RingTerrainSampleCoordinate> _desiredTiles = new();
    private readonly Dictionary<RingTerrainSampleCoordinate, int> _tilesBuilding = new();
    private readonly ConcurrentQueue<CoarseTileBuildResult> _completedTiles = new();
    private VoxelChunkGridManager? _grid;
    private Node3D? _focus;
    private Node3D? _lod1Proxy;
    private Vector3I _lastCenter = new(int.MinValue, int.MinValue, int.MinValue);
    private bool _proxyVisibilityCaptured;
    private bool _lod1ProxyWasVisible;
    private int _presentationGeneration;

    public override void _Ready()
    {
        Enabled |= Array.Exists(
            OS.GetCmdlineUserArgs(),
            static argument => argument.Equals("--coarse-sdf-lod1-pilot", StringComparison.OrdinalIgnoreCase));
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _focus = GetNodeOrNull<Node3D>(FocusPath);
        _lod1Proxy = GetNodeOrNull<Node3D>(Lod1ProxyPath);
        UpdatePresentation(force: true);
    }

    public override void _Process(double delta)
    {
        InstallCompletedTiles();
        UpdatePresentation(force: false);
    }

    public override void _ExitTree()
    {
        RestoreLod1ProxyVisibility();
    }

    private void UpdatePresentation(bool force)
    {
        if (!Enabled || (_grid?.DormantEditableBackend ?? false))
        {
            ClearTiles();
            RestoreLod1ProxyVisibility();
            return;
        }
        if (_grid == null || _focus == null || _grid.TerrainSampler == null)
            return;

        SetLod1ProxyVisibility();
        Vector3 canonical = _grid.WorldToCanonicalRingPosition(_focus.GlobalPosition);
        int around = Mathf.FloorToInt(canonical.X);
        int across = Mathf.FloorToInt(canonical.Z);
        int surface = _grid.TerrainSampler.GetSurfaceHeight(around, across);
        const int coarseSpan = RingTerrainCoarseSdfChunk.CellCount * 2;
        var center = new Vector3I(
            RingCoordMath.FloorDiv(around, coarseSpan),
            RingCoordMath.FloorDiv(surface, coarseSpan),
            RingCoordMath.FloorDiv(across, coarseSpan));
        if (!force && center == _lastCenter)
            return;
        _lastCenter = center;

        _desiredTiles.Clear();
        int radius = Mathf.Max(0, RadiusInCoarseChunks);
        for (int z = -radius; z <= radius; z++)
        for (int x = -radius; x <= radius; x++)
        {
            var fineCoordinate = new RingChunkCoordinate(
                (center.X + x) * 2,
                (center.Z + z) * 2,
                center.Y * 2);
            RingTerrainCoarseSdfChunk? coarse = _grid.CaptureCoarseTerrainSdfChunk(fineCoordinate);
            if (coarse == null) continue;
            _desiredTiles.Add(coarse.GlobalOrigin);
            if (_tiles.ContainsKey(coarse.GlobalOrigin) || _tilesBuilding.ContainsKey(coarse.GlobalOrigin)) continue;
            QueueTileBuild(coarse);
        }

        var stale = new List<RingTerrainSampleCoordinate>();
        foreach (RingTerrainSampleCoordinate origin in _tiles.Keys)
            if (!_desiredTiles.Contains(origin)) stale.Add(origin);
        foreach (RingTerrainSampleCoordinate origin in stale)
        {
            MeshInstance3D tile = _tiles[origin];
            _tiles.Remove(origin);
            tile.QueueFree();
        }
        GD.Print($"Coarse SDF LOD1 pilot: tiles={_tiles.Count}, center={center}, spacing=2m.");
    }

    private void QueueTileBuild(RingTerrainCoarseSdfChunk coarse)
    {
        if (_grid == null) return;
        VoxelBlockPalette palette = VoxelBlockPalette.Capture(_grid.BlockColorMap);
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        RingTerrainSampleCoordinate origin = coarse.GlobalOrigin;
        int generation = _presentationGeneration;
        _tilesBuilding[origin] = generation;
        _ = Task.Run(() =>
        {
            bool hasGeometry = VoxelChunkMesherNative.TryBuildSmoothRegularSdf(
                    coarse,
                    palette,
                    localVertex => geometry.CanonicalToWorld(
                        coarse.GlobalOrigin.AroundVoxel + localVertex.X,
                        coarse.GlobalOrigin.VerticalVoxel + localVertex.Y,
                        coarse.GlobalOrigin.AcrossVoxel + localVertex.Z),
                    out MeshBuildData build)
                && build.HasGeometry;
            _completedTiles.Enqueue(new CoarseTileBuildResult(origin, build, hasGeometry, generation));
        });
    }

    private void InstallCompletedTiles()
    {
        while (_completedTiles.TryDequeue(out CoarseTileBuildResult result))
        {
            if (_tilesBuilding.TryGetValue(result.Origin, out int generation)
                && generation == result.Generation)
                _tilesBuilding.Remove(result.Origin);
            if (result.Generation != _presentationGeneration
                || (_grid?.DormantEditableBackend ?? false)
                || !Enabled
                || !result.HasGeometry
                || !_desiredTiles.Contains(result.Origin)
                || _tiles.ContainsKey(result.Origin)) continue;
            InstallTile(result);
        }
    }

    private void InstallTile(in CoarseTileBuildResult result)
    {
        MeshBuildData build = result.Build;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, build.CreateGodotMeshArrays());
        var tile = new MeshInstance3D
        {
            Name = $"CoarseSdf_{result.Origin.AroundVoxel}_{result.Origin.VerticalVoxel}_{result.Origin.AcrossVoxel}",
            TopLevel = true,
            Mesh = mesh,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 1.0f,
                Metallic = 0.0f
            }
        };
        AddChild(tile);
        tile.GlobalTransform = Transform3D.Identity;
        _tiles.Add(result.Origin, tile);
    }

    private void SetLod1ProxyVisibility()
    {
        if (_lod1Proxy == null || !HideLod1ProxyWhileEnabled) return;
        if (!_proxyVisibilityCaptured)
        {
            _lod1ProxyWasVisible = _lod1Proxy.Visible;
            _proxyVisibilityCaptured = true;
        }
        _lod1Proxy.Visible = false;
    }

    private void RestoreLod1ProxyVisibility()
    {
        if (_lod1Proxy != null && _proxyVisibilityCaptured)
            _lod1Proxy.Visible = _lod1ProxyWasVisible;
        _proxyVisibilityCaptured = false;
    }

    private void ClearTiles()
    {
        if (_tiles.Count > 0 || _desiredTiles.Count > 0 || _tilesBuilding.Count > 0)
            _presentationGeneration++;
        foreach (MeshInstance3D tile in _tiles.Values)
            tile.QueueFree();
        _tiles.Clear();
        _desiredTiles.Clear();
        _tilesBuilding.Clear();
        _lastCenter = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
    }
}
