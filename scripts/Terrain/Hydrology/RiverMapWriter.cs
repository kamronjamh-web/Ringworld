using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// T2R-5 pictures (docs/work-orders/terrain-vegetation/T2.md, M1): the production centrelines drawn as curves over a smooth hillshade of the valley-floor
/// height, with the sea, lake water and barriers tinted, plus a close-up of a coastal-plain fan that also shows the old cell-to-cell D8 path.
/// Around to the right, across downward.
/// </summary>
public static class RiverMapWriter
{
    public const string Legend =
        "Production river centrelines (B-spline through the lowest-sample nodes, wander and meander) over a hillshade of the valley-floor height. "
        + "Around increases to the right, across downward. Dark blue: sea. Light blue: lake water. Violet: barrier. Curve colour and width by reach class: "
        + "calm, swift, fierce, torrent. Close-up: thin orange line is the cell-to-cell D8 path that the curve replaces.";

    private static readonly (byte R, byte G, byte B)[] ClassColors = { (90, 235, 255), (40, 190, 255), (255, 235, 90), (255, 90, 70) };

    private sealed class Canvas
    {
        public readonly int W, H;
        public readonly byte[] Rgb;
        public Canvas(int w, int h) { W = w; H = h; Rgb = new byte[w * h * 3]; }

