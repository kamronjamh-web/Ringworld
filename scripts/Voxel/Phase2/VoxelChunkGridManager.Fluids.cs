using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase1;
using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelChunkGridManager
{
    [ExportGroup("Dynamic water")]
    [Export] public bool EnableDynamicWater { get; set; } = true;
    [Export(PropertyHint.Range, "1,240,1")] public int FluidUpdatesPerTick { get; set; } = 48;
    [Export(PropertyHint.Range, "1,60,1")] public int FluidTicksPerSecond { get; set; } = 10;
    [Export(PropertyHint.Range, "1,32,1")] public int FluidMeshUpdatesPerFrame { get; set; } = 2;

    private FluidRuntime _fluids = null!;
    private double _fluidTickAccumulator;

    public int DynamicFluidCellCount => _fluids.CellCount;
    public int PendingFluidUpdateCount => _fluids.PendingCount;

    public bool TryGetDynamicWaterSurfaceAtCanonical(in RingCanonicalPosition position, out float surfaceHeight) =>
        _fluids.TryGetSurface(position, out surfaceHeight);

    public Dictionary<string, byte> CaptureDynamicWaterSources() => _fluids.CaptureSources();
    public void ApplyDynamicWaterSources(Dictionary<string, byte>? sources) => _fluids.ApplySources(sources);

    public bool TryPlaceWaterSourceByWorldPosition(Vector3 worldPosition)
    {
        if (!EnableDynamicWater) return false;
        if (!TryGetChunkAtWorldPosition(worldPosition, out VoxelChunkNode chunk, out RingChunkCoordinate coord))
        {
            coord = WorldToChunkCoordinate(worldPosition);
            if (!IsChunkWithinVerticalBounds(coord)) return false;
            chunk = EnsureChunkLoaded(coord, buildCollision: false);
        }
        Vector3I local = chunk.WorldToLocalVoxel(worldPosition);
        if (!chunk.Data.IsInBounds(local.X, local.Y, local.Z)) return false;
        RingVoxelCoordinate voxel = LocalToGlobalVoxel(CanonicalizeChunkCoordinate(coord), local);
        return _fluids.TrySetSource(voxel);
    }

    public bool TryRemoveWaterByWorldPosition(Vector3 worldPosition)
    {
        if (!EnableDynamicWater || !TryGetChunkAtWorldPosition(worldPosition, out VoxelChunkNode chunk, out RingChunkCoordinate coord))
            return false;
        Vector3I local = chunk.WorldToLocalVoxel(worldPosition);
        if (!chunk.Data.IsInBounds(local.X, local.Y, local.Z)) return false;
        return _fluids.Remove(LocalToGlobalVoxel(CanonicalizeChunkCoordinate(coord), local));
    }

    private void ProcessDynamicWater(double delta)
    {
        if (!EnableDynamicWater) return;
        _fluidTickAccumulator += delta;
        double interval = 1.0 / Mathf.Max(1, FluidTicksPerSecond);
        while (_fluidTickAccumulator >= interval)
        {
            _fluidTickAccumulator -= interval;
            _fluids.Tick(Mathf.Max(1, FluidUpdatesPerTick));
        }
        _fluids.ServiceDirtyMeshes(Mathf.Max(1, FluidMeshUpdatesPerFrame));
    }

    private void OnFluidChunkLoaded(in RingChunkCoordinate coordinate) => _fluids.OnChunkLoaded(coordinate);
    private void OnFluidChunkUnloaded(in RingChunkCoordinate coordinate) => _fluids.OnChunkUnloaded(coordinate);

    private sealed class FluidRuntime
    {
        private readonly VoxelChunkGridManager _owner;
        private readonly Dictionary<RingVoxelCoordinate, VoxelFluidState> _cells = new();
        private readonly Queue<RingVoxelCoordinate> _updates = new();
        private readonly HashSet<RingVoxelCoordinate> _queued = new();
        private readonly Dictionary<RingChunkCoordinate, HashSet<RingVoxelCoordinate>> _deferred = new();
        private readonly Queue<RingChunkCoordinate> _dirtyMeshes = new();
        private readonly HashSet<RingChunkCoordinate> _dirtyMeshSet = new();

        public FluidRuntime(VoxelChunkGridManager owner) => _owner = owner;
        public int CellCount => _cells.Count;
        public int PendingCount => _updates.Count + _deferred.Count;

        public bool TryGetSurface(in RingCanonicalPosition position, out float surfaceHeight)
        {
            surfaceHeight = 0.0f;
            var raw = new RingVoxelCoordinate(
                (long)Math.Floor(position.AroundMeters),
                Mathf.FloorToInt((float)position.AcrossMeters),
                Mathf.FloorToInt((float)position.VerticalMeters));
            if (!TryCanonical(raw, out RingVoxelCoordinate voxel, out _, out _)
                || !_cells.TryGetValue(voxel, out VoxelFluidState state)) return false;
            surfaceHeight = voxel.Vertical + state.Level / 8.0f;
            return position.VerticalMeters <= surfaceHeight + 0.01;
        }

        public Dictionary<string, byte> CaptureSources()
        {
            var sources = new Dictionary<string, byte>();
            foreach ((RingVoxelCoordinate voxel, VoxelFluidState state) in _cells)
                if (state.IsSource) sources[$"{voxel.AroundRing},{voxel.AcrossRing},{voxel.Vertical}"] = (byte)state.Flags;
            return sources;
        }

        public void ApplySources(Dictionary<string, byte>? sources)
        {
            if (sources == null) return;
            foreach ((string key, byte flags) in sources)
            {
                string[] parts = key.Split(',');
                if (parts.Length != 3 || !long.TryParse(parts[0], out long around)
                    || !int.TryParse(parts[1], out int across) || !int.TryParse(parts[2], out int vertical)) continue;
                var state = new VoxelFluidState(8, (VoxelFluidFlags)flags | VoxelFluidFlags.Source);
                if (!TryCanonical(new RingVoxelCoordinate(around, across, vertical), out RingVoxelCoordinate voxel, out RingChunkCoordinate chunk, out _)) continue;
                _cells[voxel] = state;
                MarkDirty(chunk);
                Queue(voxel, chunk);
            }
        }

        public bool TrySetSource(in RingVoxelCoordinate raw)
        {
            if (!TryCanonical(raw, out RingVoxelCoordinate voxel, out RingChunkCoordinate chunk, out _)) return false;
            if (!TryGetContainer(voxel, out bool waterlogged)) return false;
            Set(voxel, VoxelFluidState.Source(waterlogged));
            Queue(voxel, chunk);
            return true;
        }

        public bool Remove(in RingVoxelCoordinate raw)
        {
            if (!TryCanonical(raw, out RingVoxelCoordinate voxel, out RingChunkCoordinate chunk, out _)) return false;
            if (!_cells.Remove(voxel)) return false;
            MarkMeshNeighborhood(voxel);
            QueueNeighbors(voxel);
            return true;
        }

        public void OnChunkLoaded(in RingChunkCoordinate raw)
        {
            RingChunkCoordinate chunk = _owner.CanonicalizeChunkCoordinate(raw);
            if (_deferred.Remove(chunk, out HashSet<RingVoxelCoordinate>? pending))
                foreach (RingVoxelCoordinate voxel in pending) Queue(voxel, chunk);
            MarkDirty(chunk);
        }

        public void OnChunkUnloaded(in RingChunkCoordinate raw)
        {
            RingChunkCoordinate chunk = _owner.CanonicalizeChunkCoordinate(raw);
            _dirtyMeshSet.Remove(chunk);
            // State remains sparse and persistent; only its render mesh is released with the chunk.
        }

        public void Tick(int budget)
        {
            for (int i = 0; i < budget && _updates.Count > 0; i++)
            {
                RingVoxelCoordinate voxel = _updates.Dequeue();
                _queued.Remove(voxel);
                if (!_cells.TryGetValue(voxel, out VoxelFluidState state)) continue;
                if (!TryCanonical(voxel, out _, out RingChunkCoordinate chunk, out _)) continue;
                if (!_owner._chunks.ContainsKey(chunk)) { Defer(voxel, chunk); continue; }
                Simulate(voxel, state);
            }
        }

        public void ServiceDirtyMeshes(int budget)
        {
            for (int i = 0; i < budget && _dirtyMeshes.Count > 0; i++)
            {
                RingChunkCoordinate chunk = _dirtyMeshes.Dequeue();
                _dirtyMeshSet.Remove(chunk);
                if (!_owner._chunks.TryGetValue(chunk, out VoxelChunkNode? node)) continue;
                BuildChunkMesh(chunk, node);
            }
        }

        private void Simulate(in RingVoxelCoordinate voxel, in VoxelFluidState state)
        {
            if (!state.IsSource && !HasIncomingFlow(voxel, state))
            {
                _cells.Remove(voxel);
                MarkMeshNeighborhood(voxel);
                QueueNeighbors(voxel);
                return;
            }
            RingVoxelCoordinate below = new(voxel.AroundRing, voxel.AcrossRing, voxel.Vertical - 1);
            if (TryFlowInto(below, 8, falling: true))
            {
                Queue(voxel); // Sources continually feed a vertical fall; flowing cells settle next tick.
                return;
            }

            int sideLevel = state.IsSource ? 7 : state.Level - 1;
            if (sideLevel <= 0) return;
            TryFlowInto(new RingVoxelCoordinate(voxel.AroundRing - 1, voxel.AcrossRing, voxel.Vertical), sideLevel, false);
            TryFlowInto(new RingVoxelCoordinate(voxel.AroundRing + 1, voxel.AcrossRing, voxel.Vertical), sideLevel, false);
            TryFlowInto(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing - 1, voxel.Vertical), sideLevel, false);
            TryFlowInto(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing + 1, voxel.Vertical), sideLevel, false);
        }

        private bool HasIncomingFlow(in RingVoxelCoordinate voxel, in VoxelFluidState state)
        {
            if (_cells.ContainsKey(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing, voxel.Vertical + 1))) return true;
            foreach (RingVoxelCoordinate neighbor in new[]
            {
                new RingVoxelCoordinate(voxel.AroundRing - 1, voxel.AcrossRing, voxel.Vertical),
                new RingVoxelCoordinate(voxel.AroundRing + 1, voxel.AcrossRing, voxel.Vertical),
                new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing - 1, voxel.Vertical),
                new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing + 1, voxel.Vertical)
            })
            {
                if (_cells.TryGetValue(neighbor, out VoxelFluidState other)
                    && (other.IsSource || other.Level > state.Level)) return true;
            }
            return false;
        }

        private bool TryFlowInto(in RingVoxelCoordinate raw, int level, bool falling)
        {
            if (!TryCanonical(raw, out RingVoxelCoordinate target, out RingChunkCoordinate chunk, out _)) return false;
            if (!TryGetContainer(target, out bool waterlogged)) return false;
            if (_cells.TryGetValue(target, out VoxelFluidState existing)
                && (existing.IsSource || existing.Level >= level)) return false;
            Set(target, VoxelFluidState.Flowing(level, falling, waterlogged));
            Queue(target, chunk);
            return true;
        }

        private bool TryGetContainer(in RingVoxelCoordinate voxel, out bool waterlogged)
        {
            waterlogged = false;
            BlockType block = GetBlock(voxel);
            if (block == BlockType.Air) return true;
            // Glass is the first supported waterloggable construction block.
            // Additional partial blocks can opt in later without changing fluid storage.
            if (block == BlockType.Glass) { waterlogged = true; return true; }
            return false;
        }

        private BlockType GetBlock(in RingVoxelCoordinate voxel)
        {
            if (!TryCanonical(voxel, out _, out RingChunkCoordinate chunk, out Vector3I local)) return BlockType.Bedrock;
            if (_owner._chunks.TryGetValue(chunk, out VoxelChunkNode? node)
                && node.TryGetRetainedBlock(local, out BlockType retained)) return retained;
            if (_owner._chunkOverrides.TryGetValue(chunk, out Dictionary<int, ushort>? overrides)
                && overrides.TryGetValue(VoxelChunkData.ToLinearIndex(local.X, local.Y, local.Z), out ushort value))
                return (BlockType)value;
            return _owner.GetGeneratedBaselineBlock(chunk, local.X, local.Y, local.Z);
        }

        private bool TryCanonical(in RingVoxelCoordinate raw, out RingVoxelCoordinate voxel, out RingChunkCoordinate chunk, out Vector3I local)
        {
            long aroundLength = _owner.GetAroundRingVoxelLength();
            voxel = new RingVoxelCoordinate(RingCoordMath.WrapAround(raw.AroundRing, aroundLength), raw.AcrossRing, raw.Vertical);
            if (!RingCoordMath.TryToLocalVoxel(voxel, aroundLength, out chunk, out local)
                || !_owner.IsChunkWithinVerticalBounds(chunk)
                || !_owner.IsAcrossVoxelWithinRingBounds(voxel.AcrossRing)) return false;
            chunk = _owner.CanonicalizeChunkCoordinate(chunk);
            return true;
        }

        private void Set(in RingVoxelCoordinate voxel, in VoxelFluidState state)
        {
            _cells[voxel] = state;
            MarkMeshNeighborhood(voxel);
            QueueNeighbors(voxel);
        }

        private void QueueNeighbors(in RingVoxelCoordinate voxel)
        {
            Queue(voxel);
            Queue(new RingVoxelCoordinate(voxel.AroundRing - 1, voxel.AcrossRing, voxel.Vertical));
            Queue(new RingVoxelCoordinate(voxel.AroundRing + 1, voxel.AcrossRing, voxel.Vertical));
            Queue(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing - 1, voxel.Vertical));
            Queue(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing + 1, voxel.Vertical));
            Queue(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing, voxel.Vertical - 1));
            Queue(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing, voxel.Vertical + 1));
        }

        private void Queue(in RingVoxelCoordinate raw) => Queue(raw, default, hasChunk: false);
        private void Queue(in RingVoxelCoordinate raw, in RingChunkCoordinate knownChunk) => Queue(raw, knownChunk, hasChunk: true);
        private void Queue(in RingVoxelCoordinate raw, in RingChunkCoordinate knownChunk, bool hasChunk)
        {
            if (!TryCanonical(raw, out RingVoxelCoordinate voxel, out RingChunkCoordinate chunk, out _)) return;
            if (hasChunk) chunk = knownChunk;
            if (!_owner._chunks.ContainsKey(chunk)) { Defer(voxel, chunk); return; }
            if (_queued.Add(voxel)) _updates.Enqueue(voxel);
        }

        private void Defer(in RingVoxelCoordinate voxel, in RingChunkCoordinate chunk)
        {
            if (!_deferred.TryGetValue(chunk, out HashSet<RingVoxelCoordinate>? set))
            {
                set = new HashSet<RingVoxelCoordinate>();
                _deferred[chunk] = set;
            }
            set.Add(voxel);
        }

        private void MarkDirty(in RingChunkCoordinate raw)
        {
            RingChunkCoordinate chunk = _owner.CanonicalizeChunkCoordinate(raw);
            if (_dirtyMeshSet.Add(chunk)) _dirtyMeshes.Enqueue(chunk);
        }

        private void MarkMeshNeighborhood(in RingVoxelCoordinate voxel)
        {
            MarkMeshForVoxel(voxel);
            MarkMeshForVoxel(new RingVoxelCoordinate(voxel.AroundRing - 1, voxel.AcrossRing, voxel.Vertical));
            MarkMeshForVoxel(new RingVoxelCoordinate(voxel.AroundRing + 1, voxel.AcrossRing, voxel.Vertical));
            MarkMeshForVoxel(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing - 1, voxel.Vertical));
            MarkMeshForVoxel(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing + 1, voxel.Vertical));
        }

        private void MarkMeshForVoxel(in RingVoxelCoordinate voxel)
        {
            if (TryCanonical(voxel, out _, out RingChunkCoordinate chunk, out _)) MarkDirty(chunk);
        }

        private void BuildChunkMesh(in RingChunkCoordinate chunk, VoxelChunkNode node)
        {
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var indices = new List<int>();
            long baseAround = chunk.AroundRingChunk * VoxelConstants.ChunkSizeX;
            int baseAcross = chunk.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
            int baseVertical = chunk.VerticalChunk * VoxelConstants.ChunkSizeY;
            for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
            {
                var voxel = new RingVoxelCoordinate(baseAround + x, baseAcross + z, baseVertical + y);
                if (!_cells.TryGetValue(voxel, out VoxelFluidState state) || state.IsWaterlogged) continue;
                float height = state.Level / 8.0f;
                if (!_cells.ContainsKey(new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing, voxel.Vertical + 1)))
                    AddTop(vertices, normals, indices, voxel, x, y, z, height);
                AddSides(vertices, normals, indices, voxel, x, y, z, height);
            }
            node.InstallFluidMesh(vertices.ToArray(), normals.ToArray(), indices.ToArray());
        }

        private void AddTop(List<Vector3> v, List<Vector3> n, List<int> i, RingVoxelCoordinate voxel, int x, int y, int z, float height)
        {
            float h00 = CornerHeight(voxel, 0, 0, height), h10 = CornerHeight(voxel, 1, 0, height);
            float h11 = CornerHeight(voxel, 1, 1, height), h01 = CornerHeight(voxel, 0, 1, height);
            AddQuad(v, n, i, new Vector3(x, y + h00, z), new Vector3(x + 1, y + h10, z), new Vector3(x + 1, y + h11, z + 1), new Vector3(x, y + h01, z + 1));
        }

        private float CornerHeight(RingVoxelCoordinate voxel, int dx, int dz, float fallback)
        {
            float best = fallback;
            for (int oz = -1; oz <= 0; oz++) for (int ox = -1; ox <= 0; ox++)
            {
                var sample = new RingVoxelCoordinate(voxel.AroundRing + dx + ox, voxel.AcrossRing + dz + oz, voxel.Vertical);
                if (_cells.TryGetValue(sample, out VoxelFluidState state)) best = Mathf.Max(best, state.Level / 8.0f);
            }
            return best;
        }

        private void AddSides(List<Vector3> v, List<Vector3> n, List<int> i, RingVoxelCoordinate voxel, int x, int y, int z, float h)
        {
            AddSideIfExposed(v, n, i, voxel, new RingVoxelCoordinate(voxel.AroundRing - 1, voxel.AcrossRing, voxel.Vertical), new Vector3(x, y, z + 1), new Vector3(x, y, z), h);
            AddSideIfExposed(v, n, i, voxel, new RingVoxelCoordinate(voxel.AroundRing + 1, voxel.AcrossRing, voxel.Vertical), new Vector3(x + 1, y, z), new Vector3(x + 1, y, z + 1), h);
            AddSideIfExposed(v, n, i, voxel, new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing - 1, voxel.Vertical), new Vector3(x, y, z), new Vector3(x + 1, y, z), h);
            AddSideIfExposed(v, n, i, voxel, new RingVoxelCoordinate(voxel.AroundRing, voxel.AcrossRing + 1, voxel.Vertical), new Vector3(x + 1, y, z + 1), new Vector3(x, y, z + 1), h);
        }

        private void AddSideIfExposed(List<Vector3> v, List<Vector3> n, List<int> i, RingVoxelCoordinate current, RingVoxelCoordinate neighbor, Vector3 a, Vector3 b, float h)
        {
            if (_cells.TryGetValue(neighbor, out VoxelFluidState other) && other.Level >= _cells[current].Level) return;
            AddQuad(v, n, i, a, b, b + Vector3.Up * h, a + Vector3.Up * h);
        }

        private static void AddQuad(List<Vector3> v, List<Vector3> n, List<int> i, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 normal = (b - a).Cross(c - a).Normalized(); int start = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d); n.Add(normal); n.Add(normal); n.Add(normal); n.Add(normal);
            i.Add(start); i.Add(start + 1); i.Add(start + 2); i.Add(start); i.Add(start + 2); i.Add(start + 3);
        }
    }

}
