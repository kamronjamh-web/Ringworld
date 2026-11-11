using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using GodotDictionary = Godot.Collections.Dictionary;

namespace RingworldTechDemo.Terrain.Generation;

/// <summary>Validated, normalized v1 surface-recipe snapshot. This is deliberately data-only.</summary>
public sealed class TerrainGenerationParameters
{
    public const string LegacyGeneratorIdentity = "ringworld_surface_reference_v1";
    public const string GeneratorIdentity = "ringworld_surface_coastal_v2";
    public const string DetailGeneratorIdentity = "ringworld_surface_detail_v3";
    public const string ErosionGeneratorIdentity = "ringworld_surface_erosion_v4";
    public const string SitesGeneratorIdentity = "ringworld_surface_sites_v5";
    public const string ClimateGeneratorIdentity = "ringworld_surface_climate_v6";
    public const string LandformGeneratorIdentity = "ringworld_surface_landform_v7";
    public const string WeatherLungGeneratorIdentity = "ringworld_surface_weather_lung_v8";
    public const string WorldGenerationIdentity = "ringworld_surface_world_v9";
    public const string DomainIdentity = "canonical_ring_noise_v1";
    public const double CertifiedCircumferenceMeters = 31415936d, CertifiedWidthMeters = 318000d;
    public const double MinimumHeightMeters = -8000d, MaximumHeightMeters = 8000d, MinimumWavelengthMeters = 16d;

    private static readonly HashSet<string> RequiredFields = new(StringComparer.Ordinal) { "seed","generator_id","domain_id","circumference_m","width_m","min_height_m","max_height_m","land_balance","continental_scale_m","mountain_scale_m","basin_depth_m","trench_depth_m","trench_threshold","mountain_amplitude_m","valley_width","flattening","ridge_scale_m","ridge_sharpness","secondary_amplitude_m","micro_amplitude_m","detail_start_m","detail_full_m","warp_amplitude_m","continental_octaves","mountain_octaves","valley_octaves","trench_octaves","ridge_octaves","secondary_octaves","micro_octaves","warp_octaves","noise_gain","noise_lacunarity","secondary_scale_m","micro_scale_m","warp_scale_m" };
    private static readonly HashSet<string> CoastalFields = new(RequiredFields, StringComparer.Ordinal) { "beach_slope","shelf_width_m","shelf_depth_m","continental_slope_width_m" };
    private static readonly HashSet<string> DetailFields = new(CoastalFields, StringComparer.Ordinal) { "foothill_amplitude_m","foothill_scale_m","spur_amplitude_m","spur_scale_m","mountain_detail_mode","erosion_strength_m","erosion_scale_m","erosion_octaves","swiss_warp" };
    private static readonly HashSet<string> ErosionFields = new(DetailFields, StringComparer.Ordinal) { "erosion_ridge_preservation","gully_normalization" };
    private static readonly HashSet<string> SitesFields = new(ErosionFields, StringComparer.Ordinal);
    private static readonly HashSet<string> LandformFields = new(SitesFields, StringComparer.Ordinal) { "climate_machines","landform_scale_m","plains_threshold","hills_threshold","plateau_scale_m","plateau_threshold","plateau_full_threshold","hill_amplitude_m","hill_scale_m","hill_detail_weight","hill_valley_exponent","plains_hill_fraction","hill_valley_floor","plateau_lift_m","plateau_step_m","terrace_riser_width","terrace_strength","escarpment_jitter_m","escarpment_scale_m","breaks_strength","breaks_step_m","landform_shore_start_m","landform_shore_full_m","lowland_detail_floor","plains_detail_fraction","lowland_erosion_strength_m","lowland_erosion_octaves" };
    public long Seed { get; } public string GeneratorId { get; } public string DomainId { get; }
    public double CircumferenceM { get; } public double WidthM { get; } public double MinHeightM { get; } public double MaxHeightM { get; }
    public double LandBalance { get; } public double ContinentalScaleM { get; } public double MountainScaleM { get; } public double BasinDepthM { get; } public double TrenchDepthM { get; } public double TrenchThreshold { get; } public double MountainAmplitudeM { get; } public double ValleyWidth { get; } public double Flattening { get; } public double RidgeScaleM { get; } public double RidgeSharpness { get; } public double SecondaryAmplitudeM { get; } public double MicroAmplitudeM { get; } public double DetailStartM { get; } public double DetailFullM { get; } public double WarpAmplitudeM { get; }
    public int ContinentalOctaves { get; } public int MountainOctaves { get; } public int ValleyOctaves { get; } public int TrenchOctaves { get; } public int RidgeOctaves { get; } public int SecondaryOctaves { get; } public int MicroOctaves { get; } public int WarpOctaves { get; }
    public double NoiseGain { get; } public double NoiseLacunarity { get; } public double SecondaryScaleM { get; } public double MicroScaleM { get; } public double WarpScaleM { get; }
    public double BeachSlope { get; } public double ShelfWidthM { get; } public double ShelfDepthM { get; } public double ContinentalSlopeWidthM { get; }
    public double FoothillAmplitudeM { get; } public double FoothillScaleM { get; } public double SpurAmplitudeM { get; } public double SpurScaleM { get; } public int MountainDetailMode { get; } public double ErosionStrengthM { get; } public double ErosionScaleM { get; } public int ErosionOctaves { get; } public double SwissWarp { get; }
    public double ErosionRidgePreservation { get; } public double GullyNormalization { get; }
    // v7 lowland-landform fields (docs/lowland-landform-overhaul-plan.md section 4); zero for earlier recipes.
    public int ClimateMachinesField { get; }
    public double LandformScaleM { get; } public double PlainsThreshold { get; } public double HillsThreshold { get; } public double PlateauScaleM { get; } public double PlateauThreshold { get; } public double PlateauFullThreshold { get; }
    public double HillAmplitudeM { get; } public double HillScaleM { get; } public double HillDetailWeight { get; } public double HillValleyExponent { get; } public double PlainsHillFraction { get; } public double HillValleyFloor { get; }
    public double PlateauLiftM { get; } public double PlateauStepM { get; } public double TerraceRiserWidth { get; } public double TerraceStrength { get; } public double EscarpmentJitterM { get; } public double EscarpmentScaleM { get; }
    public double BreaksStrength { get; } public double BreaksStepM { get; } public double LandformShoreStartM { get; } public double LandformShoreFullM { get; } public double LowlandDetailFloor { get; } public double PlainsDetailFraction { get; }
    public double LowlandErosionStrengthM { get; } public int LowlandErosionOctaves { get; }
    public bool HasMountainDetail => GeneratorId == DetailGeneratorIdentity || GeneratorId == ErosionGeneratorIdentity || GeneratorId == SitesGeneratorIdentity || GeneratorId == ClimateGeneratorIdentity || GeneratorId == LandformGeneratorIdentity || HasWeatherLung;
    public bool HasEnhancedErosion => GeneratorId == ErosionGeneratorIdentity || GeneratorId == SitesGeneratorIdentity || GeneratorId == ClimateGeneratorIdentity || GeneratorId == LandformGeneratorIdentity || HasWeatherLung;
    /// <summary>v5 adds the emitter-site shaping operator (docs/emitter-site-terrain-spec.md section 5). It carries exactly the v4 fields; the site recipe is derived from the seed.</summary>
    public bool HasEmitterSites => GeneratorId == SitesGeneratorIdentity || GeneratorId == ClimateGeneratorIdentity || GeneratorId == LandformGeneratorIdentity || HasWeatherLung;
    /// <summary>v6 adds the 200 Wall Engine climate machines (forecourt shaping) on top of v5 (docs/emitter-grid-climate-machines-spec.md section C3). It carries the v5 fields; the climate recipe is derived from the seed.</summary>
    public bool HasClimateMachines => GeneratorId == ClimateGeneratorIdentity || ((GeneratorId == LandformGeneratorIdentity || HasWeatherLung) && ClimateMachinesField == 1);
    /// <summary>v7 adds lowland landforms (plains, hill country, plateaus, breaks) on top of the v6 field set; climate machines are opt-in through <c>climate_machines</c>.</summary>
    public bool HasLowlandLandforms => GeneratorId == LandformGeneratorIdentity || HasWeatherLung;
    /// <summary>v8 adds the Weather Lung basin operator on top of the v7 field set (docs/weather-lung-integration-plan.md D3); the placement is derived from the seed. Climate machines stay opt-in through <c>climate_machines</c>.</summary>
    public bool HasWeatherLung => GeneratorId == WeatherLungGeneratorIdentity || IsWorldGenerationRecipe;
    public double FlattenDenominator { get; }
    public string CanonicalJson { get; }
    public ulong DefinitionHash { get; }

