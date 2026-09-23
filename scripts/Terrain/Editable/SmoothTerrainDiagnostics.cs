using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>
/// Deterministic, in-process contracts for the editable smooth-terrain
/// foundation. This class is deliberately isolated from scene startup; callers
/// can invoke <see cref="RunSelfTest"/> from an editor tool or test host.
/// </summary>
public static class SmoothTerrainDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            var baseline = new AnalyticBaseline(128);
            var emptyEdits = new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), revision: 1, aroundLength: baseline.AroundLength);

            if (!TestRingWrapAndNegativeCoordinates(baseline, emptyEdits, out summary)
                || !TestSharedChunkBoundary(baseline, emptyEdits, out summary)
                || !TestDeterministicEditOrdering(baseline, out summary)
                || !TestIncrementalAppendMatchesFullCapture(baseline, out summary)
                || !TestRevisionInvalidation(baseline, out summary)
                || !TestVolumetricDensityLodContract(baseline, out summary)
                || !TestVolumetricDensityRangeRejection(out summary)
                || !TestCertifiedScaleTransformPrecision(out summary)
                || !TestReferenceMesher(baseline, emptyEdits, out summary)
                || !TestHalfOpenSeamOwnership(baseline, emptyEdits, out summary)
                || !TestCertifiedSeams(out summary)
                || !TestAllRegularCellCases(out summary))
                return false;

            summary = "wrap, negative coordinates, shared samples, edit ordering, incremental append equivalence, revisions, immutable cross-LOD volumetric-density identity/sampling and uniform-range rejection, certified-scale transform precision, all 256 regular-cell cases, reference meshes, and X/Y/Z/wrap baseline+edited seam certification passed";
            return true;
        }
        catch (Exception exception)
        {
            summary = $"unexpected {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TestRingWrapAndNegativeCoordinates(AnalyticBaseline baseline, SmoothTerrainEditSnapshot edits, out string summary)
    {
        SmoothTerrainChunkSnapshot negative = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(-1, -1, -1), baseline, edits);
        SmoothTerrainChunkSnapshot wrapped = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(3, -1, -1), baseline, edits);
        if (!SnapshotsMatch(negative, wrapped))
        {
            summary = "negative around chunk did not produce the canonical wrapped snapshot";
            return false;
        }

        if (negative.Density(0, 0, 0) != baseline.Sample(96, -32, -32).Density)
        {
            summary = "negative vertical/across coordinates sampled an incorrect canonical position";
            return false;
        }
        summary = string.Empty;
        return true;
    }

    private static bool TestHalfOpenSeamOwnership(AnalyticBaseline baseline, SmoothTerrainEditSnapshot edits, out string summary)
    {
        foreach ((int dx, int dy, int dz, bool wrap) direction in new[] { (1,0,0,false), (0,1,0,false), (0,0,1,false), (1,0,0,true) })
        {
            var aCoordinate = direction.wrap ? new SmoothTerrainChunkCoordinate(3, 0, 0) : new SmoothTerrainChunkCoordinate(0, 0, 0);
            var bCoordinate = new SmoothTerrainChunkCoordinate(aCoordinate.AroundChunk + direction.dx, aCoordinate.VerticalChunk + direction.dy, aCoordinate.AcrossChunk + direction.dz);
            SmoothTerrainMeshData a = SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(aCoordinate, baseline, edits));
            SmoothTerrainMeshData b = SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(bCoordinate, baseline, edits));
            var triangles = new HashSet<string>();
            if (!AddCanonicalTriangles(a, aCoordinate, baseline.AroundLength, triangles) || !AddCanonicalTriangles(b, bCoordinate, baseline.AroundLength, triangles))
            { summary = "adjacent chunks emitted duplicate canonical seam triangles"; return false; }
            // The plane crosses the X/Z faces; the horizontal plane is correctly absent above its Y seam.
            if (direction.dy == 0 && (a.Indices.Length == 0 || b.Indices.Length == 0)) { summary = "crossing surface left an open seam"; return false; }
        }
        summary = string.Empty; return true;
    }

    private static bool TestCertifiedSeams(out string summary)
    {
        foreach ((int axis, bool wrap) test in new[] { (0,false), (1,false), (2,false), (0,true) })
        {
            var baseline = new BoundarySphereBaseline(128, test.axis, test.wrap);
            SmoothTerrainChunkCoordinate a = test.wrap ? new(3, 0, 0) : new(0, 0, 0);
            SmoothTerrainChunkCoordinate b = test.axis switch { 0 => new(a.AroundChunk + 1, a.VerticalChunk, a.AcrossChunk), 1 => new(a.AroundChunk, a.VerticalChunk + 1, a.AcrossChunk), _ => new(a.AroundChunk, a.VerticalChunk, a.AcrossChunk + 1) };
            long seam = test.wrap ? 0 : 32;
            var empty = new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 1, baseline.AroundLength);
            if (!CertifyPair(baseline, empty, a, b, test.axis, seam, $"{AxisName(test.axis)} baseline", out summary)) return false;
            var stamp = new SmoothTerrainEditCommand(1, (ulong)(100 + test.axis + (test.wrap ? 10 : 0)), 1, false, test.axis == 0 ? seam : 16, test.axis == 1 ? (int)seam : 16, test.axis == 2 ? (int)seam : 16, 5.0f, 1);
            var edited = new SmoothTerrainEditSnapshot(new[] { stamp }, 2, baseline.AroundLength);
            if (!CertifyPair(baseline, edited, a, b, test.axis, seam, $"{AxisName(test.axis)} edited", out summary)) return false;
        }
        summary = string.Empty; return true;
    }

    private static bool CertifyPair(ISmoothTerrainBaseline baseline, SmoothTerrainEditSnapshot edits, SmoothTerrainChunkCoordinate aCoordinate, SmoothTerrainChunkCoordinate bCoordinate, int axis, long seam, string label, out string summary)
    {
        SmoothTerrainMeshData a = SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(aCoordinate, baseline, edits), true);
        SmoothTerrainMeshData b = SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(bCoordinate, baseline, edits), true);
        if (!ValidateMesh(a,out summary) || !ValidateMesh(b,out summary) || !ValidateWinding(a,out summary) || !ValidateWinding(b,out summary)) { summary=$"{label}: {summary}"; return false; }
        var verticesA = SeamNormals(a,aCoordinate,baseline.AroundLength,axis,seam); var verticesB = SeamNormals(b,bCoordinate,baseline.AroundLength,axis,seam);
        if (verticesA.Count == 0 || verticesB.Count == 0) { summary=$"{label}: crossing surface has an open seam"; return false; }
        if (verticesA.Count != verticesB.Count) { summary=$"{label}: canonical seam vertex sets differ"; return false; }
        foreach (var pair in verticesA)
        {
            if (!verticesB.TryGetValue(pair.Key,out Vector3 normal)) { summary=$"{label}: canonical seam vertex is missing"; return false; }
            if (!pair.Value.IsEqualApprox(normal)) { summary=$"{label}: canonical seam normal diverged"; return false; }
        }
        var triangles=new HashSet<string>(); var edges=new Dictionary<string,int>();
        if (!AddCertified(a,aCoordinate,baseline.AroundLength,axis,seam,triangles,edges,"A",out string duplicate)
            || !AddCertified(b,bCoordinate,baseline.AroundLength,axis,seam,triangles,edges,"B",out duplicate))
        { summary=$"{label}: duplicate canonical triangle ({duplicate})"; return false; }
        foreach(var edge in edges) if (edge.Value == 1 && edge.Key.Contains("@")) { summary=$"{label}: boundary edge lies on internal chunk plane"; return false; }
        summary=string.Empty; return true;
    }
    private static Dictionary<string,Vector3> SeamNormals(SmoothTerrainMeshData mesh,SmoothTerrainChunkCoordinate c,long length,int axis,long seam)
    { var result=new Dictionary<string,Vector3>(); Vector3 origin=new(c.AroundChunk*32,c.VerticalChunk*32,c.AcrossChunk*32); for(int i=0;i<mesh.Vertices.Length;i++){Vector3 p=CanonicalVector(mesh.Vertices[i]+origin,length); float value=axis==0?p.X:axis==1?p.Y:p.Z; if(MathF.Abs(value-seam)<0.0001f) result[Canonical(p,length)]=mesh.Normals[i];} return result; }
    private static bool AddCertified(SmoothTerrainMeshData mesh,SmoothTerrainChunkCoordinate c,long length,int axis,long seam,HashSet<string> triangles,Dictionary<string,int> edges,string owner,out string duplicate)
    { Vector3 origin=new(c.AroundChunk*32,c.VerticalChunk*32,c.AcrossChunk*32); for(int i=0;i<mesh.Indices.Length;i+=3){string[] p={Canonical(CanonicalVector(mesh.Vertices[mesh.Indices[i]]+origin,length),length),Canonical(CanonicalVector(mesh.Vertices[mesh.Indices[i+1]]+origin,length),length),Canonical(CanonicalVector(mesh.Vertices[mesh.Indices[i+2]]+origin,length),length)}; var sorted=(string[])p.Clone();Array.Sort(sorted);string triangle=string.Join("|",sorted);if(!triangles.Add(triangle)){duplicate=$"owner={owner}, triangle={i/3}, vertices={triangle}";return false;} for(int e=0;e<3;e++){string key=string.CompareOrdinal(p[e],p[(e+1)%3])<0?p[e]+"~"+p[(e+1)%3]:p[(e+1)%3]+"~"+p[e]; if(OnSeam(p[e],axis,seam)&&OnSeam(p[(e+1)%3],axis,seam))key+="@";edges[key]=edges.TryGetValue(key,out int n)?n+1:1;}}duplicate=string.Empty;return true; }
    private static bool OnSeam(string p,int axis,long seam){string[] v=p.Split(',');return MathF.Abs(float.Parse(v[axis],CultureInfo.InvariantCulture)-seam)<0.0001f;}
    private static Vector3 CanonicalVector(Vector3 p,long length)=>new((float)(((p.X%length)+length)%length),p.Y,p.Z);
    private static string AxisName(int axis)=>axis==0?"X":axis==1?"Y":"Z";

    private static bool AddCanonicalTriangles(SmoothTerrainMeshData mesh, SmoothTerrainChunkCoordinate coordinate, long length, HashSet<string> output)
    {
        Vector3 origin = new(coordinate.AroundChunk * 32, coordinate.VerticalChunk * 32, coordinate.AcrossChunk * 32);
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            string[] p = { Canonical(mesh.Vertices[mesh.Indices[i]] + origin, length), Canonical(mesh.Vertices[mesh.Indices[i + 1]] + origin, length), Canonical(mesh.Vertices[mesh.Indices[i + 2]] + origin, length) };
            Array.Sort(p); if (!output.Add(string.Join("|", p))) return false;
        }
        return true;
    }
    private static string Canonical(Vector3 p, long length) => FormattableString.Invariant($"{((p.X % length) + length) % length:R},{p.Y:R},{p.Z:R}");

    private static bool TestSharedChunkBoundary(AnalyticBaseline baseline, SmoothTerrainEditSnapshot edits, out string summary)
    {
        SmoothTerrainChunkSnapshot left = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), baseline, edits);
        SmoothTerrainChunkSnapshot right = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(1, 0, 0), baseline, edits);
        SmoothTerrainChunkSnapshot seamLeft = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(3, 0, 0), baseline, edits);
        SmoothTerrainChunkSnapshot seamRight = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), baseline, edits);
        for (int z = -1; z <= SmoothTerrainChunkSnapshot.Cells + 1; z++)
        for (int y = -1; y <= SmoothTerrainChunkSnapshot.Cells + 1; y++)
        {
            if (!SampleMatches(left, 32, y, z, right, 0, y, z)
                || !SampleMatches(left, 33, y, z, right, 1, y, z)
                || !SampleMatches(seamLeft, 32, y, z, seamRight, 0, y, z))
            {
                summary = "neighboring chunks disagree on a shared density or material sample";
                return false;
            }
        }
        summary = string.Empty;
        return true;
    }

    private static bool TestDeterministicEditOrdering(AnalyticBaseline baseline, out string summary)
    {
        var first = new SmoothTerrainEditCommand(SmoothTerrainEditCommand.CurrentVersion, 20, 4, true, 126, 8, 4, 5.0f, (int)SmoothTerrainMaterial.Soil);
        var second = new SmoothTerrainEditCommand(SmoothTerrainEditCommand.CurrentVersion, 10, 4, false, 1, 8, 4, 2.0f, (int)SmoothTerrainMaterial.Rock);
        var ordered = new SmoothTerrainEditSnapshot(new[] { first, second }, revision: 9, aroundLength: baseline.AroundLength);
        var reversed = new SmoothTerrainEditSnapshot(new[] { second, first }, revision: 9, aroundLength: baseline.AroundLength);
        if (ordered.Ordered.Length != 2 || ordered.Ordered[0].CommandId != 10 || ordered.Ordered[1].CommandId != 20)
        {
            summary = "edit commands are not ordered by sequence then command identity";
            return false;
        }

        SmoothTerrainChunkSnapshot a = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), baseline, ordered);
        SmoothTerrainChunkSnapshot b = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), baseline, reversed);
        if (!SnapshotsMatch(a, b) || a.Density(0, 8, 4) >= baseline.Sample(0, 8, 4).Density)
        {
            summary = "edit ordering is nondeterministic or a wrap-crossing stamp was not composed";
            return false;
        }
        var multiWrap = new SmoothTerrainEditSnapshot(new[] { first with { Around = first.Around + baseline.AroundLength * 3 } }, 10, baseline.AroundLength);
        var canonical = new SmoothTerrainEditSnapshot(new[] { first }, 10, baseline.AroundLength);
        if (!SnapshotsMatch(SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0,0,0), baseline, multiWrap), SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0,0,0), baseline, canonical)))
        { summary = "multi-wrap stamp canonicalization changed composition"; return false; }
        var outer = new SmoothTerrainEditCommand(1, 31, 1, true, 16, 8, 16, 8, 1);
        var inner = new SmoothTerrainEditCommand(1, 32, 2, true, 16, 8, 16, 2, 2);
        SmoothTerrainChunkSnapshot overlap = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0,0,0), baseline, new SmoothTerrainEditSnapshot(new[] { outer, inner }, 11, baseline.AroundLength));
        if (overlap.Material(16,8,16) != 1) { summary = "non-winning add stamp overwrote material"; return false; }
        summary = string.Empty;
        return true;
    }

    private static bool TestRevisionInvalidation(AnalyticBaseline baseline, out string summary)
    {
        var initial = new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), revision: 12, aroundLength: baseline.AroundLength);
        var changed = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 1, 1, true, 16, 8, 16, 4.0f, 2) }, revision: 13, aroundLength: baseline.AroundLength);
        SmoothTerrainChunkCoordinate coordinate = new(0, 0, 0);
        SmoothTerrainChunkSnapshot before = SmoothTerrainSnapshotCapture.Capture(coordinate, baseline, initial);
        SmoothTerrainChunkSnapshot after = SmoothTerrainSnapshotCapture.Capture(coordinate, baseline, changed);
        var changedBaseline = new AnalyticBaseline(128, revision: 2);
        SmoothTerrainChunkSnapshot baselineChanged = SmoothTerrainSnapshotCapture.Capture(coordinate, changedBaseline, initial);
        if (before.Revision.Edits != 12 || after.Revision.Edits != 13 || before.Revision.Baseline != 1 || baselineChanged.Revision.Baseline != 2 || SnapshotsMatch(before, after))
        {
            summary = "an edit change did not produce a distinct snapshot revision";
            return false;
        }
        summary = string.Empty;
        return true;
    }

    private static bool TestReferenceMesher(AnalyticBaseline baseline, SmoothTerrainEditSnapshot edits, out string summary)
    {
        SmoothTerrainChunkSnapshot plane = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), baseline, edits);
        SmoothTerrainMeshData first = SmoothTerrainMesher.Build(plane, buildCollisionFaces: true);
        SmoothTerrainMeshData second = SmoothTerrainMesher.Build(plane, buildCollisionFaces: true);
        ulong firstHash = ComputeMeshHash(first);
        if (!ValidateMesh(first, out summary) || !ValidateWinding(first, out summary) || !MeshesMatch(first, second) || firstHash != ComputeMeshHash(second))
        {
            summary = string.IsNullOrEmpty(summary) ? "reference mesher output was nondeterministic" : summary;
            return false;
        }

        var sphereBaseline = new AnalyticBaseline(128, AnalyticShape.Sphere);
        var tunnelBaseline = new AnalyticBaseline(128, AnalyticShape.Tunnel);
        if (!ValidateFixtureMesh(sphereBaseline, edits, "sphere", out summary)
            || !ValidateFixtureMesh(tunnelBaseline, edits, "cave/tunnel", out summary))
            return false;

        var seamEdits = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 51, 1, false, 0, 8, 16, 6.0f, 1) }, revision: 2, aroundLength: baseline.AroundLength);
        if (!ValidateFixtureMesh(baseline, seamEdits, "edited seam", out summary))
            return false;

        var solidBaseline = new AnalyticBaseline(128, densityOffset: -100.0f);
        var emptyBaseline = new AnalyticBaseline(128, densityOffset: 100.0f);
        if (SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), solidBaseline, edits)).Indices.Length != 0
            || SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), emptyBaseline, edits)).Indices.Length != 0)
        {
            summary = "solid or empty fixture emitted reference mesh geometry";
            return false;
        }
        summary = string.Empty;
        return true;
    }

    private static bool TestVolumetricDensityLodContract(AnalyticBaseline baseline, out string summary)
    {
        var edits = new SmoothTerrainEditSnapshot(new[]
        {
            new SmoothTerrainEditCommand(1, 801, 1, false, 126, -16, 12, 11.0f, (int)SmoothTerrainMaterial.Rock)
        }, revision: 41, baseline.AroundLength);
        var field = new SmoothTerrainVolumetricDensitySnapshot(baseline, edits);
        if (field.Identity.ContractVersion != SmoothTerrainVolumetricDensityIdentity.CurrentContractVersion
            || field.Identity.BaselineRevision != baseline.Revision || field.Identity.EditRevision != edits.Revision
            || field.Identity.AroundLength != baseline.AroundLength || field.Identity.AcrossWidth != baseline.AcrossWidth)
        { summary = "volumetric field identity omitted authoritative baseline or edit inputs"; return false; }

        // A near 1 m lattice and a coarse 4 m lattice share all aligned samples,
        // including negative vertical coordinates and the wrapped ring seam.
        var nearKey = new SmoothTerrainVolumetricDensityCacheKey(field.Identity, 120, -20, 8, 8, 1);
        var near = field.CaptureLattice(nearKey);
        foreach (int spacing in new[] { 2, 4, 8 })
        {
            var lod = field.CaptureLattice(new SmoothTerrainVolumetricDensityCacheKey(field.Identity, 120, -20, 8, 8 / spacing, spacing));
            for (int z = 0; z <= 8 / spacing; z++) for (int y = 0; y <= 8 / spacing; y++) for (int x = 0; x <= 8 / spacing; x++)
                if (near.Sample(x * spacing, y * spacing, z * spacing) != lod.Sample(x, y, z))
                { summary = "aligned volumetric samples diverged across LOD spacings"; return false; }
        }
        foreach(int spacing in new[]{16,64,256,1024})
        {
            var key=new SmoothTerrainVolumetricDensityCacheKey(field.Identity,0,-4096,8,1,spacing);
            var lod=field.CaptureLattice(key);
            if(lod.Sample(1,1,1)!=field.Sample(spacing,-4096+spacing,8+spacing))
            {summary=$"coarse volumetric sample diverged at {spacing} m spacing";return false;}
        }
        if(new SmoothTerrainVolumetricDensityCacheKey(field.Identity,0,0,0,SmoothTerrainVolumetricDensityCacheKey.MaximumCellsPerAxis+1,1).IsValid
            ||new SmoothTerrainVolumetricDensityCacheKey(field.Identity,0,0,0,1,3).IsValid)
        {summary="volumetric cache key accepted an unbounded or non-power-of-two lattice";return false;}
        if (field.Sample(-1, -16, 12) != field.Sample(baseline.AroundLength - 1, -16, 12))
        { summary = "volumetric field lost canonical ring-wrap sampling"; return false; }
        var chunkCoordinate=new SmoothTerrainChunkCoordinate(3,-1,0);
        if(!SnapshotsMatch(field.CaptureChunk(chunkCoordinate),SmoothTerrainSnapshotCapture.Capture(chunkCoordinate,baseline,edits)))
        { summary="near authoritative chunk capture diverged from the production regular-cell capture"; return false; }

        var changed = new SmoothTerrainVolumetricDensitySnapshot(baseline,
            new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), revision: 42, baseline.AroundLength));
        var oldAfterNewSnapshot = field.CaptureLattice(new SmoothTerrainVolumetricDensityCacheKey(field.Identity, 120, -20, 8, 2, 4));
        var changedLattice = changed.CaptureLattice(new SmoothTerrainVolumetricDensityCacheKey(changed.Identity, 120, -20, 8, 2, 4));
        if (changed.Identity == field.Identity || changedLattice.Sample(0, 1, 1) == oldAfterNewSnapshot.Sample(0, 1, 1)
            || oldAfterNewSnapshot.Sample(0, 1, 1) != near.Sample(0, 4, 4))
        { summary = "volumetric field identity or immutable edit composition did not invalidate"; return false; }
        try { field.CaptureLattice(new SmoothTerrainVolumetricDensityCacheKey(changed.Identity, 120, -20, 8, 2, 4)); }
        catch (ArgumentException) { summary = string.Empty; return true; }
        summary = "volumetric field accepted a cache key from another identity";
        return false;
    }

    private static bool TestVolumetricDensityRangeRejection(out string summary)
    {
        const int cells=32,halo=2,spacing=2;
        SmoothTerrainVolumetricDensityCacheKey Key(SmoothTerrainVolumetricDensitySnapshot field,int vertical)
            => new(field.Identity,0,vertical,0,cells+halo*2,spacing);
        SmoothTerrainDensityClassification Classify(SmoothTerrainVolumetricDensitySnapshot field,int vertical)
            => field.CaptureLattice(Key(field,vertical)).ClassifyCells(halo,halo,halo,cells,cells,cells).Classification;
        var noEdits=new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(),1,128);
        var empty=new SmoothTerrainVolumetricDensitySnapshot(new AnalyticBaseline(128,AnalyticShape.Plane,100),noEdits);
        var solid=new SmoothTerrainVolumetricDensitySnapshot(new AnalyticBaseline(128,AnalyticShape.Plane,-100),noEdits);
        var mixed=new SmoothTerrainVolumetricDensitySnapshot(new AnalyticBaseline(128),noEdits);
        if(Classify(empty,0)!=SmoothTerrainDensityClassification.Empty||Classify(solid,0)!=SmoothTerrainDensityClassification.Solid||Classify(mixed,0)!=SmoothTerrainDensityClassification.Mixed)
        {summary="volumetric density range misclassified empty, solid, or surface sector";return false;}
        // The first two samples are the medium gradient halo. A sign change
        // there cannot force a mesh for an otherwise empty central sector.
        var haloOnly=new SmoothTerrainVolumetricDensitySnapshot(new AnalyticBaseline(128,AnalyticShape.Plane,2.25f),noEdits);
        SmoothTerrainVolumetricDensityLattice haloLattice=haloOnly.CaptureLattice(Key(haloOnly,3));
        if(haloLattice.Sample(0,0,0).Density>=0||haloLattice.ClassifyCells(halo,halo,halo,cells,cells,cells).Classification!=SmoothTerrainDensityClassification.Empty)
        {summary="halo-only sign change was treated as central mesh work";return false;}
        if(Classify(solid,-100)!=SmoothTerrainDensityClassification.Solid)
        {summary="negative-Y density range rejection regressed";return false;}
        var editedBaseline=new AnalyticBaseline(128,AnalyticShape.Plane,100);
        var before=new SmoothTerrainVolumetricDensitySnapshot(editedBaseline,noEdits);
        var afterEdits=new SmoothTerrainEditSnapshot(new[]{new SmoothTerrainEditCommand(1,1,1,true,32,32,32,24,(int)SmoothTerrainMaterial.Rock)},2,128);
        var after=new SmoothTerrainVolumetricDensitySnapshot(editedBaseline,afterEdits);
        if(before.Identity==after.Identity||Classify(before,0)!=SmoothTerrainDensityClassification.Empty||Classify(after,0)!=SmoothTerrainDensityClassification.Mixed)
        {summary="edit did not invalidate or reclassify a volumetric range";return false;}
        summary=string.Empty;return true;
    }

    private static bool TestIncrementalAppendMatchesFullCapture(AnalyticBaseline baseline,out string summary)
    {
        var coordinate=new SmoothTerrainChunkCoordinate(0,0,0);
        var commands=new[]
        {
            new SmoothTerrainEditCommand(1,71,1,false,1,8,4,5,(int)SmoothTerrainMaterial.Soil),
            new SmoothTerrainEditCommand(1,72,2,true,4,7,5,2,(int)SmoothTerrainMaterial.Rock),
            new SmoothTerrainEditCommand(1,73,3,false,126,8,4,4,(int)SmoothTerrainMaterial.Soil)
        };
        SmoothTerrainChunkSnapshot incremental=SmoothTerrainSnapshotCapture.Capture(
            coordinate,baseline,new SmoothTerrainEditSnapshot(new[]{commands[0]},31,baseline.AroundLength));
        for(int i=1;i<commands.Length;i++)
        {
            incremental=SmoothTerrainSnapshotCapture.ApplyAppendedEdit(incremental,baseline,commands[i],(ulong)(31+i));
            var prefix=new SmoothTerrainEditCommand[i+1];
            Array.Copy(commands,prefix,i+1);
            SmoothTerrainChunkSnapshot full=SmoothTerrainSnapshotCapture.Capture(
                coordinate,baseline,new SmoothTerrainEditSnapshot(prefix,(ulong)(31+i),baseline.AroundLength));
            if(incremental.Revision!=full.Revision||!SnapshotsMatch(incremental,full))
            {
                summary=$"incremental appended edit {i} diverged from a full journal capture";
                return false;
            }
        }
        SmoothTerrainChunkSnapshot batchBase=SmoothTerrainSnapshotCapture.Capture(
            coordinate,baseline,new SmoothTerrainEditSnapshot(new[]{commands[0]},31,baseline.AroundLength));
        SmoothTerrainChunkSnapshot batch=SmoothTerrainSnapshotCapture.ApplyAppendedEdits(
            batchBase,baseline,new[]{commands[1],commands[2]},33);
        SmoothTerrainChunkSnapshot batchFull=SmoothTerrainSnapshotCapture.Capture(
            coordinate,baseline,new SmoothTerrainEditSnapshot(commands,33,baseline.AroundLength));
        if(batch.Revision!=batchFull.Revision||!SnapshotsMatch(batch,batchFull))
        {
            summary="coalesced appended edit batch diverged from a full journal capture";
            return false;
        }
        SmoothTerrainEditCommand duplicate=commands[1] with { CommandId=74 };
        var duplicateJournal=new[]{commands[0],commands[1],duplicate,commands[2]};
        SmoothTerrainChunkSnapshot compactedBatch=SmoothTerrainSnapshotCapture.ApplyAppendedEdits(
            batchBase,baseline,new[]{commands[1],duplicate,commands[2]},34);
        SmoothTerrainChunkSnapshot duplicateFull=SmoothTerrainSnapshotCapture.Capture(
            coordinate,baseline,new SmoothTerrainEditSnapshot(duplicateJournal,34,baseline.AroundLength));
        if(compactedBatch.Revision!=duplicateFull.Revision||!SnapshotsMatch(compactedBatch,duplicateFull))
        {
            summary="idempotent coalesced stamp compaction changed journal composition";
            return false;
        }
        summary=string.Empty;
        return true;
    }

    private static bool TestCertifiedScaleTransformPrecision(out string summary)
    {
        const long captureAround=28_779_700;
        const int captureAcross=163_800;
        var baseline=new AnalyticBaseline(CertifiedRingScale.AroundVoxelLength);
        var empty=new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(),1,baseline.AroundLength);
        var coordinate=new SmoothTerrainChunkCoordinate(
            captureAround/SmoothTerrainChunkSnapshot.Cells,
            0,
            captureAcross/SmoothTerrainChunkSnapshot.Cells);
        SmoothTerrainMeshData canonical=SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(coordinate,baseline,empty));
        var geometry=new RingLodGeometrySnapshot(
            Transform3D.Identity,
            Vector3.Zero,
            Vector3.Zero,
            CertifiedRingScale.EffectiveRadiusMeters,
            0.0,
            0.0,
            CertifiedRingScale.EffectiveCircumferenceMeters,
            CertifiedRingScale.TerrainFloorMeters,
            CertifiedRingScale.TerrainCeilingMeters,
            new RingCanonicalPosition(captureAround,8.0,captureAcross));
        SmoothTerrainMeshData transformed=SmoothTerrainGrid.TransformWorker(canonical,coordinate,geometry,Transform3D.Identity);
        for(int i=0;i<transformed.Indices.Length;i+=3)
        {
            Vector3 a=transformed.Vertices[transformed.Indices[i]];
            Vector3 b=transformed.Vertices[transformed.Indices[i+1]];
            Vector3 c=transformed.Vertices[transformed.Indices[i+2]];
            if((b-a).Cross(c-a).LengthSquared()<=1e-8f)
            {
                summary=$"certified-scale transform collapsed triangle {i/3} at around {captureAround}";
                return false;
            }
        }
        summary=string.Empty;
        return true;
    }

    private static bool TestAllRegularCellCases(out string summary)
    {
        var corners = new (int X, int Y, int Z)[]
        {
            (0,0,0),(1,0,0),(0,1,0),(1,1,0),
            (0,0,1),(1,0,1),(0,1,1),(1,1,1)
        };
        for (int code = 0; code < 256; code++)
        {
            int cellClass = SmoothTerrainRegularCellTables.Class[code];
            if ((uint)cellClass >= SmoothTerrainRegularCellTables.Geometry.Length)
            { summary = $"regular-cell case {code}: invalid class"; return false; }
            int geometry = SmoothTerrainRegularCellTables.Geometry[cellClass];
            int vertexCount = geometry >> 4;
            int triangleCount = geometry & 15;
            if (vertexCount > 12 || triangleCount > 5)
            { summary = $"regular-cell case {code}: invalid geometry counts"; return false; }
            // Complement cases are visited independently by this same 0..255
            // loop. Modified Marching Cubes may choose a different triangle
            // count for an ambiguous complement, so equality is not required.
            for (int triangleIndex = 0; triangleIndex < triangleCount * 3; triangleIndex++)
            {
                if (SmoothTerrainRegularCellTables.Indices[cellClass * 15 + triangleIndex] >= vertexCount)
                { summary = $"regular-cell case {code}: class index is out of range"; return false; }
            }
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                int edge = SmoothTerrainRegularCellTables.Vertex[code * 12 + vertexIndex] & 255;
                int a = (edge >> 4) & 15;
                int b = edge & 15;
                if (a >= 8 || b >= 8 || a == b || (((code >> a) ^ (code >> b)) & 1) == 0)
                { summary = $"regular-cell case {code}: vertex is not on a sign-changing cube edge"; return false; }
            }

            SmoothTerrainChunkSnapshot fixture = CreateRegularCellCaseFixture(code, corners);
            SmoothTerrainMeshData first = SmoothTerrainMesher.Build(fixture, buildCollisionFaces: true);
            SmoothTerrainMeshData second = SmoothTerrainMesher.Build(fixture, buildCollisionFaces: true);
            if (!ValidateMesh(first, out summary)
                || !ValidateWinding(first, out summary)
                || !MeshesMatch(first, second)
                || ComputeMeshHash(first) != ComputeMeshHash(second))
            { summary = $"regular-cell case {code}: {summary}"; return false; }
            if (code != 0 && first.Indices.Length == 0)
            { summary = $"regular-cell case {code}: fixture emitted no geometry"; return false; }
            var triangles = new HashSet<string>();
            if (!AddCanonicalTriangles(first, new SmoothTerrainChunkCoordinate(0,0,0), 1024, triangles))
            { summary = $"regular-cell case {code}: fixture emitted a duplicate triangle"; return false; }
        }
        summary = string.Empty;
        return true;
    }

    private static SmoothTerrainChunkSnapshot CreateRegularCellCaseFixture(
        int code,
        IReadOnlyList<(int X, int Y, int Z)> corners)
    {
        int total = SmoothTerrainChunkSnapshot.SampleCount
            * SmoothTerrainChunkSnapshot.SampleCount
            * SmoothTerrainChunkSnapshot.SampleCount;
        var densities = new float[total];
        var materials = new int[total];
        Array.Fill(densities, 4.0f);
        Array.Fill(materials, (int)SmoothTerrainMaterial.Soil);
        const int origin = 16;
        for (int corner = 0; corner < 8; corner++)
        {
            (int x, int y, int z) = corners[corner];
            densities[SmoothTerrainChunkSnapshot.Index(origin + x, origin + y, origin + z)] =
                (code & (1 << corner)) != 0 ? -1.0f : 1.0f;
        }
        return new SmoothTerrainChunkSnapshot(
            new SmoothTerrainChunkCoordinate(0,0,0),
            new SmoothTerrainRevision(1,1),
            densities,
            materials);
    }

    private static ulong ComputeMeshHash(SmoothTerrainMeshData mesh)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        void Add(int value) { unchecked { hash ^= (uint)value; hash *= prime; } }
        foreach (Vector3 vertex in mesh.Vertices) { Add(BitConverter.SingleToInt32Bits(vertex.X)); Add(BitConverter.SingleToInt32Bits(vertex.Y)); Add(BitConverter.SingleToInt32Bits(vertex.Z)); }
        foreach (Vector3 normal in mesh.Normals) { Add(BitConverter.SingleToInt32Bits(normal.X)); Add(BitConverter.SingleToInt32Bits(normal.Y)); Add(BitConverter.SingleToInt32Bits(normal.Z)); }
        foreach (int material in mesh.Materials) Add(material);
        foreach (int index in mesh.Indices) Add(index);
        return hash;
    }

    private static bool ValidateFixtureMesh(AnalyticBaseline baseline, SmoothTerrainEditSnapshot edits, string fixture, out string summary)
    {
        SmoothTerrainChunkSnapshot snapshot = SmoothTerrainSnapshotCapture.Capture(new SmoothTerrainChunkCoordinate(0, 0, 0), baseline, edits);
        SmoothTerrainMeshData mesh = SmoothTerrainMesher.Build(snapshot, buildCollisionFaces: true);
        if (!ValidateMesh(mesh, out summary) || mesh.Indices.Length == 0)
        {
            summary = string.IsNullOrEmpty(summary) ? $"{fixture} fixture emitted no mesh" : $"{fixture} fixture: {summary}";
            return false;
        }
        return true;
    }

    private static bool SampleMatches(SmoothTerrainChunkSnapshot a, int ax, int ay, int az, SmoothTerrainChunkSnapshot b, int bx, int by, int bz) =>
        a.Density(ax, ay, az) == b.Density(bx, by, bz) && a.Material(ax, ay, az) == b.Material(bx, by, bz);

    private static bool SnapshotsMatch(SmoothTerrainChunkSnapshot a, SmoothTerrainChunkSnapshot b) =>
        a.Densities.SequenceEqual(b.Densities) && a.Materials.SequenceEqual(b.Materials);

    private static bool MeshesMatch(SmoothTerrainMeshData a, SmoothTerrainMeshData b) =>
        a.Vertices.AsSpan().SequenceEqual(b.Vertices) && a.Normals.AsSpan().SequenceEqual(b.Normals)
        && a.Materials.AsSpan().SequenceEqual(b.Materials) && a.Indices.AsSpan().SequenceEqual(b.Indices)
        && a.CollisionFaces.AsSpan().SequenceEqual(b.CollisionFaces);

    private static bool ValidateMesh(SmoothTerrainMeshData mesh, out string summary)
    {
        if (mesh.Vertices.Length != mesh.Normals.Length || mesh.Vertices.Length != mesh.Materials.Length
            || mesh.Indices.Length % 3 != 0 || mesh.CollisionFaces.Length != mesh.Indices.Length)
        {
            summary = "mesh array lengths violate the result contract";
            return false;
        }
        foreach (Vector3 vertex in mesh.Vertices)
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z)) { summary = "mesh has a non-finite vertex"; return false; }
        foreach (Vector3 normal in mesh.Normals)
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z) || normal.LengthSquared() < 0.5f) { summary = "mesh has an invalid normal"; return false; }
        foreach (int index in mesh.Indices)
            if (index < 0 || index >= mesh.Vertices.Length) { summary = "mesh has an out-of-range index"; return false; }
        summary = string.Empty;
        return true;
    }

    private static bool ValidateWinding(SmoothTerrainMeshData mesh, out string summary)
    {
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            int a=mesh.Indices[i], b=mesh.Indices[i+1], c=mesh.Indices[i+2]; Vector3 face=(mesh.Vertices[b]-mesh.Vertices[a]).Cross(mesh.Vertices[c]-mesh.Vertices[a]);
            if (face.LengthSquared() > 1e-10f && face.Dot(mesh.Normals[a]+mesh.Normals[b]+mesh.Normals[c]) >= 0.0f) { summary="face winding does not use Godot's clockwise outward convention"; return false; }
        }
        summary=string.Empty; return true;
    }


    private enum AnalyticShape { Plane, Sphere, Tunnel }

    private sealed class AnalyticBaseline : ISmoothTerrainBaseline
    {
        private readonly float _densityOffset;
        private readonly AnalyticShape _shape;
        public AnalyticBaseline(long aroundLength, AnalyticShape shape = AnalyticShape.Plane, float densityOffset = 0.0f, ulong revision = 1)
        {
            AroundLength = aroundLength;
            _shape = shape;
            _densityOffset = densityOffset;
            Revision = revision;
        }
        public long AroundLength { get; }
        public int AcrossWidth => 4096;
        public ulong Revision { get; }
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            long canonical = around % AroundLength; if (canonical < 0) canonical += AroundLength;
            float density = _shape switch
            {
                AnalyticShape.Sphere => new Vector3(canonical - 16, vertical - 16, across - 16).Length() - 10.0f,
                AnalyticShape.Tunnel => MathF.Max(vertical - 24.0f, 4.0f - new Vector2(canonical - 16, across - 16).Length()),
                _ => vertical - 8.25f
            } + _densityOffset;
            int material = ((canonical / 16 + across / 16) & 1) == 0 ? 1 : 2;
            return new SmoothTerrainSample(density, material, 1);
        }
    }

    private sealed class BoundarySphereBaseline : ISmoothTerrainBaseline
    {
        private readonly int _axis; private readonly bool _wrap;
        public BoundarySphereBaseline(long aroundLength, int axis, bool wrap) { AroundLength=aroundLength; _axis=axis; _wrap=wrap; }
        public long AroundLength { get; }
        public int AcrossWidth => 4096;
        public ulong Revision => (ulong)(50 + _axis + (_wrap ? 10 : 0));
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            long canonical=((around%AroundLength)+AroundLength)%AroundLength;
            float x=_axis==0 ? (_wrap ? Shortest(canonical,0) : canonical-32) : canonical-16;
            float y=_axis==1 ? vertical-32 : vertical-16;
            float z=_axis==2 ? across-32 : across-16;
            return new SmoothTerrainSample(MathF.Sqrt(x*x+y*y+z*z)-12.5f,1,Revision);
        }
        private float Shortest(long value,long center){long d=value-center;if(d>AroundLength/2)d-=AroundLength;if(d<-AroundLength/2)d+=AroundLength;return d;}
    }
}
