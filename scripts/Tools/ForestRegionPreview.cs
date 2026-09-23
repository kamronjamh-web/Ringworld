using System;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Isolated, bounded reference map. The last completed snapshot stays visible
/// while a newer request is sampled; nothing is connected to world streaming.
/// </summary>
public partial class ForestRegionPreview : Node
{
    const int Width = 256, Height = 192;
    const string SettingsPath = "res://resources/voxel/voxel_terrain_settings_ring_integration.tres";
    const string ConfigPath = "res://resources/launcher/certified_ring_config.tres";
    readonly TerrainSample[] completedTerrain = new TerrainSample[Width * Height];
    readonly ForestRegionSample[] completedForest = new ForestRegionSample[Width * Height];
    readonly TerrainSample[] buildingTerrain = new TerrainSample[Width * Height];
    readonly ForestRegionSample[] buildingForest = new ForestRegionSample[Width * Height];
    record struct Request(int Seed, long Around, int Across, float Span, long Circ, int RingWidth, double Radius, int BuildHeight);
    readonly record struct Statistics(int Valid, int Dense, int Open, int Clearing, int Maple, int Fir, int Pine,
        long TerrainHash, long ForestHash, double ElapsedMilliseconds);
    Statistics published;
    Request requested, active, completed;
    RingTerrainSampler? sampler; ForestRegionSampler? regions; VoxelTerrainSettings? settings;
    Image? building, complete; ImageTexture? texture; TextureRect? map; Label? status; Label? hover;
    RichTextLabel? legend;
    SpinBox? seedBox;
    int cursor, requestRevision, completedRevision, mode, valid, dense, open, clearing, maple, fir, pine;
    bool startPending;
    long samples, terrainHash, forestHash, recolors; Stopwatch? watch; string error = "";

