using System;
using Godot;

namespace RingworldTechDemo.Terrain.Generation;

/// <summary>Managed authority for the normalized terrain recipe. Instances own their native noise resources.</summary>
public sealed class TerrainGenerationHeightModel : IDisposable
{
    public readonly record struct Diagnostics(double Height, double MacroHeight, double Continentalness, double SeaWeight, double LowlandWeight, double AlpineWeight, double DetailMask, double ValleyWeight, double RidgeWeight, double MountainUplift, double TrenchDepth, double DetailHeight, bool Clamped);
    private static readonly double[] ContinentalKnots = { -1d, -.55d, -.20d, -.06d, 0d, .45d, 1d };
    private readonly TerrainGenerationParameters _p;
    private readonly double[] _continentalHeights;
    private readonly FastNoiseLite _continental, _mountainBelt, _valley, _trench, _ridge, _secondary, _micro, _warpX, _warpZ;
    private bool _disposed;
    public TerrainGenerationParameters Parameters => _p;

    public TerrainGenerationHeightModel(TerrainGenerationParameters parameters)
    {
        _p=parameters ?? throw new ArgumentNullException(nameof(parameters));
        _continentalHeights = new[] { -_p.BasinDepthM, -.8d*_p.BasinDepthM, -1000d, -100d, 0d, 1500d, 2000d };
        _continental=Noise(101,_p.ContinentalScaleM,_p.ContinentalOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _mountainBelt=Noise(211,_p.MountainScaleM,_p.MountainOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _valley=Noise(307,Math.Max(16,_p.MountainScaleM*.45),_p.ValleyOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _trench=Noise(401,Math.Max(16,_p.ContinentalScaleM*.35),_p.TrenchOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _ridge=Noise(503,_p.RidgeScaleM,_p.RidgeOctaves,FastNoiseLite.FractalTypeEnum.Ridged);
        _secondary=Noise(601,_p.SecondaryScaleM,_p.SecondaryOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _micro=Noise(701,_p.MicroScaleM,_p.MicroOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _warpX=Noise(809,_p.WarpScaleM,_p.WarpOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
        _warpZ=Noise(907,_p.WarpScaleM,_p.WarpOctaves,FastNoiseLite.FractalTypeEnum.Fbm);
    }
    public double CalculateHeight(double around, double across) => SampleDiagnostics(around,across).Height;
    public float[] SampleGrid(double around,double across,int columns,int rows,double spacing)
    { if(columns<0||rows<0||!double.IsFinite(spacing)||spacing<=0) throw new ArgumentOutOfRangeException(nameof(spacing)); checked { var output=new float[columns*rows]; for(int row=0;row<rows;row++) for(int col=0;col<columns;col++) output[row*columns+col]=(float)CalculateHeight(around+col*spacing,across+row*spacing); return output; } }
    public double ContinentalHeight(double continentalness)
    {
        double c=Clamp(continentalness,-1,1);
        for(int i=0;i<ContinentalKnots.Length-1;i++) if(c<=ContinentalKnots[i+1]) { double u=(c-ContinentalKnots[i])/(ContinentalKnots[i+1]-ContinentalKnots[i]); double eased=SmoothStep(0,1,u); if(_continentalHeights[i]>=0) return Lerp(_continentalHeights[i],_continentalHeights[i+1],FlattenUnit(eased,_p.Flattening)); return Lerp(_continentalHeights[i],_continentalHeights[i+1],eased); }
        return 2000;
    }
    /// <summary>v2 coastal ocean depth for an estimated physical shore distance.</summary>
    public double OceanDepthForDistance(double distance)
    {
        double d=double.IsFinite(distance) ? Math.Max(0,distance) : 0d, shelf=Math.Max(1,_p.ShelfWidthM), depth=_p.ShelfDepthM;
        double shore=Math.Min(shelf*.25,1.5*8d/_p.BeachSlope);
        if(d<=shore) return Hermite(d,0,shore,0,8,_p.BeachSlope,Math.Min(.002,3*8/shore));
        if(d<=shelf) return Hermite(d,shore,shelf,8,depth,Math.Min(.002,3*8/shore),0);
        if(d<=shelf+_p.ContinentalSlopeWidthM) return Lerp(depth,_p.BasinDepthM,SmoothStep(shelf,shelf+_p.ContinentalSlopeWidthM,d));
        return _p.BasinDepthM;
    }
    public Diagnostics SampleDiagnostics(double around,double across)
    {
        ThrowIfDisposed(); if(!double.IsFinite(around)||!double.IsFinite(across)) throw new ArgumentException("Coordinates must be finite.");
        double warpedAround=Wrap(around+Sample(_warpX,around,across)*_p.WarpAmplitudeM), warpedAcross=across+Sample(_warpZ,around,across)*_p.WarpAmplitudeM;
        double continentalRaw=Sample(_continental,warpedAround,warpedAcross);
        double continental=Clamp(continentalRaw+_p.LandBalance,-1,1), macro=ContinentalHeight(continental);
        if (_p.GeneratorId == TerrainGenerationParameters.GeneratorIdentity && continental < 0)
        {
            // Symmetric, landform-scale derivative.  This deliberately filters
            // local float/noise variation before converting continentalness to
            // a physical shore-distance approximation.
            double epsilon=Math.Clamp(_p.ContinentalScaleM*.02,128,16384);
            double da=(Sample(_continental,warpedAround+epsilon,warpedAcross)-Sample(_continental,warpedAround-epsilon,warpedAcross))/(2*epsilon);
            double dz=(Sample(_continental,warpedAround,warpedAcross+epsilon)-Sample(_continental,warpedAround,warpedAcross-epsilon))/(2*epsilon);
            double gradient=Math.Max(Math.Sqrt(da*da+dz*dz),1d/(32d*_p.ContinentalScaleM));
            macro=-OceanDepthForDistance(-continental/gradient);
        }
        double land=SmoothStep(0,.12,continental), belt=SmoothStep(.4,.8,Unit(Sample(_mountainBelt,warpedAround,warpedAcross))), alpine=land*belt, sea=1-land, lowland=land*(1-belt), valley=Valley(Sample(_valley,warpedAround,warpedAcross)), ridge=Math.Pow(Unit(Sample(_ridge,warpedAround,warpedAcross)),_p.RidgeSharpness);
        double uplift=_p.MountainAmplitudeM*land*belt*(.30+.70*ridge)*(1-.70*valley), trenchWeight=SmoothStep(_p.TrenchThreshold,1,Unit(Sample(_trench,warpedAround,warpedAcross))), deepOcean=1-SmoothStep(-3000,-1000,macro), trench=_p.TrenchDepthM*trenchWeight*deepOcean;
        macro+=uplift-trench; double detailMask=DetailMaskFor(macro,alpine,ridge,valley,_p.DetailStartM,_p.DetailFullM), detail=detailMask>0 ? detailMask*(_p.SecondaryAmplitudeM*Sample(_secondary,warpedAround,warpedAcross)+_p.MicroAmplitudeM*Sample(_micro,warpedAround,warpedAcross)) : 0; double raw=macro+detail, height=Clamp(raw,_p.MinHeightM,_p.MaxHeightM);
        return new(height,macro,continental,sea,lowland,alpine,detailMask,valley,ridge,uplift,trench,detail,raw!=height);
    }
    public void Dispose() { if(_disposed)return; _disposed=true; _continental.Dispose();_mountainBelt.Dispose();_valley.Dispose();_trench.Dispose();_ridge.Dispose();_secondary.Dispose();_micro.Dispose();_warpX.Dispose();_warpZ.Dispose(); GC.SuppressFinalize(this); }
    private FastNoiseLite Noise(int offset,double scale,int octaves,FastNoiseLite.FractalTypeEnum fractal) => new() { Seed=unchecked((int)unchecked(_p.Seed+(long)offset)), NoiseType=FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency=(float)(1d/scale), FractalType=fractal, FractalOctaves=octaves, FractalGain=(float)_p.NoiseGain, FractalLacunarity=(float)_p.NoiseLacunarity };
    private double Sample(FastNoiseLite noise,double around,double across) { double theta=Wrap(around)/_p.CircumferenceM*Math.Tau, radius=_p.CircumferenceM/Math.Tau; return noise.GetNoise3D((float)(Math.Cos(theta)*radius),(float)(Math.Sin(theta)*radius),(float)across); }
    private double Wrap(double v) { double r=v%_p.CircumferenceM; return r<0?r+_p.CircumferenceM:r; }
    private static double Unit(double v)=>Clamp(v*.5+.5,0,1); private double Valley(double value) { double d=Math.Abs(value); return 1-SmoothStep(_p.ValleyWidth,Math.Min(1,_p.ValleyWidth*2),d); }
    public static double FlattenUnit(double unit,double flattening) { unit=Clamp(unit,0,1); return Math.Abs(flattening)<=1e-5d ? unit : (Math.Exp(flattening*unit)-1d)/(Math.Exp(flattening)-1d); }
    public static double DetailMaskFor(double macro,double alpine,double crest,double valley,double start,double full) { if(alpine<=0||crest<=0||valley>=1)return 0; return Clamp(alpine,0,1)*SmoothStep(start,Math.Max(start,full),macro)*Clamp(crest,0,1)*(1-Clamp(valley,0,1)); }
    private static double Clamp(double v,double lo,double hi)=>Math.Min(Math.Max(v,lo),hi); private static double SmoothStep(double a,double b,double x) { if(a==b)return x<a?0:1; double t=Clamp((x-a)/(b-a),0,1); return t*t*(3-2*t); } private static double Lerp(double a,double b,double t)=>a+(b-a)*t;
    private static double Hermite(double x,double x0,double x1,double y0,double y1,double m0,double m1) { double length=Math.Max(1e-9,x1-x0), t=Clamp((x-x0)/length,0,1), secant=(y1-y0)/length; m0=Clamp(m0,0,3*secant); m1=Clamp(m1,0,3*secant); double t2=t*t,t3=t2*t; return (2*t3-3*t2+1)*y0+(t3-2*t2+t)*length*m0+(-2*t3+3*t2)*y1+(t3-t2)*length*m1; }
    private void ThrowIfDisposed() { if(_disposed) throw new ObjectDisposedException(nameof(TerrainGenerationHeightModel)); }
}
