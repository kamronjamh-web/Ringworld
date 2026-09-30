using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainGlobalLod
{
    private readonly record struct PlanetMacroShadowBuildResult(
        RingTerrainMinMaxPyramid.DirectionalShadowMask Mask,
        ulong RequestVersion,
        ulong EditVersion,
        Vector3 Direction,
        int TraceSteps,
        float TraceDistance,
        bool IncludesStructuralWalls,
        double BuildMilliseconds);

    [Export] public NodePath PlanetLightPath { get; set; } = new("../PlanetLight");

    private DirectionalLight3D? _planetLight;
    private Task<PlanetMacroShadowBuildResult>? _planetMacroShadowTask;
    private CancellationTokenSource? _planetMacroShadowCancellation;
    private ulong _planetMacroShadowRequestVersion;
    private ulong _planetMacroShadowAppliedVersion;
    private Vector3 _lastPlanetMacroShadowRequestedDirection = new(float.PositiveInfinity, 0.0f, 0.0f);

    public bool PlanetMacroShadowBuildPending => _planetMacroShadowTask != null && !_planetMacroShadowTask.IsCompleted;
    public bool PlanetMacroShadowMaskActive { get; private set; }

    public static Vector2 ResolvePlanetMacroShadowHandoff(
        float requestedFadeStartMeters,
        float requestedFadeEndMeters,
        bool planetLocalShadowsEnabled,
        float planetShadowRangeMeters,
        float planetShadowFadeStart)
    {
        if (!planetLocalShadowsEnabled) return new Vector2(0.0f, 1.0f);
        float range = Mathf.Max(1.0f, float.IsFinite(planetShadowRangeMeters) ? planetShadowRangeMeters : 1.0f);
        float requestedEnd = Mathf.Max(1.0f, float.IsFinite(requestedFadeEndMeters) ? requestedFadeEndMeters : 1.0f);
        float end = Mathf.Min(requestedEnd, range);
        float requestedStart = Mathf.Max(0.0f, float.IsFinite(requestedFadeStartMeters) ? requestedFadeStartMeters : 0.0f);
        float atlasFadeStart = range * Mathf.Clamp(float.IsFinite(planetShadowFadeStart) ? planetShadowFadeStart : 0.0f, 0.0f, 1.0f);
        float start = Mathf.Min(requestedStart, Mathf.Min(atlasFadeStart, Mathf.Max(0.0f, end - 1.0f)));
        return new Vector2(start, end);
    }

    private Vector2 ResolvePlanetMacroShadowHandoff()
    {
        if (_planetLight == null || !GodotObject.IsInstanceValid(_planetLight)) return new Vector2(0.0f, 1.0f);
        return ResolvePlanetMacroShadowHandoff(
            MacroTerrainShadowFadeStartMeters,
            MacroTerrainShadowFadeEndMeters,
            _planetLight.ShadowEnabled,
            _planetLight.DirectionalShadowMaxDistance,
            _planetLight.DirectionalShadowFadeStart);
    }

    private void RefreshPlanetMacroShadowHandoff()
    {
        if (!PlanetMacroShadowMaskActive) return;
        Vector2 handoff = ResolvePlanetMacroShadowHandoff();
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.SetPlanetHandoff(handoff.X, handoff.Y);
    }

    private void ResolvePlanetMacroShadowLight()
    {
        _planetLight = GetNodeOrNull<DirectionalLight3D>(PlanetLightPath);
    }

    private bool TryGetPlanetMacroShadowDirection(out Vector3 direction)
    {
        direction = Vector3.Right;
        if (_planetLight == null || !GodotObject.IsInstanceValid(_planetLight)
            || !_planetLight.IsVisibleInTree() || _planetLight.LightEnergy <= 0.001f)
            return false;
        Vector3 candidate = _planetLight.GlobalTransform.Basis.Z;
        if (candidate.LengthSquared() <= 0.000001f) return false;
        direction = candidate.Normalized();
        return true;
    }

    private void RequestPlanetMacroShadowRebuildForLightChange()
    {
        if (!EnableMacroTerrainShadows || _heightPyramid == null)
        {
            DisablePlanetMacroShadows();
            return;
        }
        if (!TryGetPlanetMacroShadowDirection(out Vector3 direction))
        {
            DisablePlanetMacroShadows();
            return;
        }
        RefreshPlanetMacroShadowHandoff();
        // Keep the previous completed mask during small light motion. This is
        // the same half-degree request threshold used by the sun producer.
        if (_lastPlanetMacroShadowRequestedDirection.IsFinite()
            && direction.Dot(_lastPlanetMacroShadowRequestedDirection) > 0.99996f)
            return;
        _lastPlanetMacroShadowRequestedDirection = direction;
        QueuePlanetMacroShadowRebuild();
    }

    private void QueuePlanetMacroShadowRebuild()
    {
        _planetMacroShadowRequestVersion++;
        if (!EnableMacroTerrainShadows || _heightPyramid == null || _planetMacroShadowTask != null) return;
        if (_grid != null && _grid.LodSurfaceEditVersion != _observedEditVersion) return;
        if (!TryGetPlanetMacroShadowDirection(out _))
        {
            DisablePlanetMacroShadows();
            return;
        }
        StartPlanetMacroShadowBuild(_planetMacroShadowRequestVersion);
    }

    private void StartPlanetMacroShadowBuild(ulong requestVersion)
    {
        if (_heightPyramid == null || _planetMacroShadowTask != null || !EnableMacroTerrainShadows
            || !TryGetPlanetMacroShadowDirection(out Vector3 direction)) return;
        if (_grid != null && _grid.LodSurfaceEditVersion != _observedEditVersion) return;
        _planetMacroShadowCancellation?.Cancel();
        _planetMacroShadowCancellation?.Dispose();
        _planetMacroShadowCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _planetMacroShadowCancellation.Token;
        RingTerrainMinMaxPyramid pyramid = _heightPyramid;
        Vector3 canonicalDirection = _grid == null
            ? direction
            : (_grid.GlobalTransform.Basis.Inverse() * direction).Normalized();
        ulong editVersion = _grid?.LodSurfaceEditVersion ?? 0UL;
        int traceSteps = MacroTerrainShadowTraceSteps;
        float traceDistance = MacroTerrainShadowMaxTraceMeters;
        bool includeWalls = BuildStructuralEdgeWalls && EnableMacroTerrainWallShadows;
        float wallTop = _grid?.GetStructuralWallTopGlobalY() ?? 0.0f;
        float wallBottom = (_grid?.GetStructuralBottomGlobalY() ?? 0) * VoxelConstants.VoxelSizeMeters;
        _planetMacroShadowTask = Task.Run(() =>
        {
            using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu(
                "Terrain planet macro shadow build", worker: true);
            var watch = Stopwatch.StartNew();
            RingTerrainMinMaxPyramid.DirectionalShadowMask mask = pyramid.BuildDirectionalShadowMask(
                canonicalDirection, cancellation, traceSteps, traceDistance,
                includeStructuralWalls: includeWalls,
                structuralWallTopMeters: wallTop,
                structuralWallBottomMeters: wallBottom);
            watch.Stop();
            return new PlanetMacroShadowBuildResult(mask, requestVersion, editVersion, direction,
                traceSteps, traceDistance, includeWalls, watch.Elapsed.TotalMilliseconds);
        }, cancellation);
    }

    private void CompletePlanetMacroShadowBuildIfReady()
    {
        if (_planetMacroShadowTask == null || !_planetMacroShadowTask.IsCompleted) return;
        Task<PlanetMacroShadowBuildResult> completed = _planetMacroShadowTask;
        _planetMacroShadowTask = null;
        if (!completed.IsCanceled && !completed.IsFaulted)
        {
            PlanetMacroShadowBuildResult result = completed.Result;
            bool editsChanged = _grid != null && _grid.LodSurfaceEditVersion != result.EditVersion;
            bool settingsMatch = result.TraceSteps == MacroTerrainShadowTraceSteps
                && Mathf.IsEqualApprox(result.TraceDistance, MacroTerrainShadowMaxTraceMeters)
                && result.IncludesStructuralWalls == (BuildStructuralEdgeWalls && EnableMacroTerrainWallShadows);
            bool lightActive = TryGetPlanetMacroShadowDirection(out Vector3 currentDirection);
            bool lightStillClose = lightActive && currentDirection.Dot(result.Direction) > 0.99939f;
            if (EnableMacroTerrainShadows && !editsChanged && settingsMatch && lightActive
                && lightStillClose)
            {
                InstallPlanetMacroShadowMask(result.Mask, result.Direction, result.BuildMilliseconds);
                _planetMacroShadowAppliedVersion = result.RequestVersion;
            }
            else if (editsChanged) _planetMacroShadowRequestVersion++;
        }
        else if (completed.IsFaulted)
        {
            GD.PushError($"Terrain planet macro shadow build failed: {completed.Exception?.GetBaseException().Message}");
        }

        bool pendingPyramidEdits = _grid != null && _grid.LodSurfaceEditVersion != _observedEditVersion;
        if (EnableMacroTerrainShadows && _heightPyramid != null && !pendingPyramidEdits
            && _planetMacroShadowAppliedVersion != _planetMacroShadowRequestVersion)
            StartPlanetMacroShadowBuild(_planetMacroShadowRequestVersion);
    }

    private void InstallPlanetMacroShadowMask(
        in RingTerrainMinMaxPyramid.DirectionalShadowMask mask,
        Vector3 direction,
        double buildMilliseconds)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain planet macro shadow install");
        if (mask.Width <= 0 || mask.Height <= 0 || mask.Pixels.Length != mask.Width * mask.Height) return;
        Image image = Image.CreateFromData(mask.Width, mask.Height, false, Image.Format.R8, mask.Pixels);
        ImageTexture texture = ImageTexture.CreateFromImage(image);
        Vector2 handoff = ResolvePlanetMacroShadowHandoff();
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.UpdatePlanet(texture, direction,
            MacroTerrainShadowStrength, handoff.X, handoff.Y);
        PlanetMacroShadowMaskActive = true;
        GD.Print($"Terrain planet macro shadows ready: {mask.Width}x{mask.Height} R8, "
            + $"trace={mask.MaximumTraceDistanceMeters:0}m/{Mathf.Clamp(MacroTerrainShadowTraceSteps, 1, 32)} steps, "
            + $"buildMs={buildMilliseconds:0.00}");
    }

    private void DisablePlanetMacroShadows()
    {
        if (PlanetMacroShadowMaskActive || _planetMacroShadowTask != null)
            _planetMacroShadowCancellation?.Cancel();
        PlanetMacroShadowMaskActive = false;
        _lastPlanetMacroShadowRequestedDirection = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.DisablePlanet();
    }

    private void CancelPlanetMacroShadowBuild()
    {
        _planetMacroShadowCancellation?.Cancel();
        _planetMacroShadowCancellation?.Dispose();
        _planetMacroShadowCancellation = null;
    }
}