    private sealed record ClimateField(string Name, double Default, double Min, double Max, bool IsInteger = false);

    private static readonly ClimateField[] WorldClimateFields =
    {
        new("continental_frequency", 3.5e-6f, 1e-6f, 1e-3f),
        new("erosion_frequency", 1.2e-5f, 1e-6f, 1e-3f),
        new("ridge_frequency", 8e-6f, 1e-6f, 1e-3f),
        new("temperature_frequency", 2.5e-6f, 1e-6f, 1e-3f),
        new("moisture_frequency", 5e-6f, 1e-6f, 1e-3f),
        new("mountain_warp_frequency", 4e-6f, 1e-6f, 1e-3f),
        new("valley_frequency", 1.4e-5f, 1e-6f, 1e-3f),
        new("continental_secondary_strength", 0.28f, 0f, 0.75f),
        new("continental_contrast", 1.35f, 1f, 3f),
        new("continental_ocean_bias", 0f, -0.5f, 0.5f),
        new("mountain_warp_distance_m", 16000f, 0f, 20000f),
        new("peak_rarity_exponent", 16f, 1f, 24f),
        new("mountain_belt_broadness", 0.5f, 0f, 1f),
        new("valley_narrowness", 7.6f, 1f, 12f),
        new("ocean_moisture_influence", 0.25f, 0f, 1f),
        new("climate_latitude_influence", 0.55f, 0f, 1f),
        new("coastal_beach_strength", 0.82f, 0f, 1f),
        new("altitude_temperature_drop_per_kilometre", 0.075f, 0f, 0.35f),
        new("mountain_rain_shadow_strength", 0.18f, 0f, 0.60f),
        new("macro_region_size_m", 128000, 16000, 512000, IsInteger: true),
    };

    private static readonly HashSet<string> WorldFields = new(
        LandformFields.Concat(new[] { "world_generation", "review_features" }).Concat(WorldClimateFields.Select(field => field.Name)),
        StringComparer.Ordinal);

    private readonly WorldRecipeSchema? _schema;
    private readonly IReadOnlySet<string>? _worldFeatures;
    private readonly Dictionary<string, double>? _worldClimate;
    private readonly Dictionary<string, double>? _worldFields;

