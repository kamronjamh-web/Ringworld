using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Terrain.Editable;

public readonly record struct SmoothConstructionChunk(long AroundChunk, int VerticalChunk, int AcrossChunk);
public sealed class SmoothConstructionMeshData { public Vector3[] Vertices=Array.Empty<Vector3>(); public Vector3[] Normals=Array.Empty<Vector3>(); public int[] Indices=Array.Empty<int>(); public int[] Materials=Array.Empty<int>(); public Vector3[] CollisionFaces=Array.Empty<Vector3>(); public ulong Hash; }

public static class SmoothConstructionMesher
{
    public const int ChunkSize = 32;
    private static readonly (Vector3I N, Vector3[] P)[] Faces =
    {
        (Vector3I.Right,new[]{new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(1,1,1),new Vector3(1,0,1)}),
        (Vector3I.Left,new[]{new Vector3(0,0,1),new Vector3(0,1,1),new Vector3(0,1,0),new Vector3(0,0,0)}),
        (Vector3I.Up,new[]{new Vector3(0,1,1),new Vector3(1,1,1),new Vector3(1,1,0),new Vector3(0,1,0)}),
        (Vector3I.Down,new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,0,1),new Vector3(0,0,1)}),
        (Vector3I.Back,new[]{new Vector3(1,0,1),new Vector3(1,1,1),new Vector3(0,1,1),new Vector3(0,0,1)}),
        (Vector3I.Forward,new[]{new Vector3(0,0,0),new Vector3(0,1,0),new Vector3(1,1,0),new Vector3(1,0,0)})
    };
    /// <summary>Builds chunk-relative vertices. Greedy merging is intentionally deferred; face culling is exact.</summary>
    public static SmoothConstructionMeshData Build(SmoothConstructionChunk chunk, IReadOnlyDictionary<SmoothConstructionBlock,SmoothConstructionMaterial> blocks, long aroundLength, RingLodGeometrySnapshot geometry, Transform3D ownerInverse)
    {
        var v=new List<Vector3>();var n=new List<Vector3>();var i=new List<int>();var m=new List<int>();var faces=new List<Vector3>();
        long ax=chunk.AroundChunk*ChunkSize; int vy=chunk.VerticalChunk*ChunkSize, az=chunk.AcrossChunk*ChunkSize;
        Vector3 localOrigin=ownerInverse*geometry.CanonicalToWorld(ax,vy,az);
        foreach(var pair in blocks){var b=pair.Key;if(b.Around<ax||b.Around>=ax+32||b.Vertical<vy||b.Vertical>=vy+32||b.Across<az||b.Across>=az+32)continue;
            foreach(var f in Faces){var neighbor=new SmoothConstructionBlock(RingCoordMath.WrapAround(b.Around+f.N.X,aroundLength),b.Vertical+f.N.Y,b.Across+f.N.Z);if(blocks.ContainsKey(neighbor))continue;int start=v.Count;
                for(int q=0;q<4;q++){var p=f.P[q];var world=geometry.CanonicalToWorld(b.Around+p.X,b.Vertical+p.Y,b.Across+p.Z);v.Add(ownerInverse*world-localOrigin);}
                var w0=geometry.CanonicalToWorld(b.Around,b.Vertical,b.Across);var wn=geometry.CanonicalToWorld(b.Around+f.N.X,b.Vertical+f.N.Y,b.Across+f.N.Z);var normal=(ownerInverse.Basis.Inverse().Transposed()*(wn-w0)).Normalized();for(int q=0;q<4;q++){n.Add(normal);m.Add((int)pair.Value);} i.AddRange(new[]{start,start+1,start+2,start,start+2,start+3});faces.Add(v[start]);faces.Add(v[start+1]);faces.Add(v[start+2]);faces.Add(v[start]);faces.Add(v[start+2]);faces.Add(v[start+3]);
                // Godot's front faces are clockwise.  The authored quad order
                // has a mathematical normal pointing outwards, so reverse it
                // when it agrees with the outward normal.
                if((v[start+1]-v[start]).Cross(v[start+2]-v[start]).Dot(normal)>0){int z=i.Count;i[z-5]=start+2;i[z-4]=start+1;i[z-2]=start+3;i[z-1]=start+2;faces[^5]=v[start+2];faces[^4]=v[start+1];faces[^2]=v[start+3];faces[^1]=v[start+2];}
            }
        }
        var data=new SmoothConstructionMeshData{Vertices=v.ToArray(),Normals=n.ToArray(),Indices=i.ToArray(),Materials=m.ToArray(),CollisionFaces=faces.ToArray()}; ulong hash=1469598103934665603UL;void Mix(uint x){hash=(hash^x)*1099511628211UL;}foreach(var p in data.Vertices){Mix(BitConverter.SingleToUInt32Bits(p.X));Mix(BitConverter.SingleToUInt32Bits(p.Y));Mix(BitConverter.SingleToUInt32Bits(p.Z));}foreach(var q in data.Normals){Mix(BitConverter.SingleToUInt32Bits(q.X));Mix(BitConverter.SingleToUInt32Bits(q.Y));Mix(BitConverter.SingleToUInt32Bits(q.Z));}foreach(var x in data.Materials)Mix((uint)x);foreach(var x in data.Indices)Mix((uint)x);data.Hash=hash;return data;
    }
}
