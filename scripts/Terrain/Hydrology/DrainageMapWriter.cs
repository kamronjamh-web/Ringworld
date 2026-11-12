using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// River and lake maps of a <see cref="DrainageField"/> in the T0 palette (docs/work-orders/terrain-vegetation/T2.md, T2R-4): one pixel per cell, around to
/// the right, across downward. Grey hillshade of the valley-floor height, dark blue sea (darker with depth), light blue lake water, violet barriers,
/// indigo Weather Lung, ochre strip outlets, orange cut cells, green channels by contributing area.
/// </summary>
public static class DrainageMapWriter
{
    public const string Legend =
        "Around increases to the right, across downward, one pixel per 1 km cell (crop: 3x, nearest neighbour). "
        + "Grey: hillshade of the valley-floor height (lowest sample per cell). Dark blue: sea, darker with depth. Light blue: lake water. Pale tan: dry basin floor (salt flat or dry lakebed). "
        + "Violet: barrier (emitter sites, Wall Engines, Weather Lung with clearance). Indigo: Weather Lung basin. Ochre: strip end outlet. "
        + "Orange: cell cut by a breach (cuts over 40 m would be red; none are allowed). "
        + "Green channels at 50 km^2 and above: pale green 50 to 500 km^2, green 500 to 5,000 km^2 (2 px), dark green over 5,000 km^2 (3 px). "
        + "Overview: the ring folded into 10 panels, panel 1 on top.";

    private static readonly (double R, double G, double B)[] ChannelPalette = { (0.55, 1.0, 0.65), (0.15, 0.85, 0.40), (0.0, 0.60, 0.28) };

