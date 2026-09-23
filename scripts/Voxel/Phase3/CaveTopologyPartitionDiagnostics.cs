using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveTopologyPartitionDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            var contract = CaveGenerationContract.Create(7331);
            var p = new CaveTopologyPartition(contract, 16_385);
            var repeated = new CaveTopologyPartition(contract, 16_385);
            var zero = p.Resolve(0, 0, 0);
            var sameCell = p.Resolve(511, 255, 511);
            var nextCell = p.Resolve(512, 256, 512);
            var edge = p.Resolve(16_384, 0, 0);
            if (p.AroundCellCount != 33
                || p.AroundProvinceCount != 2
                || zero.Address != new CaveTopologyCellAddress(0, 0, 0)
                || zero.Key != sameCell.Key
                || nextCell.Address != new CaveTopologyCellAddress(1, 1, 1)
                || zero.Key == nextCell.Key
                || edge.Address.Around != 32
                || edge.AroundExtent != 1
                || edge.Province.AroundExtent != 1
                || edge.LocalAround != 0
                || p.Resolve(-1, 0, 0).Address.Around != 32
                || p.Resolve(16_385, 0, 0).Address.Around != 0)
                throw new InvalidOperationException("cell boundary or around seam ownership failed");
            var negative = p.Resolve(-1, -1, -1);
            if (negative.Address.Vertical != -1
                || negative.Address.Across != -1
                || negative.VerticalOrigin != -256
                || negative.AcrossOrigin != -512
                || negative.LocalVertical != 31
                || negative.LocalAcross != 31)
                throw new InvalidOperationException("negative floor division failed");
            var wrapped = p.Resolve(-16_385L * 9 - 1, long.MinValue / 2, long.MaxValue / 2);
            if (wrapped.Address.Around != 32 || wrapped.Key == negative.Key)
                throw new InvalidOperationException("large coordinate wrap/key failed");
            var forwardNeighbor = p.ResolveNormalizedCell(p.AroundCellCount, -1, -1);
            var backwardNeighbor = p.ResolveNormalizedCell(-1, -1, -1);
            if (forwardNeighbor.Address != new CaveTopologyCellAddress(0, -1, -1)
                || backwardNeighbor.Address != new CaveTopologyCellAddress(32, -1, -1))
                throw new InvalidOperationException("normalized lookup failed");
            if (zero.Province.Address != new CaveTopologyProvinceAddress(0, 0, 0)
                || edge.Province.Address.Around != 1
                || zero.Province.Key == edge.Province.Key
                || p.Resolve(1, 0, 0).Key != zero.Key
                || repeated.Identity != p.Identity
                || repeated.Resolve(0, 0, 0).Key != zero.Key)
                throw new InvalidOperationException("province, cell, or repeatability contract failed");
            var alternate = new CaveTopologyPartition(CaveGenerationContract.Create(7332), 16_385);
            if (alternate.Identity == p.Identity
                || alternate.Resolve(0, 0, 0).Address != zero.Address
                || alternate.Resolve(0, 0, 0).Key == zero.Key)
                throw new InvalidOperationException("seed isolation failed");

            long[] order = { 1024L, -1L, 0L, 16_384L, 513L };
            var expected = new Dictionary<long, CaveTopologyCell>();
            foreach (long around in order)
                expected.Add(around, p.Resolve(around, -257, 511));
            for (int i = order.Length - 1; i >= 0; i--)
            {
                CaveTopologyCell cell = p.Resolve(order[i], -257, 511);
                if (cell != expected[order[i]]
                    || cell.Key != p.Resolve(cell.AroundOrigin, cell.VerticalOrigin, cell.AcrossOrigin).Key
                    || cell.Province.Key != p.ResolveProvince(cell.AroundOrigin, cell.VerticalOrigin, cell.AcrossOrigin).Key)
                    throw new InvalidOperationException("load-order/province consistency failed");
            }

            Throws<ArgumentOutOfRangeException>(() => new CaveTopologyPartition(contract, 0));
            Throws<ArgumentException>(() => new CaveTopologyPartition(default, 16_385));
            summary = $"identity={p.Identity:X16} cells={p.AroundCellCount} provinces={p.AroundProvinceCount}";
            return true;
        }
        catch (Exception e) { summary = e.Message; return false; }
    }
    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("invalid partition accepted");
    }
}
