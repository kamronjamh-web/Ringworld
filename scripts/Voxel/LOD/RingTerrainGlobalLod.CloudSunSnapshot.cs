namespace RingworldTechDemo.Voxel.LOD;
public partial class RingTerrainGlobalLod
{
    public int CloudSunSourceIdentity => _heightPyramid == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_heightPyramid);
    // Called on the main thread only after the pyramid has consumed the current edits.
    public CloudSunTerrainSnapshot? CaptureCloudSunTerrainSnapshot()
    {
        if (_heightPyramid == null || _grid == null || _heightPyramid.EditVersion != _grid.LodSurfaceEditVersion)
            return null;
        return _heightPyramid.CaptureCloudSunTerrainSnapshot();
    }
}
