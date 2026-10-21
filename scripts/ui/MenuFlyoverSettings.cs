using System;
using Godot;

namespace RingworldTechDemo.UI;

[GlobalClass]
public partial class MenuFlyoverSettings : Resource
{
    [Export] public float AltitudeKm { get; set; } = 20f;
    [Export] public float PitchDegrees { get; set; } = 25f;
    [Export] public float WorldsAltitudeScale { get; set; } = 1.6f;
    [Export] public float WorldsPitchDegrees { get; set; } = 12f;
    [Export] public float DetailFovDeltaDegrees { get; set; } = -8f;
    [Export] public float PoseBlendSeconds { get; set; } = 1.6f;
    [Export] public float SpeedKmPerSecond { get; set; } = 5f;
    [Export] public float FieldOfView { get; set; } = 58f;
    [Export] public bool Paused { get; set; }
    [Export] public float SunEnergy { get; set; } = 2.15f;
    [Export] public float SunElevationDegrees { get; set; } = 49.51f;
    [Export] public float SunAzimuthDegrees { get; set; } = -141.34f;
    [Export] public float SunDriftAmplitudeDegrees { get; set; } = 12f;
    [Export] public float SunDriftPeriodMinutes { get; set; } = 12f;
    [Export] public float MotionEaseInSeconds { get; set; } = 2.5f;
    [Export] public float AmbientEnergy { get; set; } = .35f;
    [Export] public float HazeAmount { get; set; } = .12f;
    [Export] public float HazeStartKm { get; set; } = 250f;
    [Export] public float HazeEndKm { get; set; } = 1500f;
    [Export] public bool GlowEnabled { get; set; } = true;
    [Export] public float GlowIntensity { get; set; } = .45f;
    [Export] public bool CloudsEnabled { get; set; } = true;
    [Export] public float CloudCoverage { get; set; } = .74f;
    [Export] public float CloudDensity { get; set; } = 1.3f;
    [Export] public float CloudFloorKm { get; set; } = 2.2f;
    [Export] public float CloudCeilingKm { get; set; } = 6f;
    [Export] public float CloudDriftMps { get; set; } = 30f;
    [Export] public float CloudDitherSpeed { get; set; } = 0f;
    [Export] public float CloudLightEnergy { get; set; } = .45f;
    [Export] public float CloudAmbientStrength { get; set; } = 1f;
    [Export] public float CloudAmbientDay { get; set; } = 1f;
    [Export] public float CloudAmbientNight { get; set; } = 1f;
    [Export] public float CloudSharpness { get; set; } = .62f;
    [Export] public int CloudResolution { get; set; } = 1;
    [Export] public int CloudViewSteps { get; set; } = 128;
    [Export] public int CloudLightingSteps { get; set; } = 5;
    [Export] public float CloudMinStepMeters { get; set; } = 90f;
    [Export] public float CloudMaxStepMeters { get; set; } = 1600f;
    [Export] public float CloudLightingTravelKm { get; set; } = 7f;
    [Export] public float CloudLodBias { get; set; } = .9f;
    [Export] public float CloudBlurPower { get; set; } = .1f;
    [Export] public int CloudBlurQuality { get; set; } = 1;
    [Export] public bool CloudFastReconstruction { get; set; } = true;
    [Export] public float CloudRayStartJitterStrength { get; set; } = 0f;
    [Export] public float CloudStationaryHistoryWeight { get; set; } = .10f;
    [Export] public float CloudMovingHistoryWeight { get; set; } = 0f;
    [Export] public bool CloudShadowsEnabled { get; set; } = true;
    [Export] public float ShadowStrength { get; set; } = .65f;
    [Export] public float ShadowExtinctionPerKm { get; set; } = 1.35f;
    [Export] public float ShadowRadiusKm { get; set; } = 128f;
    [Export] public bool ShadowGenerationEnabled { get; set; } = true;
    [Export] public bool ShadowReceiversEnabled { get; set; } = true;
    [Export] public int ShadowResolution { get; set; } = 512;
    [Export] public float ShadowUpdatesPerSecond { get; set; } = 5f;
    [Export] public int ShadowVerticalSamples { get; set; } = 5;
    [Export] public int TerrainSpacingMeters { get; set; } = 125;
    [Export] public int FarTerrainDetail { get; set; } = 2;

