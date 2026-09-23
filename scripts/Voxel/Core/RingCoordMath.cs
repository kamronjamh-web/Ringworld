namespace RingworldTechDemo.Voxel.Core;

public static class RingCoordMath
{
    public static long WrapAround(long aroundRing, long aroundRingVoxelLength)
    {
        if (aroundRingVoxelLength <= 0)
        {
            return aroundRing;
        }

        long wrapped = aroundRing % aroundRingVoxelLength;
        return wrapped < 0 ? wrapped + aroundRingVoxelLength : wrapped;
    }

    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        return (r != 0 && ((r < 0) != (b < 0))) ? q - 1 : q;
    }

    public static long WrapAroundChunk(long aroundChunk, long aroundRingChunkLength)
    {
        if (aroundRingChunkLength <= 0)
        {
            return aroundChunk;
        }

        long wrapped = aroundChunk % aroundRingChunkLength;
        return wrapped < 0 ? wrapped + aroundRingChunkLength : wrapped;
    }

    public static RingChunkCoordinate NormalizeChunkCoordinate(in RingChunkCoordinate chunkCoordinate, long aroundRingChunkLength)
    {
        if (aroundRingChunkLength <= 0)
        {
            return chunkCoordinate;
        }

        return chunkCoordinate with
        {
            AroundRingChunk = WrapAroundChunk(chunkCoordinate.AroundRingChunk, aroundRingChunkLength)
        };
    }

    public static RingChunkCoordinate ToChunkCoordinate(in RingVoxelCoordinate voxelCoordinate, long aroundRingVoxelLength)
    {
        long wrappedAround = WrapAround(voxelCoordinate.AroundRing, aroundRingVoxelLength);

        long aroundChunk = wrappedAround / VoxelConstants.ChunkSizeX;
        int acrossChunk = FloorDiv(voxelCoordinate.AcrossRing, VoxelConstants.ChunkSizeZ);
        int verticalChunk = FloorDiv(voxelCoordinate.Vertical, VoxelConstants.ChunkSizeY);

        return new RingChunkCoordinate(aroundChunk, acrossChunk, verticalChunk);
    }

    public static RingVoxelCoordinate ToVoxelCoordinate(
        in RingChunkCoordinate chunkCoordinate,
        in Godot.Vector3I localVoxel,
        long aroundRingVoxelLength)
    {
        long around = (long)chunkCoordinate.AroundRingChunk * VoxelConstants.ChunkSizeX + localVoxel.X;
        int across = chunkCoordinate.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localVoxel.Z;
        int vertical = chunkCoordinate.VerticalChunk * VoxelConstants.ChunkSizeY + localVoxel.Y;

        return new RingVoxelCoordinate(WrapAround(around, aroundRingVoxelLength), across, vertical);
    }

    public static bool TryToLocalVoxel(
        in RingVoxelCoordinate voxelCoordinate,
        long aroundRingVoxelLength,
        out RingChunkCoordinate chunkCoordinate,
        out Godot.Vector3I localVoxel)
    {
        long wrappedAround = WrapAround(voxelCoordinate.AroundRing, aroundRingVoxelLength);

        long aroundChunk = wrappedAround / VoxelConstants.ChunkSizeX;
        int aroundLocal = (int)(wrappedAround - aroundChunk * VoxelConstants.ChunkSizeX);

        int acrossChunk = FloorDiv(voxelCoordinate.AcrossRing, VoxelConstants.ChunkSizeZ);
        int acrossLocal = voxelCoordinate.AcrossRing - acrossChunk * VoxelConstants.ChunkSizeZ;

        int verticalChunk = FloorDiv(voxelCoordinate.Vertical, VoxelConstants.ChunkSizeY);
        int verticalLocal = voxelCoordinate.Vertical - verticalChunk * VoxelConstants.ChunkSizeY;

        bool localValid = aroundLocal >= 0 && aroundLocal < VoxelConstants.ChunkSizeX
            && acrossLocal >= 0 && acrossLocal < VoxelConstants.ChunkSizeZ
            && verticalLocal >= 0 && verticalLocal < VoxelConstants.ChunkSizeY;

        if (!localValid)
        {
            chunkCoordinate = default;
            localVoxel = default;
            return false;
        }

        chunkCoordinate = new RingChunkCoordinate(aroundChunk, acrossChunk, verticalChunk);
        localVoxel = new Godot.Vector3I(aroundLocal, verticalLocal, acrossLocal);
        return true;
    }

    public static bool RunSeamWrapSelfTests()
    {
        if (!CertifiedRingScale.Validate(out _)
            || !MegaRingTopology.RunSelfTest()
            || !RunTopologyWrapSelfTest(CertifiedRingScale.AroundVoxelLength)
            || !RunFullCircumferenceTravelSelfTest())
        {
            return false;
        }

        const int aroundLength = 128;
        int aroundChunkLength = aroundLength / VoxelConstants.ChunkSizeX;

        if (WrapAround(-1, aroundLength) != aroundLength - 1)
        {
            return false;
        }

        if (WrapAround(aroundLength, aroundLength) != 0)
        {
            return false;
        }

        if (WrapAroundChunk(-1, aroundChunkLength) != aroundChunkLength - 1)
        {
            return false;
        }

        if (WrapAroundChunk(aroundChunkLength, aroundChunkLength) != 0)
        {
            return false;
        }

        var seamVoxel = new RingVoxelCoordinate(aroundLength, 5, 7);
        RingChunkCoordinate seamChunk = ToChunkCoordinate(seamVoxel, aroundLength);
        if (seamChunk.AroundRingChunk != 0)
        {
            return false;
        }

        var edgeVoxel = new RingVoxelCoordinate(-1, 0, 0);
        RingChunkCoordinate edgeChunk = ToChunkCoordinate(edgeVoxel, aroundLength);
        if (edgeChunk.AroundRingChunk != aroundChunkLength - 1)
        {
            return false;
        }

        bool localOk = TryToLocalVoxel(edgeVoxel, aroundLength, out RingChunkCoordinate localChunk, out Godot.Vector3I localVoxel);
        if (!localOk)
        {
            return false;
        }

        if (localChunk.AroundRingChunk != aroundChunkLength - 1 || localVoxel.X != VoxelConstants.ChunkSizeX - 1)
        {
            return false;
        }

        RingVoxelCoordinate roundTrip = ToVoxelCoordinate(localChunk, localVoxel, aroundLength);
        if (roundTrip.AroundRing != WrapAround(edgeVoxel.AroundRing, aroundLength))
        {
            return false;
        }

        return true;
    }

    private static bool RunTopologyWrapSelfTest(int aroundLength)
    {
        int aroundChunkLength = aroundLength / VoxelConstants.ChunkSizeX;
        if (WrapAround(-1, aroundLength) != aroundLength - 1
            || WrapAround(aroundLength, aroundLength) != 0
            || WrapAround((long)aroundLength * 5 + 17, aroundLength) != 17
            || WrapAroundChunk(-1, aroundChunkLength) != aroundChunkLength - 1)
        {
            return false;
        }

        var lastVoxel = new RingVoxelCoordinate(aroundLength - 1L, CertifiedRingScale.RingWidthMeters - 1, CertifiedRingScale.TerrainCeilingMeters);
        if (!TryToLocalVoxel(lastVoxel, aroundLength, out RingChunkCoordinate chunk, out Godot.Vector3I local))
        {
            return false;
        }

        RingVoxelCoordinate roundTrip = ToVoxelCoordinate(chunk, local, aroundLength);
        return roundTrip == lastVoxel
            && chunk.AroundRingChunk == aroundChunkLength - 1
            && local.X == VoxelConstants.ChunkSizeX - 1;
    }

    private static bool RunFullCircumferenceTravelSelfTest()
    {
        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
        var seamCrossing = new RingCanonicalPosition(circumference - 0.25, 123.5, 456.75)
            .AddLocal(new Godot.Vector3(0.5f, 0.0f, 0.0f))
            .Wrap(circumference);
        if (System.Math.Abs(seamCrossing.AroundMeters - 0.25) > 1e-9
            || seamCrossing.VerticalMeters != 123.5
            || seamCrossing.AcrossMeters != 456.75)
        {
            return false;
        }

        double totalTravel = circumference * 3.0 + 12345.75;
        var multiLoop = new RingCanonicalPosition(totalTravel, 0.0, 0.0).Wrap(circumference);
        return System.Math.Abs(multiLoop.AroundMeters - 12345.75) <= 1e-8;
    }
}