    public override void _Ready()
    {
        requested = Certified();
        BuildUi();
        RequestMap(requested.Seed, 0, requested.RingWidth / 2, 128);
    }
    public override void _ExitTree() => DisposeAll();
    public override void _Process(double delta)
    {
        if (sampler is null && startPending) Start();
        if (sampler is null) return;
        long budgetStart = Stopwatch.GetTimestamp();
        int end = Math.Min(completedForest.Length, cursor + 192);
        while (cursor < end && Stopwatch.GetElapsedTime(budgetStart).TotalMilliseconds < 2) Sample(cursor++);
        if (cursor == completedForest.Length) Finish();
        UpdateStatus();
    }
    public override void _UnhandledInput(InputEvent e)
    {
        if (seedBox?.GetLineEdit().HasFocus() == true) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k
            && k.Keycode is Key.Key1 or Key.Key2 or Key.Key3)
            SetMapMode((int)(k.Keycode - Key.Key1));
    }
    public bool IsMapReady() => complete is not null && requestRevision == completedRevision;
    public bool is_map_ready() => IsMapReady();
    public void RequestMap(int seed, long around, int across, float spanKm)
    {
        Request b = Certified();
        float safeSpan = float.IsFinite(spanKm) ? Math.Clamp(spanKm, 1, 256) : 128f;
        int halfHeight = (int)Math.Ceiling(safeSpan * 1000d * Height / Width / 2d);
        requested = b with
        {
            Seed = seed,
            Around = Wrap(around, b.Circ),
            Across = Math.Clamp(across, halfHeight, Math.Max(halfHeight, b.RingWidth - halfHeight - 1)),
            Span = safeSpan
        };
        requestRevision++;
        startPending = true;
        DisposeBuild();
        cursor = 0;
        error = "";
        UpdateStatus();
    }
    public void request_map(int seed,long around,int across,float spanKm)=>RequestMap(seed,around,across,spanKm);
    public void SetMapMode(int value)
    {
        int next = Math.Clamp(value, 0, 2);
        if (next == mode) return;
        mode = next;
        if (complete is not null) Recolor();
        UpdateLegend();
        UpdateStatus();
    }
    public void set_map_mode(int value)=>SetMapMode(value);
    public Godot.Collections.Dictionary GetPreviewDiagnostics() => new()
    {
        ["ready"] = IsMapReady(), ["building"] = sampler is not null,
        ["request_revision"] = requestRevision, ["revision"] = completedRevision,
        ["map_width"] = Width, ["map_height"] = Height,
        ["sample_count"] = samples, ["recolor_count"] = recolors,
        ["valid_samples"] = published.Valid, ["dense_count"] = published.Dense,
        ["open_count"] = published.Open, ["clearing_count"] = published.Clearing,
        ["maple_count"] = published.Maple, ["fir_count"] = published.Fir, ["pine_count"] = published.Pine,
        ["terrain_checksum"] = published.TerrainHash, ["forest_checksum"] = published.ForestHash,
        ["completed_seed"] = completed.Seed, ["completed_around"] = completed.Around,
        ["completed_across"] = completed.Across, ["completed_span_km"] = completed.Span,
        ["build_elapsed_ms"] = published.ElapsedMilliseconds, ["last_error"] = error
    };
    public Godot.Collections.Dictionary get_preview_diagnostics()=>GetPreviewDiagnostics();
    void Start()
    {
        active = requested;
        startPending = false;
        try
        {
            Resource source = GD.Load<Resource>(SettingsPath) ?? throw new InvalidOperationException("missing terrain settings");
            settings = (VoxelTerrainSettings)source.Duplicate(true);
            Apply(settings, active);
            sampler = new RingTerrainSampler(settings, true, active.Circ, active.Radius, active.RingWidth, active.BuildHeight);
            regions = new ForestRegionSampler(active.Seed, active.Circ, active.RingWidth);
            building = Image.CreateEmpty(Width, Height, false, Image.Format.Rgba8);
            cursor = valid = dense = open = clearing = maple = fir = pine = 0;
            terrainHash = forestHash = 0;
            watch = Stopwatch.StartNew();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DisposeBuild();
            GD.PushError("FOREST_PREVIEW=FAIL " + error);
            UpdateStatus();
        }
    }
    void Sample(int i)
    {
        int x = i % Width, y = i / Width;
        double w = active.Span * 1000d, h = w * Height / Width;
        long a = Wrap(active.Around + (long)Math.Round(((x + .5) / Width - .5) * w), active.Circ);
        int z = active.Across + (int)Math.Round(((y + .5) / Height - .5) * h);
        TerrainSample t = sampler!.GetTerrainSample(a, z);
        // This map previews regional cover potential. Precise local slope probes
        // belong to the production placement step, not a 500 m raster gradient.
        ForestRegionSample f = regions!.Sample(a, z, t);
        buildingTerrain[i] = t;
        buildingForest[i] = f;
        samples++;
        terrainHash = unchecked(terrainHash * 31 + t.SurfaceHeight + (int)t.Biome);
        forestHash = unchecked(forestHash * 31 + (int)(f.Density * 10000) + (int)f.Habitat);
        if (f.Density > 0)
        {
            valid++;
            if (f.MapleWeight >= f.FirWeight && f.MapleWeight >= f.PineWeight) maple++;
            else if (f.FirWeight >= f.PineWeight) fir++;
            else pine++;
        }
        if (f.Habitat == ForestHabitat.DenseForest) dense++;
        if (f.Habitat == ForestHabitat.OpenWoodland) open++;
        if (f.Habitat == ForestHabitat.Clearing) clearing++;
    }
    void Finish()
    {
        watch?.Stop();
        Array.Copy(buildingTerrain, completedTerrain, completedForest.Length);
        Array.Copy(buildingForest, completedForest, completedForest.Length);
        complete?.Dispose();
        complete = building;
        building = null;
        completed = active;
        published = new Statistics(valid, dense, open, clearing, maple, fir, pine,
            terrainHash, forestHash, watch?.Elapsed.TotalMilliseconds ?? 0);
        completedRevision = requestRevision;
        DisposeSamplers();
        Recolor();
    }
    void Recolor()
    {
        if (complete is null) return;
        for (int i = 0; i < completedForest.Length; i++)
            complete.SetPixel(i % Width, i / Width, ColorFor(completedForest[i], completedTerrain[i]));
        if (map is not null)
        {
            if (texture is null) texture = ImageTexture.CreateFromImage(complete);
            else texture.Update(complete);
            map.Texture = texture;
        }
        recolors++;
    }

    private static readonly Color WaterColor = new(.08f, .25f, .62f);
    private static readonly Color EmptyColor = new(.16f, .14f, .12f);
    private static readonly Color MapleColor = new(.78f, .3f, .1f);
    private static readonly Color FirColor = new(.45f, .16f, .82f);
    private static readonly Color PineColor = new(.12f, .68f, .3f);
    private static readonly Color DenseColor = new(.03f, .32f, .07f);
    private static readonly Color OpenColor = new(.2f, .55f, .15f);
    private static readonly Color EdgeColor = new(.55f, .65f, .2f);
    private static readonly Color ClearingColor = new(.82f, .7f, .25f);

    Color ColorFor(ForestRegionSample f, TerrainSample t)
    {
        if (t.HasSurfaceWater) return WaterColor;
        if (f.Suitability <= 0) return EmptyColor;
        if (mode == 1) return new(f.Density, f.Density, f.Density);
        if (mode == 2)
        {
            if (f.Density <= 0) return EmptyColor;
            if (f.FirWeight > f.MapleWeight && f.FirWeight > f.PineWeight) return FirColor;
            return f.PineWeight > f.MapleWeight ? PineColor : MapleColor;
        }
        return f.Habitat switch
        {
            ForestHabitat.DenseForest => DenseColor,
            ForestHabitat.OpenWoodland => OpenColor,
            ForestHabitat.Edge => EdgeColor,
            ForestHabitat.Clearing => ClearingColor,
            _ => EmptyColor
        };
    }
    Request Certified()
    {
        Resource c = GD.Load<Resource>(ConfigPath) ?? throw new InvalidOperationException("missing certified config");
        double r = c.Get("default_radius_km").AsDouble() * 1000;
        int w = (int)Math.Round(c.Get("ring_width_km").AsDouble() * 1000);
        int s = (int)Math.Round(c.Get("terrain_seed").AsDouble());
        long circumference = (long)Math.Round(Math.Tau * r / 32) * 32;
        return new(s, 0, w / 2, 128, circumference, w, circumference / Math.Tau,
            (int)Math.Round(c.Get("build_height_y").AsDouble()));
    }

    static void Apply(VoxelTerrainSettings s, Request r)
    {
        Resource c = GD.Load<Resource>(ConfigPath) ?? throw new InvalidOperationException("missing certified config");
        s.Seed = r.Seed;
        s.BaseSurfaceHeightVoxels = (int)Math.Round(c.Get("base_surface_height").AsDouble());
        s.HeightAmplitudeVoxels = (int)Math.Round(c.Get("height_amplitude").AsDouble());
        s.DetailAmplitudeVoxels = (int)Math.Round(c.Get("detail_amplitude").AsDouble());
        s.ContinentalContrast = (float)c.Get("continental_contrast").AsDouble();
        s.RegionalDetailStrength = (float)c.Get("regional_detail_strength").AsDouble();
        s.RarePeakY = (int)Math.Round(c.Get("rare_peak_height").AsDouble());
        s.BedrockY = -256;
        s.SeaLevelY = 0;
        s.AutoRaiseRarePeakToBuildLimit = false;
    }
    void BuildUi()
    {
        if (DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase)) return;
        CanvasLayer layer = new();
        AddChild(layer);
        status = new Label { Position = new(18, 8) };
        layer.AddChild(status);
        map = new TextureRect
        {
            Name = "RegionMap", Position = new(18, 58), Size = new(768, 576),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest
        };
        map.GuiInput += e => { if (e is InputEventMouseMotion motion) Hover(motion.Position); };
        layer.AddChild(map);
        layer.AddChild(new Label { Text = "Certified reference / seed", Position = new(800, 60) });
        seedBox = new SpinBox
        {
            Name = "Seed", Position = new(800, 90), Size = new(210, 32),
            MinValue = 0, MaxValue = int.MaxValue, Value = requested.Seed
        };
        layer.AddChild(seedBox);
        AddButton(layer, "Generate", 800, 130, () =>
            RequestMap((int)seedBox.Value, requested.Around, requested.Across, requested.Span));
        AddButton(layer, "Habitat", 800, 175, () => SetMapMode(0));
        AddButton(layer, "Density", 890, 175, () => SetMapMode(1));
        AddButton(layer, "Species", 970, 175, () => SetMapMode(2));
        AddButton(layer, "Left", 800, 220, () =>
            RequestMap(requested.Seed, requested.Around - (long)Math.Round(requested.Span * 1000 * .25), requested.Across, requested.Span));
        AddButton(layer, "Right", 880, 220, () =>
            RequestMap(requested.Seed, requested.Around + (long)Math.Round(requested.Span * 1000 * .25), requested.Across, requested.Span));
        AddButton(layer, "Across -", 960, 220, () =>
            RequestMap(requested.Seed, requested.Around, requested.Across - (int)Math.Round(requested.Span * 1000 * Height / (double)Width * .25), requested.Span));
        AddButton(layer, "Across +", 1050, 220, () =>
            RequestMap(requested.Seed, requested.Around, requested.Across + (int)Math.Round(requested.Span * 1000 * Height / (double)Width * .25), requested.Span));
        AddButton(layer, "Zoom in", 800, 270, () =>
            RequestMap(requested.Seed, requested.Around, requested.Across, requested.Span * .5f));
        AddButton(layer, "Zoom out", 900, 270, () =>
            RequestMap(requested.Seed, requested.Around, requested.Across, requested.Span * 2f));
        legend = new RichTextLabel
        {
            Position = new(800, 325), Size = new(400, 265), BbcodeEnabled = true,
            FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore
        };
        layer.AddChild(legend);
        hover = new Label
        {
            Name = "SampleReadout", Position = new(18, 644),
            Text = "Hover over the map to inspect a completed sample."
        };
        layer.AddChild(hover);
        layer.AddChild(new Label
        {
            Position = new(18, 689),
            Text = "Regional cover potential; precise local slopes are filtered at placement. Keys 1 / 2 / 3 change map mode."
        });
        UpdateLegend();
    }

    void AddButton(CanvasLayer layer, string text, float x, float y, Action action)
    {
        Button button = new() { Text = text, Position = new(x, y) };
        button.Pressed += action;
        layer.AddChild(button);
    }

    void UpdateLegend()
    {
        if (legend is null) return;
        static string Key(Color color, string text) => $"[color=#{color.ToHtml(false)}]■[/color] {text}\n";
        string content = mode switch
        {
            1 => "[b]DENSITY[/b]\nBlack = 0, white = 1\nNormalized cover, not a tree count.\n\n",
            2 => "[b]DOMINANT SPECIES WEIGHT[/b]\n" + Key(MapleColor, "Maple") + Key(FirColor, "Fir") + Key(PineColor, "Pine") + "\n",
            _ => "[b]HABITAT[/b]\n" + Key(DenseColor, "Dense forest") + Key(OpenColor, "Open woodland")
                + Key(EdgeColor, "Woodland edge") + Key(ClearingColor, "Clearing") + "\n"
        };
        legend.Text = content + Key(WaterColor, "Surface water") + Key(EmptyColor, "Sparse / unsuitable terrain")
            + "\nLeft / Right: around the ring\nAcross - / +: across the ring width";
    }

    // TextureRect.GuiInput coordinates are already local to the map.
    void Hover(Vector2 p)
    {
        if (map is null || hover is null || complete is null || !new Rect2(Vector2.Zero, map.Size).HasPoint(p)) return;
        int x = Math.Clamp((int)(p.X / map.Size.X * Width), 0, Width - 1);
        int y = Math.Clamp((int)(p.Y / map.Size.Y * Height), 0, Height - 1);
        ForestRegionSample f = completedForest[y * Width + x];
        TerrainSample t = completedTerrain[y * Width + x];
        double w = completed.Span * 1000d;
        long a = Wrap(completed.Around + (long)Math.Round(((x + .5) / Width - .5) * w), completed.Circ);
        int z = completed.Across + (int)Math.Round(((y + .5) / Height - .5) * w * Height / Width);
        hover.Text = $"Around {a / 1000d:F2} km | Across {z / 1000d:F2} km | Height {t.SurfaceHeight} m | {t.Biome} / {t.WaterKind}\n"
            + $"{f.Habitat} | Density {f.Density:F2} | Maple {f.MapleWeight:P0}, fir {f.FirWeight:P0}, pine {f.PineWeight:P0}";
    }

    void UpdateStatus()
    {
        if (status is null) return;
        string title = "FOREST REGIONS - Preview only; world trees unchanged";
        if (error.Length > 0) { status.Text = title + "\nError: " + error; return; }
        string coordinates = $"Seed {requested.Seed} | Around {requested.Around / 1000d:F1} km | Across {requested.Across / 1000d:F1} km | Span {requested.Span:F0} x {requested.Span * Height / Width:F0} km";
        status.Text = title + "\n" + coordinates + (IsMapReady()
            ? $" | Ready: {published.Dense} dense / {published.Open} woodland / {published.Clearing} clearing pixels"
            : $" | Sampling {cursor}/{completedForest.Length}; showing last completed map");
    }

    static long Wrap(long x, long c)
    {
        long remainder = x % c;
        return remainder < 0 ? remainder + c : remainder;
    }

    void DisposeSamplers()
    {
        sampler?.Dispose();
        sampler = null;
        regions = null;
        settings?.Dispose();
        settings = null;
    }

    void DisposeBuild()
    {
        DisposeSamplers();
        building?.Dispose();
        building = null;
        watch = null;
    }

    void DisposeAll()
    {
        DisposeBuild();
        if (map is not null) map.Texture = null;
        complete?.Dispose();
        complete = null;
        texture?.Dispose();
        texture = null;
    }
}