    public int WorldGeneration { get; } = 1;
    public IReadOnlyList<string> ReviewFeatures { get; } = Array.Empty<string>();
    public bool IsWorldGenerationRecipe => GeneratorId == WorldGenerationIdentity;
    public ulong NaturalSiteIdentity { get; }
    public double ContinentalFrequency => ClimateValue("continental_frequency");
    public double ErosionFrequency => ClimateValue("erosion_frequency");
    public double RidgeFrequency => ClimateValue("ridge_frequency");
    public double TemperatureFrequency => ClimateValue("temperature_frequency");
    public double MoistureFrequency => ClimateValue("moisture_frequency");
    public double MountainWarpFrequency => ClimateValue("mountain_warp_frequency");
    public double ValleyFrequency => ClimateValue("valley_frequency");
    public double ContinentalSecondaryStrength => ClimateValue("continental_secondary_strength");
    public double ContinentalContrast => ClimateValue("continental_contrast");
    public double ContinentalOceanBias => ClimateValue("continental_ocean_bias");
    public double MountainWarpDistanceMeters => ClimateValue("mountain_warp_distance_m");
    public double PeakRarityExponent => ClimateValue("peak_rarity_exponent");
    public double MountainBeltBroadness => ClimateValue("mountain_belt_broadness");
    public double ValleyNarrowness => ClimateValue("valley_narrowness");
    public double OceanMoistureInfluence => ClimateValue("ocean_moisture_influence");
    public double ClimateLatitudeInfluence => ClimateValue("climate_latitude_influence");
    public double CoastalBeachStrength => ClimateValue("coastal_beach_strength");
    public double AltitudeTemperatureDropPerKilometre => ClimateValue("altitude_temperature_drop_per_kilometre");
    public double MountainRainShadowStrength => ClimateValue("mountain_rain_shadow_strength");
    public int MacroRegionSizeMeters => (int)ClimateValue("macro_region_size_m");

    public bool HasWorldFeature(string id) => _worldFeatures?.Contains(id) == true;

    public double GetExtensionField(string name)
        => _worldFields != null && _worldFields.TryGetValue(name, out double value) ? value
            : throw new ArgumentException($"Recipe field '{name}' is inactive.", nameof(name));

    private double ClimateValue(string name) => _worldClimate?.GetValueOrDefault(name) ?? 0;

    private static HashSet<string> WorldFieldNames(RecipeFieldSpec[] activeFields)
        => activeFields.Length == 0 ? WorldFields : new(WorldFields.Concat(activeFields.Select(field => field.Name)), StringComparer.Ordinal);

    private static ulong HashCanonical(string json)
    {
        ulong hash = Fnv1a64(Encoding.UTF8.GetBytes(json));
        return hash == 0 ? 1 : hash;
    }

    private static double ReadClimateField(JsonElement root, ClimateField field)
    {
        if (field.IsInteger)
        {
            int integer = Int(root, field.Name);
            if (integer < field.Min || integer > field.Max) throw new ArgumentOutOfRangeException(field.Name);
            return integer;
        }
        float value = (float)Number(root, field.Name);
        if (!float.IsFinite(value) || value < field.Min || value > field.Max)
            throw new ArgumentOutOfRangeException(field.Name);
        return value == 0f ? 0d : value;
    }

    private static double ReadExtensionField(JsonElement root, RecipeFieldSpec field)
    {
        if (field.Kind == RecipeFieldKind.Float) return Range(root, field.Name, field.Min, field.Max);
        int value = Int(root, field.Name);
        if (value < field.Min || value > field.Max) throw new ArgumentOutOfRangeException(field.Name);
        return value;
    }

