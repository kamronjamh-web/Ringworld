using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The level knots of all stems in compact form (T2R-6): S and W in whole centimetres, kept as int16 deltas from the previous knot of the stem (the first knot of a stem
/// is absolute), with an absolute anchor every <see cref="BlockSize"/> knots so a lookup scans at most one block. This is also the file layout. Values are returned as
/// metres from the centimetre integers, so the bake and the loader agree exactly. Knots are piecewise linear and non-increasing in W within a stem.
/// </summary>
public sealed class RiverKnotTable
{
    public const int BlockSize = 16;
    public static readonly RiverKnotTable Empty = FromCm(Array.Empty<List<(int S, int W)>>());

    private readonly int[] _start, _count, _blockBase;
    private readonly short[] _dS, _dW;
    private readonly int[] _anchorS, _anchorW;

    public int Count { get; }
    public int Stems => _start.Length;
    public long ResidentBytes => 4L * _dS.Length + 8L * _anchorS.Length + 12L * _start.Length;

    private RiverKnotTable(int[] counts, int[] firstS, int[] firstW, short[] dS, short[] dW)
    {
        int stems = counts.Length;
        _count = counts; _dS = dS; _dW = dW;
        _start = new int[stems]; _blockBase = new int[stems];
        int total = 0, blocks = 0;
        for (int i = 0; i < stems; i++)
        {
            _start[i] = total; _blockBase[i] = blocks;
            total += counts[i]; blocks += (counts[i] + BlockSize - 1) / BlockSize;
        }
        Count = total;
        _anchorS = new int[blocks]; _anchorW = new int[blocks];
        for (int i = 0; i < stems; i++)
        {
            if (counts[i] == 0) continue;
            int s = firstS[i], w = firstW[i];
            for (int j = 0; j < counts[i]; j++)
            {
                if (j > 0) { s += dS[_start[i] + j]; w += dW[_start[i] + j]; }
                if (j % BlockSize == 0) { _anchorS[_blockBase[i] + j / BlockSize] = s; _anchorW[_blockBase[i] + j / BlockSize] = w; }
            }
        }
    }

    /// <summary>From per-stem knots in whole centimetres. A delta that does not fit int16 throws (<see cref="Quantise"/> splits such segments first).</summary>
    public static RiverKnotTable FromCm(IReadOnlyList<List<(int S, int W)>> perStem)
    {
        int stems = perStem.Count, total = 0;
        foreach (List<(int S, int W)> list in perStem) total += list.Count;
        var counts = new int[stems];
        var firstS = new int[stems];
        var firstW = new int[stems];
        var dS = new short[total];
        var dW = new short[total];
        int at = 0;
        for (int i = 0; i < stems; i++)
        {
            List<(int S, int W)> list = perStem[i];
            counts[i] = list.Count;
            for (int j = 0; j < list.Count; j++, at++)
            {
                if (j == 0) { firstS[i] = list[0].S; firstW[i] = list[0].W; continue; }
                dS[at] = checked((short)(list[j].S - list[j - 1].S));
                dW[at] = checked((short)(list[j].W - list[j - 1].W));
            }
        }
        return new RiverKnotTable(counts, firstS, firstW, dS, dW);
    }

    /// <summary>From the file arrays: per-stem counts and first knots, and per-knot deltas (0 at each stem's first knot).</summary>
    public static RiverKnotTable FromDeltas(int[] counts, int[] firstS, int[] firstW, short[] dS, short[] dW) => new(counts, firstS, firstW, dS, dW);

    /// <summary>
    /// Rounds a fitted knot list to whole centimetres and splits any segment whose S or W step does not fit int16 (327 m) into equal steps along its line. Rounding is
    /// monotone, so W stays non-increasing; the fit keeps 7 mm of margin for it (<see cref="RiverLevel"/>).
    /// </summary>
    public static List<(int S, int W)> Quantise(List<RiverKnot> knots)
    {
        var result = new List<(int S, int W)>(knots.Count);
        for (int j = 0; j < knots.Count; j++)
        {
            int s = (int)Math.Round(knots[j].S * 100.0), w = (int)Math.Round(knots[j].W * 100.0);
            if (j > 0)
            {
                (int ps, int pw) = result[^1];
                int pieces = Math.Max(1, Math.Max((int)Math.Ceiling(Math.Abs((long)s - ps) / 32000.0), (int)Math.Ceiling(Math.Abs((long)w - pw) / 32000.0)));
                for (int k = 1; k < pieces; k++) result.Add((ps + (int)Math.Round((double)(s - ps) * k / pieces), pw + (int)Math.Round((double)(w - pw) * k / pieces)));
            }
            result.Add((s, w));
        }
        return result;
    }

    public int KnotCount(int stem) => _count[stem];
    public int FirstS(int stem) => _anchorS[_blockBase[stem]];
    public int FirstW(int stem) => _anchorW[_blockBase[stem]];
    public short DeltaS(int stem, int j) => _dS[_start[stem] + j];
    public short DeltaW(int stem, int j) => _dW[_start[stem] + j];

    public double S(int stem, int j) => SCm(stem, j) * 0.01;
    public double W(int stem, int j) => WCm(stem, j) * 0.01;

    private int SCm(int stem, int j)
    {
        int b = j / BlockSize, v = _anchorS[_blockBase[stem] + b];
        for (int i = _start[stem] + b * BlockSize + 1, end = _start[stem] + j; i <= end; i++) v += _dS[i];
        return v;
    }

    private int WCm(int stem, int j)
    {
        int b = j / BlockSize, v = _anchorW[_blockBase[stem] + b];
        for (int i = _start[stem] + b * BlockSize + 1, end = _start[stem] + j; i <= end; i++) v += _dW[i];
        return v;
    }

    public List<(int S, int W)> StemCm(int stem)
    {
        var list = new List<(int S, int W)>(_count[stem]);
        for (int j = 0; j < _count[stem]; j++) list.Add((SCm(stem, j), WCm(stem, j)));
        return list;
    }

    /// <summary>Largest knot index j of the stem with S(j) at most <paramref name="s"/>; the first knot must satisfy it.</summary>
    public int LastIndexSAtMost(int stem, double s)
    {
        int blocks = (_count[stem] + BlockSize - 1) / BlockSize, lo = 0, hi = blocks - 1, bb = _blockBase[stem];
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_anchorS[bb + mid] * 0.01 <= s) lo = mid; else hi = mid - 1;
        }
        int j = lo * BlockSize, end = Math.Min(_count[stem], j + BlockSize) - 1, v = _anchorS[bb + lo];
        while (j < end)
        {
            int next = v + _dS[_start[stem] + j + 1];
            if (next * 0.01 > s) break;
            v = next; j++;
        }
        return j;
    }

    /// <summary>Largest knot index j of the stem with W(j) at least <paramref name="level"/>; the first knot must satisfy it.</summary>
    public int LastIndexWAtLeast(int stem, double level)
    {
        int blocks = (_count[stem] + BlockSize - 1) / BlockSize, lo = 0, hi = blocks - 1, bb = _blockBase[stem];
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_anchorW[bb + mid] * 0.01 >= level) lo = mid; else hi = mid - 1;
        }
        int j = lo * BlockSize, end = Math.Min(_count[stem], j + BlockSize) - 1, v = _anchorW[bb + lo];
        while (j < end)
        {
            int next = v + _dW[_start[stem] + j + 1];
            if (next * 0.01 < level) break;
            v = next; j++;
        }
        return j;
    }
}
