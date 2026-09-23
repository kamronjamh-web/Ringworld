using Godot;
using RingworldTechDemo.Voxel.Core;
using System;

namespace RingworldTechDemo.Voxel.Phase1;

public partial class VoxelChunkNode : Node3D
{
    [Export] public bool GenerateOnReady { get; set; } = true;
    [Export(PropertyHint.Range, "1,31,1")] public int InitialSolidHeight { get; set; } = 10;
    [Export] public VoxelBlockColorMap? BlockColorMap { get; set; }
    public bool BuildCollisionOnRebuild { get; set; } = true;

    private MeshInstance3D? _meshInstance;
    private MeshInstance3D? _fluidMeshInstance;
    private StaticBody3D? _staticBody;
    private CollisionShape3D? _collisionShape;
    private Func<int, int, int, BlockType>? _externalBlockSampler;
    private Func<Vector3, Vector3>? _localMeshVertexWarp;
    private Func<Vector3, Vector3>? _localMeshVertexUnwarp;
    private Material? _sharedMaterialOverride;
    private Vector3[] _collisionFaces = Array.Empty<Vector3>();
    private VoxelChunkData? _data = new();
    private Func<VoxelChunkData>? _voxelDataRestorer;

    /// <summary>
    /// Resident chunks may drop deterministic, unedited voxel storage outside
    /// the collision bubble. The grid supplies an on-demand baseline + edit
    /// restorer so gameplay edits and collision promotion remain exact.
    /// </summary>
    public VoxelChunkData Data => _data ??= _voxelDataRestorer?.Invoke() ?? new VoxelChunkData();
    public bool HasRetainedVoxelData => _data != null;
    public double LastMeshArrayPreparationMilliseconds { get; private set; }
    public double LastMeshSurfaceUploadMilliseconds { get; private set; }
    public double LastMeshAssignmentMilliseconds { get; private set; }
    public double LastCollisionInstallMilliseconds { get; private set; }
    public double LastMeshInstallMilliseconds { get; private set; }
    /// <summary>
    /// True only after a render mesh has been installed for this chunk.  This is
    /// deliberately independent of Node.Visible: a streaming node can be visible
    /// for a frame while its asynchronous mesh upload is still pending.
    /// </summary>
    public bool IsRenderMeshReady => _meshInstance?.Mesh != null;
    public bool IsCollisionReady => _collisionShape?.Shape != null;

    public override void _Ready()
    {
        EnsureChildNodes();

        if (GenerateOnReady)
        {
            Data.FillFlatTestTerrain(InitialSolidHeight);
            Rebuild();
        }
    }

    public bool TrySetBlock(Vector3I localVoxel, BlockType blockType, bool rebuild = true)
    {
        if (!Data.IsInBounds(localVoxel.X, localVoxel.Y, localVoxel.Z))
        {
            return false;
        }

        BlockType current = Data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
        // Bedrock is immutable in Phase 1 test behavior.
        if (current == BlockType.Bedrock && blockType != BlockType.Bedrock)
        {
            return false;
        }

        if (current == blockType)
        {
            return true;
        }

        Data.SetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z, blockType);

        if (rebuild)
        {
            Rebuild();
        }