    private static void AppendNumericField(StringBuilder json, string name, double value)
        => json.Append(',').Append(JsonSerializer.Serialize(name)).Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));

    private static void AppendWorldFields(StringBuilder json, int generation, string review)
        => json.Append(",\"world_generation\":").Append(generation.ToString(CultureInfo.InvariantCulture))
            .Append(",\"review_features\":").Append(JsonSerializer.Serialize(review));

    private string Canonical(RecipeFieldSpec[] activeFields)
    {
        string body = CanonicalBase(GeneratorId);
        if (!IsWorldGenerationRecipe) return body;
        var json = new StringBuilder(body[..^1]);
        AppendWorldFields(json, WorldGeneration, string.Join(",", ReviewFeatures));
        foreach (ClimateField field in WorldClimateFields)
            AppendNumericField(json, field.Name, ClimateValue(field.Name));
        foreach (RecipeFieldSpec field in activeFields)
            AppendNumericField(json, field.Name, GetExtensionField(field.Name));
        return json.Append('}').ToString();
    }

    public static TerrainGenerationParameters WorldDefault(int generation = 1, long seed = 1337,
        IReadOnlyList<string>? review = null, bool climateMachines = false, WorldRecipeSchema? schema = null)
    {
        schema ??= WorldRecipeSchema.Production;
        if (review != null && review.Any(id => !WorldGenerationLedger.IsFeatureId(id)))
            throw new ArgumentException("Review selections must be individual feature IDs.", nameof(review));
        string reviewCsv = review == null ? "" : string.Join(",", review.OrderBy(id => id, StringComparer.Ordinal));
        string[] reviewIds = WorldGenerationLedger.ParseReviewFeatures(reviewCsv);
        if (!schema.Ledger.Validate(generation, reviewIds, out string error)) throw new ArgumentException(error);
        string body = WeatherLungDefault().CanonicalJson
            .Replace(WeatherLungGeneratorIdentity, WorldGenerationIdentity)
            .Replace("\"seed\":1337", "\"seed\":" + seed.ToString(CultureInfo.InvariantCulture))
            .Replace("\"climate_machines\":0", climateMachines ? "\"climate_machines\":1" : "\"climate_machines\":0");
        var json = new StringBuilder(body[..^1]);
        AppendWorldFields(json, generation, reviewCsv);
        foreach (ClimateField field in WorldClimateFields)
            AppendNumericField(json, field.Name, field.Default);
        foreach (RecipeFieldSpec field in schema.ActiveRecipeFields(generation, reviewIds))
            AppendNumericField(json, field.Name, field.Default);
        return FromJson(json.Append('}').ToString(), schema);
    }

    public TerrainGenerationParameters WithFields(IReadOnlyDictionary<string, double> overrides)
    {
        using JsonDocument original = JsonDocument.Parse(CanonicalJson);
        foreach (var (name, value) in overrides)
            if (!original.RootElement.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.Number || !double.IsFinite(value))
                throw new ArgumentException($"Recipe field '{name}' is inactive or is not a finite number.", nameof(overrides));

        var json = new StringBuilder("{");
        foreach (JsonProperty property in original.RootElement.EnumerateObject())
        {
            if (json.Length > 1) json.Append(',');
            json.Append(JsonSerializer.Serialize(property.Name)).Append(':');
            json.Append(overrides.TryGetValue(property.Name, out double value)
                ? value.ToString("R", CultureInfo.InvariantCulture) : property.Value.GetRawText());
        }
        TerrainGenerationParameters recipe = FromJson(json.Append('}').ToString(), _schema);
        if (_schema != null && !_schema.Ledger.Validate(recipe, out string error)) throw new ArgumentException(error);
        return recipe;
    }

    internal GodotDictionary WorldDefaultFields()
    {
        var result = new GodotDictionary { ["world_generation"] = WorldGeneration, ["review_features"] = string.Join(",", ReviewFeatures) };
        foreach (ClimateField field in WorldClimateFields)
            result[field.Name] = field.IsInteger ? Variant.From((int)ClimateValue(field.Name)) : Variant.From(ClimateValue(field.Name));
        foreach (RecipeFieldSpec field in _schema!.ActiveRecipeFields(WorldGeneration, ReviewFeatures))
            result[field.Name] = field.Kind == RecipeFieldKind.Int ? Variant.From((int)GetExtensionField(field.Name)) : Variant.From(GetExtensionField(field.Name));
        return result;
    }

    private TerrainGenerationParameters(JsonElement root, WorldRecipeSchema schema)
    {
        GeneratorId = Text(root, "generator_id"); DomainId = Text(root, "domain_id");
        RecipeFieldSpec[] activeFields = Array.Empty<RecipeFieldSpec>();
        if (IsWorldGenerationRecipe)
        {
            _schema = schema;
            WorldGeneration = Int(root, "world_generation");
            if (WorldGeneration < 1) throw new ArgumentOutOfRangeException("world_generation");
            ReviewFeatures = Array.AsReadOnly(WorldGenerationLedger.ParseReviewFeatures(Text(root, "review_features")));
            _worldFeatures = schema.Ledger.FeatureSet(WorldGeneration, ReviewFeatures);
            activeFields = schema.ActiveRecipeFields(WorldGeneration, ReviewFeatures).ToArray();
        }
        ValidateShape(root, activeFields);
        if (DomainId != DomainIdentity) throw new ArgumentException("Terrain recipe generator_id or domain_id is incompatible.");
        Seed = Int64(root, "seed"); CircumferenceM = Number(root, "circumference_m"); WidthM = Number(root, "width_m"); MinHeightM = Number(root, "min_height_m"); MaxHeightM = Number(root, "max_height_m");
        Equal(CircumferenceM, CertifiedCircumferenceMeters, "circumference_m"); Equal(WidthM, CertifiedWidthMeters, "width_m"); Equal(MinHeightM, MinimumHeightMeters, "min_height_m"); Equal(MaxHeightM, MaximumHeightMeters, "max_height_m");
        LandBalance = Range(root,"land_balance",-.35,.35); ContinentalScaleM=Range(root,"continental_scale_m",16,1000000); MountainScaleM=Range(root,"mountain_scale_m",16,500000); BasinDepthM=Range(root,"basin_depth_m",1250,8000); TrenchDepthM=Range(root,"trench_depth_m",0,8000); TrenchThreshold=Range(root,"trench_threshold",0,1); MountainAmplitudeM=Range(root,"mountain_amplitude_m",0,7936); ValleyWidth=Range(root,"valley_width",.01,1); Flattening=Range(root,"flattening",0,8); RidgeScaleM=Range(root,"ridge_scale_m",16,100000); RidgeSharpness=Range(root,"ridge_sharpness",.1,4); SecondaryAmplitudeM=Range(root,"secondary_amplitude_m",0,240); MicroAmplitudeM=Range(root,"micro_amplitude_m",0,32); DetailStartM=Range(root,"detail_start_m",0,7999); DetailFullM=Range(root,"detail_full_m",DetailStartM+1,8000); WarpAmplitudeM=Range(root,"warp_amplitude_m",0,50000);
        if (HasMountainDetail) { FoothillAmplitudeM=Range(root,"foothill_amplitude_m",0,240); FoothillScaleM=Range(root,"foothill_scale_m",128,8192); SpurAmplitudeM=Range(root,"spur_amplitude_m",0,400); SpurScaleM=Range(root,"spur_scale_m",128,8192); MountainDetailMode=Int(root,"mountain_detail_mode"); if(MountainDetailMode<0||MountainDetailMode>3) throw new ArgumentOutOfRangeException("mountain_detail_mode"); ErosionStrengthM=Range(root,"erosion_strength_m",0,160); ErosionScaleM=Range(root,"erosion_scale_m",64,2048); SwissWarp=Range(root,"swiss_warp",0,1); if (HasEnhancedErosion) { ErosionRidgePreservation=Range(root,"erosion_ridge_preservation",0,1); GullyNormalization=Range(root,"gully_normalization",0,1); } }
        if (TrenchDepthM > -MinHeightM-BasinDepthM) throw new ArgumentOutOfRangeException("trench_depth_m");
        if (MountainAmplitudeM > MaxHeightM-2000d-SecondaryAmplitudeM-MicroAmplitudeM-FoothillAmplitudeM-SpurAmplitudeM) throw new ArgumentOutOfRangeException("mountain_amplitude_m");
        NoiseGain=Range(root,"noise_gain",0,1); NoiseLacunarity=Range(root,"noise_lacunarity",1,2); SecondaryScaleM=Range(root,"secondary_scale_m",16,10000); MicroScaleM=Range(root,"micro_scale_m",16,1000); WarpScaleM=Range(root,"warp_scale_m",16,1000000);
        if (GeneratorId == GeneratorIdentity || HasMountainDetail) { BeachSlope=Range(root,"beach_slope",.005,.1); ShelfWidthM=Range(root,"shelf_width_m",2000,100000); ShelfDepthM=Range(root,"shelf_depth_m",50,300); ContinentalSlopeWidthM=Range(root,"continental_slope_width_m",5000,100000); }
        ContinentalOctaves=Octaves(root,"continental_octaves",ContinentalScaleM,8); MountainOctaves=Octaves(root,"mountain_octaves",MountainScaleM,8); ValleyOctaves=Octaves(root,"valley_octaves",Math.Max(16,MountainScaleM*.45),5); TrenchOctaves=Octaves(root,"trench_octaves",Math.Max(16,ContinentalScaleM*.35),5); RidgeOctaves=Octaves(root,"ridge_octaves",RidgeScaleM,8); SecondaryOctaves=Octaves(root,"secondary_octaves",SecondaryScaleM,5); MicroOctaves=Octaves(root,"micro_octaves",MicroScaleM,2); WarpOctaves=Octaves(root,"warp_octaves",WarpScaleM,5); if (HasMountainDetail) ErosionOctaves=Octaves(root,"erosion_octaves",ErosionScaleM,4);
        if (HasLowlandLandforms)
        {
            ClimateMachinesField=Int(root,"climate_machines"); if(ClimateMachinesField<0||ClimateMachinesField>1) throw new ArgumentOutOfRangeException("climate_machines");
            LandformScaleM=Range(root,"landform_scale_m",8000,200000); PlainsThreshold=Range(root,"plains_threshold",.05,.95); HillsThreshold=Range(root,"hills_threshold",PlainsThreshold+.01,.99); PlateauScaleM=Range(root,"plateau_scale_m",16000,400000); PlateauThreshold=Range(root,"plateau_threshold",.05,.95); PlateauFullThreshold=Range(root,"plateau_full_threshold",PlateauThreshold+.01,.99);
            HillAmplitudeM=Range(root,"hill_amplitude_m",0,900); HillScaleM=Range(root,"hill_scale_m",1000,16000); HillDetailWeight=Range(root,"hill_detail_weight",0,1); HillValleyExponent=Range(root,"hill_valley_exponent",.5,3); PlainsHillFraction=Range(root,"plains_hill_fraction",0,.5); HillValleyFloor=Range(root,"hill_valley_floor",0,1);
            PlateauLiftM=Range(root,"plateau_lift_m",0,900); PlateauStepM=Range(root,"plateau_step_m",20,600); TerraceRiserWidth=Range(root,"terrace_riser_width",.04,.5); TerraceStrength=Range(root,"terrace_strength",0,1); EscarpmentJitterM=Range(root,"escarpment_jitter_m",0,300); EscarpmentScaleM=Range(root,"escarpment_scale_m",2000,50000);
            BreaksStrength=Range(root,"breaks_strength",0,1); BreaksStepM=Range(root,"breaks_step_m",8,200); LandformShoreStartM=Range(root,"landform_shore_start_m",0,500); LandformShoreFullM=Range(root,"landform_shore_full_m",LandformShoreStartM+1,1500); LowlandDetailFloor=Range(root,"lowland_detail_floor",0,1); PlainsDetailFraction=Range(root,"plains_detail_fraction",0,1);
            LowlandErosionStrengthM=Range(root,"lowland_erosion_strength_m",0,120); LowlandErosionOctaves=Int(root,"lowland_erosion_octaves"); if(LowlandErosionOctaves<1||LowlandErosionOctaves>ErosionOctaves) throw new ArgumentOutOfRangeException("lowland_erosion_octaves");
        }
        if (IsWorldGenerationRecipe)
        {
            _worldClimate = WorldClimateFields.ToDictionary(field => field.Name, field => ReadClimateField(root, field), StringComparer.Ordinal);
            _worldFields = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (RecipeFieldSpec spec in activeFields)
                _worldFields.Add(spec.Name, ReadExtensionField(root, spec));
        }
        FlattenDenominator = Math.Exp(Flattening)-1d;
        CanonicalJson = Canonical(activeFields);
        DefinitionHash = HashCanonical(CanonicalJson);
        // The v8 projection preserves registry identity and emitter shot seeds for equivalent natural site terrain.
        NaturalSiteIdentity = IsWorldGenerationRecipe ? HashCanonical(CanonicalBase(WeatherLungGeneratorIdentity)) : DefinitionHash;
    }

    public static TerrainGenerationParameters LegacyDefault() => FromJson(DefaultJson(LegacyGeneratorIdentity, ""));
    public static TerrainGenerationParameters CoastalDefault() => FromJson(DefaultJson(GeneratorIdentity, ",\"beach_slope\":0.025,\"shelf_width_m\":12000,\"shelf_depth_m\":150,\"continental_slope_width_m\":30000"));
    public static TerrainGenerationParameters Default() => FromJson(DefaultJson(DetailGeneratorIdentity, ",\"beach_slope\":0.025,\"shelf_width_m\":12000,\"shelf_depth_m\":150,\"continental_slope_width_m\":30000,\"foothill_amplitude_m\":100,\"foothill_scale_m\":1500,\"spur_amplitude_m\":220,\"spur_scale_m\":1000,\"mountain_detail_mode\":2,\"erosion_strength_m\":60,\"erosion_scale_m\":256,\"erosion_octaves\":3,\"swiss_warp\":0.35"));
    public static TerrainGenerationParameters ErosionDefault() => FromJson(DefaultJson(ErosionGeneratorIdentity, ",\"beach_slope\":0.025,\"shelf_width_m\":12000,\"shelf_depth_m\":150,\"continental_slope_width_m\":30000,\"foothill_amplitude_m\":100,\"foothill_scale_m\":1500,\"spur_amplitude_m\":220,\"spur_scale_m\":1000,\"mountain_detail_mode\":2,\"erosion_strength_m\":60,\"erosion_scale_m\":256,\"erosion_octaves\":3,\"swiss_warp\":0.35,\"erosion_ridge_preservation\":0.75,\"gully_normalization\":0.6"));
    public static TerrainGenerationParameters SitesDefault() => FromJson(DefaultJson(SitesGeneratorIdentity, ",\"beach_slope\":0.025,\"shelf_width_m\":12000,\"shelf_depth_m\":150,\"continental_slope_width_m\":30000,\"foothill_amplitude_m\":100,\"foothill_scale_m\":1500,\"spur_amplitude_m\":220,\"spur_scale_m\":1000,\"mountain_detail_mode\":2,\"erosion_strength_m\":60,\"erosion_scale_m\":256,\"erosion_octaves\":3,\"swiss_warp\":0.35,\"erosion_ridge_preservation\":0.75,\"gully_normalization\":0.6"));
    /// <summary>v6 default: exactly the v5 field set with the climate generator id.</summary>
    public static TerrainGenerationParameters ClimateDefault() => FromJson(SitesDefault().CanonicalJson.Replace(SitesGeneratorIdentity, ClimateGeneratorIdentity));
    /// <summary>v7 default: the v5 field set with the landform generator id, plus the v7 lowland-landform fields at their defaults (climate machines off).</summary>
    public static TerrainGenerationParameters LandformDefault() => FromJson(SitesDefault().CanonicalJson.Replace(SitesGeneratorIdentity, LandformGeneratorIdentity)[..^1] + LandformDefaultSegment + "}");
    /// <summary>v8 default: exactly the v7 default (climate machines off) with the Weather Lung generator id.</summary>
    public static TerrainGenerationParameters WeatherLungDefault() => FromJson(LandformDefault().CanonicalJson.Replace(LandformGeneratorIdentity, WeatherLungGeneratorIdentity));
    private const string LandformDefaultSegment = ",\"climate_machines\":0,\"landform_scale_m\":32000,\"plains_threshold\":0.445,\"hills_threshold\":0.575,\"plateau_scale_m\":64000,\"plateau_threshold\":0.58,\"plateau_full_threshold\":0.68,\"hill_amplitude_m\":480,\"hill_scale_m\":5000,\"hill_detail_weight\":0.35,\"hill_valley_exponent\":1.35,\"plains_hill_fraction\":0.08,\"hill_valley_floor\":0.6,\"plateau_lift_m\":320,\"plateau_step_m\":160,\"terrace_riser_width\":0.14,\"terrace_strength\":0.85,\"escarpment_jitter_m\":70,\"escarpment_scale_m\":12000,\"breaks_strength\":0.6,\"breaks_step_m\":40,\"landform_shore_start_m\":20,\"landform_shore_full_m\":140,\"lowland_detail_floor\":0.45,\"plains_detail_fraction\":0.2,\"lowland_erosion_strength_m\":24,\"lowland_erosion_octaves\":2";
    private static string DefaultJson(string id, string additions) => "{\"seed\":1337,\"generator_id\":\""+id+"\",\"domain_id\":\"canonical_ring_noise_v1\",\"circumference_m\":31415936,\"width_m\":318000,\"min_height_m\":-8000,\"max_height_m\":8000,\"land_balance\":0,\"continental_scale_m\":300000,\"mountain_scale_m\":80000,\"basin_depth_m\":4500,\"trench_depth_m\":3000,\"trench_threshold\":0.8,\"mountain_amplitude_m\":5400,\"valley_width\":0.18,\"flattening\":3,\"ridge_scale_m\":8000,\"ridge_sharpness\":1.5,\"secondary_amplitude_m\":60,\"micro_amplitude_m\":4,\"detail_start_m\":2000,\"detail_full_m\":4500,\"warp_amplitude_m\":6000,\"continental_octaves\":4,\"mountain_octaves\":4,\"valley_octaves\":3,\"trench_octaves\":3,\"ridge_octaves\":4,\"secondary_octaves\":3,\"micro_octaves\":2,\"warp_octaves\":3,\"noise_gain\":0.5,\"noise_lacunarity\":2,\"secondary_scale_m\":256,\"micro_scale_m\":32,\"warp_scale_m\":180000"+additions+"}";
    public static TerrainGenerationParameters FromJson(string json, WorldRecipeSchema? schema = null) { using JsonDocument doc=JsonDocument.Parse(json); if(doc.RootElement.ValueKind!=JsonValueKind.Object) throw new ArgumentException("Terrain recipe must be a JSON object."); return new TerrainGenerationParameters(doc.RootElement, schema ?? WorldRecipeSchema.Production); }
    public static TerrainGenerationParameters FromDictionary(GodotDictionary snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool worldRecipe = snapshot.TryGetValue("generator_id", out Variant generator)
            && generator.VariantType == Variant.Type.String && generator.AsString() == WorldGenerationIdentity;
        var values = new StringBuilder("{");
        foreach (Variant key in snapshot.Keys)
        {
            if (key.VariantType != Variant.Type.String) throw new ArgumentException("Terrain recipe keys must be strings.");
            string name = key.AsString();
            if (values.Length > 1) values.Append(',');
            values.Append(JsonSerializer.Serialize(name)).Append(':');
            Variant value = snapshot[key];
            switch (value.VariantType)
            {
                case Variant.Type.String:
                    values.Append(JsonSerializer.Serialize(value.AsString()));
                    break;
                case Variant.Type.Int:
                    values.Append(value.AsInt64().ToString(CultureInfo.InvariantCulture));
                    break;
                case Variant.Type.Float:
                    double number = value.AsDouble();
                    if (!double.IsFinite(number)) throw new ArgumentException($"{name} must be finite.");
                    string numericText = number.ToString("R", CultureInfo.InvariantCulture);
                    values.Append(numericText);
                    // Preserve the Variant kind for integer-only v9 fields even for an integral float.
                    if (worldRecipe && !numericText.Contains('.') && !numericText.Contains('E')) values.Append(".0");
                    break;
                default:
                    throw new ArgumentException($"Terrain recipe field '{name}' has incompatible type.");
            }
        }
        return FromJson(values.Append('}').ToString());
    }
    private static string Text(JsonElement r,string n) { var e=Field(r,n); if(e.ValueKind!=JsonValueKind.String) throw new ArgumentException($"{n} must be a string."); return e.GetString()!; }
    private static int Int(JsonElement r,string n) { var e=Field(r,n); if(!e.TryGetInt32(out int v)) throw new ArgumentException($"{n} must be an int32."); return v; }
    private static long Int64(JsonElement r,string n) { var e=Field(r,n); if(!e.TryGetInt64(out long v)) throw new ArgumentException($"{n} must be an int64."); return v; }
    private static double Number(JsonElement r,string n) { var e=Field(r,n); if(e.ValueKind!=JsonValueKind.Number || !e.TryGetDouble(out double v) || !double.IsFinite(v)) throw new ArgumentException($"{n} must be finite."); return v; }
    private static double Range(JsonElement r,string n,double min,double max) { double v=Number(r,n); if(v<min||v>max) throw new ArgumentOutOfRangeException(n); return v; }
    private static JsonElement Field(JsonElement r,string n) => r.TryGetProperty(n,out JsonElement e) ? e : throw new ArgumentException($"Terrain recipe is missing '{n}'.");
    private void ValidateShape(JsonElement root, RecipeFieldSpec[] activeFields)
    {
        HashSet<string> required = GeneratorId switch
        {
            WorldGenerationIdentity => WorldFieldNames(activeFields),
            LandformGeneratorIdentity or WeatherLungGeneratorIdentity => LandformFields,
            ClimateGeneratorIdentity or SitesGeneratorIdentity => SitesFields,
            ErosionGeneratorIdentity => ErosionFields,
            DetailGeneratorIdentity => DetailFields,
            GeneratorIdentity => CoastalFields,
            LegacyGeneratorIdentity => RequiredFields,
            _ => throw new ArgumentException("Terrain recipe generator_id is incompatible.")
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException($"Terrain recipe contains duplicate '{property.Name}'.");
            if (!required.Contains(property.Name)) throw new ArgumentException($"Terrain recipe contains unknown '{property.Name}'.");
        }
        if (seen.Count != required.Count) throw new ArgumentException("Terrain recipe is incomplete.");
    }
    private static void Equal(double value,double expected,string name) { if(value!=expected) throw new ArgumentException($"Only certified {name} is supported."); }
    private int Octaves(JsonElement r,string name,double scale,int configuredMaximum) { int requested=Int(r,name), maximum=1; double wavelength=scale; while(maximum<configuredMaximum && wavelength/NoiseLacunarity>=16) { wavelength/=NoiseLacunarity; maximum++; } if(requested<1||requested>maximum) throw new ArgumentOutOfRangeException(name); return requested; }
    private string CanonicalBase(string generatorId) { string v1=CanonicalV1(generatorId); if (GeneratorId==LegacyGeneratorIdentity) return v1; string coastal=v1[..^1]+string.Format(CultureInfo.InvariantCulture,",\"beach_slope\":{0:R},\"shelf_width_m\":{1:R},\"shelf_depth_m\":{2:R},\"continental_slope_width_m\":{3:R}}}",BeachSlope,ShelfWidthM,ShelfDepthM,ContinentalSlopeWidthM); if (GeneratorId==GeneratorIdentity) return coastal; string detail=coastal[..^1]+string.Format(CultureInfo.InvariantCulture,",\"foothill_amplitude_m\":{0:R},\"foothill_scale_m\":{1:R},\"spur_amplitude_m\":{2:R},\"spur_scale_m\":{3:R},\"mountain_detail_mode\":{4},\"erosion_strength_m\":{5:R},\"erosion_scale_m\":{6:R},\"erosion_octaves\":{7},\"swiss_warp\":{8:R}}}",FoothillAmplitudeM,FoothillScaleM,SpurAmplitudeM,SpurScaleM,MountainDetailMode,ErosionStrengthM,ErosionScaleM,ErosionOctaves,SwissWarp); if (GeneratorId==DetailGeneratorIdentity) return detail; string erosionJson=detail[..^1]+string.Format(CultureInfo.InvariantCulture,",\"erosion_ridge_preservation\":{0:R},\"gully_normalization\":{1:R}}}",ErosionRidgePreservation,GullyNormalization); if (!HasLowlandLandforms) return erosionJson; return erosionJson[..^1]+string.Format(CultureInfo.InvariantCulture,",\"climate_machines\":{0},\"landform_scale_m\":{1:R},\"plains_threshold\":{2:R},\"hills_threshold\":{3:R},\"plateau_scale_m\":{4:R},\"plateau_threshold\":{5:R},\"plateau_full_threshold\":{6:R},\"hill_amplitude_m\":{7:R},\"hill_scale_m\":{8:R},\"hill_detail_weight\":{9:R},\"hill_valley_exponent\":{10:R},\"plains_hill_fraction\":{11:R},\"hill_valley_floor\":{12:R},\"plateau_lift_m\":{13:R},\"plateau_step_m\":{14:R},\"terrace_riser_width\":{15:R},\"terrace_strength\":{16:R},\"escarpment_jitter_m\":{17:R},\"escarpment_scale_m\":{18:R},\"breaks_strength\":{19:R},\"breaks_step_m\":{20:R},\"landform_shore_start_m\":{21:R},\"landform_shore_full_m\":{22:R},\"lowland_detail_floor\":{23:R},\"plains_detail_fraction\":{24:R},\"lowland_erosion_strength_m\":{25:R},\"lowland_erosion_octaves\":{26}}}",ClimateMachinesField,LandformScaleM,PlainsThreshold,HillsThreshold,PlateauScaleM,PlateauThreshold,PlateauFullThreshold,HillAmplitudeM,HillScaleM,HillDetailWeight,HillValleyExponent,PlainsHillFraction,HillValleyFloor,PlateauLiftM,PlateauStepM,TerraceRiserWidth,TerraceStrength,EscarpmentJitterM,EscarpmentScaleM,BreaksStrength,BreaksStepM,LandformShoreStartM,LandformShoreFullM,LowlandDetailFloor,PlainsDetailFraction,LowlandErosionStrengthM,LowlandErosionOctaves); }
    private string CanonicalV1(string generatorId) => string.Format(CultureInfo.InvariantCulture,"{{\"seed\":{0},\"generator_id\":{1},\"domain_id\":{2},\"circumference_m\":{3:R},\"width_m\":{4:R},\"min_height_m\":{5:R},\"max_height_m\":{6:R},\"land_balance\":{7:R},\"continental_scale_m\":{8:R},\"mountain_scale_m\":{9:R},\"basin_depth_m\":{10:R},\"trench_depth_m\":{11:R},\"trench_threshold\":{12:R},\"mountain_amplitude_m\":{13:R},\"valley_width\":{14:R},\"flattening\":{15:R},\"ridge_scale_m\":{16:R},\"ridge_sharpness\":{17:R},\"secondary_amplitude_m\":{18:R},\"micro_amplitude_m\":{19:R},\"detail_start_m\":{20:R},\"detail_full_m\":{21:R},\"warp_amplitude_m\":{22:R},\"continental_octaves\":{23},\"mountain_octaves\":{24},\"valley_octaves\":{25},\"trench_octaves\":{26},\"ridge_octaves\":{27},\"secondary_octaves\":{28},\"micro_octaves\":{29},\"warp_octaves\":{30},\"noise_gain\":{31:R},\"noise_lacunarity\":{32:R},\"secondary_scale_m\":{33:R},\"micro_scale_m\":{34:R},\"warp_scale_m\":{35:R}}}",Seed,JsonSerializer.Serialize(generatorId),JsonSerializer.Serialize(DomainId),CircumferenceM,WidthM,MinHeightM,MaxHeightM,LandBalance,ContinentalScaleM,MountainScaleM,BasinDepthM,TrenchDepthM,TrenchThreshold,MountainAmplitudeM,ValleyWidth,Flattening,RidgeScaleM,RidgeSharpness,SecondaryAmplitudeM,MicroAmplitudeM,DetailStartM,DetailFullM,WarpAmplitudeM,ContinentalOctaves,MountainOctaves,ValleyOctaves,TrenchOctaves,RidgeOctaves,SecondaryOctaves,MicroOctaves,WarpOctaves,NoiseGain,NoiseLacunarity,SecondaryScaleM,MicroScaleM,WarpScaleM);
    private static ulong Fnv1a64(byte[] bytes) { ulong h=14695981039346656037UL; foreach(byte b in bytes) { h^=b; h*=1099511628211UL; } return h; }
}
