using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>What a scatter tile file holds: the rows and cobble blocks built so far, and the records.</summary>
internal sealed record ScatterTileData(int BuiltRows, ushort CobbleBlockMask, List<ScatterRecord> Records);

/// <summary>
/// T5 design A6: the scatter tile file, <c>lod_cache/scatter/&lt;identity&gt;/r_&lt;shardAround&gt;_&lt;shardAcross&gt;/s_&lt;around&gt;_&lt;across&gt;.bin</c>.
/// Header 48 bytes (magic SCT1, cache version, key around, key across, algorithm version, class table hash, built rows, cobble block mask, a reserved word, record count),
/// then 44-byte records, then an FNV-1a 64 trailer over header and records. Written to a temporary file and renamed, through the generated LOD cache budget.
/// </summary>
internal static class RingScatterTileFile
{
    public const int Magic = 0x31544353; // SCT1
    public const int HeaderBytes = 48;
    public const int TrailerBytes = 8;
    public const int MaximumRecords = 65_536;

    /// <summary>Test hook: lets <see cref="DiskCacheAllowed"/> say yes while scatter.rocks is still open.</summary>
    internal static bool ScatterDiskCacheForTests { get; set; }

    /// <summary>Open features never touch the disk: true only when every scatter feature (bits 4, 5 and 6) set in the mask is frozen, or the test override is on.</summary>
    public static bool DiskCacheAllowed(int worldFeatureMask, WorldGenerationLedger ledger, bool testOverride)
    {
        if (testOverride) return true;
        bool any = false;
        foreach (WorldGenerationLedger.FeatureInfo feature in ledger.Features)
        {
            if (feature.Bit != RingHabitatFieldGrid.ScatterRocksBit && feature.Bit != 5 && feature.Bit != RingHabitatFieldGrid.ScatterVegetationBit) continue;
            if ((worldFeatureMask & (1 << feature.Bit)) == 0) continue;
            any = true;
            if (feature.FrozenIn <= 0) return false;
        }
        return any;
    }

    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (byte b in data) { h ^= b; h *= 0x100000001B3UL; }
        return h;
    }

    private static long FloorDiv(long value, long divisor)
    {
        long q = value / divisor;
        return (value % divisor != 0 && ((value < 0) != (divisor < 0))) ? q - 1 : q;
    }

    /// <summary>
    /// The identity directory name: SHA-256 of the A6 string, first 12 bytes as hex (the tree files' style). The rock catalog version is "m" + RockCatalog.CatalogVersion (T5-6).
    /// </summary>
    public static string Identity(RingTerrainSampler sampler, RingScatterClassTable table, ulong treeSettingsHash,
        int algorithmVersion = RingBiomeDecorationLayer.ScatterAlgorithmVersion, string? rockCatalogVersion = null)
    {
        rockCatalogVersion ??= "m" + RockCatalog.CatalogVersion;
        string flags = (sampler.EmitterSites != null ? "|es1" : "") + (sampler.ClimateMachines != null ? "|cm1" : "") + (sampler.WeatherLung != null ? "|wl1" : "");
        string definition = FormattableString.Invariant(
            $"sv{RingBiomeDecorationLayer.ScatterCacheVersion}|a{algorithmVersion}|c{table.Hash:X16}|{rockCatalogVersion}|g{sampler.TerrainGeneratorVersion}|seed{sampler.Seed}|h{sampler.TerrainDefinitionHash:X16}|t{treeSettingsHash:X16}{flags}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)).AsSpan(0, 12));
    }

    public static string PathFor(string lodCacheRoot, string identity, long around, long across)
    {
        long shardAround = FloorDiv(around, 64), shardAcross = FloorDiv(across, 64);
        return Path.Combine(lodCacheRoot, "scatter", identity, $"r_{shardAround}_{shardAcross}", $"s_{around}_{across}.bin");
    }

    internal static byte[] Serialize(long keyAround, long keyAcross, int algorithmVersion, RingScatterClassTable table, ScatterTileData data)
    {
        if (data.Records.Count > MaximumRecords) throw new ArgumentException("too many records");
        var bytes = new byte[HeaderBytes + data.Records.Count * ScatterRecord.DiskBytes + TrailerBytes];
        using (var stream = new MemoryStream(bytes, 0, bytes.Length - TrailerBytes, true))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(RingBiomeDecorationLayer.ScatterCacheVersion);
            writer.Write(keyAround);
            writer.Write(keyAcross);
            writer.Write(algorithmVersion);
            writer.Write(table.Hash);
            writer.Write(data.BuiltRows);
            writer.Write(data.CobbleBlockMask);
            writer.Write((ushort)0);
            writer.Write(data.Records.Count);
            foreach (ScatterRecord r in data.Records)
            {
                writer.Write(r.Id); writer.Write(r.AroundCm); writer.Write(r.AcrossCm); writer.Write(r.SurfaceHeight);
                writer.Write(r.ClassId); writer.Write(r.Mesh); writer.Write(r.Size); writer.Write(r.Yaw); writer.Write(r.UpOct); writer.Write(r.BaseTint);
            }
        }
        ulong trailer = Fnv1a64(bytes.AsSpan(0, bytes.Length - TrailerBytes));
        BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - TrailerBytes), trailer);
        return bytes;
    }

    /// <summary>Rejects a wrong magic, version, key, algorithm version, table hash or trailer, and out-of-range values (unknown class id, mesh outside the class's range, non-finite floats).</summary>
    internal static bool TryParse(byte[] bytes, long keyAround, long keyAcross, int algorithmVersion, RingScatterClassTable table, out ScatterTileData? data)
    {
        data = null;
        if (bytes.Length < HeaderBytes + TrailerBytes) return false;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            if (reader.ReadInt32() != Magic
                || reader.ReadInt32() != RingBiomeDecorationLayer.ScatterCacheVersion
                || reader.ReadInt64() != keyAround
                || reader.ReadInt64() != keyAcross
                || reader.ReadInt32() != algorithmVersion
                || reader.ReadUInt64() != table.Hash) return false;
            int builtRows = reader.ReadInt32();
            ushort mask = reader.ReadUInt16();
            if (reader.ReadUInt16() != 0) return false;
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumRecords || builtRows < 0 || builtRows > table.BuildOrder.Count) return false;
            if (bytes.Length != HeaderBytes + (long)count * ScatterRecord.DiskBytes + TrailerBytes) return false;
            ulong expected = BitConverter.ToUInt64(bytes, bytes.Length - TrailerBytes);
            if (Fnv1a64(bytes.AsSpan(0, bytes.Length - TrailerBytes)) != expected) return false;
            var records = new List<ScatterRecord>(count);
            for (int i = 0; i < count; i++)
            {
                var r = new ScatterRecord(reader.ReadUInt64(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadSingle(),
                    reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadUInt32(), reader.ReadUInt32());
                RingScatterClass? row = table.Find(r.ClassId);
                if (row == null
                    || r.Mesh < row.MeshFirst || r.Mesh >= row.MeshFirst + row.MeshCount
                    || !float.IsFinite(r.SurfaceHeight) || !float.IsFinite(r.Size) || r.Size <= 0f || !float.IsFinite(r.Yaw)
                    || r.AroundCm < 0 || (r.BaseTint >> 24) != 0) return false;
                records.Add(r);
            }
            data = new ScatterTileData(builtRows, mask, records);
            return true;
        }
        catch (IOException) { return false; }
    }

    public static bool TryLoad(string path, long keyAround, long keyAcross, RingScatterClassTable table, out ScatterTileData? data,
        int algorithmVersion = RingBiomeDecorationLayer.ScatterAlgorithmVersion)
    {
        data = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { return TryParse(File.ReadAllBytes(path), keyAround, keyAcross, algorithmVersion, table, out data); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool TryWrite(string path, string budgetRoot, long budgetBytes, long minimumFreeBytes, long keyAround, long keyAcross,
        RingScatterClassTable table, ScatterTileData data, int algorithmVersion = RingBiomeDecorationLayer.ScatterAlgorithmVersion)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        long estimatedBytes = HeaderBytes + data.Records.Count * (long)ScatterRecord.DiskBytes + TrailerBytes;
        if (!RingGeneratedLodCacheBudget.CanWrite(budgetRoot, path, budgetBytes, minimumFreeBytes, estimatedBytes)) return false;
        string? temporary = null;
        bool recorded = false;
        try
        {
            byte[] bytes = Serialize(keyAround, keyAcross, algorithmVersion, table, data);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
            temporary = null;
            RingGeneratedLodCacheBudget.RecordWrite(budgetRoot, path, new FileInfo(path).Length);
            recorded = true;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            if (!recorded) RingGeneratedLodCacheBudget.CancelWriteReservation(budgetRoot, path);
            if (temporary != null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
