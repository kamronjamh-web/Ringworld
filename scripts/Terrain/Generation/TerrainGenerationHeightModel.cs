using System;
using Godot;

namespace RingworldTechDemo.Terrain.Generation;

/// <summary>Managed authority for the normalized terrain recipe. Instances own their native noise resources.</summary>
/// <remarks>Each worker must own its model, or callers must serialize access to its mutable gully cache.</remarks>
public sealed class TerrainGenerationHeightModel : IDisposable
{
    public readonly record struct Diagnostics(double Height, double MacroHeight, double Continentalness, double SeaWeight, double LowlandWeight, double AlpineWeight, double DetailMask, double ValleyWeight, double RidgeWeight, double MountainUplift, double TrenchDepth, double DetailHeight, bool Clamped, double FoothillHeight=0d, double SpurHeight=0d, double ErosionHeight=0d);
    private static readonly double[] ContinentalKnots = { -1d, -.55d, -.20d, -.06d, 0d, .45d, 1d };
    private readonly TerrainGenerationParameters _p;
    private readonly double[] _continentalHeights;
    private readonly FastNoiseLite _continental, _mountainBelt, _valley, _trench, _ridge, _secondary, _micro, _warpX, _warpZ;
    // These resources exist only for the v3 recipe.  Earlier recipes retain their
    // original resource set as well as their original sampling and arithmetic order.
    private readonly FastNoiseLite? _foothill, _spur;
    private readonly FastNoiseLite[]? _swissSpurs;
    private readonly double _radius, _continentalDerivativeEpsilon, _gullyDerivativeEpsilon;
    private readonly double[] _swissScales, _swissWeights, _swissSteps,
        _swissDisplacementWeights;
    private readonly double[] _gullyScales, _gullyWeights, _gullyCellWidths;
    private readonly int[] _gullyCellCounts;
    // Models are worker-confined by RingTerrainSampler. The small mutable cache is
    // therefore safe and avoids synchronization or per-sample allocation.
    private readonly GullyStencil[] _gullyStencils;
    private readonly GullyCell[] _gullyCells;
    private bool _disposed;
    public TerrainGenerationParameters Parameters => _p;