    public static byte[] Render(DrainageField f, double thresholdKm2 = 50d)
    {
        int w = f.Width, h = f.Height;
        var rgb = new byte[w * h * 3];
        ReadOnlySpan<float> lowestSpan = f.Lowest;
        float[] lowest = lowestSpan.ToArray();
        DrainageCellKind[] kind = f.Kind.ToArray();
        int[] area = f.AreaCells.ToArray(), down = f.Down.ToArray();
        double exaggeration = 6d, step = f.AroundStepMeters;
        double lx = -1 / Math.Sqrt(3), ly = 1 / Math.Sqrt(3), lz = -1 / Math.Sqrt(3);
        void Put(int cell, double r, double g, double b)
        {
            rgb[cell * 3] = (byte)Math.Clamp((int)(r * 255 + 0.5), 0, 255);
            rgb[cell * 3 + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5), 0, 255);
            rgb[cell * 3 + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5), 0, 255);
        }
        Parallel.For(0, h, j =>
        {
            for (int i = 0; i < w; i++)
            {
                int cell = j * w + i;
                switch (kind[cell])
                {
                    case DrainageCellKind.Ocean:
                    {
                        double k = 1 - 0.6 * Math.Clamp(-lowest[cell] / 600d, 0, 1);
                        Put(cell, 0.10 * k, 0.20 * k, 0.45 * k);
                        continue;
                    }
                    case DrainageCellKind.Barrier: Put(cell, 0.80, 0.20, 0.80); continue;
                    case DrainageCellKind.Lung: Put(cell, 0.30, 0.25, 0.70); continue;
                    case DrainageCellKind.StripOutlet: Put(cell, 0.80, 0.65, 0.25); continue;
                }
                if (f.IsLakeWater(cell)) { Put(cell, 0.24, 0.64, 0.86); continue; }
                float hl = lowest[j * w + Math.Max(i - 1, 0)], hr = lowest[j * w + Math.Min(i + 1, w - 1)];
                float hu = lowest[Math.Max(j - 1, 0) * w + i], hd = lowest[Math.Min(j + 1, h - 1) * w + i];
                double dx = exaggeration * (hr - hl) / (2d * step), dz = exaggeration * (hd - hu) / (2d * step);
                double inv = 1d / Math.Sqrt(dx * dx + 1 + dz * dz);
                double lit = Math.Max(0, -dx * inv * lx + inv * ly - dz * inv * lz);
                if (f.IsDryFloor(cell)) { double k = 0.8 + 0.2 * lit; Put(cell, 0.86 * k, 0.78 * k, 0.60 * k); continue; }
                double v = 0.35 + 0.55 * lit;
                Put(cell, v, v, v);
            }
        });
        // Cut cells, then channels (widest last so the large rivers stay on top).
        foreach (DrainageBreach cut in f.Cuts.Span)
            for (int i = 0; i < cut.Path.Length - 1; i++)
                if (kind[cut.Path[i]] == DrainageCellKind.Land && cut.Path[i] != cut.PitCell) Put(cut.Path[i], 0.98, 0.55, 0.10);
        int minCells = (int)Math.Ceiling(thresholdKm2 / f.CellAreaKm2);
        for (int pass = 0; pass < 3; pass++)
            for (int cell = 0; cell < kind.Length; cell++)
            {
                if (area[cell] < minCells || down[cell] < 0 || kind[cell] != DrainageCellKind.Land || f.IsLakeWater(cell)) continue;
                double km2 = area[cell] * f.CellAreaKm2;
                int width = km2 < 500 ? 1 : km2 < 5000 ? 2 : 3;
                if (width - 1 != pass) continue;
                int cx = cell % w, cy = cell / w, lo = -(width - 1) / 2, hi = width / 2;
                var col = ChannelPalette[pass];
                for (int yy = cy + lo; yy <= cy + hi; yy++)
                for (int xx = cx + lo; xx <= cx + hi; xx++)
                {
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int m = yy * w + xx;
                    if (kind[m] != DrainageCellKind.Land || f.IsLakeWater(m)) continue;
                    Put(m, col.R, col.G, col.B);
                }
            }
        return rgb;
    }

    /// <summary>Writes the folded overview and the crop, plus the raw one-pixel-per-cell map. Returns (overview, crop, raw) paths.</summary>
    public static (string Overview, string Crop, string Raw) Write(DrainageField f, string directory, string label, double cropStartMeters, double cropMeters, bool writeRaw)
    {
        Directory.CreateDirectory(directory);
        byte[] rgb = Render(f);
        int w = f.Width, h = f.Height;
        string overview = Path.Combine(directory, $"river_map_{label}_overview.png");
        string crop = Path.Combine(directory, $"river_map_{label}_crop.png");
        string raw = Path.Combine(directory, $"river_map_{label}_raw.png");

        int panels = w >= 10000 ? 10 : 1, panelWidth = (w + panels - 1) / panels, gap = panels > 1 ? 6 : 0;
        int outW = panelWidth, outH = panels * h + (panels - 1) * gap;
        var folded = new byte[outW * outH * 3];
        for (int i = 0; i < folded.Length; i++) folded[i] = 40;
        for (int p = 0; p < panels; p++)
            for (int y = 0; y < h; y++)
            {
                int start = p * panelWidth, length = Math.Min(panelWidth, w - start);
                if (length <= 0) continue;
                Buffer.BlockCopy(rgb, (y * w + start) * 3, folded, ((p * (h + gap) + y) * outW) * 3, length * 3);
            }
        Save(folded, outW, outH, overview);

        int first = (int)Math.Round(cropStartMeters / f.AroundStepMeters) - (f.Spec?.ColumnStart ?? 0);
        int cw = Math.Min((int)Math.Round(cropMeters / f.AroundStepMeters), w);
        if (first < 0 || first + cw > w) first = Math.Clamp(first, 0, Math.Max(0, w - cw));
        const int scale = 3;
        var cropRgb = new byte[cw * scale * h * scale * 3];
        for (int y = 0; y < h * scale; y++)
            for (int x = 0; x < cw * scale; x++)
            {
                int src = ((y / scale) * w + first + x / scale) * 3, dst = (y * cw * scale + x) * 3;
                cropRgb[dst] = rgb[src]; cropRgb[dst + 1] = rgb[src + 1]; cropRgb[dst + 2] = rgb[src + 2];
            }
        Save(cropRgb, cw * scale, h * scale, crop);
        if (writeRaw) Save(rgb, w, h, raw);
        File.WriteAllText(Path.Combine(directory, "legend.txt"), Legend);
        return (overview, crop, writeRaw ? raw : "");
    }

    private static void Save(byte[] rgb, int w, int h, string path)
    {
        using Image image = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb);
        Error err = image.SavePng(path);
        if (err != Error.Ok) throw new IOException("SavePng failed: " + err + " " + path);
    }
}
