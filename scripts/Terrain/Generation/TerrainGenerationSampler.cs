using System;
using Godot;
using GodotDictionary = Godot.Collections.Dictionary;

namespace RingworldTechDemo.Terrain.Generation;

/// <summary>Godot bridge around the single C# terrain-model authority.</summary>
public partial class TerrainGenerationSampler : RefCounted
{
    private TerrainGenerationHeightModel? _model;
    private bool _disposed;
    public string LastError { get; private set; } = string.Empty;
    public string CanonicalJson => _model?.Parameters.CanonicalJson ?? string.Empty;
    public string DefinitionHash => _model?.Parameters.DefinitionHash.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    public bool Configure(GodotDictionary snapshot)
    {
        if (_disposed) { LastError="Sampler is disposed."; return false; }
        try
        {
            var next=new TerrainGenerationHeightModel(TerrainGenerationParameters.FromDictionary(snapshot));
            TerrainGenerationHeightModel? previous=_model; _model=next; previous?.Dispose(); LastError=string.Empty; return true;
        }
        catch(Exception exception) { LastError=exception.Message; return false; }
    }
    public bool ConfigureJson(string json)
    {
        if (_disposed) { LastError="Sampler is disposed."; return false; }
        try
        {
            var next=new TerrainGenerationHeightModel(TerrainGenerationParameters.FromJson(json));
            TerrainGenerationHeightModel? previous=_model; _model=next; previous?.Dispose(); LastError=string.Empty; return true;
        }
        catch(Exception exception) { LastError=exception.Message; return false; }
    }
    public double CalculateHeight(double around,double across) => Require().CalculateHeight(around,across);
    public double ContinentalHeight(double continentalness) => Require().ContinentalHeight(continentalness);
    public double OceanDepthForDistance(double distanceMeters) => Require().OceanDepthForDistance(distanceMeters);
    public float[] SampleGrid(double originAround,double originAcross,int columns,int rows,double spacingMeters) => Require().SampleGrid(originAround,originAcross,columns,rows,spacingMeters);
    public static double FlattenUnit(double unit, double flattening) => TerrainGenerationHeightModel.FlattenUnit(unit, flattening);
    public static double DetailMaskFor(double macroHeight, double alpineWeight, double crestWeight, double valleyWeight, double startM, double fullM) => TerrainGenerationHeightModel.DetailMaskFor(macroHeight, alpineWeight, crestWeight, valleyWeight, startM, fullM);
    public GodotDictionary SampleDiagnostics(double around,double across)
    {
        TerrainGenerationHeightModel.Diagnostics d=Require().SampleDiagnostics(around,across);
        return new GodotDictionary { ["height"]=d.Height,["macro_height"]=d.MacroHeight,["continentalness"]=d.Continentalness,["sea_weight"]=d.SeaWeight,["lowland_weight"]=d.LowlandWeight,["alpine_weight"]=d.AlpineWeight,["detail_mask"]=d.DetailMask,["valley_weight"]=d.ValleyWeight,["ridge_weight"]=d.RidgeWeight,["mountain_uplift"]=d.MountainUplift,["trench_depth"]=d.TrenchDepth,["detail_height"]=d.DetailHeight,["foothill_height"]=d.FoothillHeight,["spur_height"]=d.SpurHeight,["erosion_height"]=d.ErosionHeight,["landform_height"]=d.LandformHeight,["lowland_erosion_height"]=d.LowlandErosionHeight,["ruggedness"]=d.Ruggedness,["plateau_weight"]=d.PlateauWeight,["landform_gate"]=d.LandformGate,["clamped"]=d.Clamped };
    }
    /// <summary>GDScript-callable lifecycle release for bridges held outside a Node tree.</summary>
    public void Release() => Dispose();
    protected override void Dispose(bool disposing) { if(!_disposed) { _disposed=true; _model?.Dispose(); _model=null; } base.Dispose(disposing); }
    private TerrainGenerationHeightModel Require() { if(_disposed) throw new ObjectDisposedException(nameof(TerrainGenerationSampler)); return _model ?? throw new InvalidOperationException("TerrainGenerationSampler is not configured: "+LastError); }
}