        public void Disc(double cx, double cy, double radius, (byte R, byte G, byte B) c)
        {
            int x0 = (int)Math.Floor(cx - radius), x1 = (int)Math.Ceiling(cx + radius), y0 = (int)Math.Floor(cy - radius), y1 = (int)Math.Ceiling(cy + radius);
            for (int y = Math.Max(0, y0); y <= Math.Min(H - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                if (dx * dx + dy * dy > radius * radius) continue;
                int i = (y * W + x) * 3;
                Rgb[i] = c.R; Rgb[i + 1] = c.G; Rgb[i + 2] = c.B;
            }
        }

        public void Line(double x0, double y0, double x1, double y1, double radius, (byte R, byte G, byte B) c)
        {
            double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
            int steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(0.5, radius * 0.5)));
            for (int i = 0; i <= steps; i++) Disc(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps, radius, c);
        }
    }

    private static Canvas Base(DrainageField f, double firstColumn, double columns, double rows, double scale, double firstRow = 0)
    {
        int w = (int)Math.Round(columns * scale), h = (int)Math.Round(rows * scale);
        var canvas = new Canvas(w, h);
        float[] lowest = f.Lowest.ToArray();
        DrainageCellKind[] kind = f.Kind.ToArray();
        int fw = f.Width, fh = f.Height;
        double step = f.AroundStepMeters, exaggeration = 6d;
        double lx = -1 / Math.Sqrt(3), ly = 1 / Math.Sqrt(3), lz = -1 / Math.Sqrt(3);
        double Height(double gx, double gy)
        {
            gx = Math.Clamp(gx - 0.5, 0, fw - 1.001); gy = Math.Clamp(gy - 0.5, 0, fh - 1.001);
            int x = (int)gx, y = (int)gy;
            double tx = gx - x, ty = gy - y;
            return (lowest[y * fw + x] * (1 - tx) + lowest[y * fw + x + 1] * tx) * (1 - ty) + (lowest[(y + 1) * fw + x] * (1 - tx) + lowest[(y + 1) * fw + x + 1] * tx) * ty;
        }
        for (int py = 0; py < h; py++)
        for (int px = 0; px < w; px++)
        {
            double gx = firstColumn + (px + 0.5) / scale, gy = firstRow + (py + 0.5) / scale;
            int cx = Math.Clamp((int)gx, 0, fw - 1), cy = Math.Clamp((int)gy, 0, fh - 1), cell = cy * fw + cx;
            double r, g, b;
            if (kind[cell] == DrainageCellKind.Ocean) { double k = 1 - 0.6 * Math.Clamp(-Height(gx, gy) / 600d, 0, 1); r = 0.10 * k; g = 0.20 * k; b = 0.45 * k; }
            else if (kind[cell] == DrainageCellKind.Barrier) { r = 0.8; g = 0.2; b = 0.8; }
            else if (kind[cell] == DrainageCellKind.Lung) { r = 0.3; g = 0.25; b = 0.7; }
            else if (f.IsLakeWater(cell)) { r = 0.24; g = 0.64; b = 0.86; }
            else
            {
                double e = 0.5;
                double dx = exaggeration * (Height(gx + e, gy) - Height(gx - e, gy)) / (2 * e * step), dz = exaggeration * (Height(gx, gy + e) - Height(gx, gy - e)) / (2 * e * step);
                double inv = 1d / Math.Sqrt(dx * dx + 1 + dz * dz);
                double lit = Math.Max(0, -dx * inv * lx + inv * ly - dz * inv * lz);
                double v = 0.35 + 0.55 * lit;
                r = g = b = v;
            }
            int i = (py * w + px) * 3;
            canvas.Rgb[i] = (byte)Math.Clamp((int)(r * 255 + 0.5), 0, 255);
            canvas.Rgb[i + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5), 0, 255);
            canvas.Rgb[i + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5), 0, 255);
        }
        return canvas;
    }

    private static void Curves(RiverNetwork net, DrainageGridSpec spec, Canvas canvas, double originAround, double scale, double firstColumn, double columns, double thicknessScale)
    {
        double scalePerMeter = scale / spec.AroundStepMeters;
        double lo = -50 * scalePerMeter, hiX = canvas.W + 50 * scalePerMeter, hiY = canvas.H + 50 * scalePerMeter;
        for (int stem = 0; stem < net.Stems.Length; stem++)
        {
            double end = net.MetricsEnd[stem];
            double px = double.NaN, py = double.NaN;
            for (double s = 0; s <= end + 1e-6; s += 25d)
            {
                net.Centre(stem, Math.Min(s, end), out double around, out double across, out _, out _);
                double x = (net.WrapAround(around - originAround)) * scalePerMeter, y = across * scalePerMeter;
                if (!double.IsNaN(px) && x >= lo && x <= hiX && y >= lo && y <= hiY && Math.Abs(x - px) < canvas.W)
                {
                    int reach = net.ReachAt(stem, s);
                    RiverSection sec = net.Sections[reach];
                    canvas.Line(px, py, x, y, Math.Max(0.7, 0.5 * sec.Width * scalePerMeter * thicknessScale), ClassColors[(int)sec.Class]);
                }
                px = x; py = y;
            }
        }
    }

    /// <summary>The M1 crop with the production curves, and the coastal-plain close-up. Returns (crop path, close-up path).</summary>
    public static (string Crop, string CloseUp) Write(DrainageField f, DrainageGrid grid, RiverNetwork net, string directory, string label, double cropStartMeters, double cropMeters)
    {
        Directory.CreateDirectory(directory);
        DrainageGridSpec spec = grid.Spec;
        int first = (int)Math.Round(cropStartMeters / f.AroundStepMeters) - spec.ColumnStart;
        int columns = Math.Min((int)Math.Round(cropMeters / f.AroundStepMeters), f.Width);
        first = Math.Clamp(first, 0, Math.Max(0, f.Width - columns));
        double scale = 6;
        Canvas crop = Base(f, first, columns, f.Height, scale);
        double originAround = (spec.ColumnStart + first) * f.AroundStepMeters;
        Curves(net, spec, crop, originAround, scale, first, columns, 12d);
        string cropPath = Path.Combine(directory, $"river_curves_{label}_crop.png");
        Save(crop, cropPath);

        // Close-up: a 24 x 18 km window on a coastal plain (sea within 12 km), maximising the channel length drawn.
        int winW = 24, winH = 18;
        DrainageCellKind[] kind = f.Kind.ToArray();
        float[] lowest = f.Lowest.ToArray();
        int bestX = first, bestY = 0;
        double best = -1;
        for (int wy = 0; wy + winH <= f.Height; wy += 2)
        for (int wx = first; wx + winW <= first + columns; wx += 2)
        {
            bool sea = false;
            double mean = 0;
            for (int y = Math.Max(0, wy - 8); y < Math.Min(f.Height, wy + winH + 8) && !sea; y++)
                for (int x = Math.Max(0, wx - 8); x < Math.Min(f.Width, wx + winW + 8); x++) if (kind[y * f.Width + x] == DrainageCellKind.Ocean) { sea = true; break; }
            if (!sea) continue;
            int land = 0;
            for (int y = wy; y < wy + winH; y++) for (int x = wx; x < wx + winW; x++) if (kind[y * f.Width + x] == DrainageCellKind.Land) { land++; mean += lowest[y * f.Width + x]; }
            if (land < winW * winH * 0.9 || mean / Math.Max(1, land) > 60) continue;
            double score = 0;
            for (int stem = 0; stem < net.Stems.Length; stem++)
            {
                RiverStem st = net.Stems[stem];
                for (int k = 0; k < st.NodeCount; k++)
                {
                    double nx = net.WrapAround(net.NodeAroundDm[st.NodeStart + k] / 10d - (spec.ColumnStart + wx) * f.AroundStepMeters) / f.AroundStepMeters;
                    double ny = net.NodeAcrossDm[st.NodeStart + k] / 10d / f.AcrossStepMeters - wy;
                    if (nx >= 0 && nx < winW && ny >= 0 && ny < winH) score++;
                }
            }
            if (score > best) { best = score; bestX = wx; bestY = wy; }
        }
        double zoom = 40;
        Canvas window = Base(f, bestX, winW, winH, zoom, bestY);
        double closeOrigin = (spec.ColumnStart + bestX) * f.AroundStepMeters;
        DrawD8(f, grid, window, bestX, bestY, zoom);
        double perMeter = zoom / spec.AroundStepMeters;
        var shifted = new RiverNetworkView(net, spec, closeOrigin, bestY * f.AcrossStepMeters);
        shifted.Draw(window, perMeter, ClassColors, 1d);
        string closePath = Path.Combine(directory, $"river_curves_{label}_closeup.png");
        Save(window, closePath);
        File.WriteAllText(Path.Combine(directory, "river_curves_legend.txt"), Legend + $" Close-up window: columns {bestX}..{bestX + winW}, rows {bestY}..{bestY + winH} (24 x 18 km).");
        return (cropPath, closePath);
    }

    private static void DrawD8(DrainageField f, DrainageGrid grid, Canvas canvas, int firstColumn, int firstRow, double zoom)
    {
        int[] down = f.Down.ToArray(), area = f.AreaCells.ToArray();
        DrainageCellKind[] kind = f.Kind.ToArray();
        int minCells = (int)Math.Ceiling(100d / f.CellAreaKm2);
        (byte, byte, byte) orange = (255, 150, 40);
        for (int cell = 0; cell < f.Count; cell++)
        {
            if (area[cell] < minCells || down[cell] < 0 || kind[cell] != DrainageCellKind.Land) continue;
            int cx = cell % f.Width, cy = cell / f.Width, nx = down[cell] % f.Width, ny = down[cell] / f.Width;
            if (Math.Abs(nx - cx) > 1) continue;
            if (cx < firstColumn - 1 || cx > firstColumn + 25 || cy < firstRow - 1 || cy > firstRow + 19) continue;
            canvas.Line((cx + 0.5 - firstColumn) * zoom, (cy + 0.5 - firstRow) * zoom, (nx + 0.5 - firstColumn) * zoom, (ny + 0.5 - firstRow) * zoom, 1.2, orange);
        }
    }

    private sealed class RiverNetworkView
    {
        private readonly RiverNetwork _net;
        private readonly DrainageGridSpec _spec;
        private readonly double _origin, _acrossOrigin;
        public RiverNetworkView(RiverNetwork net, DrainageGridSpec spec, double origin, double acrossOrigin) { _net = net; _spec = spec; _origin = origin; _acrossOrigin = acrossOrigin; }

        public void Draw(Canvas canvas, double perMeter, (byte R, byte G, byte B)[] colors, double thicknessScale)
        {
            double lo = -60 * perMeter, hiX = canvas.W + 60 * perMeter, hiY = canvas.H + 60 * perMeter;
            for (int stem = 0; stem < _net.Stems.Length; stem++)
            {
                double end = _net.MetricsEnd[stem], px = double.NaN, py = double.NaN;
                for (double s = 0; s <= end + 1e-6; s += 10d)
                {
                    _net.Centre(stem, Math.Min(s, end), out double around, out double across, out _, out _);
                    double x = _net.WrapAround(around - _origin) * perMeter, y = (across - _acrossOrigin) * perMeter;
                    if (x > _net.CircumferenceM * perMeter / 2) x -= _net.CircumferenceM * perMeter;
                    if (!double.IsNaN(px) && x >= lo && x <= hiX && y >= lo && y <= hiY)
                    {
                        RiverSection sec = _net.Sections[_net.ReachAt(stem, s)];
                        canvas.Line(px, py, x, y, Math.Max(1.2, 0.5 * sec.Width * perMeter * thicknessScale), colors[(int)sec.Class]);
                    }
                    px = x; py = y;
                }
            }
        }
    }

    private static void Save(Canvas canvas, string path)
    {
        using Image image = Image.CreateFromData(canvas.W, canvas.H, false, Image.Format.Rgb8, canvas.Rgb);
        Error err = image.SavePng(path);
        if (err != Error.Ok) throw new IOException("SavePng failed: " + err + " " + path);
    }
}