        return true;
    }

    public void SetExternalBlockSampler(Func<int, int, int, BlockType>? sampler)
    {
        _externalBlockSampler = sampler;
    }

    public void SetVoxelDataRestorer(Func<VoxelChunkData>? restorer)
    {
        _voxelDataRestorer = restorer;
    }

    public bool TryGetRetainedBlock(Vector3I localVoxel, out BlockType block)
    {
        if (_data != null && _data.IsInBounds(localVoxel.X, localVoxel.Y, localVoxel.Z))
        {
            block = _data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
            return true;
        }
        block = BlockType.Air;
        return false;
    }

    public bool ReleaseVoxelData()
    {
        if (BuildCollisionOnRebuild || _data == null)
            return false;
        _data = null;
        return true;
    }

    public void SetSharedMaterialOverride(Material? material)
    {
        _sharedMaterialOverride = material;
        if (_meshInstance != null)
        {
            _meshInstance.MaterialOverride = material;
        }
    }

    public void ConfigureRingCurvatureWarp(
        bool enabled,
        float angularRadiusMeters,
        float radialBaseMeters,
        float arcStartMeters,
        float warpStrength = 1.0f,
        bool radialDepthAxisY = false)
    {
        if (!enabled || warpStrength <= 0.0001f)
        {
            _localMeshVertexWarp = null;
            _localMeshVertexUnwarp = null;
            return;
        }

        _ = arcStartMeters;

        float safeAngularRadius = Mathf.Max(1.0f, angularRadiusMeters);
        float safeRadialBase = Mathf.Max(1.0f, radialBaseMeters);
        float safeStrength = Mathf.Clamp(warpStrength, 0.0f, 1.0f);

        _localMeshVertexWarp = CreateRingVertexWarp(
            safeAngularRadius,
            safeRadialBase,
            safeStrength,
            radialDepthAxisY);
        _localMeshVertexUnwarp = local => UnwarpLocalVertexForRing(local, safeAngularRadius, safeRadialBase, safeStrength, radialDepthAxisY);
    }

    public static Func<Vector3, Vector3> CreateRingVertexWarp(
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength,
        bool radialDepthAxisY)
    {
        float safeAngularRadius = Mathf.Max(1.0f, angularRadiusMeters);
        float safeRadialBase = Mathf.Max(1.0f, radialBaseMeters);
        float safeStrength = Mathf.Clamp(warpStrength, 0.0f, 1.0f);
        return local => WarpLocalVertexForRing(
            local,
            safeAngularRadius,
            safeRadialBase,
            safeStrength,
            radialDepthAxisY);
    }

    public BlockType GetBlockLocalOrAir(Vector3I localVoxel)
    {
        return Data.GetBlockSafe(localVoxel.X, localVoxel.Y, localVoxel.Z);
    }

    /// <returns>True when the requested collision state is immediately ready.</returns>
    public bool SetCollisionEnabled(bool enabled)
    {
        EnsureChildNodes();
        BuildCollisionOnRebuild = enabled;
        if (!enabled)
        {
            _collisionShape!.Disabled = true;
            _collisionShape.Shape = null;
            // Collision triangles are expanded vertex data, so retaining them
            // outside the physics bubble is a substantial and unnecessary RAM
            // cost. Re-entering the bubble schedules an async collision remesh.
            _collisionFaces = Array.Empty<Vector3>();
            return true;
        }

        _collisionShape!.Disabled = false;
        RebuildCollisionFromCurrentMesh();
        bool ready = _collisionShape.Shape != null;
        if (!ready) _collisionShape.Disabled = true;
        return ready;
    }

    public void SetTerrainShadowCasting(bool enabled)
    {
        EnsureChildNodes();
        _meshInstance!.CastShadow = enabled
            ? GeometryInstance3D.ShadowCastingSetting.On
            : GeometryInstance3D.ShadowCastingSetting.Off;
    }

    public void PrepareForPool()
    {
        EnsureChildNodes();
        _externalBlockSampler = null;
        _localMeshVertexWarp = null;
        _localMeshVertexUnwarp = null;
        _sharedMaterialOverride = null;
        _meshInstance!.Mesh = null;
        _meshInstance.MaterialOverride = null;
        _fluidMeshInstance!.Mesh = null;
        _collisionShape!.Shape = null;
        _collisionShape.Disabled = true;
        _collisionFaces = Array.Empty<Vector3>();
        _data = null;
        _voxelDataRestorer = null;
        BuildCollisionOnRebuild = false;
        Visible = false;
        ProcessMode = ProcessModeEnum.Disabled;
    }

    public void PrepareForReuse(bool buildCollision)
    {
        EnsureChildNodes();
        ProcessMode = ProcessModeEnum.Inherit;
        _data = new VoxelChunkData();
        BuildCollisionOnRebuild = buildCollision;
        _collisionShape!.Disabled = !buildCollision;
        if (!buildCollision)
            _collisionShape.Shape = null;
        _fluidMeshInstance!.Mesh = null;
    }

    public void Rebuild()
    {
        EnsureChildNodes();

        MeshBuildData build = VoxelChunkMesher.Build(
            Data,
            VoxelBlockPalette.Capture(BlockColorMap),
            _externalBlockSampler,
            _localMeshVertexWarp,
            buildCollisionFaces: BuildCollisionOnRebuild);

        InstallMeshBuildData(build);
    }

    public void InstallStreamingBuild(VoxelChunkData data, in MeshBuildData build)
    {
        _data = data;
        InstallMeshBuildData(build);
    }

    public void InstallRebuild(in MeshBuildData build)
    {
        InstallMeshBuildData(build);
    }

    /// <summary>Installs an independently meshed, non-colliding water surface.</summary>
    public void InstallFluidMesh(Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        EnsureChildNodes();
        if (vertices.Length == 0 || indices.Length == 0)
        {
            _fluidMeshInstance!.Mesh = null;
            return;
        }
        var warped = new Vector3[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
            warped[index] = _localMeshVertexWarp?.Invoke(vertices[index]) ?? vertices[index];
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = warped;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _fluidMeshInstance!.Mesh = mesh;
    }

    private void InstallMeshBuildData(in MeshBuildData build)
    {
        ulong installStartUsec = Time.GetTicksUsec();
        EnsureChildNodes();
        _collisionFaces = build.CollisionFaces;
        var mesh = new ArrayMesh();
        LastMeshArrayPreparationMilliseconds = 0.0;
        LastMeshSurfaceUploadMilliseconds = 0.0;
        LastMeshAssignmentMilliseconds = 0.0;
        LastCollisionInstallMilliseconds = 0.0;
        if (build.HasGeometry)
        {
            ulong arraysStartUsec = Time.GetTicksUsec();
            Godot.Collections.Array arrays = build.CreateGodotMeshArrays();
            LastMeshArrayPreparationMilliseconds = (Time.GetTicksUsec() - arraysStartUsec) / 1000.0;
            ulong surfaceStartUsec = Time.GetTicksUsec();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            LastMeshSurfaceUploadMilliseconds = (Time.GetTicksUsec() - surfaceStartUsec) / 1000.0;
        }

        ulong assignmentStartUsec = Time.GetTicksUsec();
        _meshInstance!.Mesh = mesh;

        _meshInstance.MaterialOverride = _sharedMaterialOverride ?? new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 1.0f,
            Metallic = 0.0f
        };
        LastMeshAssignmentMilliseconds = (Time.GetTicksUsec() - assignmentStartUsec) / 1000.0;

        if (!build.HasGeometry || !BuildCollisionOnRebuild)
        {
            _collisionShape!.Shape = null;
            LastMeshInstallMilliseconds = (Time.GetTicksUsec() - installStartUsec) / 1000.0;
            return;
        }

        ulong collisionStartUsec = Time.GetTicksUsec();
        RebuildCollisionFromCurrentMesh();
        LastCollisionInstallMilliseconds = (Time.GetTicksUsec() - collisionStartUsec) / 1000.0;
        LastMeshInstallMilliseconds = (Time.GetTicksUsec() - installStartUsec) / 1000.0;
    }

    public Func<Vector3, Vector3>? CaptureLocalMeshVertexWarp() => _localMeshVertexWarp;

    private void RebuildCollisionFromCurrentMesh()
    {
        EnsureChildNodes();
        if (_meshInstance?.Mesh == null)
        {
            _collisionShape!.Shape = null;
            return;
        }

        Vector3[] faces = _collisionFaces;
        // Never read geometry back from the render mesh here. ArrayMesh.GetFaces
        // is synchronous and caused 30-40ms ground-streaming stalls. The grid
        // schedules the existing asynchronous resident remesh when CPU collision
        // faces are unavailable.
        if (faces.Length == 0)
        {
            _collisionShape!.Shape = null;
            return;
        }

        _collisionFaces = faces;
        var shape = new ConcavePolygonShape3D
        {
            Data = faces
        };

        _collisionShape!.Shape = shape;
    }

    public Vector3I WorldToLocalVoxel(Vector3 worldPosition)
    {
        Vector3 local = ToLocal(worldPosition);
        if (_localMeshVertexUnwarp != null)
        {
            local = _localMeshVertexUnwarp(local);
        }

        return new Vector3I(
            Mathf.FloorToInt(local.X / VoxelConstants.VoxelSizeMeters),
            Mathf.FloorToInt(local.Y / VoxelConstants.VoxelSizeMeters),
            Mathf.FloorToInt(local.Z / VoxelConstants.VoxelSizeMeters)
        );
    }

    public Vector3 LinearLocalToWorldPoint(Vector3 linearLocalPoint)
    {
        Vector3 warpedLocal = _localMeshVertexWarp != null
            ? _localMeshVertexWarp(linearLocalPoint)
            : linearLocalPoint;

        return ToGlobal(warpedLocal);
    }

    private static Vector3 WarpLocalVertexForRing(
        Vector3 localVertex,
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength,
        bool radialDepthAxisY)
    {
        if (radialDepthAxisY)
        {
            Vector2 curvedDepth = MapLinearToCurvedInwardLocal(
                localVertex.X,
                localVertex.Y,
                angularRadiusMeters,
                radialBaseMeters,
                warpStrength);
            return new Vector3(curvedDepth.X, curvedDepth.Y, localVertex.Z);
        }

        Vector2 curved = MapLinearToCurvedLocal(localVertex.X, localVertex.Z, angularRadiusMeters, radialBaseMeters, warpStrength);
        return new Vector3(curved.X, localVertex.Y, curved.Y);
    }

    private static Vector3 UnwarpLocalVertexForRing(
        Vector3 localVertex,
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength,
        bool radialDepthAxisY)
    {
        if (radialDepthAxisY)
        {
            Vector2 linearDepth = MapCurvedToLinearInwardLocal(
                localVertex.X,
                localVertex.Y,
                angularRadiusMeters,
                radialBaseMeters,
                warpStrength);
            return new Vector3(linearDepth.X, linearDepth.Y, localVertex.Z);
        }

        Vector2 linear = MapCurvedToLinearLocal(localVertex.X, localVertex.Z, angularRadiusMeters, radialBaseMeters, warpStrength);
        return new Vector3(linear.X, localVertex.Y, linear.Y);
    }

    private static Vector2 MapLinearToCurvedInwardLocal(
        float linearX,
        float linearY,
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength)
    {
        if (warpStrength <= 0.0001f)
        {
            return new Vector2(linearX, linearY);
        }

        double theta = linearX / (double)angularRadiusMeters;
        double radial = radialBaseMeters - (double)linearY;
        double sinTheta = Math.Sin(theta);
        // 1-cos(theta) is evaluated as 2*sin²(theta/2), avoiding the
        // subtraction of two five-million-metre floats for a sub-millimetre arc.
        double sinHalf = Math.Sin(theta * 0.5);
        double oneMinusCos = 2.0 * sinHalf * sinHalf;
        float curvedX = (float)(radial * sinTheta);
        float curvedY = (float)(linearY + radial * oneMinusCos);

        return new Vector2(
            Mathf.Lerp(linearX, curvedX, warpStrength),
            Mathf.Lerp(linearY, curvedY, warpStrength));
    }

    private static Vector2 MapCurvedToLinearInwardLocal(
        float warpedX,
        float warpedY,
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength)
    {
        if (warpStrength <= 0.0001f)
        {
            return new Vector2(warpedX, warpedY);
        }

        float x = warpedX;
        float y = warpedY;
        for (int i = 0; i < 6; i++)
        {
            Vector2 forward = MapLinearToCurvedInwardLocal(x, y, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 error = forward - new Vector2(warpedX, warpedY);
            if (error.LengthSquared() < 0.000001f)
            {
                break;
            }

            const float h = 0.01f;
            Vector2 xPlus = MapLinearToCurvedInwardLocal(x + h, y, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 xMinus = MapLinearToCurvedInwardLocal(x - h, y, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 yPlus = MapLinearToCurvedInwardLocal(x, y + h, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 yMinus = MapLinearToCurvedInwardLocal(x, y - h, angularRadiusMeters, radialBaseMeters, warpStrength);
            float a = (xPlus.X - xMinus.X) / (2.0f * h);
            float b = (yPlus.X - yMinus.X) / (2.0f * h);
            float c = (xPlus.Y - xMinus.Y) / (2.0f * h);
            float d = (yPlus.Y - yMinus.Y) / (2.0f * h);
            float det = (a * d) - (b * c);
            if (Mathf.Abs(det) < 0.000001f)
            {
                break;
            }

            x -= ((d * error.X) - (b * error.Y)) / det;
            y -= ((-c * error.X) + (a * error.Y)) / det;
        }

        return new Vector2(x, y);
    }

    private static Vector2 MapLinearToCurvedLocal(
        float linearX,
        float linearZ,
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength)
    {
        if (warpStrength <= 0.0001f)
        {
            return new Vector2(linearX, linearZ);
        }

        double theta = linearX / (double)angularRadiusMeters;
        double radial = radialBaseMeters + (double)linearZ;
        double sinTheta = Math.Sin(theta);
        double sinHalf = Math.Sin(theta * 0.5);
        double oneMinusCos = 2.0 * sinHalf * sinHalf;

        float curvedX = (float)(radial * sinTheta);
        float curvedZ = (float)(linearZ - radial * oneMinusCos);

        return new Vector2(
            Mathf.Lerp(linearX, curvedX, warpStrength),
            Mathf.Lerp(linearZ, curvedZ, warpStrength));
    }

    private static Vector2 MapCurvedToLinearLocal(
        float warpedX,
        float warpedZ,
        float angularRadiusMeters,
        float radialBaseMeters,
        float warpStrength)
    {
        if (warpStrength <= 0.0001f)
        {
            return new Vector2(warpedX, warpedZ);
        }

        float x = warpedX;
        float z = warpedZ;

        for (int i = 0; i < 6; i++)
        {
            Vector2 forward = MapLinearToCurvedLocal(x, z, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 error = forward - new Vector2(warpedX, warpedZ);
            if (error.LengthSquared() < 0.000001f)
            {
                break;
            }

            const float h = 0.01f;
            Vector2 forwardXPlus = MapLinearToCurvedLocal(x + h, z, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 forwardXMinus = MapLinearToCurvedLocal(x - h, z, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 forwardZPlus = MapLinearToCurvedLocal(x, z + h, angularRadiusMeters, radialBaseMeters, warpStrength);
            Vector2 forwardZMinus = MapLinearToCurvedLocal(x, z - h, angularRadiusMeters, radialBaseMeters, warpStrength);

            float a = (forwardXPlus.X - forwardXMinus.X) / (2.0f * h);
            float b = (forwardZPlus.X - forwardZMinus.X) / (2.0f * h);
            float c = (forwardXPlus.Y - forwardXMinus.Y) / (2.0f * h);
            float d = (forwardZPlus.Y - forwardZMinus.Y) / (2.0f * h);

            float det = (a * d) - (b * c);
            if (Mathf.Abs(det) < 0.000001f)
            {
                break;
            }

            float deltaX = ((d * error.X) - (b * error.Y)) / det;
            float deltaZ = ((-c * error.X) + (a * error.Y)) / det;

            x -= deltaX;
            z -= deltaZ;
        }

        return new Vector2(x, z);
    }

    private static float ComputeInwardOffsetForArc(float aroundArcMeters, float radiusMeters)
    {
        return radiusMeters * (1.0f - Mathf.Cos(aroundArcMeters / radiusMeters));
    }

    private void EnsureChildNodes()
    {
        _meshInstance ??= GetNodeOrNull<MeshInstance3D>("Mesh");
        _fluidMeshInstance ??= GetNodeOrNull<MeshInstance3D>("FluidMesh");
        _staticBody ??= GetNodeOrNull<StaticBody3D>("StaticBody");
        _collisionShape ??= _staticBody?.GetNodeOrNull<CollisionShape3D>("CollisionShape3D");

        if (_meshInstance == null)
        {
            _meshInstance = new MeshInstance3D { Name = "Mesh" };
            AddChild(_meshInstance);
        }
        if (_fluidMeshInstance == null)
        {
            _fluidMeshInstance = new MeshInstance3D { Name = "FluidMesh", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _fluidMeshInstance.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.04f, 0.33f, 0.52f, 0.78f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Roughness = 0.12f
            };
            AddChild(_fluidMeshInstance);
        }

        if (_staticBody == null)
        {
            _staticBody = new StaticBody3D { Name = "StaticBody" };
            AddChild(_staticBody);
        }

        if (_collisionShape == null)
        {
            _collisionShape = new CollisionShape3D { Name = "CollisionShape3D" };
            _staticBody.AddChild(_collisionShape);
        }
    }
}