    public TerrainGenerationHeightModel(TerrainGenerationParameters parameters)
    {
        _p=parameters ?? throw new ArgumentNullException(nameof(parameters));
        _radius=_p.CircumferenceM/Math.Tau;
        _continentalDerivativeEpsilon=Math.Clamp(_p.ContinentalScaleM*.02,128,16384);
        bool swissEnabled=_p.GeneratorId==TerrainGenerationParameters.DetailGeneratorIdentity
            && (_p.MountainDetailMode&1)!=0;
        bool gullyEnabled=_p.GeneratorId==TerrainGenerationParameters.DetailGeneratorIdentity
            && (_p.MountainDetailMode&2)!=0 && _p.ErosionStrengthM>0d;
        _swissScales=swissEnabled ? new double[4] : Array.Empty<double>();
        _swissWeights=swissEnabled ? new double[4] : Array.Empty<double>();
        _swissSteps=swissEnabled ? new double[4] : Array.Empty<double>();
        _swissDisplacementWeights=swissEnabled ? new double[4] : Array.Empty<double>();
        _gullyScales=gullyEnabled ? new double[_p.ErosionOctaves] : Array.Empty<double>();
        _gullyWeights=gullyEnabled ? new double[_p.ErosionOctaves] : Array.Empty<double>();
        _gullyCellWidths=gullyEnabled ? new double[_p.ErosionOctaves] : Array.Empty<double>();
        _gullyCellCounts=gullyEnabled ? new int[_p.ErosionOctaves] : Array.Empty<int>();
        _gullyStencils=gullyEnabled ? new GullyStencil[_p.ErosionOctaves] : Array.Empty<GullyStencil>();
        _gullyCells=gullyEnabled ? new GullyCell[_p.ErosionOctaves*9] : Array.Empty<GullyCell>();
        _gullyDerivativeEpsilon=gullyEnabled ? Math.Clamp(_p.ErosionScaleM*.125d,8d,64d) : 0d;
        if(swissEnabled) for(int i=0;i<4;i++)
        {
            _swissScales[i]=_p.SpurScaleM/Math.Pow(_p.NoiseLacunarity,i);
            _swissWeights[i]=Math.Pow(_p.NoiseGain,i);
            _swissSteps[i]=Math.Max(2d,_swissScales[i]*.01d);
            _swissDisplacementWeights[i]=_swissWeights[i]*_p.SwissWarp*.25d
                *_swissScales[i]*_swissScales[i];
        }
        if(gullyEnabled) for(int i=0;i<_p.ErosionOctaves;i++)
        {
            _gullyScales[i]=_p.ErosionScaleM/Math.Pow(_p.NoiseLacunarity,i);
            _gullyWeights[i]=Math.Pow(_p.NoiseGain,i);
            _gullyCellCounts[i]=Math.Max(1,(int)Math.Round(_p.CircumferenceM/_gullyScales[i]));
            _gullyCellWidths[i]=_p.CircumferenceM/_gullyCellCounts[i];
        }
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
        if (_p.GeneratorId==TerrainGenerationParameters.DetailGeneratorIdentity)
        {
            _foothill=Noise(1009,_p.FoothillScaleM,3,FastNoiseLite.FractalTypeEnum.Fbm);
            _spur=Noise(1103,_p.SpurScaleM,4,FastNoiseLite.FractalTypeEnum.Ridged);
            if ((_p.MountainDetailMode&1)!=0)
            {
                _swissSpurs=new FastNoiseLite[4];
                for(int i=0;i<_swissSpurs.Length;i++) _swissSpurs[i]=Noise(1103+i,_swissScales[i],1,FastNoiseLite.FractalTypeEnum.None);
            }
        }
    }
    public double CalculateHeight(double around, double across) => SampleDiagnostics(around,across).Height;
    public float[] SampleGrid(double around,double across,int columns,int rows,double spacing)
    { if(columns<0||rows<0||!double.IsFinite(spacing)||spacing<=0) throw new ArgumentOutOfRangeException(nameof(spacing)); checked { var output=new float[columns*rows]; for(int row=0;row<rows;row++) for(int col=0;col<columns;col++) output[row*columns+col]=(float)CalculateHeight(around+col*spacing,across+row*spacing); return output; } }
    public double ContinentalHeight(double continentalness)
    {
        double c=Clamp(continentalness,-1,1);
        for(int i=0;i<ContinentalKnots.Length-1;i++) if(c<=ContinentalKnots[i+1]) { double u=(c-ContinentalKnots[i])/(ContinentalKnots[i+1]-ContinentalKnots[i]); double eased=SmoothStep(0,1,u); if(_continentalHeights[i]>=0) return Lerp(_continentalHeights[i],_continentalHeights[i+1],FlattenContinental(eased)); return Lerp(_continentalHeights[i],_continentalHeights[i+1],eased); }
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
        CylindricalProjection unwarped=new(around,_p.CircumferenceM,_radius);
        double warpedAround=Wrap(around+Sample(_warpX,unwarped,across)*_p.WarpAmplitudeM), warpedAcross=across+Sample(_warpZ,unwarped,across)*_p.WarpAmplitudeM;
        CylindricalProjection warped=new(warpedAround,_p.CircumferenceM,_radius);
        double continentalRaw=Sample(_continental,warped,warpedAcross);
        double continental=Clamp(continentalRaw+_p.LandBalance,-1,1), macro=ContinentalHeight(continental);
        if ((_p.GeneratorId == TerrainGenerationParameters.GeneratorIdentity || _p.GeneratorId == TerrainGenerationParameters.DetailGeneratorIdentity) && continental < 0)
        {
            // Symmetric, landform-scale derivative.  This deliberately filters
            // local float/noise variation before converting continentalness to
            // a physical shore-distance approximation.
            double epsilon=_continentalDerivativeEpsilon;
            double da=(Sample(_continental,new CylindricalProjection(warpedAround+epsilon,_p.CircumferenceM,_radius),warpedAcross)
                -Sample(_continental,new CylindricalProjection(warpedAround-epsilon,_p.CircumferenceM,_radius),warpedAcross))/(2*epsilon);
            double dz=(Sample(_continental,warped,warpedAcross+epsilon)
                -Sample(_continental,warped,warpedAcross-epsilon))/(2*epsilon);
            double gradient=Math.Max(Math.Sqrt(da*da+dz*dz),1d/(32d*_p.ContinentalScaleM));
            macro=-OceanDepthForDistance(-continental/gradient);
        }
        double land=SmoothStep(0,.12,continental), belt=SmoothStep(.4,.8,Unit(Sample(_mountainBelt,warped,warpedAcross))), alpine=land*belt, sea=1-land, lowland=land*(1-belt), valley=Valley(Sample(_valley,warped,warpedAcross)), ridge=Math.Pow(Unit(Sample(_ridge,warped,warpedAcross)),_p.RidgeSharpness);
        double uplift=_p.MountainAmplitudeM*land*belt*(.30+.70*ridge)*(1-.70*valley), trenchWeight=SmoothStep(_p.TrenchThreshold,1,Unit(Sample(_trench,warped,warpedAcross))), deepOcean=1-SmoothStep(-3000,-1000,macro), trench=_p.TrenchDepthM*trenchWeight*deepOcean;
        macro+=uplift-trench;
        // Keep this expression byte-for-byte ordered for v1/v2 goldens.
        double detailMask=DetailMaskFor(macro,alpine,ridge,valley,_p.DetailStartM,_p.DetailFullM), detail=detailMask>0 ? detailMask*(_p.SecondaryAmplitudeM*Sample(_secondary,warped,warpedAcross)+_p.MicroAmplitudeM*Sample(_micro,warped,warpedAcross)) : 0;
        if (_p.GeneratorId!=TerrainGenerationParameters.DetailGeneratorIdentity)
        {
            double legacyRaw=macro+detail, legacyHeight=Clamp(legacyRaw,_p.MinHeightM,_p.MaxHeightM);
            return new(legacyHeight,macro,continental,sea,lowland,alpine,detailMask,valley,ridge,uplift,trench,detail,legacyRaw!=legacyHeight);
        }
        double coast=land*SmoothStep(50d,250d,macro), basin=1d-valley;
        double foothill=(_p.FoothillAmplitudeM>0d && coast>0d) ? _p.FoothillAmplitudeM*coast*basin*(1d-.65d*belt)*Unit(Sample(_foothill!,warped,warpedAcross)) : 0d;
        double spurMask=coast*basin*belt;
        double spur=(_p.SpurAmplitudeM>0d && spurMask>0d) ? _p.SpurAmplitudeM*spurMask*SpurField(warpedAround,warped,warpedAcross) : 0d;
        double erosion=(_p.ErosionStrengthM>0d && spurMask>0d && (_p.MountainDetailMode&2)!=0) ? DirectionalGullies(warpedAround,warped,warpedAcross,alpine,valley,spurMask,macro) : 0d;
        double totalDetail=detail+foothill+spur+erosion, raw=macro+totalDetail, height=Clamp(raw,_p.MinHeightM,_p.MaxHeightM);
        return new(height,macro,continental,sea,lowland,alpine,detailMask,valley,ridge,uplift,trench,totalDetail,raw!=height,foothill,spur,erosion);
    }
    private double SpurField(double around,CylindricalProjection projection,double across)
    {
        if ((_p.MountainDetailMode&1)==0) return Unit(Sample(_spur!,projection,across));
        return SwissSpurField(around,projection,across);
    }
    /// <summary>
    /// Swiss-inspired turbulence adaptation, not an exact paper port.  It uses
    /// numerical noise derivatives to warp the next octave lookup by a bounded,
    /// continuous ridge-following displacement; every octave remains canonical.
    /// </summary>
    private double SwissSpurField(double around,CylindricalProjection projection,double across)
    {
        FastNoiseLite[] noises=_swissSpurs!;
        double sum=0d, weights=0d, displacementA=0d, displacementZ=0d;
        double maxDisplacement=_p.SpurScaleM*_p.SwissWarp;
        for(int i=0;i<noises.Length;i++)
        {
            double weight=_swissWeights[i];
            CylindricalProjection center=i==0 ? projection : new CylindricalProjection(around+displacementA,_p.CircumferenceM,_radius);
            double sample=Sample(noises[i],center,across+displacementZ);
            sum+=weight*(1d-Math.Abs(sample)); weights+=weight;
            if (_p.SwissWarp<=0d || i==noises.Length-1) continue;
            double step=_swissSteps[i];
            double da=(Sample(noises[i],new CylindricalProjection(around+displacementA+step,_p.CircumferenceM,_radius),across+displacementZ)
                -Sample(noises[i],new CylindricalProjection(around+displacementA-step,_p.CircumferenceM,_radius),across+displacementZ))/(2d*step);
            double dz=(Sample(noises[i],center,across+displacementZ+step)
                -Sample(noises[i],center,across+displacementZ-step))/(2d*step);
            // A continuous derivative warp: unlike a normalized direction, it fades
            // naturally at flat points and follows the sampled ridge field.
            double displacementWeight=_swissDisplacementWeights[i];
            displacementA-=sample*da*displacementWeight;
            displacementZ-=sample*dz*displacementWeight;
            double length=Math.Sqrt(displacementA*displacementA+displacementZ*displacementZ);
            if(length>maxDisplacement && length>0d)
            {
                double cap=maxDisplacement/length;
                displacementA*=cap;
                displacementZ*=cap;
            }
        }
        return weights>0d ? Clamp(sum/weights,0d,1d) : 0d;
    }
    /// <summary>
    /// Bounded directional erosion filter. This is original procedural carving,
    /// not a physical river simulation or a Runevision implementation: its guide is
    /// the numerical gradient of the existing ridge field, not final-height slope.
    /// </summary>
    private double DirectionalGullies(double around,CylindricalProjection projection,double across,double alpine,double valley,double mask,double macro)
    {
        double epsilon=_gullyDerivativeEpsilon;
        double ridgeA=(Sample(_ridge,new CylindricalProjection(around+epsilon,_p.CircumferenceM,_radius),across)
            -Sample(_ridge,new CylindricalProjection(around-epsilon,_p.CircumferenceM,_radius),across))/(2d*epsilon);
        double ridgeZ=(Sample(_ridge,projection,across+epsilon)
            -Sample(_ridge,projection,across-epsilon))/(2d*epsilon);
        double approximateSlope=Math.Sqrt(ridgeA*ridgeA+ridgeZ*ridgeZ)*_p.MountainAmplitudeM*.7d*alpine*(1d-.7d*valley);
        double slopeMask=SmoothStep(.01d,.35d,approximateSlope);
        if(slopeMask<=0d) return 0d;
        double guideA=-ridgeA, guideZ=-ridgeZ, total=0d, weights=0d;
        for(int octave=0;octave<_p.ErosionOctaves;octave++)
        {
            double scale=_gullyScales[octave];
            double weight=_gullyWeights[octave];
            if (!CellGullyField(around,across,scale,octave,ref guideA,ref guideZ,out double field)) continue;
            total+=weight*(-.5d*(1d-field)); weights+=weight;
        }
        double amplitude=Math.Min(_p.ErosionStrengthM,Math.Max(0d,macro)*.5d);
        return weights>0d ? amplitude*mask*slopeMask*Clamp(total/weights,-1d,0d) : 0d;
    }
    // Wrapped integer around-cells and unwrapped local offsets make the pattern periodic at the ring seam.
    private bool CellGullyField(double around,double across,double scale,int octave,ref double guideA,ref double guideZ,out double field)
    {
        double length=Math.Sqrt(guideA*guideA+guideZ*guideZ);
        field=0d;
        if(length<=1e-10d) return false;
        guideA/=length; guideZ/=length;
        int count=_gullyCellCounts[octave];
        double cellA=_gullyCellWidths[octave];
        int baseA=(int)Math.Floor(Wrap(around)/cellA);
        double baseTurn=Math.Floor(around/_p.CircumferenceM), baseZ=Math.Floor(across/scale), sum=0d, weightSum=0d, gradientA=0d, gradientZ=0d;
        bool needsFeedback=octave+1<_p.ErosionOctaves;
        int baseZInt=(int)baseZ;
        RefillGullyStencil(octave,baseA,baseZInt,baseTurn,count,cellA,scale);
        double perpendicularA=-guideZ, perpendicularZ=guideA;
        for(int z=-1;z<=1;z++) for(int a=-1;a<=1;a++)
        {
            int cellIndex=octave*9+(z+1)*3+(a+1);
            double pivotA=_gullyCells[cellIndex].PivotA, pivotZ=_gullyCells[cellIndex].PivotZ;
            double offsetA=around-pivotA, offsetZ=across-pivotZ, normalizedA=offsetA/cellA, normalizedZ=offsetZ/scale, distanceSquared=normalizedA*normalizedA+normalizedZ*normalizedZ;
            if(distanceSquared>=1d) continue;
            double localWeight=Math.Pow(1d-distanceSquared,3d), phase=Math.Tau*(perpendicularA*offsetA+perpendicularZ*offsetZ)/scale, value=Math.Cos(phase);
            sum+=localWeight*value; weightSum+=localWeight;
            if(needsFeedback)
            {
                double derivative=-Math.Sin(phase)*Math.Tau/scale;
                gradientA+=localWeight*derivative*perpendicularA;
                gradientZ+=localWeight*derivative*perpendicularZ;
            }
        }
        if(weightSum<=1e-12d) return false;
        field=Clamp(sum/weightSum,-1d,1d);
        // The final octave has no consumer for the guide, so its phase gradient
        // and feedback are dead work. Earlier octaves retain the exact feedback.
        if(needsFeedback)
        {
            guideA-=gradientA/weightSum*scale*.15d;
            guideZ-=gradientZ/weightSum*scale*.15d;
        }
        return true;
    }
    private void RefillGullyStencil(int octave,int baseA,int baseZ,double baseTurn,int count,double cellA,double scale)
    {
        ref GullyStencil stencil=ref _gullyStencils[octave];
        if(stencil.Valid && stencil.BaseA==baseA && stencil.BaseZ==baseZ && stencil.BaseTurn==baseTurn) return;
        stencil.Valid=true;
        stencil.BaseA=baseA;
        stencil.BaseZ=baseZ;
        stencil.BaseTurn=baseTurn;
        for(int z=-1;z<=1;z++) for(int a=-1;a<=1;a++)
        {
            int rawA=baseA+a, wrappedA=((rawA%count)+count)%count, cellZ=baseZ+z;
            double pivotA=(baseTurn*count+rawA+CellJitter(wrappedA,cellZ,octave,0))*cellA;
            double pivotZ=(cellZ+CellJitter(wrappedA,cellZ,octave,1))*scale;
            _gullyCells[octave*9+(z+1)*3+(a+1)]=new(pivotA,pivotZ);
        }
    }
    private double CellJitter(int a,int z,int octave,int axis) { unchecked { uint h=((uint)a*0x1f123bb5u)^((uint)z*0x6d2b79f5u)^((uint)octave*0x9e3779b9u)^((uint)axis*0x85ebca6bu)^((uint)_p.Seed)^((uint)(_p.Seed>>32)); h^=h>>16; h*=0x7feb352du; h^=h>>15; h*=0x846ca68bu; h^=h>>16; return .3d+.4d*(h/(double)uint.MaxValue); } }
    public void Dispose() { if(_disposed)return; _disposed=true; _continental.Dispose();_mountainBelt.Dispose();_valley.Dispose();_trench.Dispose();_ridge.Dispose();_secondary.Dispose();_micro.Dispose();_warpX.Dispose();_warpZ.Dispose(); _foothill?.Dispose(); _spur?.Dispose(); if(_swissSpurs is not null) foreach(FastNoiseLite noise in _swissSpurs) noise.Dispose(); GC.SuppressFinalize(this); }
    private FastNoiseLite Noise(int offset,double scale,int octaves,FastNoiseLite.FractalTypeEnum fractal) => new() { Seed=unchecked((int)unchecked(_p.Seed+(long)offset)), NoiseType=FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency=(float)(1d/scale), FractalType=fractal, FractalOctaves=octaves, FractalGain=(float)_p.NoiseGain, FractalLacunarity=(float)_p.NoiseLacunarity };
    private double Sample(FastNoiseLite noise,CylindricalProjection projection,double across) => noise.GetNoise3D(projection.X,projection.Y,(float)across);
    private double Wrap(double v) { double r=v%_p.CircumferenceM; return r<0?r+_p.CircumferenceM:r; }
    private static double Unit(double v)=>Clamp(v*.5+.5,0,1); private double Valley(double value) { double d=Math.Abs(value); return 1-SmoothStep(_p.ValleyWidth,Math.Min(1,_p.ValleyWidth*2),d); }
    private double FlattenContinental(double unit) { unit=Clamp(unit,0,1); return Math.Abs(_p.Flattening)<=1e-5d ? unit : (Math.Exp(_p.Flattening*unit)-1d)/_p.FlattenDenominator; }
    public static double FlattenUnit(double unit,double flattening) { unit=Clamp(unit,0,1); return Math.Abs(flattening)<=1e-5d ? unit : (Math.Exp(flattening*unit)-1d)/(Math.Exp(flattening)-1d); }
    public static double DetailMaskFor(double macro,double alpine,double crest,double valley,double start,double full) { if(alpine<=0||crest<=0||valley>=1)return 0; return Clamp(alpine,0,1)*SmoothStep(start,Math.Max(start,full),macro)*Clamp(crest,0,1)*(1-Clamp(valley,0,1)); }
    private static double Clamp(double v,double lo,double hi)=>Math.Min(Math.Max(v,lo),hi); private static double SmoothStep(double a,double b,double x) { if(a==b)return x<a?0:1; double t=Clamp((x-a)/(b-a),0,1); return t*t*(3-2*t); } private static double Lerp(double a,double b,double t)=>a+(b-a)*t;
    private static double Hermite(double x,double x0,double x1,double y0,double y1,double m0,double m1) { double length=Math.Max(1e-9,x1-x0), t=Clamp((x-x0)/length,0,1), secant=(y1-y0)/length; m0=Clamp(m0,0,3*secant); m1=Clamp(m1,0,3*secant); double t2=t*t,t3=t2*t; return (2*t3-3*t2+1)*y0+(t3-2*t2+t)*length*m0+(-2*t3+3*t2)*y1+(t3-t2)*length*m1; }
    private void ThrowIfDisposed() { if(_disposed) throw new ObjectDisposedException(nameof(TerrainGenerationHeightModel)); }
    private readonly struct CylindricalProjection
    {
        public readonly float X, Y;
        public CylindricalProjection(double around,double circumference,double radius)
        {
            double wrapped=around%circumference;
            if(wrapped<0) wrapped+=circumference;
            double theta=wrapped/circumference*Math.Tau;
            X=(float)(Math.Cos(theta)*radius);
            Y=(float)(Math.Sin(theta)*radius);
        }
    }
    private struct GullyStencil { public bool Valid; public int BaseA, BaseZ; public double BaseTurn; }
    private readonly struct GullyCell { public readonly double PivotA, PivotZ; public GullyCell(double pivotA,double pivotZ) { PivotA=pivotA; PivotZ=pivotZ; } }
}
