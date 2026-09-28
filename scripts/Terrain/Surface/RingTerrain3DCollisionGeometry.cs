using System;
using System.Diagnostics;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Single-use CPU geometry for bounded staged curved collision installation.</summary>
public sealed partial class RingTerrain3DCollisionGeometry : RefCounted
{
    private const int WindowSide = 384;
    private const int ColliderSide = 193;
    private const int FirstCache = 32;
    private float[]? _heights;
    private Vector3[][]? _tiles;
    private long _around;
    private int _across;
    private float _frameHeight;
    private int _direction;
    private int _tileCells;
    private double _projectionMilliseconds;
    private string _error = string.Empty;

    public bool Prepare(float[] heights, long around, int across, float frameHeight, int direction, int tileCells = 32)
    {
        return PrepareInternal(heights, around, across, frameHeight, direction, -8000.0f, 8000.0f, tileCells);
    }

    /// <summary>Builds gameplay collision from the declared legacy or runtime surface-height contract.</summary>
    public bool PrepareGameplay(float[] heights, long around, int across, float frameHeight, int direction,
        float minimumHeight, float maximumHeight, int tileCells = 32)
    {
        return PrepareInternal(heights, around, across, frameHeight, direction, minimumHeight, maximumHeight, tileCells);
    }

    private bool PrepareInternal(float[] heights, long around, int across, float frameHeight, int direction,
        float minimumHeight, float maximumHeight, int tileCells)
    {
        if (_heights != null) { _error = "Geometry is single-use."; return false; }
        if (heights == null || heights.Length != WindowSide * WindowSide || !float.IsFinite(minimumHeight) || !float.IsFinite(maximumHeight) || minimumHeight > maximumHeight ||
            !float.IsFinite(frameHeight) || frameHeight < minimumHeight || frameHeight > maximumHeight || direction is not (1 or -1) || tileCells is not (16 or 32) || around % 2 != 0 || across % 2 != 0)
        { _error = "Geometry input is invalid."; return false; }
        for (int i = 0; i < heights.Length; i++) if (!float.IsFinite(heights[i]) || heights[i] < minimumHeight || heights[i] > maximumHeight) { _error = "Geometry heights are invalid."; return false; }
        var clock = Stopwatch.StartNew();
        try
        {
            _heights = (float[])heights.Clone(); _around = RingSurfaceFrame.Wrap(around, CertifiedRingScale.AroundVoxelLength); _across = across; _frameHeight = frameHeight; _direction = direction; _tileCells = tileCells;
            int firstAcross = across - direction * 192, lastAcross = across + direction * 192;
            if (Math.Min(firstAcross, lastAcross) < 0 || Math.Max(firstAcross, lastAcross) >= CertifiedRingScale.RingWidthMeters) { _error = "Geometry frame is outside ring width."; _heights = null; return false; }
            int count = (ColliderSide - 1) / tileCells;
            _tiles = new Vector3[count * count][];
            var frame = new RingSurfaceFrame(_around, _across, _frameHeight, CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, 2, direction);
            var points = new Vector3[ColliderSide, ColliderSide];
            for (int z = 0; z < ColliderSide; z++) for (int x = 0; x < ColliderSide; x++) points[x, z] = Point(frame, x, z);
            for (int tz = 0; tz < count; tz++) for (int tx = 0; tx < count; tx++)
            {
                var vertices = new Vector3[tileCells * tileCells * 6]; int output = 0;
                for (int z = 0; z < tileCells; z++) for (int x = 0; x < tileCells; x++)
                {
                    int px = tx * tileCells + x, pz = tz * tileCells + z;
                    Vector3 a = points[px, pz], b = points[px + 1, pz], c = points[px, pz + 1], d = points[px + 1, pz + 1];
                    vertices[output++] = a; vertices[output++] = b; vertices[output++] = c; vertices[output++] = b; vertices[output++] = d; vertices[output++] = c;
                }
                _tiles[tz * count + tx] = vertices;
            }
            return true;
        }
        catch (Exception exception) { _error = exception.Message; _heights = null; _tiles = null; return false; }
        finally { clock.Stop(); _projectionMilliseconds = clock.Elapsed.TotalMilliseconds; }
    }

    internal bool Matches(float[] heights, long around, int across, float frameHeight, int direction) => _heights != null && heights.Length == _heights.Length && RingSurfaceFrame.Wrap(around, CertifiedRingScale.AroundVoxelLength) == _around && across == _across && frameHeight == _frameHeight && direction == _direction && Same(heights, _heights);
    internal Vector3[] GetTile(int index) => _tiles![index];
    internal int TileCount => _tiles?.Length ?? 0;
    internal int TileCells => _tileCells;
    public Dictionary GetDiagnostics() => new() { ["projection_ms"] = _projectionMilliseconds, ["tile_cells"] = _tileCells, ["tile_count"] = TileCount, ["geometry_bytes"] = (_tiles?.Length ?? 0) * _tileCells * _tileCells * 6 * 12, ["error"] = _error };
    private Vector3 Point(RingSurfaceFrame frame, int x, int z) { int cx = FirstCache + x, cz = FirstCache + z; return frame.MapSample(_around + (cx - 128) * 2L, _across + _direction * (cz - 128) * 2, _heights![cz * WindowSide + cx]); }
    private static bool Same(float[] a, float[] b) { for (int i = 0; i < a.Length; i++) if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false; return true; }
}
