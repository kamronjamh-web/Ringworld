using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainProxySector
{
    /// <summary>Headless regression for the 3D proxy handoff proof.</summary>
    public Godot.Collections.Dictionary RunVerticalLodCoverageContractTest()
    {
        var result = new Godot.Collections.Dictionary();
        var coordinate = new RingTerrainSectorCoordinate(0, 0);
        var focus = new Node3D();
        var node = new MeshInstance3D { Mesh = CreateVerticalCoverageFixtureMesh() };
        try
        {
            StreamingActive = true;
            _transitHidden = false;
            SectorSizeMeters = 32;
            EnableFarFade = true;
            FadeOutStartMeters = 100.0f;
            FadeOutEndMeters = 200.0f;
            _focus = focus;
            AddChild(focus);
            AddChild(node);
            _active[coordinate] = node;

            void Install(Aabb bounds)
            {
                _activeVisualBounds[coordinate] = bounds;
                _warmUntil.Remove(coordinate);
                _innerExcluded.Remove(coordinate);
            }
            bool Query() => IsSurfaceRegionVisuallyCovered(0, 32, 0, 32);

            // Viewer altitude over its own ground can be zero while the nearby
            // mountain geometry exceeds the fine tier's true 3D far handoff.
            focus.GlobalPosition = Vector3.Zero;
            Install(new Aabb(new Vector3(0, 155, 0), new Vector3(2, 2, 2)));
            bool mountainRejected = !Query();

            // A summit can equally expose low terrain beyond the 3D cutoff.
            focus.GlobalPosition = new Vector3(0, 180, 0);
            Install(new Aabb(new Vector3(0, 0, 0), new Vector3(2, 2, 2)));
            bool summitRejected = !Query();

            focus.GlobalPosition = Vector3.Zero;
            Install(new Aabb(new Vector3(20, 0, 0), new Vector3(2, 2, 2)));
            bool levelAccepted = Query();

            // Runtime chords must be part of the cached bound, including when
            // the original vertex itself is safely in range.
            Aabb chordBounds = CalculateCompleteVisualBounds(
                new[] { Vector3.Zero }, new[] { Vector2.Zero },
                new[] { new Vector3(180, 0, 0) }, true);
            Install(chordBounds);
            bool chordRejected = !Query();

            // A runtime-chord sector still uses UV2.x for water/skirt markers.
            // That endpoint must not disappear from the cached visual bound.
            Aabb mixedBounds = CalculateCompleteVisualBounds(
                new[] { Vector3.Zero, Vector3.Zero },
                new[] { Vector2.Zero, new Vector2(180, 1) },
                new[] { new Vector3(20, 0, 0), Vector3.Zero }, true);
            Install(mixedBounds);
            bool mixedEndpointRejected = !Query();

            _active.Remove(coordinate);
            bool missingRejected = !Query();
            _active[coordinate] = node;

            // An inner-excluded sector may only hand off if its own provider
            // can prove replacement; an empty deeper level must fail closed.
            var absentDeeperLevel = new RingTerrainProxySector
            {
                StreamingActive = true,
                EnableFarFade = true,
                FadeOutStartMeters = FadeOutStartMeters,
                FadeOutEndMeters = FadeOutEndMeters,
                SectorSizeMeters = SectorSizeMeters
            };
            absentDeeperLevel._focus = focus;
            _finerCoverageLevel = absentDeeperLevel;
            _innerExcluded.Add(coordinate);
            bool innerExcludedRejected = !Query();
            _innerExcluded.Remove(coordinate);
            _finerCoverageLevel = null;
            absentDeeperLevel.QueueFree();

            Install(new Aabb(new Vector3(20, 0, 0), new Vector3(2, 2, 2)));
            _warmUntil[coordinate] = double.MaxValue;
            bool warmRejected = !Query();
            _warmUntil.Remove(coordinate);

            // Bounds are local. Moving the installed node and viewer together
            // models a floating-origin rebase and must preserve the proof.
            node.GlobalPosition = new Vector3(10_000, 0, 0);
            focus.GlobalPosition = new Vector3(10_000, 0, 0);
            bool rebaseAccepted = Query();

            // Revoke an already-installed handoff bit after movement within a
            // 1024m coarse sector. The off-tree grid supplies the exact same
            // canonical conversion used by the production safety service.
            var grid = new FixtureGrid
            {
                UseRingCurvedPlacement = false,
                UseHabitableRingAxisLayout = false
            };
            AddChild(grid);
            var movingFine = new RingTerrainProxySector
            {
                StreamingActive = true,
                EnableFarFade = true,
                FadeOutStartMeters = 100.0f,
                FadeOutEndMeters = 200.0f,
                SectorSizeMeters = 1024
            };
            movingFine.CoordinatorManaged = true;
            var movingFineNode = new MeshInstance3D { Mesh = CreateVerticalCoverageFixtureMesh() };
            AddChild(movingFine);
            movingFine._grid = grid;
            movingFine._focus = focus;
            movingFine.AddChild(movingFineNode);
            var movementCoordinate = new RingTerrainSectorCoordinate(7, 0);
            movingFine._active[movementCoordinate] = movingFineNode;
            movingFine._activeVisualBounds[movementCoordinate] = new Aabb(new Vector3(7520, 0, 0), new Vector3(2, 2, 2));
            _grid = grid;
            _finerCoverageLevel = movingFine;
            SectorSizeMeters = 1024;
            SampleSpacingMeters = 1024;
            _active[movementCoordinate] = node;
            Mesh fullSourceMesh = node.Mesh!;
            var alternateMesh = CreateVerticalCoverageFixtureMesh();
            node.Mesh = alternateMesh;
            _partialCoverageSources[movementCoordinate] = new PartialCoverageMeshSource
            {
                Vertices = Array.Empty<Vector3>(), Normals = Array.Empty<Vector3>(), Colors = Array.Empty<Color>(),
                Uvs = Array.Empty<Vector2>(), MorphData = Array.Empty<Vector2>(), ParentChordOffsets = Array.Empty<Vector3>(),
                TerrainIndices = Array.Empty<int>(), OceanIndices = Array.Empty<int>(), UsesRuntimeParentChord = false,
                Cells = 1, FullMesh = fullSourceMesh
            };
            var movementMask = new SurfaceCoverageMask(1, 1, new[] { true });
            _activeCoverageMasks[movementCoordinate] = movementMask;
            _latestCoverageMasks[movementCoordinate] = movementMask;
            node.GlobalPosition = Vector3.Zero;
            focus.GlobalPosition = new Vector3(7500, 0, 0);
            bool directFineSafe = movingFine.IsSurfaceRegionVisuallyCovered(7168, 8192, 0, 1024);
            ServiceCoverageSafetyInvalidation();
            bool movementInitiallySafe = directFineSafe && _activeCoverageMasks[movementCoordinate].CoveredCells[0];
            focus.GlobalPosition = new Vector3(7340, 0, 0);
            ServiceCoverageSafetyInvalidation();
            bool movementRevoked = !_activeCoverageMasks[movementCoordinate].CoveredCells[0]
                && !_latestCoverageMasks[movementCoordinate].CoveredCells[0] && node.Visible
                && ReferenceEquals(node.Mesh, fullSourceMesh);
            movingFine.RemoveChild(movingFineNode);
            RemoveChild(movingFine);
            movingFineNode.QueueFree();
            movingFine.QueueFree();
            RemoveChild(grid);
            grid.QueueFree();
            _activeCoverageMasks.Remove(movementCoordinate);
            _latestCoverageMasks.Remove(movementCoordinate);
            _active.Remove(movementCoordinate);
            _partialCoverageSources.Remove(movementCoordinate);
            alternateMesh.Dispose();
            _finerCoverageLevel = null;
            _grid = null;

            if (!mountainRejected || !summitRejected || !levelAccepted || !chordRejected
                || !mixedEndpointRejected || !missingRejected || !innerExcludedRejected
                || !warmRejected || !rebaseAccepted || !movementInitiallySafe || !movementRevoked)
                throw new InvalidOperationException($"Vertical LOD coverage contract failed: mountain={mountainRejected}, summit={summitRejected}, level={levelAccepted}, chord={chordRejected}, mixed={mixedEndpointRejected}, missing={missingRejected}, excluded={innerExcludedRejected}, warm={warmRejected}, rebase={rebaseAccepted}, directFine={directFineSafe}, movementSafe={movementInitiallySafe}, movementRevoked={movementRevoked}.");

            result["passed"] = true;
            result["mountain_rejected"] = mountainRejected;
            result["summit_rejected"] = summitRejected;
            result["level_accepted"] = levelAccepted;
            result["runtime_chord_rejected"] = chordRejected;
            result["mixed_radial_endpoint_rejected"] = mixedEndpointRejected;
            result["missing_rejected"] = missingRejected;
            result["inner_excluded_without_replacement_rejected"] = innerExcludedRejected;
            result["warm_rejected"] = warmRejected;
            result["rebase_accepted"] = rebaseAccepted;
            result["movement_initially_safe"] = movementInitiallySafe;
            result["movement_revoked"] = movementRevoked;
            return result;
        }
        catch (Exception exception)
        {
            GD.PushError($"Vertical LOD coverage contract failed: {exception.Message}");
            result["passed"] = false;
            result["error"] = exception.Message;
            return result;
        }
        finally
        {
            _active.Remove(coordinate);
            _activeVisualBounds.Remove(coordinate);
            _warmUntil.Remove(coordinate);
            _innerExcluded.Remove(coordinate);
            _finerCoverageLevel = null;
            if (node.GetParent() == this) RemoveChild(node);
            if (focus.GetParent() == this) RemoveChild(focus);
            node.QueueFree();
            focus.QueueFree();
            _focus = null;
        }
    }

    private static ArrayMesh CreateVerticalCoverageFixtureMesh()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Forward };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // The coverage contract needs the grid's canonical conversion but must not
    // start a world/chunk generation pipeline when attached to the test tree.
    private sealed partial class FixtureGrid : VoxelChunkGridManager
    {
        public override void _Ready() { }
    }
}
