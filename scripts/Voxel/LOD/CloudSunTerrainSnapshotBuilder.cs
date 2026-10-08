using System;
using System.Collections.Generic;
namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Fallback whole-ring sampled LOD for worlds without a legacy HLOD pyramid.</summary>
public static class CloudSunTerrainSnapshotBuilder
{
    public static CloudSunTerrainSnapshot Build(double circumference,double width,ulong editVersion,
        Func<double,double,float> sample,int maximumAroundCells=4096,int maximumAcrossCells=128)
    {
        if(!double.IsFinite(circumference)||!double.IsFinite(width)||circumference<=0||width<=0)throw new ArgumentOutOfRangeException(nameof(circumference));
        double size=Math.Max(32,Math.Max(circumference/maximumAroundCells,width/maximumAcrossCells));
        int nx=Math.Max(1,(int)Math.Ceiling(circumference/size)),ny=Math.Max(1,(int)Math.Ceiling(width/size));
        var ranges=new float[nx*ny*2];
        for(int y=0;y<ny;y++)for(int x=0;x<nx;x++){
            double a0=x*size,a1=Math.Min(circumference-.001,(x+1)*size),z0=y*size,z1=Math.Min(width-.001,(y+1)*size);
            float h00=sample(a0,z0),h10=sample(a1,z0),h01=sample(a0,z1),h11=sample(a1,z1),hc=sample((a0+a1)*.5,(z0+z1)*.5);
            if(!float.IsFinite(h00)||!float.IsFinite(h10)||!float.IsFinite(h01)||!float.IsFinite(h11)||!float.IsFinite(hc))throw new InvalidOperationException("Nonfinite terrain sample");
            float minimum=Math.Min(Math.Min(h00,h10),Math.Min(Math.Min(h01,h11),hc));
            float maximum=Math.Max(Math.Max(h00,h10),Math.Max(Math.Max(h01,h11),hc));
            int index=(y*nx+x)*2;ranges[index]=minimum;ranges[index+1]=maximum;
        }
        var levels=new List<CloudSunHeightLevel>();
        while(true){
            levels.Add(new(nx,ny,(float)size,ranges));if(nx==1&&ny==1)break;
            int px=(nx+1)/2,py=(ny+1)/2;var parents=new float[px*py*2];
            for(int y=0;y<py;y++)for(int x=0;x<px;x++){
                float minimum=float.PositiveInfinity,maximum=float.NegativeInfinity;
                for(int oy=0;oy<2;oy++)for(int ox=0;ox<2;ox++){
                    int child=(Math.Min(y*2+oy,ny-1)*nx+Math.Min(x*2+ox,nx-1))*2;
                    minimum=Math.Min(minimum,ranges[child]);maximum=Math.Max(maximum,ranges[child+1]);
                }
                int index=(y*px+x)*2;parents[index]=minimum;parents[index+1]=maximum;
            }
            ranges=parents;nx=px;ny=py;size*=2;
        }
        return new(circumference,width,editVersion,levels.AsReadOnly(),ranges[0],ranges[1]);
    }
}
