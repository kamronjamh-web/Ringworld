using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only probes of the installed proxy water (T2R-11); kept out of runtime code paths.</summary>
public partial class RingTerrainProxySector
{
    /// <summary>True when this level has an installed sector holding water geometry at the canonical position (the sector's rectangle, whichever sample spacing the level has).</summary>
    public bool HasActiveWaterSectorAt(double around, double across)
    {
        int size = Mathf.Max(32, SectorSizeMeters);
        long aroundLength = _grid?.TerrainSampler?.AroundRingVoxelLength ?? 0L;
        double wrapped = aroundLength > 0 ? ((around % aroundLength) + aroundLength) % aroundLength : around;
        var coordinate = new RingTerrainSectorCoordinate((long)System.Math.Floor(wrapped / size), (int)System.Math.Floor(across / size));
        return _activeWaterSectors.Contains(coordinate);
    }

    /// <summary>One raw worker sector of a river-width fixture (T2R-12c): the arrays the true-width test reads, plus the packed slot attribute block.</summary>
    public sealed class RiverWidthFixtureSector
    {
        private readonly RingLodGeometrySnapshot _geometry;
        internal RiverWidthFixtureSector(object source, int cells, int spacing, RingLodGeometrySnapshot geometry, VertexTableSlotBlocks? blocks)
        {
            var data = (SectorData)source;
            Coordinate = data.Coordinate; Cells = cells; Spacing = spacing; _geometry = geometry;
            Vertices = data.Vertices; MorphData = data.MorphData; WaterIndices = data.OceanIndices; Attr = blocks?.Attr;
        }

        public RingTerrainSectorCoordinate Coordinate { get; }
        public int Cells { get; }
        public int Spacing { get; }
        /// <summary>Terrain lattice vertices (row-major), then the water lattice, then the clipped water vertices.</summary>
        public Vector3[] Vertices { get; }
        public Vector2[] MorphData { get; }
        /// <summary>Water triangles: every wet triangle of the lattice and the clipped partial ones.</summary>
        public int[] WaterIndices { get; }
        /// <summary>The packed slot's attribute block (four uints per texel), null when the slot cannot hold the sector.</summary>
        public byte[]? Attr { get; }
        public int LatticeVertices => (Cells + 1) * (Cells + 1);

        /// <summary>Canonical (around, across) of a vertex of this sector, by inverting the fixture's canonical-to-local mapping (around is unwrapped near the sector).</summary>
        public (double Around, double Across) ToCanonical(Vector3 local)
        {
            RingCanonicalPosition anchor = _geometry.FloatingOriginCanonicalAnchor!.Value;
            (double ax, double ay, double az) = _geometry.CanonicalToAbsoluteLocal(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
            double x = local.X + ax, y = local.Y + ay, z = local.Z + az;
            double angle = System.Math.Atan2(z, x), around = angle * _geometry.RingRadiusMeters;
            double circumference = _geometry.CircumferenceMeters;
            around = anchor.AroundMeters + (around - anchor.AroundMeters) - System.Math.Round((around - anchor.AroundMeters) / circumference) * circumference;
            return (around, y - _geometry.RingAcrossBaseOffsetMeters - _geometry.RingCenterLocalOffset.Y);
        }
    }

    /// <summary>Builds raw worker sectors without a scene (T2R-12c true-width test).</summary>
    public sealed class RiverWidthFixtureSession
    {
        private readonly RingLodSurfaceEditSnapshot _edits;
        private readonly RingTerrainSampler _sampler;
        private readonly int _ringWidth;
        internal RiverWidthFixtureSession(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, int ringWidth) { _edits = edits; _sampler = sampler; _ringWidth = ringWidth; }

        public RiverWidthFixtureSector Build(RingTerrainSectorCoordinate coordinate, int spacing, int cells, double ringRadius)
        {
            int size = cells * spacing;
            var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, _sampler.AroundRingVoxelLength, -100000.0, 100000.0,
                new RingCanonicalPosition(coordinate.AroundSector * (long)size, 0.0, coordinate.AcrossSector * size));
            using var cache = new TerrainBuildSampleCache(_edits, _sampler, _ringWidth);
            SectorData data = BuildRawSector(
                requestVersion: 1, coordinate: coordinate, editSnapshot: _edits, sampler: _sampler, sampleCache: cache,
                geometry: geometry, ownerInverse: Transform3D.Identity, size: size, spacing: spacing,
                parentSpacing: spacing * 2, globalMorphTarget: null, cells: cells, underlay: 0.0f, parentUnderlay: 0.0f,
                parentRestoresWaterUnderlay: false, ringWidth: _ringWidth, buildWalls: false, buildSkirts: false,
                skirtDepth: 32.0f, blocky: false, alignSurfaceToTerrainSdf: false, heightQuantum: 1,
                low: Colors.Black, high: Colors.White, coverageMask: null);
            VertexTableSlotBlocks? blocks = PackVertexTableSlot(data, cells, new SkirtPlan(size, _ringWidth, false));
            (double centreAround, double centreAcross) = ResolveSectorCanonicalCenter(coordinate, size, _ringWidth);
            return new RiverWidthFixtureSector(data, cells, spacing, geometry.CenteredAt(centreAround, centreAcross), blocks);
        }
    }

    public static RiverWidthFixtureSession CreateRiverWidthFixtureSession(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, int ringWidth)
        => new(edits, sampler, ringWidth);
}