    public void Sanitize()
    {
        AltitudeKm = Clamp(AltitudeKm, 8, 80);
        PitchDegrees = Clamp(PitchDegrees, 5, 65);
        WorldsAltitudeScale = Clamp(WorldsAltitudeScale, 1, 3);
        WorldsPitchDegrees = Clamp(WorldsPitchDegrees, 5, 65);
        DetailFovDeltaDegrees = Clamp(DetailFovDeltaDegrees, -25, 0);
        PoseBlendSeconds = Clamp(PoseBlendSeconds, 0, 6);
        SpeedKmPerSecond = Clamp(SpeedKmPerSecond, 0, 15);
        FieldOfView = Clamp(FieldOfView, 35, 95);
        SunEnergy = Clamp(SunEnergy, 0, 5);
        SunElevationDegrees = Clamp(SunElevationDegrees, -90, 90);
        SunAzimuthDegrees = Clamp(SunAzimuthDegrees, -180, 180);
        SunDriftAmplitudeDegrees = Clamp(SunDriftAmplitudeDegrees, 0, 60);
        SunDriftPeriodMinutes = Clamp(SunDriftPeriodMinutes, 1, 120);
        MotionEaseInSeconds = Clamp(MotionEaseInSeconds, 0, 10);
        AmbientEnergy = Clamp(AmbientEnergy, 0, 2);
        HazeAmount = Clamp(HazeAmount, 0, .4f);
        HazeStartKm = Clamp(HazeStartKm, 20, 2000);
        HazeEndKm = Math.Max(HazeStartKm + 20, Clamp(HazeEndKm, 40, 4000));
        GlowIntensity = Clamp(GlowIntensity, 0, 1.5f);
        CloudCoverage = Clamp(CloudCoverage, 0, 1);
        CloudDensity = Clamp(CloudDensity, 0, 3);
        CloudFloorKm = Clamp(CloudFloorKm, 0, 19);
        CloudCeilingKm = Math.Max(CloudFloorKm + .05f, Clamp(CloudCeilingKm, .5f, 20));
        CloudDriftMps = Clamp(CloudDriftMps, 0, 100);
        CloudDitherSpeed = Clamp(CloudDitherSpeed, 0, 120);
        CloudLightEnergy = Clamp(CloudLightEnergy, 0, 2);
        CloudAmbientStrength = Clamp(CloudAmbientStrength, 0, 2);
        CloudAmbientDay = Clamp(CloudAmbientDay, 0, 1);
        CloudAmbientNight = Clamp(CloudAmbientNight, 0, 1);
        CloudSharpness = Clamp(CloudSharpness, 0, 1);
        CloudResolution = Math.Clamp(CloudResolution, 0, 2);
        CloudViewSteps = (int)Math.Round(Math.Clamp(CloudViewSteps, 64, 256) / 8.0) * 8;
        CloudLightingSteps = Math.Clamp(CloudLightingSteps, 1, 8);
        CloudMinStepMeters = Clamp(CloudMinStepMeters, 10, 500);
        // Keep a non-degenerate adaptive step range, including the depth fade.
        CloudMaxStepMeters = Math.Max(CloudMinStepMeters + 10, Clamp(CloudMaxStepMeters, 100, 4000));
        CloudLightingTravelKm = Clamp(CloudLightingTravelKm, .5f, 20);
        CloudLodBias = Clamp(CloudLodBias, .1f, 2);
        CloudBlurPower = Clamp(CloudBlurPower, 0, 6);
        CloudBlurQuality = Math.Clamp(CloudBlurQuality, 1, 4);
        CloudRayStartJitterStrength = Clamp(CloudRayStartJitterStrength, 0, 1);
        CloudStationaryHistoryWeight = Clamp(CloudStationaryHistoryWeight, 0, .95f);
        CloudMovingHistoryWeight = Clamp(CloudMovingHistoryWeight, 0, CloudStationaryHistoryWeight);
        ShadowStrength = Clamp(ShadowStrength, 0, 1);
        ShadowExtinctionPerKm = Clamp(ShadowExtinctionPerKm, 0, 10);
        ShadowRadiusKm = Clamp(ShadowRadiusKm, 8, 200);
        ShadowResolution = ShadowResolution <= 128 ? 128 : ShadowResolution <= 256 ? 256 : ShadowResolution <= 512 ? 512 : ShadowResolution <= 1024 ? 1024 : 2048;
        ShadowUpdatesPerSecond = Clamp(ShadowUpdatesPerSecond, .25f, 20);
        ShadowVerticalSamples = Math.Clamp(ShadowVerticalSamples, 1, 64);
        TerrainSpacingMeters = TerrainSpacingMeters <= 125 ? 125 : TerrainSpacingMeters <= 250 ? 250 : 500;
        FarTerrainDetail = Math.Clamp(FarTerrainDetail, 0, 2);
    }

    public MenuFlyoverSettings CopyValidated()
    {
        var copy = (MenuFlyoverSettings)Duplicate(true);
        copy.Sanitize();
        return copy;
    }
    private static float Clamp(float value, float min, float max) => float.IsFinite(value) ? Mathf.Clamp(value, min, max) : min;
}
