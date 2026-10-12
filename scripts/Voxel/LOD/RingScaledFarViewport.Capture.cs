using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    private double _directCaptureElapsed;
    private double _directCaptureReadySeconds;

    private void UpdateDirectCapture(double delta)
    {
        if (!_captureDirectView) return;
        _directCaptureElapsed += delta;
        bool globalReady = _grid != null && _globalSurfaceActive.Count > 0
            && _globalSurfaceBuild == null && _globalSurfaceUpload == null
            && _globalPublishedLayoutVersion == _globalGeometryVersion
            && _lastSeededSurfaceEditVersion == _grid.LodSurfaceEditVersion;
        bool regionalReady = !RegionalFarLodsEnabled || (_farRegionalTerrains.Count > 0
            && _farRegionalBuildTask == null && _farRegionalUpload == null);
        _captureFrame = globalReady && regionalReady && SurfaceTilePendingCount == 0
            ? _captureFrame + 1 : 0;
        _directCaptureReadySeconds = _captureFrame > 0 ? _directCaptureReadySeconds + delta : 0.0;
        // Allow resident texture fades and deferred render-thread uploads to settle.
        if (_captureFrame >= 30 && _directCaptureReadySeconds >= 0.25)
        {
            CaptureDirectViewAndQuit();
        }
        else if (_directCaptureElapsed >= 120.0)
        {
            GD.PushError($"Far-ring capture timed out: globalReady={globalReady}, regionalReady={regionalReady}, pendingTiles={SurfaceTilePendingCount}.");
            CaptureDirectViewAndQuit();
            GetTree().Quit(1);
        }
    }
}
