using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Save;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Standalone headless smoke entry point; run with --smooth-terrain-runtime-smoke.</summary>
public partial class SmoothTerrainRuntimeSmoke : Node
{
    public override async void _Ready()
    {
        if (!Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--smooth-terrain-runtime-smoke")) { GetTree().Quit(); return; }
        try
        {
            if (!RingTerrainLodCoordinator.FastTravelVisibilityRegressionTest(out string fastTravelVisibility))
                throw new InvalidOperationException($"fast-travel terrain visibility: {fastTravelVisibility}");
            if (!VoxelChunkGridManager.FastTravelChunkVisibilityRegressionTest(out string fastTravelVoxelVisibility))
                throw new InvalidOperationException($"fast-travel voxel visibility: {fastTravelVoxelVisibility}");
            GD.Print($"FAST_TRAVEL_TERRAIN_VISIBILITY_PASS: proxies=({fastTravelVisibility}) voxels=({fastTravelVoxelVisibility})");
            if (!SmoothTerrainGrid.CavePreloadPriorityRegressionTest(out string cavePreloadPriority))
                throw new InvalidOperationException($"cave preload priority: {cavePreloadPriority}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_PRELOAD_PRIORITY_PASS: {cavePreloadPriority}");
            if (!SmoothTerrainDiagnostics.RunSelfTest(out string diagnostics)) throw new InvalidOperationException($"smooth diagnostics: {diagnostics}");
            if (!SmoothTerrainGrid.CertifiedRingStampPrecisionRegressionTest(out string certifiedRingStampPrecision))
                throw new InvalidOperationException($"certified-ring stamp precision: {certifiedRingStampPrecision}");
            GD.Print($"SMOOTH_TERRAIN_CERTIFIED_RING_STAMP_PRECISION_PASS: {certifiedRingStampPrecision}");
            var presenter = new SmoothTerrainPresenter(); AddChild(presenter);
            var mesh = new SmoothTerrainMeshData { Vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }, Normals = new[] { Vector3.Back, Vector3.Back, Vector3.Back }, Materials = new[] { 1, 1, 1 }, Indices = new[] { 0, 1, 2 }, CollisionFaces = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) } };
            presenter.Install(mesh, true);
            if (!presenter.HasRenderMesh || !presenter.HasCollision) throw new InvalidOperationException("presenter did not create render/collision resources");
            if (presenter.RenderShadowCasting != GeometryInstance3D.ShadowCastingSetting.On)
                throw new InvalidOperationException("smooth terrain presenter did not explicitly cast shadows");
            if (presenter.RenderMaterial is not StandardMaterial3D fallback
                || fallback.Transparency != BaseMaterial3D.TransparencyEnum.Disabled
                || fallback.DepthDrawMode != BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly)
                throw new InvalidOperationException("smooth terrain fallback material is not opaque");
            if (fallback.CullMode != BaseMaterial3D.CullModeEnum.Back)
                throw new InvalidOperationException("smooth terrain fallback material does not preserve clockwise front-face culling");
            presenter.Reset(); if (presenter.HasRenderMesh || presenter.HasCollision) throw new InvalidOperationException("pool reset retained resources");
            var lazyPresenter = new SmoothTerrainPresenter(); AddChild(lazyPresenter);
            var physicalMesh = new SmoothTerrainMeshData { Vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0) }, Normals = new[] { Vector3.Up, Vector3.Up, Vector3.Up }, Materials = new[] { 2, 2, 2 }, Indices = new[] { 0, 1, 2 }, CollisionFaces = Array.Empty<Vector3>() };
            lazyPresenter.Install(physicalMesh, false); if (lazyPresenter.HasCollision || lazyPresenter.CollisionFaceExpansionCount != 0) throw new InvalidOperationException("visual-only presenter eagerly expanded collision faces"); lazyPresenter.SetCollision(true); if (!lazyPresenter.HasCollision || lazyPresenter.CollisionFaceExpansionCount != 1) throw new InvalidOperationException("lazy exact-topology collision promotion failed"); lazyPresenter.SetCollision(false); lazyPresenter.SetCollision(true); if (lazyPresenter.CollisionFaceExpansionCount != 1) throw new InvalidOperationException("collision promotion expanded retained topology twice");
            var physicsBody = new CharacterBody3D(); var physicsShape = new CollisionShape3D { Shape = new SphereShape3D { Radius = .2f } }; physicsBody.AddChild(physicsShape); physicsBody.Position = new Vector3(.25f, 2, .25f); AddChild(physicsBody); await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); if (physicsBody.MoveAndCollide(Vector3.Down * 4) == null) throw new InvalidOperationException("smooth visual topology was not present in the physics world"); physicsBody.QueueFree(); lazyPresenter.QueueFree();
            var settings = new VoxelTerrainSettings { Seed = 7, BaseSurfaceHeightVoxels = 8, HeightAmplitudeVoxels = 0, DetailAmplitudeVoxels = 0, BedrockY = -64, EnableUnified3dDensityTerrain = false };
            if (!CaveGenerationContractDiagnostics.RunSelfTest(out string caveGenerationDiagnostics))
                throw new InvalidOperationException($"cave generation contract diagnostics: {caveGenerationDiagnostics}");
            using (var caveHashBaseline = new RingTerrainSampler(settings, true, 4096, 1024.0, 128, 512))
            using (var changedCaveContractSettings = new RingTerrainSampler(new VoxelTerrainSettings
            {
                Seed = settings.Seed, BaseSurfaceHeightVoxels = settings.BaseSurfaceHeightVoxels,
                HeightAmplitudeVoxels = settings.HeightAmplitudeVoxels, DetailAmplitudeVoxels = settings.DetailAmplitudeVoxels,
                BedrockY = settings.BedrockY, EnableUnified3dDensityTerrain = settings.EnableUnified3dDensityTerrain,
                DesignedCaveGenerationContractSettings = new CaveGenerationSettings()
            }, true, 4096, 1024.0, 128, 512))
            using (var changedTerrainSeed = new RingTerrainSampler(new VoxelTerrainSettings
            {
                Seed = settings.Seed + 1, BaseSurfaceHeightVoxels = settings.BaseSurfaceHeightVoxels,
                HeightAmplitudeVoxels = settings.HeightAmplitudeVoxels, DetailAmplitudeVoxels = settings.DetailAmplitudeVoxels,
                BedrockY = settings.BedrockY, EnableUnified3dDensityTerrain = settings.EnableUnified3dDensityTerrain
            }, true, 4096, 1024.0, 128, 512))
            {
                settings.DesignedCaveGenerationContractSettings = new CaveGenerationSettings();
                using var independentCopy = caveHashBaseline.CreateIndependentCopy();
                CaveChamberAnchorSet baselineAnchors = caveHashBaseline.CaveChamberAnchorGenerator.Resolve(
                    caveHashBaseline.CaveProvinceDistribution.ResolvePlan(-1, -257, -1));
                CaveChamberAnchorSet copiedAnchors = independentCopy.CaveChamberAnchorGenerator.Resolve(
                    independentCopy.CaveProvinceDistribution.ResolvePlan(-1, -257, -1));
                CaveChamberGraph baselineGraph = caveHashBaseline.CaveChamberGraphGenerator.Resolve(
                    caveHashBaseline.CaveProvinceDistribution.ResolvePlan(-1, -257, -1));
                CaveChamberGraph copiedGraph = independentCopy.CaveChamberGraphGenerator.Resolve(
                    independentCopy.CaveProvinceDistribution.ResolvePlan(-1, -257, -1));
                CaveCellBoundaryConnectionMap baselineBoundaryMap = caveHashBaseline.CaveCellBoundaryConnectionMapGenerator.Resolve(
                    caveHashBaseline.CaveProvinceDistribution.ResolvePlan(-1, -257, -1));
                CaveCellBoundaryConnectionMap copiedBoundaryMap = independentCopy.CaveCellBoundaryConnectionMapGenerator.Resolve(
                    independentCopy.CaveProvinceDistribution.ResolvePlan(-1, -257, -1));
                var caveRegionRequest = new CaveRegionFeatureRequest(-1, -257, -1, 1, 1, 1, 0);
                CaveRegionFeatureSnapshot baselineRegionFeatures = caveHashBaseline.CaveRegionFeatureSnapshotGenerator.Capture(
                    caveRegionRequest);
                CaveRegionFeatureSnapshot copiedRegionFeatures = independentCopy.CaveRegionFeatureSnapshotGenerator.Capture(
                    caveRegionRequest);
                var caveWallProfile = new CaveWallNoiseProfile(8.0, 4.0, 16.0);
                CaveWallNoiseResult baselineWallNoise = caveHashBaseline.CaveWallNoiseApplicator.Apply(
                    -1, -257, -1, 0.0, caveWallProfile);
                CaveWallNoiseResult copiedWallNoise = independentCopy.CaveWallNoiseApplicator.Apply(
                    -1, -257, -1, 0.0, caveWallProfile);
                if (caveHashBaseline.CaveGenerationContract.Version != CaveGenerationSettings.CurrentVersion
                    || caveHashBaseline.CaveGenerationContract.TerrainSeed != settings.Seed
                    || caveHashBaseline.CaveGenerationContract != independentCopy.CaveGenerationContract
                    || caveHashBaseline.CaveTopologyPartition.Identity != independentCopy.CaveTopologyPartition.Identity
                    || caveHashBaseline.CaveTopologyPartition.Resolve(-1, -257, -1).Key
                        != independentCopy.CaveTopologyPartition.Resolve(-1, -257, -1).Key
                    || caveHashBaseline.CaveProvinceDistribution.Identity != independentCopy.CaveProvinceDistribution.Identity
                    || caveHashBaseline.CaveProvinceDistribution.ResolvePlan(-1, -257, -1)
                        != independentCopy.CaveProvinceDistribution.ResolvePlan(-1, -257, -1)
                    || caveHashBaseline.CaveChamberAnchorGenerator.Identity != independentCopy.CaveChamberAnchorGenerator.Identity
                    || baselineAnchors.Identity != copiedAnchors.Identity
                    || baselineAnchors.ProvinceAddress != copiedAnchors.ProvinceAddress
                    || baselineAnchors.Count != copiedAnchors.Count
                    || caveHashBaseline.CaveChamberGraphGenerator.Identity != independentCopy.CaveChamberGraphGenerator.Identity
                    || baselineGraph.Identity != copiedGraph.Identity
                    || baselineGraph.Count != copiedGraph.Count
                    || caveHashBaseline.CaveCellBoundaryConnectionMapGenerator.Identity != independentCopy.CaveCellBoundaryConnectionMapGenerator.Identity
                    || baselineBoundaryMap.Identity != copiedBoundaryMap.Identity
                    || baselineBoundaryMap.Count != copiedBoundaryMap.Count
                    || baselineBoundaryMap.RouteCount != copiedBoundaryMap.RouteCount
                    || caveHashBaseline.CaveRegionFeatureSnapshotGenerator.Identity != independentCopy.CaveRegionFeatureSnapshotGenerator.Identity
                    || baselineRegionFeatures.Identity != copiedRegionFeatures.Identity
                    || baselineRegionFeatures.Request != copiedRegionFeatures.Request
                    || baselineRegionFeatures.CandidateProvinceCount != copiedRegionFeatures.CandidateProvinceCount
                    || baselineRegionFeatures.ActiveProvinceCount != copiedRegionFeatures.ActiveProvinceCount
                    || baselineRegionFeatures.Count != copiedRegionFeatures.Count
                    || caveHashBaseline.CaveWallNoiseApplicator.Identity != independentCopy.CaveWallNoiseApplicator.Identity
                    || baselineWallNoise != copiedWallNoise
                    || caveHashBaseline.CaveTopologyDensityComposer.Identity != independentCopy.CaveTopologyDensityComposer.Identity)
                    throw new InvalidOperationException("sampler copy did not retain its captured terrain-seed-derived cave contract");
                if (caveHashBaseline.TerrainDefinitionHash != changedCaveContractSettings.TerrainDefinitionHash
                    || caveHashBaseline.CaveGenerationContract.Identity != changedCaveContractSettings.CaveGenerationContract.Identity
                    || caveHashBaseline.CaveWallNoiseApplicator.Identity != changedCaveContractSettings.CaveWallNoiseApplicator.Identity
                    || caveHashBaseline.CaveTopologyDensityComposer.Identity != changedCaveContractSettings.CaveTopologyDensityComposer.Identity)
                    throw new InvalidOperationException("cave contract settings changed the shared terrain-seed identity");
                foreach (var point in new (long Around, int Vertical, int Across)[]
                {
                    (64, 0, 64), (-2049, -48, -33), (long.MaxValue - 4096, -320, 127)
                })
                {
                    if (caveHashBaseline.SampleNaturalTerrainDensity(point.Around, point.Vertical, point.Across)
                        != changedCaveContractSettings.SampleNaturalTerrainDensity(point.Around, point.Vertical, point.Across))
                        throw new InvalidOperationException($"cave contract settings changed legacy density at {point}");
                }
                if (caveHashBaseline.TerrainDefinitionHash == changedTerrainSeed.TerrainDefinitionHash
                    || caveHashBaseline.CaveGenerationContract.Identity == changedTerrainSeed.CaveGenerationContract.Identity
                    || caveHashBaseline.CaveWallNoiseApplicator.Identity == changedTerrainSeed.CaveWallNoiseApplicator.Identity
                    || caveHashBaseline.CaveTopologyDensityComposer.Identity == changedTerrainSeed.CaveTopologyDensityComposer.Identity)
                    throw new InvalidOperationException("terrain seed did not drive the cave contract");
            }
            GD.Print($"SMOOTH_TERRAIN_SHARED_SEED_CONTRACT_PASS: {caveGenerationDiagnostics}");
            if (!CaveTopologyPartitionDiagnostics.RunSelfTest(out string cavePartitionDiagnostics))
                throw new InvalidOperationException($"cave topology partition diagnostics: {cavePartitionDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_PARTITION_PASS: {cavePartitionDiagnostics}");
            if (!CaveProvinceDistributionDiagnostics.RunSelfTest(out string caveProvinceDiagnostics))
                throw new InvalidOperationException($"cave province distribution diagnostics: {caveProvinceDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_PROVINCE_MASK_PASS: {caveProvinceDiagnostics}");
            if (!CaveChamberAnchorDiagnostics.RunSelfTest(out string caveAnchorDiagnostics))
                throw new InvalidOperationException($"cave anchor diagnostics: {caveAnchorDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_ANCHOR_PASS: {caveAnchorDiagnostics}");
            if (!CaveChamberGraphDiagnostics.RunSelfTest(out string caveGraphDiagnostics))
                throw new InvalidOperationException($"cave graph diagnostics: {caveGraphDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_GRAPH_PASS: {caveGraphDiagnostics}");
            if (!CaveCellBoundaryConnectionDiagnostics.RunSelfTest(out string caveBoundaryDiagnostics))
                throw new InvalidOperationException($"cave boundary diagnostics: {caveBoundaryDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_CELL_BOUNDARY_PASS: {caveBoundaryDiagnostics}");
            if (!CaveRegionFeatureDiagnostics.RunSelfTest(out string caveRegionFeatureDiagnostics))
                throw new InvalidOperationException($"cave region feature diagnostics: {caveRegionFeatureDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_REGION_FEATURE_PASS: {caveRegionFeatureDiagnostics}");
            if (!CaveWallNoiseDiagnostics.RunSelfTest(out string caveWallNoiseDiagnostics))
                throw new InvalidOperationException($"cave wall noise diagnostics: {caveWallNoiseDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_WALL_NOISE_PASS: {caveWallNoiseDiagnostics}");
            if (!CaveTopologyDensityComposerDiagnostics.RunSelfTest(out string caveTopologyDensityDiagnostics))
                throw new InvalidOperationException($"cave topology density diagnostics: {caveTopologyDensityDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_TOPOLOGY_DENSITY_PASS: {caveTopologyDensityDiagnostics}");
            if (!CaveFeatureFamilyDiagnostics.RunSelfTest(out string caveFamilyDiagnostics))
                throw new InvalidOperationException($"cave feature family diagnostics: {caveFamilyDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_FEATURE_FAMILY_PASS: {caveFamilyDiagnostics}");
            if (!CaveFeaturePlacementDiagnostics.RunSelfTest(out string cavePlacementDiagnostics))
                throw new InvalidOperationException($"cave feature placement diagnostics: {cavePlacementDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_FEATURE_PLACEMENT_PASS: {cavePlacementDiagnostics}");
            if (!CaveGameplayIntegrationDiagnostics.RunSelfTest(out string caveGameplayDiagnostics))
                throw new InvalidOperationException($"cave gameplay integration diagnostics: {caveGameplayDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_GAMEPLAY_HANDOFF_PASS: {caveGameplayDiagnostics}");
            if (!UndergroundCheckpointSafetyPolicy.RunSelfTest(out string checkpointDiagnostics))
                throw new InvalidOperationException($"underground checkpoint safety diagnostics: {checkpointDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_UNDERGROUND_CHECKPOINT_SAFETY_PASS: {checkpointDiagnostics}");
            if (!RingWorldCaveContractDiagnostics.RunSelfTest(out string caveSaveContractDiagnostics))
                throw new InvalidOperationException($"cave save contract diagnostics: {caveSaveContractDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_SAVE_CONTRACT_PASS: {caveSaveContractDiagnostics}");
            if (!DesignedCaveDensityCompositionDiagnostics.RunSelfTest(out string caveDensityCompositionDiagnostics))
                throw new InvalidOperationException($"cave density composition diagnostics: {caveDensityCompositionDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_DENSITY_COMPOSITION_PASS: {caveDensityCompositionDiagnostics}");
            if (!CaveTopologyFinalDiagnostics.RunSelfTest(out string caveTopologyFinalDiagnostics))
                throw new InvalidOperationException($"final cave topology diagnostics: {caveTopologyFinalDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_CAVE_TOPOLOGY_FINAL_PASS: {caveTopologyFinalDiagnostics}");
            var source = new VoxelChunkGridManager { BuildOnReady = false, EnableChunkStreaming = false, UseDeterministicTerrainGeneration = true, TerrainSettings = settings, UseRingCurvedPlacement = true, AroundRingChunkLength = 4, RingAcrossWidthMeters = 128, EnforceRingAcrossBounds = true, EnableCanonicalFloatingOriginAnchor = true }; source.Name = "Source"; AddChild(source);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!SmoothTerrainTransitionMesherDiagnostics.RunSelfTest(out string transitionDiagnostics)) throw new InvalidOperationException($"smooth transition diagnostics: {transitionDiagnostics}");
            GD.Print($"SMOOTH_TERRAIN_TRANSITION_CELL_SMOKE_PASS: {transitionDiagnostics}");
            int expectedStructuralBottom=source.GetStructuralBottomGlobalY();
            using(RingTerrainSampler finiteSampler=source.CreateIndependentTerrainSampler()??throw new InvalidOperationException("finite structural SDF sampler unavailable"))
            {
                var finiteBaseline=new RingTerrainSmoothBaseline(finiteSampler,expectedStructuralBottom,structuralWallTopVertical:source.GetStructuralWallTopGlobalY());
                if(expectedStructuralBottom!=-22304||finiteBaseline.Sample(8,expectedStructuralBottom,8).Density>=0||finiteBaseline.Sample(8,expectedStructuralBottom-1,8).Density<=0)
                    throw new InvalidOperationException($"finite structural SDF boundary failed at {expectedStructuralBottom}");
                var domains=source.GetVerticalDomainConfiguration(); if(!domains.Validate(out string domainError)||domains.TerrainBottomGlobalY!=-8192||domains.ProtectedRoofTopGlobalY!=-7680||domains.LowerHullTopGlobalY!=-21504)throw new InvalidOperationException($"unexpected deep vertical domains: {domainError}");
                int structuralWallTop=source.GetStructuralWallTopGlobalY();
                var cavityBaseline=new RingTerrainSmoothBaseline(finiteSampler,domains.StructuralBottomGlobalY,domains.TerrainBottomGlobalY,domains.ProtectedRoofThicknessMeters,domains.LowerHullTopGlobalY,structuralWallTop);
                if(cavityBaseline.Sample(8,domains.TerrainBottomGlobalY,8).Density>=0||cavityBaseline.Sample(8,domains.TerrainBottomGlobalY-1,8).Density<=0||cavityBaseline.Sample(8,(domains.TerrainBottomGlobalY+domains.LowerHullTopGlobalY)/2,8).Density<=0||cavityBaseline.Sample(8,domains.LowerHullTopGlobalY,8).Density>=0||cavityBaseline.Sample(8,domains.LowerHullTopGlobalY+1,8).Density<=0||cavityBaseline.Sample(8,domains.StructuralBottomGlobalY,8).Density>=0||cavityBaseline.Sample(8,domains.StructuralBottomGlobalY-1,8).Density<=0||cavityBaseline.Sample(8,domains.LowerHullTopGlobalY,8).Material!=(int)SmoothTerrainMaterial.Bedrock)throw new InvalidOperationException("deep cavity SDF/material boundaries failed");
                int cavityY=(domains.TerrainBottomGlobalY+domains.LowerHullTopGlobalY)/2;
                if(cavityBaseline.Sample(8,cavityY,-1).Density>=0||cavityBaseline.Sample(8,cavityY,-32).Density>=0||cavityBaseline.Sample(8,cavityY,-33).Density<=0||cavityBaseline.Sample(8,cavityY,0).Density<=0||cavityBaseline.Sample(8,cavityY,127).Density<=0||cavityBaseline.Sample(8,cavityY,128).Density>=0||cavityBaseline.Sample(8,cavityY,159).Density>=0||cavityBaseline.Sample(8,cavityY,160).Density<=0||cavityBaseline.Sample(8,structuralWallTop+1,-1).Density<=0||cavityBaseline.Sample(8,domains.StructuralBottomGlobalY-1,-1).Density<=0)throw new InvalidOperationException("finite structural sidewall SDF boundaries failed");
                var changedBottom=new RingTerrainSmoothBaseline(finiteSampler,domains.StructuralBottomGlobalY,domains.TerrainBottomGlobalY-32,domains.ProtectedRoofThicknessMeters,domains.LowerHullTopGlobalY);
                var changedRoof=new RingTerrainSmoothBaseline(finiteSampler,domains.StructuralBottomGlobalY,domains.TerrainBottomGlobalY,domains.ProtectedRoofThicknessMeters+32,domains.LowerHullTopGlobalY);
                var changedHull=new RingTerrainSmoothBaseline(finiteSampler,domains.StructuralBottomGlobalY,domains.TerrainBottomGlobalY,domains.ProtectedRoofThicknessMeters,domains.LowerHullTopGlobalY+32);
                var changedStructure=new RingTerrainSmoothBaseline(finiteSampler,domains.StructuralBottomGlobalY-32,domains.TerrainBottomGlobalY,domains.ProtectedRoofThicknessMeters,domains.LowerHullTopGlobalY);
                if(cavityBaseline.Revision==changedBottom.Revision||cavityBaseline.Revision==changedRoof.Revision||cavityBaseline.Revision==changedHull.Revision||cavityBaseline.Revision==changedStructure.Revision)throw new InvalidOperationException("a deep vertical-domain bound did not affect baseline identity");
                if(new RingVerticalDomainConfiguration(domains.TerrainBottomGlobalY-1,domains.ProtectedRoofThicknessMeters,domains.StructuralBottomGlobalY,domains.LowerHullTopGlobalY-domains.StructuralBottomGlobalY).Validate(out _))throw new InvalidOperationException("unaligned deep vertical domain was accepted");
                AssertBoundaryMesh(cavityBaseline,new SmoothTerrainChunkCoordinate(0,RingCoordMath.FloorDiv(domains.TerrainBottomGlobalY-1,SmoothTerrainChunkSnapshot.Cells),1),Vector3.Down,"terrain roof underside");
                AssertBoundaryMesh(cavityBaseline,new SmoothTerrainChunkCoordinate(0,RingCoordMath.FloorDiv(domains.LowerHullTopGlobalY,SmoothTerrainChunkSnapshot.Cells),1),Vector3.Up,"lower hull cavity floor");
                AssertBoundaryMesh(cavityBaseline,new SmoothTerrainChunkCoordinate(0,RingCoordMath.FloorDiv(domains.StructuralBottomGlobalY-1,SmoothTerrainChunkSnapshot.Cells),1),Vector3.Down,"structural underside");
                int cavityChunk=RingCoordMath.FloorDiv(cavityY,SmoothTerrainChunkSnapshot.Cells);
                AssertBoundaryMesh(cavityBaseline,new SmoothTerrainChunkCoordinate(0,cavityChunk,-1),Vector3.Back,"left structural sidewall");
                AssertBoundaryMesh(cavityBaseline,new SmoothTerrainChunkCoordinate(0,cavityChunk,3),Vector3.Forward,"right structural sidewall");
                AssertOpaqueTerrainShader();
                int roofChunk=RingCoordMath.FloorDiv(domains.TerrainBottomGlobalY-1,SmoothTerrainChunkSnapshot.Cells);
                int hullChunk=RingCoordMath.FloorDiv(domains.LowerHullTopGlobalY,SmoothTerrainChunkSnapshot.Cells);
                AssertChunkSeam(cavityBaseline, new SmoothTerrainChunkCoordinate(0,roofChunk,1), new SmoothTerrainChunkCoordinate(1,roofChunk,1), 0, 32, "terrain roof around seam");
                AssertChunkSeam(cavityBaseline, new SmoothTerrainChunkCoordinate(0,hullChunk,1), new SmoothTerrainChunkCoordinate(1,hullChunk,1), 0, 32, "lower hull around seam");
                AssertChunkSeam(cavityBaseline, new SmoothTerrainChunkCoordinate(0,roofChunk,0), new SmoothTerrainChunkCoordinate(0,roofChunk,1), 2, 32, "terrain roof across seam");
                AssertChunkSeam(cavityBaseline, new SmoothTerrainChunkCoordinate(0,hullChunk,0), new SmoothTerrainChunkCoordinate(0,hullChunk,1), 2, 32, "lower hull across seam");
                // These are movement-path tests over the exact production SDF
                // that supplies both the collider mesher and the controller's
                // residency guard. They cover the four ways gravity could
                // otherwise expose exterior space.
                var intactField=new SmoothTerrainVolumetricDensitySnapshot(cavityBaseline,new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(),0,finiteSampler.AroundRingVoxelLength));
                if(!VerticalPathHitsSolid(intactField,8,domains.ProtectedRoofTopGlobalY+2,domains.TerrainBottomGlobalY-2,8))throw new InvalidOperationException("intact roof did not block downward traversal");
                var breachEdits=new SmoothTerrainEditSnapshot(new[]
                {
                    new SmoothTerrainEditCommand(1,80_001,80_001,false,8,-7740,8,64,(int)SmoothTerrainMaterial.Rock),
                    new SmoothTerrainEditCommand(1,80_002,80_002,false,8,-7860,8,64,(int)SmoothTerrainMaterial.Rock),
                    new SmoothTerrainEditCommand(1,80_003,80_003,false,8,-7980,8,64,(int)SmoothTerrainMaterial.Rock),
                    new SmoothTerrainEditCommand(1,80_004,80_004,false,8,-8100,8,64,(int)SmoothTerrainMaterial.Rock),
                    new SmoothTerrainEditCommand(1,80_005,80_005,false,8,-8160,8,64,(int)SmoothTerrainMaterial.Rock)
                },1,finiteSampler.AroundRingVoxelLength);
                var breachField=new SmoothTerrainVolumetricDensitySnapshot(cavityBaseline,breachEdits);
                if(VerticalPathHitsSolid(breachField,8,domains.ProtectedRoofTopGlobalY+2,domains.TerrainBottomGlobalY-2,8)
                    ||!VerticalPathHitsSolid(breachField,8,domains.ProtectedRoofTopGlobalY+2,domains.TerrainBottomGlobalY-2,80))throw new InvalidOperationException("excavated roof breach was not isolated to its opening");
                if(!VerticalPathHitsSolid(intactField,8,domains.LowerHullTopGlobalY+8,domains.LowerHullTopGlobalY-8,8))throw new InvalidOperationException("cavity floor/lower hull did not block downward traversal");
                if(!AcrossPathHitsSolid(intactField,8,cavityY,8,-40)
                    ||!AcrossPathHitsSolid(intactField,8,cavityY,120,168))throw new InvalidOperationException("structural side boundary did not block lateral traversal");
                GD.Print("SMOOTH_TERRAIN_FALL_BOUNDARY_SMOKE_PASS: intact-roof, isolated-breach, cavity-floor, sidewalls");
            }
            var grid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), RadiusAround = 1, RadiusAcross = 1, RadiusVertical = 0, CollisionRadius = 0, MaxWorkers = 2, EnableHotChunkPatches = false }; grid.Name = "Grid"; AddChild(grid);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); grid.SetCanonicalFocus(new RingCanonicalPosition(0, 8, 16));
            await WaitForQuiescence(grid);
            SmoothTerrainGrid.CoverageOwnershipSnapshot initialOwnership = grid.CaptureCoverageOwnershipSnapshot();
            int initialExpectedResident = initialOwnership.ResidentBuildDesired;
            if (initialExpectedResident <= 0
                || grid.ReadyRenderCount != initialExpectedResident
                || grid.ReadyCollisionCount != initialOwnership.CollisionDesired)
                throw new InvalidOperationException($"grid readiness render={grid.ReadyRenderCount}/{initialExpectedResident} collision={grid.ReadyCollisionCount}/{initialOwnership.CollisionDesired}");
            if (!grid.TexturedTerrainMaterialReady) throw new InvalidOperationException("smooth terrain did not bind the triplanar terrain material");
            if (!grid.IsQuiescent || grid.Metrics.Desired != initialExpectedResident || grid.Metrics.Resident != initialExpectedResident || grid.Metrics.Ready != initialExpectedResident || grid.Metrics.Pending != 0 || grid.Metrics.QueueDepth != 0 || grid.Metrics.ActiveWorkers != 0 || grid.Metrics.Pool != 0 || grid.Metrics.EstimatedBytes <= 0) throw new InvalidOperationException($"initial metrics desired={grid.Metrics.Desired} resident={grid.Metrics.Resident} ready={grid.Metrics.Ready} pending={grid.Metrics.Pending} queue={grid.Metrics.QueueDepth} active={grid.Metrics.ActiveWorkers} pool={grid.Metrics.Pool} bytes={grid.Metrics.EstimatedBytes}");
            SmoothTerrainVolumetricDensitySnapshot initialVolumetric=grid.CaptureVolumetricDensitySnapshot()??throw new InvalidOperationException("authoritative volumetric density snapshot was unavailable");
            RingVerticalDomainConfiguration liveDomains=source.GetVerticalDomainConfiguration();int liveCavityY=(liveDomains.TerrainBottomGlobalY+liveDomains.LowerHullTopGlobalY)/2;
            if(!ReferenceEquals(initialVolumetric,grid.CaptureVolumetricDensitySnapshot())
                ||initialVolumetric.Identity.BaselineRevision!=grid.TerrainDefinitionHash
                ||initialVolumetric.Identity.EditRevision!=grid.EditRevision
                ||initialVolumetric.Sample(8,liveDomains.TerrainBottomGlobalY,8).Density>=0
                ||initialVolumetric.Sample(8,liveDomains.TerrainBottomGlobalY-1,8).Density<=0
                ||initialVolumetric.Sample(8,liveCavityY,8).Density<=0
                ||initialVolumetric.Sample(8,liveDomains.LowerHullTopGlobalY,8).Density>=0
                ||initialVolumetric.Sample(8,liveCavityY,-33).Density<=0)
                throw new InvalidOperationException($"production volumetric field omitted structural bounds or identity: {initialVolumetric.Identity}");
            var liveNearKey=new SmoothTerrainVolumetricDensityCacheKey(initialVolumetric.Identity,0,liveDomains.TerrainBottomGlobalY-8,8,8,1);SmoothTerrainVolumetricDensityLattice liveNear=initialVolumetric.CaptureLattice(liveNearKey);
            foreach(int spacing in new[]{2,4,8}){SmoothTerrainVolumetricDensityLattice liveLod=initialVolumetric.CaptureLattice(new SmoothTerrainVolumetricDensityCacheKey(initialVolumetric.Identity,0,liveDomains.TerrainBottomGlobalY-8,8,8/spacing,spacing));for(int z=0;z<=8/spacing;z++)for(int y=0;y<=8/spacing;y++)for(int x=0;x<=8/spacing;x++)if(liveNear.Sample(x*spacing,y*spacing,z*spacing)!=liveLod.Sample(x,y,z))throw new InvalidOperationException($"production volumetric LOD spacing {spacing} diverged at {x}/{y}/{z}");}
            GD.Print($"SMOOTH_TERRAIN_VOLUMETRIC_SDF_LOD_SMOKE_PASS: field={initialVolumetric.Identity.Version:X16}, spacings=1/2/4/8m, roof={liveDomains.TerrainBottomGlobalY}, hull={liveDomains.LowerHullTopGlobalY}");
            var initialLodSnapshot = grid.CaptureLodSurfaceEditSnapshot(); if (initialLodSnapshot.Version != 0 || !ReferenceEquals(initialLodSnapshot, grid.CaptureLodSurfaceEditSnapshot())) throw new InvalidOperationException("LOD edit snapshot was not immutable/cached"); if (!grid.IsSurfaceRegionVisualReady(0, 32, 0, 32) || !grid.IsSurfaceRegionVisualReady(-32, 0, 0, 32) || grid.IsSurfaceRegionVisualReady(64, 96, 0, 32)) throw new InvalidOperationException("smooth visual coverage readiness or seam canonicalization failed"); ulong initialCoverageRevision = grid.VisualCoverageRevision;
            var initialDiagnostics = grid.CaptureDiagnostics(); if (initialDiagnostics.Count != initialExpectedResident) throw new InvalidOperationException($"diagnostic coordinate count {initialDiagnostics.Count}/{initialExpectedResident}"); foreach (var d in initialDiagnostics.Values) if (d.Resident && d.HasRenderMesh && (d.LastInstalledRevision.Value == 0 || d.LastInstalledMeshHash == 0)) throw new InvalidOperationException("rendered diagnostic lacks revision/hash");
            if (grid.Metrics.MaxActiveWorkers < 1 || grid.Metrics.MaxActiveWorkers > grid.MaxWorkers) throw new InvalidOperationException($"worker bound {grid.Metrics.MaxActiveWorkers}");
            var initial = grid.CaptureDiagnostics(); int created = grid.CreatedPresenterCount, reused = grid.ReusedPresenterCount;
            const int terrainVerticalChunk = 51, editVertical = 1640;
            var c0 = new SmoothTerrainChunkCoordinate(0, terrainVerticalChunk, 0); var c1 = new SmoothTerrainChunkCoordinate(1, terrainVerticalChunk, 0); var c2 = new SmoothTerrainChunkCoordinate(2, terrainVerticalChunk, 0); var c3 = new SmoothTerrainChunkCoordinate(3, terrainVerticalChunk, 0);
            grid.SetCanonicalFocus(new RingCanonicalPosition(33, 8, 16)); await WaitForQuiescence(grid);
            if (grid.VisualCoverageRevision <= initialCoverageRevision) throw new InvalidOperationException("visual coverage revision was not monotonic after streaming");
            var moved = grid.CaptureDiagnostics(); foreach (long around in new long[] { 0, 1 }) for (int across = -1; across <= 1; across++) { var c = new SmoothTerrainChunkCoordinate(around, terrainVerticalChunk, across); if (!initial.TryGetValue(c, out var prior) || !moved.TryGetValue(c, out var current)) throw new InvalidOperationException($"missing overlap {c}; initial={string.Join(',', initial.Keys)} moved={string.Join(',', moved.Keys)}"); AssertSameBuild(prior, current, $"overlap {c}"); }
            SmoothTerrainChunkCoordinate[] firstDepartures = initial.Where(pair => pair.Value.Resident && (!moved.TryGetValue(pair.Key,out SmoothTerrainGrid.ChunkDiagnostics state) || !state.Resident)).Select(pair => pair.Key).ToArray();
            SmoothTerrainChunkCoordinate[] firstEntries = moved.Where(pair => pair.Value.Resident && (!initial.TryGetValue(pair.Key,out SmoothTerrainGrid.ChunkDiagnostics state) || !state.Resident)).Select(pair => pair.Key).ToArray();
            if(firstDepartures.Length==0||firstEntries.Length==0)throw new InvalidOperationException($"first move did not replace a spatial boundary: departed={firstDepartures.Length} entered={firstEntries.Length}");
            foreach(SmoothTerrainChunkCoordinate entering in firstEntries)if(moved[entering].ScheduleCount!=1||moved[entering].CompletedBuildCount!=1||moved[entering].InstallCount!=1)throw new InvalidOperationException($"entering {entering} counts {moved[entering]}");
            if (grid.CreatedPresenterCount != created || grid.ReusedPresenterCount != reused + firstEntries.Length || grid.PoolCount != firstDepartures.Length-firstEntries.Length) throw new InvalidOperationException($"first move pool created={grid.CreatedPresenterCount} reused={grid.ReusedPresenterCount} pool={grid.PoolCount}, departed={firstDepartures.Length} entered={firstEntries.Length}");
            grid.SetCanonicalFocus(new RingCanonicalPosition(0, 8, 16)); await WaitForQuiescence(grid);
            var returned = grid.CaptureDiagnostics(); foreach (long around in new long[] { 0, 1 }) for (int across = -1; across <= 1; across++) AssertSameBuild(initial[new SmoothTerrainChunkCoordinate(around, terrainVerticalChunk, across)], returned[new SmoothTerrainChunkCoordinate(around, terrainVerticalChunk, across)], $"return overlap {around}/{across}");
            foreach(SmoothTerrainChunkCoordinate back in firstDepartures){SmoothTerrainGrid.ChunkDiagnostics state=returned[back];if(!state.Resident||state.ScheduleCount!=initial[back].ScheduleCount+1||state.CompletedBuildCount!=initial[back].CompletedBuildCount+1||state.InstallCount!=initial[back].InstallCount+1||state.LastInstalledMeshHash!=initial[back].LastInstalledMeshHash||state.LastInstalledRevision!=initial[back].LastInstalledRevision)throw new InvalidOperationException($"return determinism {back} {state}");}
            foreach(SmoothTerrainChunkCoordinate gone in firstEntries)if(returned[gone].Resident)throw new InvalidOperationException($"second departed {gone} remained resident");
            int expectedReturnReuse=reused+firstEntries.Length+firstDepartures.Length;
            if (grid.CreatedPresenterCount != created || grid.ReusedPresenterCount != expectedReturnReuse || grid.PoolCount != 0) throw new InvalidOperationException($"return pool created={grid.CreatedPresenterCount} reused={grid.ReusedPresenterCount}/{expectedReturnReuse} pool={grid.PoolCount}");
            // Exercise the same public raycast -> brush route used by the gameplay editor.
            Vector3 rayOrigin = source.CanonicalRingPositionToWorld(new RingCanonicalPosition(16, editVertical + 64, 16));
            Vector3 rayBelowTerrain = source.CanonicalRingPositionToWorld(new RingCanonicalPosition(16, editVertical - 64, 16));
            Vector3 rayDirection = (rayBelowTerrain - rayOrigin).Normalized();
            bool raycastAccepted=grid.TryRaycastEditableSurface(rayOrigin,rayDirection,160,out Vector3 rayHit,out Vector3 rayNormal);
            float densityAtHit=float.NaN;SmoothTerrainEditCommand rayBrush=default;
            bool hitDensityAccepted=raycastAccepted&&grid.TryQueryEditableDensityAtWorldPosition(rayHit,out densityAtHit);
            bool brushAccepted=hitDensityAccepted&&MathF.Abs(densityAtHit)<=.1f&&grid.TryApplyNaturalBrushAtWorldPosition(rayHit-rayNormal*.05f,false,2,SmoothTerrainMaterial.Soil,out rayBrush);
            if(!brushAccepted||rayBrush.AddsMatter||rayBrush.Radius!=2)
            {
                grid.TryQueryEditableDensityAtWorldPosition(rayOrigin,out float originDensity);
                grid.TryQueryEditableDensityAtWorldPosition(rayBelowTerrain,out float belowDensity);
                throw new InvalidOperationException($"public smooth raycast-to-excavation route failed: ray={raycastAccepted} hitQuery={hitDensityAccepted} hitDensity={densityAtHit} normal={rayNormal} brush={brushAccepted} originDensity={originDensity} belowDensity={belowDensity}");
            }
            Vector3 brushCenter = source.CanonicalRingPositionToWorld(new RingCanonicalPosition(rayBrush.Around, rayBrush.Vertical, rayBrush.Across));
            if (!grid.TryQueryEditableDensityAtWorldPosition(brushCenter, out float densityAtBrushCenter) || densityAtBrushCenter <= 0)
                throw new InvalidOperationException("public smooth excavation brush did not carve its command center");
            SmoothTerrainVolumetricDensitySnapshot editedVolumetric=grid.CaptureVolumetricDensitySnapshot()??throw new InvalidOperationException("edited volumetric density snapshot was unavailable");
            if(ReferenceEquals(initialVolumetric,editedVolumetric)
                ||editedVolumetric.Identity.EditRevision!=grid.EditRevision
                ||editedVolumetric.Identity==initialVolumetric.Identity
                ||editedVolumetric.Sample(rayBrush.Around,rayBrush.Vertical,rayBrush.Across).Density<=initialVolumetric.Sample(rayBrush.Around,rayBrush.Vertical,rayBrush.Across).Density)
                throw new InvalidOperationException("volumetric density snapshot did not invalidate and compose the authoritative excavation");
            await WaitForQuiescence(grid);
            ulong revisionBeforeInvalid = grid.EditRevision; ThrowsArgument(() => grid.AddEdit(new SmoothTerrainEditCommand(1, 99, 1, false, 32, editVertical, 16, SmoothTerrainEditSnapshot.MaximumStampRadiusMeters + 1, 1)), "oversized edit"); if (grid.EditRevision != revisionBeforeInvalid) throw new InvalidOperationException("oversized edit changed revision");
            grid.AddEdit(new SmoothTerrainEditCommand(1, 100, 1, false, 32, editVertical, 16, 4, 1)); ulong revisionBeforeDuplicate = grid.EditRevision; ThrowsArgument(() => grid.AddEdit(new SmoothTerrainEditCommand(1, 100, 2, false, 32, editVertical, 16, 4, 1)), "duplicate edit"); if (grid.EditRevision != revisionBeforeDuplicate) throw new InvalidOperationException("duplicate edit changed revision");
            var beforeCenter = grid.CaptureDiagnostics(); var editTimer = Stopwatch.StartNew(); grid.AddEdit(new SmoothTerrainEditCommand(1, 201, 3, false, 32, editVertical, 16, 4, 1)); var duringCenter = grid.CaptureDiagnostics(); if (!duringCenter[c0].HasRenderMesh || !duringCenter[c0].HasCollision || !grid.IsSurfaceRegionVisualReady(0, 32, 0, 32)) throw new InvalidOperationException("interactive edit temporarily removed the ready smooth surface"); await WaitForQuiescence(grid); editTimer.Stop(); if (editTimer.Elapsed.TotalMilliseconds > 500.0) throw new InvalidOperationException($"interactive edit replacement exceeded 500 ms: {editTimer.Elapsed.TotalMilliseconds:0.00} ms"); GD.Print($"SMOOTH_TERRAIN_EDIT_LATENCY_MS={editTimer.Elapsed.TotalMilliseconds:0.00}"); var afterCenter = grid.CaptureDiagnostics(); AssertEditScope(beforeCenter, afterCenter, new[] { c0, c1 }, grid.EditRevision, "center edit"); if (!afterCenter[c0].HasRenderMesh || !afterCenter[c0].HasCollision) throw new InvalidOperationException("interactive edit replacement did not install render and collision together");
            var editedLodSnapshot = grid.CaptureLodSurfaceEditSnapshot(); if (editedLodSnapshot.Version != grid.EditRevision || ReferenceEquals(initialLodSnapshot, editedLodSnapshot) || !ReferenceEquals(editedLodSnapshot, grid.CaptureLodSurfaceEditSnapshot())) throw new InvalidOperationException("LOD edit snapshot revision/cache invalidation failed");
            var beforeSeam = grid.CaptureDiagnostics(); grid.AddEdit(new SmoothTerrainEditCommand(1, 202, 4, false, 0, editVertical, 16, 4, 1)); await WaitForQuiescence(grid); var afterSeam = grid.CaptureDiagnostics(); AssertEditScope(beforeSeam, afterSeam, new[] { c3, c0 }, grid.EditRevision, "seam edit");
            if (afterSeam[c0].ScheduleCount != beforeCenter[c0].ScheduleCount + 2) throw new InvalidOperationException("center coordinate did not receive both edit rebuilds");
            var burstTimer = Stopwatch.StartNew(); for (int i = 0; i < 16; i++) grid.AddEdit(new SmoothTerrainEditCommand(1, (ulong)(300 + i), 10 + i, false, 16 + i % 3, editVertical, 16, 2, 1)); double burstQueueMilliseconds=burstTimer.Elapsed.TotalMilliseconds; ulong burstRevision = grid.EditRevision; await WaitForQuiescence(grid); burstTimer.Stop(); var afterBurst = grid.CaptureDiagnostics(); if (afterBurst[c0].LastInstalledRevision.Edits != burstRevision || !afterBurst[c0].HasRenderMesh || !afterBurst[c0].HasCollision) throw new InvalidOperationException($"rapid edit burst did not install its final revision {burstRevision}: {afterBurst[c0]}"); if(grid.RetainedEditRevisionHistoryCountForTests!=0)throw new InvalidOperationException($"surface edit burst retained unnecessary coarse revision history: {grid.RetainedEditRevisionHistoryCountForTests}"); if (burstTimer.Elapsed.TotalMilliseconds > 500.0) throw new InvalidOperationException($"rapid 16-edit burst exceeded 500 ms: total={burstTimer.Elapsed.TotalMilliseconds:0.00} ms queue={burstQueueMilliseconds:0.00} ms"); GD.Print($"SMOOTH_TERRAIN_16_EDIT_BURST_MS={burstTimer.Elapsed.TotalMilliseconds:0.00} queueMs={burstQueueMilliseconds:0.00}");
            if (!grid.IsQuiescent || grid.Metrics.Pending != 0 || grid.Metrics.ActiveWorkers != 0) throw new InvalidOperationException("edit queue did not drain");
            // Keep a completed result queued, then make its coordinate leave the desired window.
            var staleBefore = grid.CaptureDiagnostics()[c0]; int globalStale = grid.Metrics.StaleResults; grid.SuspendInstallsForTests = true; if (!grid.RebuildCoordinateForTests(c0)) throw new InvalidOperationException("stale rebuild was rejected"); for (int i = 0; i < 360 && grid.ReadyResultCount < 1; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); if (grid.ReadyResultCount < 1) throw new InvalidOperationException("stale result did not queue"); var queuedTelemetry=grid.CaptureCaveLodTelemetry(); if(queuedTelemetry.Base.QueuedReady<1||queuedTelemetry.PendingReadyResults<1||queuedTelemetry.QueuedReadyBytes<=0||queuedTelemetry.TotalEstimatedBytes<queuedTelemetry.ResidentAndPoolBytes)throw new InvalidOperationException($"queued smooth telemetry missing: {queuedTelemetry}"); grid.SetCanonicalFocus(new RingCanonicalPosition(65, 8, 16)); grid.SuspendInstallsForTests = false; await WaitForQuiescence(grid); var drainedTelemetry=grid.CaptureCaveLodTelemetry(); if(drainedTelemetry.Base.QueuedReady!=0||drainedTelemetry.PendingReadyResults!=0||drainedTelemetry.QueuedReadyBytes!=0||drainedTelemetry.PeakEstimatedBytes<queuedTelemetry.TotalEstimatedBytes)throw new InvalidOperationException($"queued smooth telemetry did not drain/peak: before={queuedTelemetry}, after={drainedTelemetry}"); var staleAfter = grid.CaptureDiagnostics()[c0]; if (staleAfter.InstallCount != staleBefore.InstallCount || staleAfter.StaleResultCount != staleBefore.StaleResultCount + 1 || grid.Metrics.StaleResults != globalStale + 1) throw new InvalidOperationException($"stale result installed/counts before={staleBefore} after={staleAfter} global={grid.Metrics.StaleResults}");
            // The same gate is used by ordinary workers; cancelling a gated generation must not build/install it.
            var gateCoordinate = new SmoothTerrainChunkCoordinate(1, terrainVerticalChunk, 0); var gateBefore = grid.CaptureDiagnostics()[gateCoordinate]; long cancellationsBefore=grid.CaptureCaveLodTelemetry().CancellationRequests; grid.PauseWorkersForTests(); if (!grid.RebuildCoordinateForTests(gateCoordinate)) throw new InvalidOperationException("gate rebuild rejected"); for (int i = 0; i < 360 && grid.Metrics.ActiveWorkers < 1; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); if (!grid.RebuildCoordinateForTests(gateCoordinate)) throw new InvalidOperationException("gate cancellation rebuild rejected"); grid.ReleaseWorkersForTests(); await WaitForQuiescence(grid); var gateAfter = grid.CaptureDiagnostics()[gateCoordinate]; var cancellationTelemetry=grid.CaptureCaveLodTelemetry(); if (gateAfter.CancelRequestCount != gateBefore.CancelRequestCount + 1 || cancellationTelemetry.CancellationRequests<=cancellationsBefore || gateAfter.CompletedBuildCount != gateBefore.CompletedBuildCount + 1 || gateAfter.InstallCount != gateBefore.InstallCount + 1) throw new InvalidOperationException($"gated cancellation counts before={gateBefore} after={gateAfter}, telemetry={cancellationTelemetry}");
            grid.ResetMetricPeaks(); if (grid.Metrics.MaxActiveWorkers != 0 || grid.Metrics.MaxInstallMilliseconds != 0) throw new InvalidOperationException("metric reset failed"); if (!grid.RebuildCoordinateForTests(gateCoordinate)) throw new InvalidOperationException("peak rebuild rejected"); await WaitForQuiescence(grid); if (grid.Metrics.MaxActiveWorkers < 1 || grid.Metrics.MaxWorkerMilliseconds <= 0 || grid.Metrics.MaxInstallMilliseconds <= 0 || grid.Metrics.Pending != 0 || grid.Metrics.QueueDepth != 0 || grid.Metrics.ActiveWorkers != 0) throw new InvalidOperationException($"metric peaks workers={grid.Metrics.MaxActiveWorkers}/{grid.Metrics.MaxWorkerMilliseconds} install={grid.Metrics.MaxInstallMilliseconds}");
            var reanchorBefore = grid.CaptureDiagnostics(); var positionsBefore = grid.CapturePresenterPositionsForTests(); source.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(16, 8, 16)); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); var positionsAnchored = grid.CapturePresenterPositionsForTests(); bool movedPosition = false; foreach (var pair in positionsBefore) if (positionsAnchored.TryGetValue(pair.Key, out var next) && !pair.Value.IsEqualApprox(next)) { movedPosition = true; break; }
            if (!movedPosition || !grid.IsQuiescent) throw new InvalidOperationException("reanchor did not reposition quiescent presenters"); foreach (var pair in reanchorBefore) AssertSameBuild(pair.Value, grid.CaptureDiagnostics()[pair.Key], $"reanchor {pair.Key}"); source.ClearFloatingOriginCanonicalAnchor(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); var positionsRestored = grid.CapturePresenterPositionsForTests(); foreach (var pair in positionsBefore) if (!positionsRestored[pair.Key].IsEqualApprox(pair.Value)) throw new InvalidOperationException("reanchor restore changed presenter position");
            grid.AddEdit(new SmoothTerrainEditCommand(1, 203, 30, true, 65, editVertical + 40, 16, 4, (int)SmoothTerrainMaterial.Rock)); await WaitForQuiescence(grid); var raisedCoordinate = new SmoothTerrainChunkCoordinate(2, RingCoordMath.FloorDiv(editVertical + 43, 32), 0); var raisedDiagnostics = grid.CaptureDiagnostics(); if (!raisedDiagnostics.TryGetValue(raisedCoordinate, out var raised) || !raised.Resident || !raised.HasRenderMesh) { int projected = grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(source.TerrainSampler!, 64, 16); throw new InvalidOperationException($"edited surface did not recenter vertical streaming expected={raisedCoordinate} projected={projected} residentKeys={string.Join(',', raisedDiagnostics.Where(p => p.Value.Resident).Select(p => p.Key))} state={raised}"); }
            var journal = grid.CaptureJournalEnvelope(); string journalPath = SmoothTerrainEditJournalStore.PreparePathOnMainThread($"res://.godot/smooth_terrain_runtime_smoke_journal_{Guid.NewGuid():N}.json"); var savedJournal = await SmoothTerrainEditJournalStore.SaveSnapshotAsync(journalPath, journal); if (!savedJournal.Success) throw new InvalidOperationException($"journal file save: {savedJournal.Error}"); var loadedJournal = SmoothTerrainEditJournalStore.LoadSnapshot(journalPath, journal.TerrainDefinitionHash, journal.CircumferenceMeters); if (!loadedJournal.Success || loadedJournal.Envelope.Commands.Length != journal.Commands.Length || SmoothTerrainEditJournalStore.LoadSnapshot(journalPath, journal.TerrainDefinitionHash + 1, journal.CircumferenceMeters).Success) throw new InvalidOperationException("journal file load/compatibility failed"); System.IO.File.Delete(journalPath); var journalGrid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), RadiusAround = 1, RadiusAcross = 1, RadiusVertical = 0, CollisionRadius = 0, MaxWorkers = 1 }; AddChild(journalGrid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); journalGrid.SetCanonicalFocus(new RingCanonicalPosition(65, 8, 16)); await WaitForQuiescence(journalGrid); if (!journalGrid.RestoreJournalEnvelope(journal)) throw new InvalidOperationException("valid journal restore rejected"); await WaitForQuiescence(journalGrid); var restored = journalGrid.CaptureJournalEnvelope(); if (restored.Revision != journal.Revision || restored.NextCommandId != journal.NextCommandId || restored.NextSequence != journal.NextSequence || restored.Commands.Length != journal.Commands.Length) throw new InvalidOperationException("journal restore envelope mismatch"); for (int i = 0; i < journal.Commands.Length; i++) if (restored.Commands[i] != journal.Commands[i]) throw new InvalidOperationException("journal restore command mismatch"); var legacyJournal=journal with { TerrainDefinitionHash=journalGrid.LegacyTerrainDefinitionHash }; var preCavityJournal=journal with { TerrainDefinitionHash=journalGrid.PreCavityLegacyTerrainDefinitionHash }; if(!journalGrid.RestoreJournalEnvelope(legacyJournal)||!journalGrid.RestoreJournalEnvelope(preCavityJournal)||journalGrid.CaptureJournalEnvelope().TerrainDefinitionHash!=journal.TerrainDefinitionHash)throw new InvalidOperationException("safe legacy journal migration failed"); var oldCommands=(SmoothTerrainEditCommand[])journal.Commands.Clone();oldCommands[0]=oldCommands[0] with { Vertical=journalGrid.TerrainBottomVertical-1 }; var unsafeLegacy=preCavityJournal with { Commands=oldCommands }; var beforeBad = journalGrid.CaptureJournalEnvelope(); var debugBad = journalGrid.CaptureDiagnostics(); var badNext = journal with { NextCommandId = 1 }; if (journalGrid.RestoreJournalEnvelope(badNext) || journalGrid.RestoreJournalEnvelope(unsafeLegacy) || journalGrid.RestoreJournalEnvelope(journal with { NextSequence = 1 }) || journalGrid.RestoreJournalEnvelope(journal with { TerrainDefinitionHash = journal.TerrainDefinitionHash + 1 })) throw new InvalidOperationException("invalid journal restore accepted"); var afterBad = journalGrid.CaptureJournalEnvelope(); if (afterBad.Revision != beforeBad.Revision || afterBad.NextCommandId != beforeBad.NextCommandId || afterBad.NextSequence != beforeBad.NextSequence || afterBad.Commands.Length != beforeBad.Commands.Length || journalGrid.CaptureDiagnostics().Count != debugBad.Count) throw new InvalidOperationException("invalid journal restore mutated grid"); Task journalShutdown = journalGrid.AwaitShutdownForTestsAsync(); journalGrid.QueueFree(); for (int i = 0; i < 360 && !journalShutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await journalShutdown;
            Task shutdown = grid.AwaitShutdownForTestsAsync(); grid.QueueFree(); for (int i = 0; i < 360 && !shutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); if (!shutdown.IsCompleted) throw new InvalidOperationException("shutdown timeout"); await shutdown; await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); if (grid.TaskCount != 0 || grid.ReadyResultCount != 0 || grid.PresenterCount != 0 || grid.Metrics.ActiveWorkers != 0 || grid.Metrics.Resident != 0 || grid.Metrics.Pool != 0 || grid.Metrics.EstimatedBytes != 0 || grid.Metrics.Desired != 0 || grid.Metrics.Pending != 0) throw new InvalidOperationException($"shutdown cleanup task={grid.TaskCount} readyResult={grid.ReadyResultCount} presenter={grid.PresenterCount} active={grid.Metrics.ActiveWorkers} resident={grid.Metrics.Resident} pool={grid.Metrics.Pool} bytes={grid.Metrics.EstimatedBytes} desired={grid.Metrics.Desired} pending={grid.Metrics.Pending}");
            var collisionGrid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), RadiusAround = 1, RadiusAcross = 1, RadiusVertical = 1, CollisionRadius = 0, MaxWorkers = 1 }; AddChild(collisionGrid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); collisionGrid.SetCanonicalFocus(new RingCanonicalPosition(0, 8, 16)); for (int i = 0; i < 720 && collisionGrid.ReadyRenderCount == 0; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); if (collisionGrid.ReadyRenderCount == 0 || collisionGrid.ReadyCollisionCount == 0) throw new InvalidOperationException($"collision-owned center was not installed first: render={collisionGrid.ReadyRenderCount} collision={collisionGrid.ReadyCollisionCount}"); await WaitForQuiescence(collisionGrid); SmoothTerrainGrid.CoverageOwnershipSnapshot collisionInitialOwnership=collisionGrid.CaptureCoverageOwnershipSnapshot();if(collisionGrid.ReadyRenderCount!=collisionInitialOwnership.ResidentBuildDesired||collisionGrid.ReadyCollisionCount!=collisionInitialOwnership.CollisionDesired)throw new InvalidOperationException($"collision initial ready={collisionGrid.ReadyRenderCount}/{collisionInitialOwnership.ResidentBuildDesired} collision={collisionGrid.ReadyCollisionCount}/{collisionInitialOwnership.CollisionDesired}");
            AssertCollisionColumn(collisionGrid, 0, 0); collisionGrid.SetCanonicalFocus(new RingCanonicalPosition(33, 8, 16)); await WaitForQuiescence(collisionGrid); AssertCollisionColumn(collisionGrid, 1, 0); collisionGrid.SetCanonicalFocus(new RingCanonicalPosition(33, 8, 48)); await WaitForQuiescence(collisionGrid); AssertCollisionColumn(collisionGrid, 1, 1); int centerCollisionCount=collisionGrid.ReadyCollisionCount;collisionGrid.CollisionRadius = 1; await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await WaitForQuiescence(collisionGrid);SmoothTerrainGrid.CoverageOwnershipSnapshot expandedCollisionOwnership=collisionGrid.CaptureCoverageOwnershipSnapshot();if(collisionGrid.ReadyCollisionCount<=centerCollisionCount||expandedCollisionOwnership.CollisionDesired<=collisionInitialOwnership.CollisionDesired||collisionGrid.CaptureDiagnostics().Any(pair=>pair.Value.Resident&&pair.Value.HasCollision&&!collisionGrid.IsCollisionCoverageOwnerForTests(pair.Key)))throw new InvalidOperationException($"collision radius one coverage ready={collisionGrid.ReadyCollisionCount}, desired={expandedCollisionOwnership.CollisionDesired}, center={centerCollisionCount}"); collisionGrid.CollisionRadius = 0; await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);await WaitForQuiescence(collisionGrid);AssertCollisionColumn(collisionGrid,1,1); Task collisionShutdown = collisionGrid.AwaitShutdownForTestsAsync(); collisionGrid.QueueFree(); for (int i = 0; i < 360 && !collisionShutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await collisionShutdown;
            // Ownership regression: visual radius is intentionally smaller
            // than collision radius. The collision bubble must still receive
            // its own resident parents, and hot edit subdivision must not
            // redefine either visual or collision ownership set.
            var ownershipGrid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), RadiusAround = 0, RadiusAcross = 0, RadiusVertical = 0, CollisionRadius = 1, EnableFocusCenteredCollisionCoverage = true, MaxWorkers = 1, EnableHotChunkPatches = false };
            AddChild(ownershipGrid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            ownershipGrid.SetCanonicalFocus(new RingCanonicalPosition(8, editVertical, 8)); await WaitForQuiescence(ownershipGrid);
            SmoothTerrainGrid.CoverageOwnershipContract ownershipContract = ownershipGrid.CaptureCoverageOwnershipContract();
            SmoothTerrainGrid.CoverageOwnershipSnapshot ownershipBeforeHot = ownershipGrid.CaptureCoverageOwnershipSnapshot();
            var collisionOnly = new SmoothTerrainChunkCoordinate(1, terrainVerticalChunk, 0);
            if (!ownershipContract.IsValid || ownershipContract.VisualRadiusAround != 0 || ownershipContract.CollisionRadius != 1
                || ownershipBeforeHot.CollisionDesired <= ownershipBeforeHot.VisualDesired
                || !ownershipGrid.IsCollisionCoverageOwnerForTests(collisionOnly)
                || ownershipGrid.IsVisualCoverageOwnerForTests(collisionOnly)
                || ownershipGrid.IsVisualRepresentationVisibleForTests(collisionOnly)
                || ownershipGrid.IsEditSubdivisionOwnerForTests(collisionOnly))
                throw new InvalidOperationException($"independent visual/collision ownership regressed: contract={ownershipContract}, snapshot={ownershipBeforeHot}, collisionOnly={collisionOnly}");
            ownershipGrid.EnableHotChunkPatches = true; await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await WaitForQuiescence(ownershipGrid);
            SmoothTerrainGrid.CoverageOwnershipSnapshot ownershipAfterHot = ownershipGrid.CaptureCoverageOwnershipSnapshot();
            if (ownershipAfterHot.EditSubdivisionDesired != 8
                || ownershipAfterHot.VisualDesired != ownershipBeforeHot.VisualDesired
                || ownershipAfterHot.CollisionDesired != ownershipBeforeHot.CollisionDesired
                || ownershipGrid.IsVisualRepresentationVisibleForTests(collisionOnly)
                || !ownershipGrid.IsEditSubdivisionOwnerForTests(new SmoothTerrainChunkCoordinate(0, terrainVerticalChunk, 0)))
                throw new InvalidOperationException($"hot edit topology leaked into visual/collision ownership: before={ownershipBeforeHot}, after={ownershipAfterHot}");
            GD.Print($"SMOOTH_TERRAIN_COVERAGE_OWNERSHIP_SMOKE_PASS: visual={ownershipAfterHot.VisualDesired}, collision={ownershipAfterHot.CollisionDesired}, editParents={ownershipAfterHot.EditSubdivisionDesired}, resident={ownershipAfterHot.ResidentBuildDesired}");
            Task ownershipShutdown = ownershipGrid.AwaitShutdownForTestsAsync(); ownershipGrid.QueueFree(); for (int i = 0; i < 360 && !ownershipShutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ownershipShutdown;
            // Near volumetric visibility follows actual 3D depth, is full 1 m
            // parent geometry, and never impersonates surface proxy coverage
            // or the 2x2x2 edit subdivision ownership.
            var deepVisualGrid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), RadiusAround = 0, RadiusAcross = 0, RadiusVertical = 0, CollisionRadius = 0, EnableFocusCenteredCollisionCoverage = true, EnableFocusCenteredVisualCoverage = true, FocusVisualRadiusAround = 0, FocusVisualRadiusAcross = 0, FocusVisualRadiusVertical = 0, EnableHotChunkPatches = false, MaxWorkers = 1 };
            AddChild(deepVisualGrid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // The protected roof has a guaranteed authoritative solid/air
            // boundary at deep negative Y, exercising actual mesh/collider install.
            deepVisualGrid.SetCanonicalFocus(new RingCanonicalPosition(8, -8180, 8)); await WaitForQuiescence(deepVisualGrid);
            var deepParent = new SmoothTerrainChunkCoordinate(0, -256, 0);
            SmoothTerrainGrid.NearVolumetricMeshContract nearContract = deepVisualGrid.CaptureNearVolumetricMeshContract();
            SmoothTerrainGrid.CoverageOwnershipSnapshot deepOwnership = deepVisualGrid.CaptureCoverageOwnershipSnapshot();
            SmoothTerrainGrid.ChunkDiagnostics deepState=default;
            var deepMeshParent=new[]{ new SmoothTerrainChunkCoordinate(0,-257,0), deepParent }
                .FirstOrDefault(candidate=>deepVisualGrid.CaptureDiagnostics().TryGetValue(candidate,out SmoothTerrainGrid.ChunkDiagnostics state)&&state.HasRenderMesh&&state.HasCollision);
            if (!nearContract.IsValid || !deepVisualGrid.IsNearVolumetricVisualCoverageOwnerForTests(deepParent)
                || !deepVisualGrid.IsVisualRepresentationVisibleForTests(deepParent)
                || !deepVisualGrid.IsCollisionCoverageOwnerForTests(deepParent)
                || deepMeshParent==default || !deepVisualGrid.CaptureDiagnostics().TryGetValue(deepMeshParent, out deepState)
                || deepVisualGrid.IsEditSubdivisionOwnerForTests(deepMeshParent)
                || deepVisualGrid.IsSurfaceVisualCoverageOwnerForTests(deepParent))
                throw new InvalidOperationException($"deep near volumetric ownership regressed: near={nearContract}, ownership={deepOwnership}, state={deepState}");
            // Moving only vertically across a 32 m parent boundary must refresh
            // the focus-centred set even when around/across are unchanged.
            deepVisualGrid.SetCanonicalFocus(new RingCanonicalPosition(8, -8148, 8)); await WaitForQuiescence(deepVisualGrid);
            var movedDeepParent = new SmoothTerrainChunkCoordinate(0, -255, 0);
            if (!deepVisualGrid.IsNearVolumetricVisualCoverageOwnerForTests(movedDeepParent)
                || !deepVisualGrid.IsVisualRepresentationVisibleForTests(movedDeepParent))
                throw new InvalidOperationException("vertical-only focus movement did not refresh near volumetric visibility");
            // Enabling the focus-centred cave tier must not revive local voxel
            // residency in space. Altitude suspension owns that outer bound.
            int deepSurface=source.TerrainSampler!.GetSurfaceHeight(8,8);
            deepVisualGrid.SetCanonicalFocus(new RingCanonicalPosition(8,deepSurface+deepVisualGrid.SurfaceResidencyExitAltitudeMeters+1.0,8));
            await WaitForQuiescence(deepVisualGrid);
            if(deepVisualGrid.SurfaceResidencyActive||deepVisualGrid.Metrics.Desired!=0||deepVisualGrid.Metrics.Resident!=0)
                throw new InvalidOperationException($"near volumetric coverage remained resident in space: active={deepVisualGrid.SurfaceResidencyActive} desired={deepVisualGrid.Metrics.Desired} resident={deepVisualGrid.Metrics.Resident}");
            GD.Print($"SMOOTH_TERRAIN_NEAR_VOLUMETRIC_SMOKE_PASS: near={deepOwnership.NearVolumetricVisualDesired}, collision={deepOwnership.CollisionDesired}, parent={deepParent}, spaceResident={deepVisualGrid.Metrics.Resident}");
            Task deepVisualShutdown = deepVisualGrid.AwaitShutdownForTestsAsync(); deepVisualGrid.QueueFree(); for (int i = 0; i < 360 && !deepVisualShutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await deepVisualShutdown;
            // Medium cave tier is an independent 64 m / 2 m visual sector:
            // it must follow depth, never create physics, and unload in space.
            var mediumGrid=new SmoothTerrainGrid { SourceGridPath=new NodePath("../Source"), RadiusAround=0,RadiusAcross=0,RadiusVertical=0,CollisionRadius=0,EnableMediumVolumetricVisualCoverage=true,MediumVisualRadiusAround=0,MediumVisualRadiusAcross=0,MediumVisualRadiusVertical=0,EnableHotChunkPatches=false,MaxWorkers=1};
            AddChild(mediumGrid);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            mediumGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-8180,8));await WaitForQuiescence(mediumGrid);
            var mediumParent=new SmoothTerrainChunkCoordinate(0,-256,0);var mediumContract=mediumGrid.CaptureMediumVolumetricMeshContract();var mediumMeshParent=new[]{new SmoothTerrainChunkCoordinate(0,-258,0),mediumParent}.FirstOrDefault(mediumGrid.IsMediumVolumetricVisibleForTests);
            if(!mediumContract.IsValid||!mediumGrid.IsMediumVolumetricVisualCoverageOwnerForTests(mediumParent)||mediumMeshParent==default||mediumGrid.HasMediumVolumetricCollisionForTests(mediumMeshParent)||mediumGrid.IsNearVolumetricVisualCoverageOwnerForTests(mediumMeshParent)||mediumGrid.IsSurfaceVisualCoverageOwnerForTests(mediumParent)||mediumGrid.IsEditSubdivisionOwnerForTests(mediumMeshParent))throw new InvalidOperationException($"medium volumetric ownership regressed: contract={mediumContract}, mesh={mediumMeshParent}");
            // Requested near ownership must not punch a hole through an
            // already-installed medium boundary while its replacement meshes
            // are still pending. Handoff happens only after all child parents
            // are visibly ready, and reverses immediately when near releases.
            mediumGrid.PauseWorkersForTests();mediumGrid.EnableFocusCenteredVisualCoverage=true;mediumGrid.FocusVisualRadiusAround=1;mediumGrid.FocusVisualRadiusAcross=1;mediumGrid.FocusVisualRadiusVertical=2;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(!mediumGrid.IsMediumVolumetricVisibleForTests(mediumMeshParent))throw new InvalidOperationException("medium sector hid before pending near replacement was ready");
            mediumGrid.ReleaseWorkersForTests();await WaitForQuiescence(mediumGrid);
            if(mediumGrid.IsMediumVolumetricVisibleForTests(mediumMeshParent))throw new InvalidOperationException("medium sector remained visible after fully-ready near handoff");
            mediumGrid.EnableFocusCenteredVisualCoverage=false;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(!mediumGrid.IsMediumVolumetricVisibleForTests(mediumMeshParent))throw new InvalidOperationException("medium sector did not return when near ownership released");
            ulong mediumBeforeDistantEdit=mediumGrid.CaptureMediumVolumetricIdentityForTests(mediumParent)?.EditRevision??ulong.MaxValue;mediumGrid.AddEdit(new SmoothTerrainEditCommand(1,9001,9001,true,8,-10000,8,1,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(mediumGrid);
            if(mediumGrid.CaptureMediumVolumetricIdentityForTests(mediumParent)?.EditRevision!=mediumBeforeDistantEdit||mediumGrid.EditRevision==mediumBeforeDistantEdit||mediumGrid.RetainedEditRevisionHistoryCountForTests!=0)throw new InvalidOperationException("distant edit rebuilt an unaffected medium sector or retained validated edit history");
            mediumGrid.AddEdit(new SmoothTerrainEditCommand(1,9002,9002,true,8,-8180,8,1,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(mediumGrid);
            if(mediumGrid.CaptureMediumVolumetricIdentityForTests(mediumParent)?.EditRevision!=mediumGrid.EditRevision||mediumGrid.EditRevision==mediumBeforeDistantEdit||mediumGrid.RetainedEditRevisionHistoryCountForTests!=0)throw new InvalidOperationException("overlapping edit did not replace its medium density identity or release edit history");
            mediumGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-8116,8));await WaitForQuiescence(mediumGrid);var movedMediumParent=new SmoothTerrainChunkCoordinate(0,-254,0);
            if(!mediumGrid.IsMediumVolumetricVisualCoverageOwnerForTests(movedMediumParent)||mediumGrid.IsMediumVolumetricVisualCoverageOwnerForTests(new SmoothTerrainChunkCoordinate(0,-258,0)))throw new InvalidOperationException("vertical-only focus movement did not refresh medium volumetric ownership");
            // A sector comfortably above the local surface remains inside the
            // residency band but is uniformly empty. It must install as an
            // intentional render-only rejection, not spend work meshing an
            // empty 64 m volume or create collision.
            int mediumSurface=source.TerrainSampler!.GetSurfaceHeight(8,8);mediumGrid.SetCanonicalFocus(new RingCanonicalPosition(8,mediumSurface+256,8));await WaitForQuiescence(mediumGrid);
            SmoothTerrainChunkCoordinate rejectedMedium=mediumGrid.CaptureCoverageOwnershipSnapshot().MediumVolumetricVisualDesired>0
                ? new[]{new SmoothTerrainChunkCoordinate(0,RingCoordMath.FloorDiv(mediumSurface+256,64)*2,0),new SmoothTerrainChunkCoordinate(0,(RingCoordMath.FloorDiv(mediumSurface+256,64)-1)*2,0)}.FirstOrDefault(c=>mediumGrid.CaptureMediumVolumetricClassificationForTests(c)==SmoothTerrainDensityClassification.Empty)
                : default;
            int uniformRejectedBeforeSpace=mediumGrid.MediumUniformRejectedCountForTests;
            if(rejectedMedium==default||uniformRejectedBeforeSpace==0||mediumGrid.IsMediumVolumetricVisibleForTests(rejectedMedium)||mediumGrid.HasMediumVolumetricCollisionForTests(rejectedMedium))throw new InvalidOperationException($"medium uniform rejection failed: sector={rejectedMedium}, rejected={uniformRejectedBeforeSpace}, classification={mediumGrid.CaptureMediumVolumetricClassificationForTests(rejectedMedium)}");
            mediumGrid.SetCanonicalFocus(new RingCanonicalPosition(8,mediumSurface+mediumGrid.SurfaceResidencyExitAltitudeMeters+1.0,8));await WaitForQuiescence(mediumGrid);
            if(mediumGrid.SurfaceResidencyActive||mediumGrid.IsMediumVolumetricVisualCoverageOwnerForTests(movedMediumParent)||mediumGrid.IsMediumVolumetricVisibleForTests(movedMediumParent))throw new InvalidOperationException("medium volumetric coverage remained resident in space");
            GD.Print($"SMOOTH_TERRAIN_MEDIUM_VOLUMETRIC_SMOKE_PASS: spacing={mediumContract.SampleSpacingMeters}m, sector={mediumContract.SectorExtentMeters}m, collision={mediumGrid.HasMediumVolumetricCollisionForTests(mediumParent)}, uniformRejected={uniformRejectedBeforeSpace}");
            Task mediumShutdown=mediumGrid.AwaitShutdownForTestsAsync();mediumGrid.QueueFree();for(int i=0;i<360&&!mediumShutdown.IsCompleted;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await mediumShutdown;
            // Coarse megacavern tier: 2 km / 64 m render-only sectors are
            // deliberately gated below the local surface so they cannot
            // duplicate the established surface LODs.
            var coarseGrid=new SmoothTerrainGrid { SourceGridPath=new NodePath("../Source"),RadiusAround=0,RadiusAcross=0,RadiusVertical=0,CollisionRadius=0,EnableCoarseVolumetricVisualCoverage=true,CoarseVisualRadiusAround=0,CoarseVisualRadiusAcross=0,CoarseVisualRadiusVertical=0,CoarseUndergroundActivationDepthMeters=256,EnableHotChunkPatches=false,MaxWorkers=1};
            AddChild(coarseGrid);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            int coarseSurface=source.TerrainSampler!.GetSurfaceHeight(8,8);coarseGrid.SetCanonicalFocus(new RingCanonicalPosition(8,coarseSurface,8));await WaitForQuiescence(coarseGrid);
            if(coarseGrid.CaptureCoverageOwnershipSnapshot().CoarseVolumetricVisualDesired!=0)throw new InvalidOperationException("coarse tier activated at the surface");
            coarseGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-8180,8));await WaitForQuiescence(coarseGrid);
            var coarseContract=coarseGrid.CaptureCoarseVolumetricMeshContract();var coarseCandidates=new[]{new SmoothTerrainChunkCoordinate(0,-256,0),new SmoothTerrainChunkCoordinate(0,-320,0)};SmoothTerrainChunkCoordinate? coarseMesh=coarseCandidates.Where(c=>coarseGrid.IsCoarseVolumetricVisibleForTests(c)).Select(c=>(SmoothTerrainChunkCoordinate?)c).FirstOrDefault();
            if(!coarseContract.IsValid||coarseGrid.CaptureCoverageOwnershipSnapshot().CoarseVolumetricVisualDesired==0||coarseMesh is null||coarseGrid.HasCoarseVolumetricCollisionForTests(coarseMesh.Value)||coarseGrid.IsSurfaceVisualCoverageOwnerForTests(coarseMesh.Value)||coarseGrid.IsNearVolumetricVisualCoverageOwnerForTests(coarseMesh.Value)||coarseGrid.IsMediumVolumetricVisualCoverageOwnerForTests(coarseMesh.Value)||coarseGrid.IsEditSubdivisionOwnerForTests(coarseMesh.Value))throw new InvalidOperationException($"coarse ownership/contract regressed: contract={coarseContract}, mesh={coarseMesh}");
            var coarseSidewall=new SmoothTerrainChunkCoordinate(0,coarseMesh.Value.VerticalChunk,-64);
            if(!coarseGrid.IsCoarseVolumetricVisualCoverageOwnerForTests(coarseSidewall)||!coarseGrid.IsCoarseVolumetricVisibleForTests(coarseSidewall)||coarseGrid.HasCoarseVolumetricCollisionForTests(coarseSidewall))throw new InvalidOperationException("coarse external sidewall ownership regressed");
            coarseGrid.CoarseVisualRadiusAround=1;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await WaitForQuiescence(coarseGrid);long coarseSectorCount=Math.Max(1,(long)Math.Ceiling(source.CaptureLodGeometrySnapshot().CircumferenceMeters/2048.0));var coarseWrappedPrevious=new SmoothTerrainChunkCoordinate((coarseSectorCount-1)*64,coarseMesh.Value.VerticalChunk,0);
            if(!coarseGrid.IsCoarseVolumetricVisualCoverageOwnerForTests(coarseWrappedPrevious))throw new InvalidOperationException("coarse around-ring wrapping regressed");
            var coarseIdentitiesBeforeLocalEdit=coarseGrid.CaptureCoarseVolumetricCoordinatesForTests().ToDictionary(c=>c,c=>coarseGrid.CaptureCoarseVolumetricIdentityForTests(c));
            coarseGrid.AddEdit(new SmoothTerrainEditCommand(1,9100,9100,true,8,-8180,8,1,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(coarseGrid);
            var changedCoarseSnapshots=coarseIdentitiesBeforeLocalEdit.Where(pair=>coarseGrid.CaptureCoarseVolumetricIdentityForTests(pair.Key)?.EditRevision!=pair.Value?.EditRevision).Select(pair=>pair.Key).ToArray();
            if(changedCoarseSnapshots.Length==0||changedCoarseSnapshots.Any(c=>c.AroundChunk!=0))throw new InvalidOperationException($"local edit rebuilt an unexpected coarse sector: changed={string.Join(',',changedCoarseSnapshots)}");
            coarseGrid.CoarseVisualRadiusAround=0;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await WaitForQuiescence(coarseGrid);
            var coarseOwner=coarseMesh.Value;ulong coarseBeforeEdit=coarseGrid.CaptureCoarseVolumetricIdentityForTests(coarseOwner)?.EditRevision??ulong.MaxValue;coarseGrid.AddEdit(new SmoothTerrainEditCommand(1,9101,9101,true,8,-8180,8,1,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(coarseGrid);
            if(coarseGrid.CaptureCoarseVolumetricIdentityForTests(coarseOwner)?.EditRevision!=coarseGrid.EditRevision||coarseGrid.EditRevision==coarseBeforeEdit)throw new InvalidOperationException("coarse sector did not replace density identity after edit");
            coarseGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-6130,8));await WaitForQuiescence(coarseGrid);var movedCoarse=new SmoothTerrainChunkCoordinate(0,-192,0);
            // The one-sector downward owner halo intentionally retains the
            // prior sector at this boundary; the new sector is the proof that
            // vertical-only movement refreshed the coarse focus window.
            if(!coarseGrid.IsCoarseVolumetricVisualCoverageOwnerForTests(movedCoarse))throw new InvalidOperationException($"vertical-only movement did not recenter coarse sector: moved={movedCoarse}, ownership={coarseGrid.CaptureCoverageOwnershipSnapshot()}");
            int lowerHullTop=source.GetLowerHullTopGlobalY();coarseGrid.SetCanonicalFocus(new RingCanonicalPosition(8,lowerHullTop+8,8));await WaitForQuiescence(coarseGrid);
            int lowerHullSector=RingCoordMath.FloorDiv(lowerHullTop,2048)*64;SmoothTerrainChunkCoordinate? lowerHullMesh=new[]{new SmoothTerrainChunkCoordinate(0,lowerHullSector,0),new SmoothTerrainChunkCoordinate(0,lowerHullSector-64,0)}.Where(c=>coarseGrid.IsCoarseVolumetricVisibleForTests(c)).Select(c=>(SmoothTerrainChunkCoordinate?)c).FirstOrDefault();
            if(lowerHullMesh is null||coarseGrid.HasCoarseVolumetricCollisionForTests(lowerHullMesh.Value))throw new InvalidOperationException($"coarse lower-hull boundary was not visible/render-only: sector={lowerHullSector}, mesh={lowerHullMesh}");
            // A uniform coarse volume must be rejected before snapshot conversion/meshing.
            coarseGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-10220,8));await WaitForQuiescence(coarseGrid);SmoothTerrainChunkCoordinate? rejectedCoarse=coarseGrid.CaptureCoarseVolumetricCoordinatesForTests().Where(c=>coarseGrid.CaptureCoarseVolumetricClassificationForTests(c) is SmoothTerrainDensityClassification.Empty or SmoothTerrainDensityClassification.Solid).Select(c=>(SmoothTerrainChunkCoordinate?)c).FirstOrDefault();int coarseRejected=coarseGrid.CoarseUniformRejectedCountForTests;
            if(rejectedCoarse is null||coarseRejected==0||coarseGrid.IsCoarseVolumetricVisibleForTests(rejectedCoarse.Value)||coarseGrid.HasCoarseVolumetricCollisionForTests(rejectedCoarse.Value))throw new InvalidOperationException($"coarse uniform rejection failed: sector={rejectedCoarse}, rejected={coarseRejected}");
            coarseGrid.SetCanonicalFocus(new RingCanonicalPosition(8,coarseSurface+coarseGrid.SurfaceResidencyExitAltitudeMeters+1.0,8));await WaitForQuiescence(coarseGrid);
            if(coarseGrid.SurfaceResidencyActive||coarseGrid.CaptureCoverageOwnershipSnapshot().CoarseVolumetricVisualDesired!=0)throw new InvalidOperationException("coarse coverage remained resident in space");
            GD.Print($"SMOOTH_TERRAIN_COARSE_VOLUMETRIC_SMOKE_PASS: spacing={coarseContract.SampleSpacingMeters}m, cells={coarseContract.CellsPerSector}, sector={coarseContract.SectorExtentMeters}m, desired={coarseGrid.CaptureCoverageOwnershipSnapshot().CoarseVolumetricVisualDesired}, rejected={coarseRejected}");
            Task coarseShutdown=coarseGrid.AwaitShutdownForTestsAsync();coarseGrid.QueueFree();for(int i=0;i<360&&!coarseShutdown.IsCompleted;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await coarseShutdown;
            // The tiny seam fixture above has only one 2 km sector around its
            // circumference, so use a wider lazy source to prove that a stamp
            // rebuilds its overlapping coarse sector without touching either
            // neighboring sector or retaining revision history indefinitely.
            var localitySource=new VoxelChunkGridManager { Name="CoarseLocalitySource",BuildOnReady=false,EnableChunkStreaming=false,UseDeterministicTerrainGeneration=true,TerrainSettings=settings,UseRingCurvedPlacement=true,AroundRingChunkLength=256,RingAcrossWidthMeters=4096,EnforceRingAcrossBounds=true,EnableCanonicalFloatingOriginAnchor=true };
            AddChild(localitySource);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            var localityGrid=new SmoothTerrainGrid { SourceGridPath=new NodePath("../CoarseLocalitySource"),RadiusAround=0,RadiusAcross=0,RadiusVertical=0,CollisionRadius=0,EnableCoarseVolumetricVisualCoverage=true,CoarseVisualRadiusAround=1,CoarseVisualRadiusAcross=0,CoarseVisualRadiusVertical=0,CoarseUndergroundActivationDepthMeters=256,EnableHotChunkPatches=false,MaxWorkers=2 };
            AddChild(localityGrid);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            const long localityAround=5120;const int localityAcross=2048;const int localityVertical=-8180;const long localityOwnerAroundChunk=128;
            localityGrid.SetCanonicalFocus(new RingCanonicalPosition(localityAround,localityVertical,localityAcross));await WaitForQuiescence(localityGrid);
            var localityBefore=localityGrid.CaptureCoarseVolumetricCoordinatesForTests().ToDictionary(c=>c,c=>localityGrid.CaptureCoarseVolumetricIdentityForTests(c));
            localityGrid.AddEdit(new SmoothTerrainEditCommand(1,9150,9150,true,localityAround,localityVertical,localityAcross,1,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(localityGrid);
            var localityChanged=localityBefore.Where(pair=>localityGrid.CaptureCoarseVolumetricIdentityForTests(pair.Key)?.EditRevision!=pair.Value?.EditRevision).Select(pair=>pair.Key).ToArray();
            int localityUnchanged=localityBefore.Count-localityChanged.Length;
            if(localityChanged.Length==0||localityUnchanged==0||localityChanged.Any(c=>c.AroundChunk!=localityOwnerAroundChunk)||localityGrid.RetainedEditRevisionHistoryCountForTests!=0)throw new InvalidOperationException($"coarse locality regression: changed={string.Join(',',localityChanged)}, unchanged={localityUnchanged}, retainedHistory={localityGrid.RetainedEditRevisionHistoryCountForTests}");
            GD.Print($"SMOOTH_TERRAIN_EDIT_LOCALITY_SMOKE_PASS: coarseChanged={localityChanged.Length}, coarseUnchanged={localityUnchanged}, retainedHistory={localityGrid.RetainedEditRevisionHistoryCountForTests}");
            Task localityShutdown=localityGrid.AwaitShutdownForTestsAsync();localityGrid.QueueFree();for(int i=0;i<720&&!localityShutdown.IsCompleted;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await localityShutdown;localitySource.QueueFree();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            // The live cave chain is a complete 1/2/4/8/16/32/64 m hierarchy.
            // Exercise all four bridge lifecycles together at the protected
            // roof, where every exposed horizontal envelope face intersects a
            // real structural isosurface.
            if(SmoothTerrainTransitionMesher.IsNativeAvailable)
            {
                var stitchGrid=new SmoothTerrainGrid
                {
                    SourceGridPath=new NodePath("../Source"),RadiusAround=0,RadiusAcross=0,RadiusVertical=0,CollisionRadius=0,
                    EnableFocusCenteredVisualCoverage=true,EnableMediumVolumetricVisualCoverage=true,
                    EnableVolumetricBridgeShells=true,EnableCoarseVolumetricVisualCoverage=true,
                    CoarseUndergroundActivationDepthMeters=256,EnableHotChunkPatches=false,
                    MaxWorkers=4,MaxInstallsPerFrame=256,InstallBudgetMilliseconds=1000
                };
                AddChild(stitchGrid);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                stitchGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-8180,8));await WaitForQuiescence(stitchGrid);
                IReadOnlyList<SmoothTerrainGrid.BridgeVolumetricMeshContract> contracts=stitchGrid.CaptureBridgeVolumetricMeshContracts();
                IReadOnlyDictionary<int,int> bridgeEntries=stitchGrid.CaptureBridgeEntryCountsForTests();
                IReadOnlyDictionary<int,int> bridgeTriangles=stitchGrid.CaptureBridgeTransitionTriangleCountsForTests();
                if(contracts.Count!=4||contracts.Any(contract=>!contract.IsValid)
                    ||new[]{4,8,16,32}.Any(spacing=>!bridgeEntries.TryGetValue(spacing,out int entries)||entries==0)
                    ||new[]{4,8,16,32}.Any(spacing=>!bridgeTriangles.TryGetValue(spacing,out int triangles)||triangles==0)
                    ||stitchGrid.BridgeVolumetricEntryCountForTests==0||stitchGrid.HasBridgeVolumetricCollisionForTests
                    ||stitchGrid.NearTransitionEntryCountForTests==0||stitchGrid.NearTransitionTriangleCountForTests==0
                    ||stitchGrid.CaveLodVisibleOwnershipOverlapCountForTests!=0)
                    throw new InvalidOperationException($"stitched cave LOD chain regressed: contracts={string.Join(',',contracts)}, entries={string.Join(',',bridgeEntries)}, transitionTriangles={string.Join(',',bridgeTriangles)}, nearTransitions={stitchGrid.NearTransitionEntryCountForTests}/{stitchGrid.NearTransitionTriangleCountForTests}, overlap={stitchGrid.CaveLodVisibleOwnershipOverlapCountForTests}");
                var caveTelemetry=stitchGrid.CaptureCaveLodTelemetry();
                if(caveTelemetry.NearTransition.Resident==0||caveTelemetry.Medium.Resident==0||caveTelemetry.Bridge4.Resident==0||caveTelemetry.Bridge8.Resident==0||caveTelemetry.Bridge16.Resident==0||caveTelemetry.Bridge32.Resident==0||caveTelemetry.Coarse.Resident==0||caveTelemetry.TotalEstimatedBytes<caveTelemetry.ResidentAndPoolBytes||caveTelemetry.LastInstalls<0||caveTelemetry.MaxInstalls<caveTelemetry.LastInstalls)
                    throw new InvalidOperationException($"cave LOD telemetry consistency failed: {caveTelemetry}");
                int stitchSurface=source.TerrainSampler!.GetSurfaceHeight(8,8);
                stitchGrid.SetCanonicalFocus(new RingCanonicalPosition(8,stitchSurface+stitchGrid.SurfaceResidencyExitAltitudeMeters+1,8));await WaitForQuiescence(stitchGrid);
                if(stitchGrid.BridgeVolumetricEntryCountForTests!=0||stitchGrid.NearTransitionEntryCountForTests!=0)
                    throw new InvalidOperationException("stitched cave LOD presenters remained resident in space");
                GD.Print($"SMOOTH_TERRAIN_CAVE_LOD_TELEMETRY_PASS: visible={caveTelemetry.NearTransition.Visible+caveTelemetry.Medium.Visible+caveTelemetry.Bridge4.Visible+caveTelemetry.Bridge8.Visible+caveTelemetry.Bridge16.Visible+caveTelemetry.Bridge32.Visible+caveTelemetry.Coarse.Visible}, ready={caveTelemetry.NearTransition.Ready+caveTelemetry.Medium.Ready+caveTelemetry.Bridge4.Ready+caveTelemetry.Bridge8.Ready+caveTelemetry.Bridge16.Ready+caveTelemetry.Bridge32.Ready+caveTelemetry.Coarse.Ready}, queuedBytes={caveTelemetry.QueuedReadyBytes}, totalBytes={caveTelemetry.TotalEstimatedBytes}");
                GD.Print($"SMOOTH_TERRAIN_CAVE_LOD_STITCH_SMOKE_PASS: chain=1/2/4/8/16/32/64m, bridgeEntries={string.Join(',',bridgeEntries)}, transitionTriangles={string.Join(',',bridgeTriangles)}");
                Task stitchShutdown=stitchGrid.AwaitShutdownForTestsAsync();stitchGrid.QueueFree();for(int i=0;i<1440&&!stitchShutdown.IsCompleted;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await stitchShutdown;
            }
            var focusNode = new Node3D { Name = "FocusProvider" }; AddChild(focusNode); var focusGeometry = source.CaptureLodGeometrySnapshot(); focusNode.GlobalPosition = focusGeometry.CanonicalToWorld(0, 8, 16); var focusGrid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), StreamingFocusPath = new NodePath("../FocusProvider"), StreamingUpdateIntervalSeconds = .01f, RadiusAround = 1, RadiusAcross = 1, RadiusVertical = 0, CollisionRadius = 0, MaxWorkers = 1 }; AddChild(focusGrid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await WaitForFocusPoll(focusGrid); await WaitForQuiescence(focusGrid); SmoothTerrainGrid.CoverageOwnershipSnapshot focusOwnership = focusGrid.CaptureCoverageOwnershipSnapshot(); if (focusOwnership.ResidentBuildDesired <= 0 || focusGrid.ReadyRenderCount != focusOwnership.ResidentBuildDesired) throw new InvalidOperationException($"focus provider initial window ready={focusGrid.ReadyRenderCount}/{focusOwnership.ResidentBuildDesired}"); var focusInitial = focusGrid.CaptureDiagnostics(); int focusCreated = focusGrid.CreatedPresenterCount, focusReuse = focusGrid.ReusedPresenterCount, focusPool = focusGrid.PoolCount; focusNode.GlobalPosition = focusGeometry.CanonicalToWorld(15, 8, 16); await WaitForFocusPoll(focusGrid); await WaitForQuiescence(focusGrid); var focusSame = focusGrid.CaptureDiagnostics(); foreach (var pair in focusInitial) if (pair.Value.Resident) AssertSameBuild(pair.Value, focusSame[pair.Key], $"within focus {pair.Key}"); focusNode.GlobalPosition = focusGeometry.CanonicalToWorld(33, 8, 16); await WaitForFocusPoll(focusGrid); await WaitForQuiescence(focusGrid); var focusMoved = focusGrid.CaptureDiagnostics(); int departures = 0, enters = 0; foreach (var pair in focusInitial) { if (!pair.Value.Resident) continue; var now = focusMoved[pair.Key]; if (!now.Resident) departures++; else AssertSameBuild(pair.Value, now, $"provider overlap {pair.Key}"); }
            foreach (var pair in focusMoved) if (pair.Value.Resident && (!focusInitial.TryGetValue(pair.Key, out var before) || !before.Resident)) { enters++; if (pair.Value.ScheduleCount != 1 || pair.Value.CompletedBuildCount != 1 || pair.Value.InstallCount != 1) throw new InvalidOperationException("provider entering rebuild counts"); }
            SmoothTerrainGrid.CoverageOwnershipSnapshot movedFocusOwnership=focusGrid.CaptureCoverageOwnershipSnapshot();int availableFocusPool=focusPool+departures;int expectedFocusReuse=focusReuse+Math.Min(enters,availableFocusPool);int expectedFocusCreated=focusCreated+Math.Max(0,enters-availableFocusPool);int expectedFocusPool=Math.Max(0,availableFocusPool-enters);
            if (departures <= 0 || enters <= 0 || enters-departures!=movedFocusOwnership.ResidentBuildDesired-focusOwnership.ResidentBuildDesired || focusGrid.ReadyRenderCount!=movedFocusOwnership.ResidentBuildDesired || focusGrid.CreatedPresenterCount != expectedFocusCreated || focusGrid.ReusedPresenterCount != expectedFocusReuse || focusGrid.PoolCount!=expectedFocusPool) throw new InvalidOperationException($"provider selectivity departed={departures} entered={enters} ready={focusGrid.ReadyRenderCount}/{movedFocusOwnership.ResidentBuildDesired} created={focusGrid.CreatedPresenterCount}/{expectedFocusCreated} reused={focusGrid.ReusedPresenterCount}/{expectedFocusReuse} pool={focusGrid.PoolCount}/{expectedFocusPool}"); Task focusShutdown = focusGrid.AwaitShutdownForTestsAsync(); focusGrid.QueueFree(); for (int i = 0; i < 360 && !focusShutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await focusShutdown; focusNode.QueueFree();
            // Space residency is presentation-only: climbing beyond the exit
            // threshold must release every smooth presenter, while returning
            // below the enter threshold rebuilds the local surface window.
            // Journal state must survive both transitions.
            var residencyGrid = new SmoothTerrainGrid
            {
                SourceGridPath = new NodePath("../Source"),
                RadiusAround = 1,
                RadiusAcross = 1,
                RadiusVertical = 0,
                CollisionRadius = 0,
                MaxWorkers = 1
            };
            AddChild(residencyGrid);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            int residencySurface = source.TerrainSampler!.GetSurfaceHeight(0, 16);
            residencyGrid.SetCanonicalFocus(new RingCanonicalPosition(0, residencySurface, 16));
            await WaitForQuiescence(residencyGrid);
            int residencyExpected = residencyGrid.CaptureCoverageOwnershipSnapshot().ResidentBuildDesired;
            if (residencyExpected <= 0 || !residencyGrid.SurfaceResidencyActive || residencyGrid.Metrics.Resident != residencyExpected)
                throw new InvalidOperationException($"smooth surface residency did not initialize: active={residencyGrid.SurfaceResidencyActive} resident={residencyGrid.Metrics.Resident}");
            residencyGrid.AddEdit(new SmoothTerrainEditCommand(1, 9801, 9801, false, 8, residencySurface, 16, 1, (int)SmoothTerrainMaterial.Rock));
            await WaitForQuiescence(residencyGrid);
            SmoothTerrainJournalEnvelope residencyJournal = residencyGrid.CaptureJournalEnvelope();
            residencyGrid.SetCanonicalFocus(new RingCanonicalPosition(
                0,
                residencySurface + residencyGrid.SurfaceResidencyExitAltitudeMeters + 1.0,
                16));
            await WaitForQuiescence(residencyGrid);
            SmoothTerrainJournalEnvelope suspendedJournal = residencyGrid.CaptureJournalEnvelope();
            if (residencyGrid.SurfaceResidencyActive
                || residencyGrid.Metrics.Desired != 0
                || residencyGrid.Metrics.Resident != 0
                || residencyGrid.ReadyRenderCount != 0
                || !SameJournal(residencyJournal, suspendedJournal))
                throw new InvalidOperationException($"smooth space suspension retained presentation or changed its journal: active={residencyGrid.SurfaceResidencyActive} desired={residencyGrid.Metrics.Desired} resident={residencyGrid.Metrics.Resident} ready={residencyGrid.ReadyRenderCount}");
            residencyGrid.SetCanonicalFocus(new RingCanonicalPosition(
                0,
                residencySurface + residencyGrid.SurfaceResidencyEnterAltitudeMeters - 1.0,
                16));
            await WaitForQuiescence(residencyGrid);
            if (!residencyGrid.SurfaceResidencyActive
                || residencyGrid.Metrics.Desired != residencyExpected
                || residencyGrid.Metrics.Resident != residencyExpected
                || residencyGrid.ReadyRenderCount != residencyExpected
                || !SameJournal(residencyJournal, residencyGrid.CaptureJournalEnvelope()))
                throw new InvalidOperationException($"smooth surface residency did not restore: active={residencyGrid.SurfaceResidencyActive} desired={residencyGrid.Metrics.Desired} resident={residencyGrid.Metrics.Resident} ready={residencyGrid.ReadyRenderCount}");
            Task residencyShutdown = residencyGrid.AwaitShutdownForTestsAsync();
            residencyGrid.QueueFree();
            for (int i = 0; i < 360 && !residencyShutdown.IsCompleted; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await residencyShutdown;
            var hotGrid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../Source"), RadiusAround = 0, RadiusAcross = 0, RadiusVertical = 0, CollisionRadius = 0, EnableFocusCenteredCollisionCoverage = true, MaxWorkers = 1, EnableHotChunkPatches = true, HotChunkHysteresisMeters = 4.0f, HotChunkDemotionIdleSeconds = .25f };
            AddChild(hotGrid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); hotGrid.SetCanonicalFocus(new RingCanonicalPosition(8, editVertical, 8)); await WaitForQuiescence(hotGrid);
            if (hotGrid.Metrics.Resident != 8 || hotGrid.HotParentCount != 8 || hotGrid.HotPatchPresenterCount != 64 || hotGrid.ReadyCollisionCount == 0 || !hotGrid.IsSurfaceRegionVisualReady(0, 32, 0, 32) || !hotGrid.IsSurfaceCollisionReady(8, 8)) throw new InvalidOperationException($"hot bubble expected 8 parent/64 patch presenters with visual and collision coverage, got resident={hotGrid.Metrics.Resident} parents={hotGrid.HotParentCount} patches={hotGrid.HotPatchPresenterCount} collision={hotGrid.ReadyCollisionCount}");
            SmoothTerrainChunkCoordinate initialHotAnchor=hotGrid.HotPatchAnchorForTests??throw new InvalidOperationException("hot bubble anchor was not recorded");
            // Movement readiness is the pre-physics fall guard. A valid
            // excavated interior may be empty, but no unloaded probe may be
            // accepted below the lower hull or through either sidewall.
            if(!hotGrid.IsMovementTerrainResident(8,editVertical,8,.3f)
                ||hotGrid.IsMovementTerrainResident(8,hotGrid.StructuralBottomVertical-1,8,.3f)
                ||hotGrid.IsMovementTerrainResident(8,editVertical,-.1,.3f)
                ||hotGrid.IsMovementTerrainResident(8,editVertical,source.RingAcrossWidthMeters+.1,.3f))throw new InvalidOperationException("movement fall guard accepted an exterior or unloaded structural boundary");
            var hotBefore = hotGrid.CaptureDiagnostics(); hotGrid.AddEdit(new SmoothTerrainEditCommand(1, 9901, 9901, false, 8, editVertical, 8, 2, (int)SmoothTerrainMaterial.Rock));
            if(!hotGrid.IsSurfaceRegionVisualReady(0,32,0,32)||!hotGrid.IsSurfaceCollisionReady(8,8))throw new InvalidOperationException("hot excavation dropped visual/collision readiness while its replacement was pending");
            await WaitForQuiescence(hotGrid); var hotAfter = hotGrid.CaptureDiagnostics();
            SmoothTerrainChunkCoordinate hotCenter=new(0,terrainVerticalChunk,0);
            var firstChanged=hotAfter.Where(pair=>pair.Value.Resident&&hotBefore.TryGetValue(pair.Key,out var before)&&pair.Value.InstallCount==before.InstallCount+1).Select(pair=>pair.Key).ToArray();
            var firstMasks=hotGrid.CaptureHotPatchMasksForTests();
            byte firstMask=firstMasks.TryGetValue(hotCenter,out byte foundFirstMask)?foundFirstMask:(byte)0;
            if(firstChanged.Length!=1||firstChanged[0]!=hotCenter||firstMask!=0x01||hotGrid.HotPatchPresenterCount!=64||hotGrid.ReadyCollisionCount==0||!hotGrid.IsSurfaceRegionVisualReady(0,32,0,32)||!hotGrid.IsSurfaceCollisionReady(8,8))throw new InvalidOperationException($"small hot edit was not isolated to one ready 16^3 patch: changed={string.Join(',',firstChanged)} mask={firstMask:x2} patches={hotGrid.HotPatchPresenterCount} collision={hotGrid.ReadyCollisionCount}");
            var internalBefore=hotGrid.CaptureDiagnostics();hotGrid.AddEdit(new SmoothTerrainEditCommand(1,9902,9902,false,16,editVertical,8,2,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(hotGrid);var internalAfter=hotGrid.CaptureDiagnostics();var internalMasks=hotGrid.CaptureHotPatchMasksForTests();
            byte internalMask=internalMasks.TryGetValue(hotCenter,out byte foundInternalMask)?foundInternalMask:(byte)0;
            if(internalAfter[hotCenter].InstallCount!=internalBefore[hotCenter].InstallCount+1||internalMask!=0x03||hotGrid.HotPatchPresenterCount!=64)throw new InvalidOperationException($"internal patch-boundary edit expected mask 03, got {internalMask:x2}");
            var beforeHysteresis=hotGrid.CaptureDiagnostics();hotGrid.SetCanonicalFocus(new RingCanonicalPosition(19,editVertical,8));await WaitForQuiescence(hotGrid);
            if(hotGrid.HotPatchAnchorForTests!=initialHotAnchor)throw new InvalidOperationException("hot bubble crossed its anchor before the hysteresis threshold");
            foreach(var pair in beforeHysteresis)if(pair.Value.Resident)AssertSameBuild(pair.Value,hotGrid.CaptureDiagnostics()[pair.Key],$"hot hysteresis {pair.Key}");
            hotGrid.RadiusAround=1;hotGrid.RadiusVertical=1;await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await WaitForQuiescence(hotGrid);
            hotGrid.SetCanonicalFocus(new RingCanonicalPosition(21,editVertical,8));
            if(hotGrid.HotParentCount<=8)throw new InvalidOperationException($"parents outside the new hot bubble were not retained for delayed merge: {hotGrid.HotParentCount}");
            for(int i=0;i<1440&&(hotGrid.HotParentCount!=8||hotGrid.HotPatchPresenterCount!=64||!hotGrid.IsQuiescent);i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(hotGrid.HotPatchAnchorForTests==initialHotAnchor||hotGrid.HotParentCount!=8||hotGrid.HotPatchPresenterCount!=64||hotGrid.HotPatchMergeCount<1||!hotGrid.IsQuiescent)throw new InvalidOperationException($"hot bubble promotion/delayed merge failed: anchor={hotGrid.HotPatchAnchorForTests} parents={hotGrid.HotParentCount} patches={hotGrid.HotPatchPresenterCount} merges={hotGrid.HotPatchMergeCount} quiescent={hotGrid.IsQuiescent}");
            var boundaryBefore=hotGrid.CaptureDiagnostics();int collisionBeforeBoundary=hotGrid.ReadyCollisionCount;hotGrid.SuspendInstallsForTests=true;hotGrid.AddEdit(new SmoothTerrainEditCommand(1,9903,9903,false,32,editVertical,8,2,(int)SmoothTerrainMaterial.Rock));
            for(int i=0;i<1440&&hotGrid.ReadyResultCount<2;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(hotGrid.ReadyResultCount<2||hotGrid.ReadyCollisionCount!=collisionBeforeBoundary||hotGrid.HotPatchPresenterCount!=64)throw new InvalidOperationException($"parent-boundary edit did not retain old collision while rebuilding: ready={hotGrid.ReadyResultCount} collision={hotGrid.ReadyCollisionCount}/{collisionBeforeBoundary} patches={hotGrid.HotPatchPresenterCount}");
            hotGrid.SuspendInstallsForTests=false;await WaitForQuiescence(hotGrid);var boundaryAfter=hotGrid.CaptureDiagnostics();var boundaryMasks=hotGrid.CaptureHotPatchMasksForTests();SmoothTerrainChunkCoordinate hotRight=new(1,terrainVerticalChunk,0);
            byte leftMask=boundaryMasks.TryGetValue(hotCenter,out byte foundLeftMask)?foundLeftMask:(byte)0;byte rightMask=boundaryMasks.TryGetValue(hotRight,out byte foundRightMask)?foundRightMask:(byte)0;
            if(boundaryAfter[hotCenter].InstallCount!=boundaryBefore[hotCenter].InstallCount+1||boundaryAfter[hotRight].InstallCount!=boundaryBefore[hotRight].InstallCount+1||leftMask!=0x02||rightMask!=0x01||hotGrid.ReadyCollisionCount==0)throw new InvalidOperationException($"parent-boundary transaction failed: masks={leftMask:x2}/{rightMask:x2} collision={hotGrid.ReadyCollisionCount}");
            // Regression: a player in a shaft at Y=-55 must anchor the 64 m
            // hot bubble below zero using floor division, and resident empty
            // shaft space is valid movement coverage even without triangles.
            hotGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-55,8));await WaitForQuiescence(hotGrid);
            SmoothTerrainChunkCoordinate deepAnchor=hotGrid.HotPatchAnchorForTests??throw new InvalidOperationException("deep hot anchor was not recorded");
            if(deepAnchor.VerticalChunk!=-3||!hotGrid.IsTerrainResidentAt(8,-55,8)||!hotGrid.IsMovementTerrainResident(8,-55,8,.3f))throw new InvalidOperationException($"deep shaft residency failed: anchor={deepAnchor}, resident={hotGrid.IsTerrainResidentAt(8,-55,8)}");
            var deepWorld=source.CanonicalRingPositionToWorld(new RingCanonicalPosition(8,-64,8));
            if(!hotGrid.TryQueryEditableDensityAtWorldPosition(deepWorld,out float deepBefore)||deepBefore>=0)throw new InvalidOperationException($"expected solid negative-Y terrain before excavation, density={deepBefore}");
            var deepLow=new SmoothTerrainChunkCoordinate(0,-3,0);var deepHigh=new SmoothTerrainChunkCoordinate(0,-2,0);var deepBeforeDiagnostics=hotGrid.CaptureDiagnostics();
            hotGrid.AddEdit(new SmoothTerrainEditCommand(1,9904,9904,false,8,-64,8,2,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(hotGrid);
            if(!hotGrid.TryQueryEditableDensityAtWorldPosition(deepWorld,out float deepAfter)||deepAfter<=0)throw new InvalidOperationException($"negative-Y excavation did not remove terrain, density={deepAfter}");
            var deepAfterDiagnostics=hotGrid.CaptureDiagnostics();
            SmoothTerrainGrid.ChunkDiagnostics deepLowResult=default,deepHighResult=default;
            bool hasDeepLow=deepAfterDiagnostics.TryGetValue(deepLow,out deepLowResult);
            bool hasDeepHigh=deepAfterDiagnostics.TryGetValue(deepHigh,out deepHighResult);
            if(!hasDeepLow||!hasDeepHigh
                ||deepLowResult.InstallCount!=deepBeforeDiagnostics[deepLow].InstallCount+1||deepHighResult.InstallCount!=deepBeforeDiagnostics[deepHigh].InstallCount+1
                ||!deepLowResult.HasRenderMesh||!deepHighResult.HasRenderMesh||!deepLowResult.HasCollision||!deepHighResult.HasCollision)throw new InvalidOperationException($"negative-Y parent rebuild failed: low={deepLowResult}, high={deepHighResult}");
            // Exercise a second negative slab below -96 m; this catches a
            // truncating division implementation that only appears beyond the
            // first underground parent transition.
            hotGrid.SetCanonicalFocus(new RingCanonicalPosition(8,-119,8));await WaitForQuiescence(hotGrid);
            if(hotGrid.HotPatchAnchorForTests?.VerticalChunk!=-5)throw new InvalidOperationException($"deep second-slab anchor was wrong: {hotGrid.HotPatchAnchorForTests}");
            var deeperWorld=source.CanonicalRingPositionToWorld(new RingCanonicalPosition(8,-128,8));
            var deeperLow=new SmoothTerrainChunkCoordinate(0,-5,0);var deeperHigh=new SmoothTerrainChunkCoordinate(0,-4,0);var deeperBefore=hotGrid.CaptureDiagnostics();
            hotGrid.AddEdit(new SmoothTerrainEditCommand(1,9905,9905,false,8,-128,8,2,(int)SmoothTerrainMaterial.Rock));await WaitForQuiescence(hotGrid);
            var deeperAfter=hotGrid.CaptureDiagnostics();
            if(!hotGrid.TryQueryEditableDensityAtWorldPosition(deeperWorld,out float deeperDensity)||deeperDensity<=0
                ||deeperAfter[deeperLow].InstallCount!=deeperBefore[deeperLow].InstallCount+1||deeperAfter[deeperHigh].InstallCount!=deeperBefore[deeperHigh].InstallCount+1
                ||!deeperAfter[deeperLow].HasCollision||!deeperAfter[deeperHigh].HasCollision)throw new InvalidOperationException($"second negative-Y parent rebuild failed: density={deeperDensity}, low={deeperAfter[deeperLow]}, high={deeperAfter[deeperHigh]}");
            int structuralBottom=hotGrid.StructuralBottomVertical;
            if(structuralBottom!=-22304)throw new InvalidOperationException($"unexpected shared structural lower bound {structuralBottom}");
            var bottomWorld=source.CanonicalRingPositionToWorld(new RingCanonicalPosition(8,structuralBottom,8));var belowBottomWorld=source.CanonicalRingPositionToWorld(new RingCanonicalPosition(8,structuralBottom-1,8));
            float bottomDensity=float.NaN,belowBottomDensity=float.NaN;bool queriedBottom=hotGrid.TryQueryEditableDensityAtWorldPosition(bottomWorld,out bottomDensity);bool queriedBelowBottom=hotGrid.TryQueryEditableDensityAtWorldPosition(belowBottomWorld,out belowBottomDensity);
            if(!queriedBottom||bottomDensity>=0||!queriedBelowBottom||belowBottomDensity<=0)throw new InvalidOperationException($"structural underside SDF was not finite/closed: bottom={bottomDensity}, below={belowBottomDensity}");
            ThrowsArgument(()=>hotGrid.AddEdit(new SmoothTerrainEditCommand(1,9906,9906,false,8,structuralBottom-1,8,2,(int)SmoothTerrainMaterial.Rock)),"structural lower-bound edit");
            ThrowsArgument(()=>hotGrid.AddEdit(new SmoothTerrainEditCommand(1,9907,9907,false,8,0,0,2,(int)SmoothTerrainMaterial.Rock)),"structural left-sidewall edit");
            Task hotShutdown = hotGrid.AwaitShutdownForTestsAsync(); hotGrid.QueueFree(); for (int i = 0; i < 360 && !hotShutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await hotShutdown;
            await RunCaveUniformProofDiagnostics();
            if (!Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--skip-cave-viewpoint-benchmark"))
                await RunDesignedCaveViewpointBenchmark();
            GD.Print("SMOOTH_TERRAIN_RUNTIME_SMOKE_TEST_PASS"); GetTree().Quit(0);
        }
        catch (Exception e) { GD.PushError(e.ToString()); GD.Print("SMOOTH_TERRAIN_RUNTIME_SMOKE_TEST_FAIL"); GetTree().Quit(1); }
    }
    private async Task WaitForQuiescence(SmoothTerrainGrid grid)
    {
        ulong deadline=Time.GetTicksMsec()+180_000;
        while(!grid.IsQuiescent&&Time.GetTicksMsec()<deadline)await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!grid.IsQuiescent) throw new InvalidOperationException($"grid quiescence timeout pending={grid.Metrics.Pending} queue={grid.Metrics.QueueDepth} active={grid.Metrics.ActiveWorkers}");
    }
    private async Task RunCaveUniformProofDiagnostics()
    {
        // Same production-shaped cave worlds as the viewpoint benchmark, one per
        // seed, so the interval proof is certified against full lattice sampling.
        int lattices = 0, referenceChecks = 0, proven = 0, interior = 0, effective = 0;
        foreach (int seed in new[] { 7331, 1, 424242 })
        {
            var settings = new VoxelTerrainSettings { Seed = seed, BaseSurfaceHeightVoxels = 8, HeightAmplitudeVoxels = 0, DetailAmplitudeVoxels = 0, BedrockY = -22304, EnableUnified3dDensityTerrain = true, EnableDesignedCaveDensityComposition = true, DesignedCaveGenerationContractSettings = new CaveGenerationSettings() };
            var proofSource = new VoxelChunkGridManager { Name = $"CaveProofSource{seed}", BuildOnReady = false, EnableChunkStreaming = false, UseDeterministicTerrainGeneration = true, TerrainSettings = settings, UseRingCurvedPlacement = true, AroundRingChunkLength = 4096 * 512, RingAcrossWidthMeters = 50000, EnforceRingAcrossBounds = true, EnableCanonicalFloatingOriginAnchor = true };
            AddChild(proofSource); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            using (RingTerrainSampler proofSampler = proofSource.CreateIndependentTerrainSampler() ?? throw new InvalidOperationException("cave proof sampler unavailable"))
            {
                CaveUniformProofDiagnostics.SeedResult result = CaveUniformProofDiagnostics.RunSeed(
                    seed, proofSampler, proofSource.GetStructuralBottomGlobalY(), proofSource.TerrainBottomGlobalY,
                    proofSource.ProtectedRoofThicknessMeters, proofSource.GetLowerHullTopGlobalY(), proofSource.GetStructuralWallTopGlobalY());
                GD.Print($"SMOOTH_TERRAIN_CAVE_UNIFORM_PROOF_SEED_PASS: {result.Summary}");
                lattices += result.Lattices; referenceChecks += result.ReferenceChecks; proven += result.ProvenEmpty + result.ProvenFallThrough;
                interior += result.EffectivenessInterior; effective += result.EffectivenessProven;
            }
            proofSource.QueueFree();
        }
        if (interior == 0 || effective * 2 < interior)
            throw new InvalidOperationException($"cave uniform proof effectiveness below 50%: {effective}/{interior}");
        GD.Print($"SMOOTH_TERRAIN_CAVE_UNIFORM_PROOF_PASS: lattices={lattices} proven={proven} referenceChecks={referenceChecks} mismatches=0 effectiveness={effective}/{interior}");
    }
    private async Task RunDesignedCaveViewpointBenchmark()
    {
        // This separate, cave-enabled source keeps the broad existing smoke
        // fixture unchanged while exercising the exact production sampler and
        // SmoothTerrainGrid presentation path at real generated descriptors.
        var settings = new VoxelTerrainSettings { Seed = 7331, BaseSurfaceHeightVoxels = 8, HeightAmplitudeVoxels = 0, DetailAmplitudeVoxels = 0, BedrockY = -22304, EnableUnified3dDensityTerrain = true, EnableDesignedCaveDensityComposition = true, DesignedCaveGenerationContractSettings = new CaveGenerationSettings() };
        // Four thousand topology provinces give the rare (0.2%) supermassive
        // family enough real production anchors to be a deterministic fixture;
        // the sampler remains lazy and does not allocate the circumference.
        const int benchmarkAroundProvinceCount = 4096;
        var benchmarkSource = new VoxelChunkGridManager { Name = "CaveBenchmarkSource", BuildOnReady = false, EnableChunkStreaming = false, UseDeterministicTerrainGeneration = true, TerrainSettings = settings, UseRingCurvedPlacement = true, AroundRingChunkLength = benchmarkAroundProvinceCount * 512, RingAcrossWidthMeters = 50000, EnforceRingAcrossBounds = true, EnableCanonicalFloatingOriginAnchor = true };
        AddChild(benchmarkSource); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        using RingTerrainSampler sampler = benchmarkSource.CreateIndependentTerrainSampler()
            ?? throw new InvalidOperationException("cave benchmark sampler unavailable");
        var contract = new CaveGameplayIntegrationContract();
        bool grandOnly = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--cave-benchmark-grand-only");
        bool sharedWorkerSampler = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--cave-benchmark-shared-sampler");
        var wanted = grandOnly
            ? new[] { CaveFeatureFamily.GrandChamber }
            : new[] { CaveFeatureFamily.GrandChamber, CaveFeatureFamily.Megacavern, CaveFeatureFamily.SupermassiveCavern };
        var selected = new Dictionary<CaveFeatureFamily, CaveGameplayFeatureRecord>();
        CaveTopologyPartition partition = sampler.CaveTopologyPartition;
        CaveProvinceDistribution distribution = sampler.CaveProvinceDistribution;
        CaveTopologyDensityComposer composer = sampler.CaveTopologyDensityComposer;
        CaveFeaturePlacementPolicy placement = sampler.CavePlacementPolicy;
        long verticalFirst = FloorDivision(
            Math.Max(
                placement.ProtectedRoofTopMeters + CaveFeaturePlacementPolicy.RoofClearanceMeters,
                placement.LowerHullTopMeters + CaveFeaturePlacementPolicy.LowerHullClearanceMeters),
            CaveTopologyPartition.ProvinceVerticalMeters);
        long verticalLast = FloorDivision(
            placement.SurfaceFloorMeters - CaveFeaturePlacementPolicy.SurfaceClearanceMeters - 1,
            CaveTopologyPartition.ProvinceVerticalMeters);
        long acrossFirst = FloorDivision(
            CaveFeaturePlacementPolicy.SidewallClearanceMeters,
            CaveTopologyPartition.ProvinceAcrossMeters);
        long acrossLast = FloorDivision(
            placement.AcrossWidthMeters - CaveFeaturePlacementPolicy.SidewallClearanceMeters - 1,
            CaveTopologyPartition.ProvinceAcrossMeters);
        int examinedPlans = 0;
        // Walk actual normalized provinces and their real graph anchors. Region
        // windows are a consumer API, not an efficient rare-family index.
        for (long aroundProvince = 0;
             aroundProvince < partition.AroundProvinceCount && selected.Count < wanted.Length;
             aroundProvince++)
        for (long verticalProvince = verticalFirst;
             verticalProvince <= verticalLast && selected.Count < wanted.Length;
             verticalProvince++)
        for (long acrossProvince = acrossFirst;
             acrossProvince <= acrossLast && selected.Count < wanted.Length;
             acrossProvince++)
        {
            examinedPlans++;
            CaveProvincePlan plan = distribution.ResolvePlan(new CaveTopologyProvinceAddress(
                aroundProvince, verticalProvince, acrossProvince));
            if (!plan.Active) continue;
            CaveChamberGraph graph = sampler.CaveChamberGraphGenerator.Resolve(plan);
            for (int anchorIndex = 0; anchorIndex < graph.Anchors.Count; anchorIndex++)
            {
                CaveChamberAnchor anchor = graph.Anchors[anchorIndex];
                CaveRegionFeature feature = composer.Features.CreateAnchorFeatureForLocator(anchor);
                CaveFeatureShape shape = composer.Families.ResolveAnchorForLocator(feature);
                CaveGameplayEligibility eligibility = contract.Evaluate(true, placement, shape);
                if (!eligibility.IsEligible
                    || !composer.Families.IsDepthEligibleForLocator(shape)
                    || !wanted.Contains(shape.Family)
                    || selected.ContainsKey(shape.Family)) continue;
                selected.Add(shape.Family, new CaveGameplayFeatureRecord(shape, eligibility));
            }
        }
        if (selected.Count != wanted.Length) throw new InvalidOperationException($"cave viewpoint benchmark did not resolve all required generated families after {examinedPlans} production plans: {string.Join(',', wanted.Where(f => !selected.ContainsKey(f)))}");
        // Exercise the bounded downstream handoff query around each selected
        // production anchor, and require it to reproduce the same stable key.
        foreach (CaveGameplayFeatureRecord expected in selected.Values)
        {
            CaveFeatureShape shape = expected.Shape;
            IReadOnlyList<CaveGameplayFeatureRecord> records = contract.ResolveEligibleFeatures(
                sampler,
                true,
                new CaveRegionFeatureRequest(
                    shape.Around - 1,
                    shape.Vertical - 1,
                    shape.Across - 1,
                    2,
                    2,
                    2,
                    CaveTopologyDensityComposer.MaximumInfluenceHaloMeters));
            if (!records.Any(record => record.Shape.Key == shape.Key))
                throw new InvalidOperationException($"production cave handoff query omitted {shape.Family} {shape.Key:X16}");
        }
        var grid = new SmoothTerrainGrid { SourceGridPath = new NodePath("../CaveBenchmarkSource"), RadiusAround = 0, RadiusAcross = 0, RadiusVertical = 0, CollisionRadius = 0, EnableFocusCenteredVisualCoverage = true, FocusVisualRadiusAround = 0, FocusVisualRadiusAcross = 0, FocusVisualRadiusVertical = 0, EnableMediumVolumetricVisualCoverage = true, MediumVisualRadiusAround = 0, MediumVisualRadiusAcross = 0, MediumVisualRadiusVertical = 0, EnableVolumetricBridgeShells = true, EnableCoarseVolumetricVisualCoverage = true, CoarseVisualRadiusAround = 0, CoarseVisualRadiusAcross = 0, CoarseVisualRadiusVertical = 0, CoarseUndergroundActivationDepthMeters = 256, MaxWorkers = 4, MaxInstallsPerFrame = 64, InstallBudgetMilliseconds = 1000, UseIndependentWorkerSamplers = !sharedWorkerSampler };
        AddChild(grid); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        foreach (CaveFeatureFamily family in wanted)
        {
            CaveFeatureShape shape = selected[family].Shape; Stopwatch watch = Stopwatch.StartNew();
            CaveGenerationPerformanceTelemetry.Reset(composer.Identity);
            sampler.CaveRegionFeatureSnapshotGenerator.ResetProvinceCacheForDiagnostics();
            SmoothTerrainVolumetricDensitySnapshot benchmarkField = grid.CaptureVolumetricDensitySnapshot()
                ?? throw new InvalidOperationException("cave benchmark density field unavailable");
            benchmarkField.ResetTransitionLatticeCacheForDiagnostics();
            benchmarkField.ResetRegularChunkCacheForDiagnostics();
            grid.ResetMetricPeaks(); grid.SetCanonicalFocus(new RingCanonicalPosition(shape.Around, shape.Vertical, shape.Across));
            await WaitForQuiescence(grid); watch.Stop();
            SmoothTerrainGrid.CaveLodTelemetry telemetry = grid.CaptureCaveLodTelemetry();
            int resident = telemetry.Base.Resident + telemetry.NearTransition.Resident + telemetry.Medium.Resident + telemetry.Bridge4.Resident + telemetry.Bridge8.Resident + telemetry.Bridge16.Resident + telemetry.Bridge32.Resident + telemetry.Coarse.Resident;
            int visible = telemetry.Base.Visible + telemetry.NearTransition.Visible + telemetry.Medium.Visible + telemetry.Bridge4.Visible + telemetry.Bridge8.Visible + telemetry.Bridge16.Visible + telemetry.Bridge32.Visible + telemetry.Coarse.Visible;
            if (resident == 0 || visible == 0 || telemetry.TotalEstimatedBytes <= 0 || telemetry.TotalEstimatedBytes < telemetry.ResidentAndPoolBytes || telemetry.MaxInstallMilliseconds < telemetry.LastInstallMilliseconds)
                throw new InvalidOperationException($"{family} cave viewpoint telemetry invalid: {telemetry}");
            CaveGenerationPerformanceTelemetry.Diagnostics phases=CaveGenerationPerformanceTelemetry.Capture(composer.Identity);
            CaveRegionFeatureSnapshotGenerator.ProvinceCacheDiagnostics cache=sampler.CaveRegionFeatureSnapshotGenerator.CaptureProvinceCacheDiagnostics();
            SmoothTerrainVolumetricDensitySnapshot.TransitionLatticeCacheDiagnostics latticeCache=benchmarkField.CaptureTransitionLatticeCacheDiagnostics();
            SmoothTerrainVolumetricDensitySnapshot.RegularChunkCacheDiagnostics regularCache=benchmarkField.CaptureRegularChunkCacheDiagnostics();
            GD.Print($"SMOOTH_TERRAIN_CAVE_VIEWPOINT_BENCHMARK_PASS: family={family} key={shape.Key:X16} elapsedMs={watch.Elapsed.TotalMilliseconds:0.0} buildMs={grid.Metrics.MaxWorkerMilliseconds:0.000} topologyMs={phases.RegionMilliseconds:0.000}/{phases.RegionCaptures} densityMs={phases.DensityMilliseconds:0.000}/{phases.DensityLattices} densitySamples={phases.DensitySamples} uniformRejects={phases.UniformDensityRejections} cache={cache.Hits}/{cache.Misses} evictions={cache.Evictions} cached={cache.Count}/{cache.Capacity} latticeCache={latticeCache.Hits}/{latticeCache.Misses} latticeEvictions={latticeCache.Evictions} latticeCached={latticeCache.Count}/{latticeCache.Capacity} latticeBytes={latticeCache.RetainedBytes} regularCache={regularCache.Hits}/{regularCache.Misses} regularEvictions={regularCache.Evictions} regularCached={regularCache.Count}/{regularCache.Capacity} regularBytes={regularCache.RetainedBytes} installMs={telemetry.MaxInstallMilliseconds:0.000} memoryBytes={telemetry.TotalEstimatedBytes} tiers=base:{telemetry.Base.Resident}/{telemetry.Base.Visible},near:{telemetry.NearTransition.Resident}/{telemetry.NearTransition.Visible},medium:{telemetry.Medium.Resident}/{telemetry.Medium.Visible},coarse:{telemetry.Coarse.Resident}/{telemetry.Coarse.Visible}");
        }
        Task shutdown = grid.AwaitShutdownForTestsAsync(); grid.QueueFree(); for (int i = 0; i < 720 && !shutdown.IsCompleted; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await shutdown; benchmarkSource.QueueFree();

        static long FloorDivision(long value,long divisor) =>
            value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);
    }
    private static bool SameJournal(in SmoothTerrainJournalEnvelope left, in SmoothTerrainJournalEnvelope right)
    {
        return left.FormatVersion == right.FormatVersion
            && left.TerrainDefinitionHash == right.TerrainDefinitionHash
            && left.CircumferenceMeters.Equals(right.CircumferenceMeters)
            && left.Revision == right.Revision
            && left.NextCommandId == right.NextCommandId
            && left.NextSequence == right.NextSequence
            && left.Commands.SequenceEqual(right.Commands);
    }
    private async Task WaitForFocusPoll(SmoothTerrainGrid grid)
    {
        ulong before = grid.StreamingFocusPollCount;
        for (int i = 0; i < 720 && grid.StreamingFocusPollCount == before; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (grid.StreamingFocusPollCount == before) throw new InvalidOperationException("automatic focus poll timeout");
    }
    private static void ThrowsArgument(Action action, string label)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException($"{label} was accepted");
    }
    private static void AssertBoundaryMesh(ISmoothTerrainBaseline baseline,SmoothTerrainChunkCoordinate coordinate,Vector3 expectedOutward,string label)
    {
        var empty=new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(),0,baseline.AroundLength);
        SmoothTerrainMeshData mesh=SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(coordinate,baseline,empty),true);
        if(mesh.Vertices.Length==0||mesh.Indices.Length==0||mesh.CollisionFaces.Length!=mesh.Indices.Length)throw new InvalidOperationException($"{label} did not produce matching render/collision topology");
        for(int i=0;i<mesh.Normals.Length;i++)if(mesh.Normals[i].Dot(expectedOutward)<.9f)throw new InvalidOperationException($"{label} normal {i} points {mesh.Normals[i]} instead of {expectedOutward}");
        for(int i=0;i<mesh.Indices.Length;i+=3)
        {
            int ia=mesh.Indices[i],ib=mesh.Indices[i+1],ic=mesh.Indices[i+2];
            Vector3 face=(mesh.Vertices[ib]-mesh.Vertices[ia]).Cross(mesh.Vertices[ic]-mesh.Vertices[ia]);
            if(face.Dot(mesh.Normals[ia]+mesh.Normals[ib]+mesh.Normals[ic])>1e-5f)throw new InvalidOperationException($"{label} triangle {i/3} violates Godot clockwise front-face winding");
        }
    }
    private static void AssertOpaqueTerrainShader()
    {
        foreach(string path in new[] { "res://materials/voxel_ring_terrain.gdshader", "res://materials/ring_terrain_proxy.gdshader" })
        {
            string shader=FileAccess.GetFileAsString(path);
            if(string.IsNullOrWhiteSpace(shader)
                || !shader.Contains("render_mode cull_back, depth_draw_opaque",StringComparison.Ordinal)
                || shader.Contains("blend_",StringComparison.Ordinal)
                || shader.Contains("ALPHA",StringComparison.Ordinal))
                throw new InvalidOperationException($"terrain closure shader is not opaque: {path}");
        }
    }
    private static void AssertChunkSeam(ISmoothTerrainBaseline baseline,SmoothTerrainChunkCoordinate left,SmoothTerrainChunkCoordinate right,int axis,int seam,string label)
    {
        var edits=new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(),0,baseline.AroundLength);
        SmoothTerrainMeshData a=SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(left,baseline,edits),true);
        SmoothTerrainMeshData b=SmoothTerrainMesher.Build(SmoothTerrainSnapshotCapture.Capture(right,baseline,edits),true);
        Dictionary<string,Vector3> aBoundary=CaptureBoundaryVertices(a,left,axis,seam);
        Dictionary<string,Vector3> bBoundary=CaptureBoundaryVertices(b,right,axis,seam);
        if(aBoundary.Count==0||bBoundary.Count==0)
            throw new InvalidOperationException($"{label} did not generate boundary geometry: {aBoundary.Count}/{bBoundary.Count}");
        if(aBoundary.Count!=bBoundary.Count)
            throw new InvalidOperationException($"{label} has unmatched boundary vertex counts: {aBoundary.Count}/{bBoundary.Count}");
        foreach(var pair in aBoundary)
        {
            if(!bBoundary.TryGetValue(pair.Key,out Vector3 normal)||normal.Dot(pair.Value)<.999f)
                throw new InvalidOperationException($"{label} has an open or normal-discontinuous boundary at {pair.Key}");
        }
        HashSet<string> aEdges=CaptureBoundaryEdges(a,left,axis,seam);
        HashSet<string> bEdges=CaptureBoundaryEdges(b,right,axis,seam);
        if(aEdges.Count==0||bEdges.Count==0)
            throw new InvalidOperationException($"{label} did not generate boundary connectivity: {aEdges.Count}/{bEdges.Count}");
        if(!aEdges.SetEquals(bEdges))
            throw new InvalidOperationException($"{label} has an open or mismatched boundary edge set");
    }
    private static Dictionary<string,Vector3> CaptureBoundaryVertices(SmoothTerrainMeshData mesh,SmoothTerrainChunkCoordinate coordinate,int axis,int seam)
    {
        var result=new Dictionary<string,Vector3>();
        RingTerrainSampleCoordinate origin=coordinate.Origin;
        foreach(int index in mesh.Indices.Distinct())
        {
            Vector3 local=mesh.Vertices[index];
            Vector3 global=local+new Vector3(origin.AroundVoxel,origin.VerticalVoxel,origin.AcrossVoxel);
            float component=axis switch { 0=>global.X, 1=>global.Y, _=>global.Z };
            if(MathF.Abs(component-seam)>.001f)continue;
            string key=$"{MathF.Round(global.X*1000):0},{MathF.Round(global.Y*1000):0},{MathF.Round(global.Z*1000):0}";
            if(result.TryGetValue(key,out Vector3 previous)&&previous.Dot(mesh.Normals[index])<.999f)
                throw new InvalidOperationException($"chunk has discontinuous duplicate normals at {key}");
            result[key]=mesh.Normals[index];
        }
        return result;
    }
    private static HashSet<string> CaptureBoundaryEdges(SmoothTerrainMeshData mesh,SmoothTerrainChunkCoordinate coordinate,int axis,int seam)
    {
        var result=new HashSet<string>();
        RingTerrainSampleCoordinate origin=coordinate.Origin;
        for(int index=0;index<mesh.Indices.Length;index+=3)
        {
            int a=mesh.Indices[index],b=mesh.Indices[index+1],c=mesh.Indices[index+2];
            AddBoundaryEdge(a,b); AddBoundaryEdge(b,c); AddBoundaryEdge(c,a);
        }
        return result;
        void AddBoundaryEdge(int a,int b)
        {
            Vector3 start=mesh.Vertices[a]+new Vector3(origin.AroundVoxel,origin.VerticalVoxel,origin.AcrossVoxel);
            Vector3 end=mesh.Vertices[b]+new Vector3(origin.AroundVoxel,origin.VerticalVoxel,origin.AcrossVoxel);
            if(!IsOnSeam(start)||!IsOnSeam(end))return;
            string startKey=VertexKey(start),endKey=VertexKey(end);
            result.Add(string.CompareOrdinal(startKey,endKey)<=0?$"{startKey}|{endKey}":$"{endKey}|{startKey}");
        }
        bool IsOnSeam(Vector3 point)=>MathF.Abs((axis switch { 0=>point.X, 1=>point.Y, _=>point.Z })-seam)<=.001f;
    }
    private static string VertexKey(Vector3 point)
        => $"{MathF.Round(point.X*1000):0},{MathF.Round(point.Y*1000):0},{MathF.Round(point.Z*1000):0}";
    private static void AssertSameBuild(SmoothTerrainGrid.ChunkDiagnostics before, SmoothTerrainGrid.ChunkDiagnostics after, string label)
    {
        if (before.ScheduleCount != after.ScheduleCount || before.CompletedBuildCount != after.CompletedBuildCount || before.InstallCount != after.InstallCount || before.LastInstalledMeshHash != after.LastInstalledMeshHash) throw new InvalidOperationException($"unexpected rebuild {label}");
    }
    private static bool VerticalPathHitsSolid(SmoothTerrainVolumetricDensitySnapshot field,long around,int startVertical,int endVertical,int across)
    {
        int step=startVertical<=endVertical?1:-1;
        for(int vertical=startVertical;;vertical+=step)
        {
            if(field.Sample(around,vertical,across).Density<=0)return true;
            if(vertical==endVertical)break;
        }
        return false;
    }
    private static bool AcrossPathHitsSolid(SmoothTerrainVolumetricDensitySnapshot field,long around,int vertical,int startAcross,int endAcross)
    {
        int step=startAcross<=endAcross?1:-1;
        for(int across=startAcross;;across+=step)
        {
            if(field.Sample(around,vertical,across).Density<=0)return true;
            if(across==endAcross)break;
        }
        return false;
    }
    private static void AssertEditScope(IReadOnlyDictionary<SmoothTerrainChunkCoordinate, SmoothTerrainGrid.ChunkDiagnostics> before, IReadOnlyDictionary<SmoothTerrainChunkCoordinate, SmoothTerrainGrid.ChunkDiagnostics> after, IReadOnlyCollection<SmoothTerrainChunkCoordinate> affected, ulong revision, string label)
    {
        var expected = new System.Collections.Generic.HashSet<SmoothTerrainChunkCoordinate>(affected);
        foreach (var pair in before) { if (!pair.Value.Resident) continue; var current = after[pair.Key]; bool hit = expected.Contains(pair.Key); if (hit) { if (current.ScheduleCount != pair.Value.ScheduleCount + 1 || current.CompletedBuildCount < pair.Value.CompletedBuildCount + 1 || current.InstallCount != pair.Value.InstallCount + 1 || current.LastInstalledRevision.Edits != revision) throw new InvalidOperationException($"{label} missing rebuild {pair.Key}; before={pair.Value}; after={current}; revision={revision}"); } else AssertSameBuild(pair.Value, current, $"{label} unaffected {pair.Key}"); }
    }
    private static void AssertCollisionColumn(SmoothTerrainGrid grid,long expectedAround,int expectedAcross)
    {
        var diagnostics = grid.CaptureDiagnostics(); int count = 0; foreach (var pair in diagnostics) if (pair.Value.Resident && pair.Value.HasCollision) { count++; if (pair.Key.AroundChunk != expectedAround || pair.Key.AcrossChunk != expectedAcross) throw new InvalidOperationException($"unexpected collision outside {expectedAround}/*/{expectedAcross}: {pair.Key}"); }
        if (count == 0) throw new InvalidOperationException($"collision column {expectedAround}/*/{expectedAcross} was empty");
    }
}
