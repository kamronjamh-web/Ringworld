using Godot;

namespace RingworldTechDemo.Terrain.Surface;

public partial class RingTerrain3DGameplay
{
    /// <summary>Checks opt-in, saved-state hold, and cancellation without native setup.</summary>
    public string RunStartupStateContract()
    {
        var legacy = new RingTerrain3DGameplay();
        var staged = new RingTerrain3DGameplay { UseStagedStartup = true, WaitForSavedState = true };
        try
        {
            if (legacy.UseStagedStartup || legacy.WaitForSavedState || legacy.InitializationPending)
                return "FAIL: legacy initialization defaults changed.";
            if (staged.EnsureInitialized() || !staged.InitializationPending || staged.InitializationFailed)
                return "FAIL: saved-state hold did not remain pending.";
            if (staged._field != null || staged._sampler != null || staged._stream != null || staged._surface != null)
                return "FAIL: saved-state hold allocated terrain or workers.";
            var status = staged.GetLoadingStatus();
            if (status["stage"].AsString() != "waiting_for_saved_state" || status["ready"].AsBool()
                || status["visual_ready"].AsBool() || status["collision_ready"].AsBool() || status["error"].AsString().Length > 0)
                return "FAIL: held loading status falsely reported readiness or failure.";
            staged.ReleaseSavedStateHold();
            if (staged.WaitForSavedState || !staged.InitializationPending)
                return "FAIL: releasing the hold did not permit staged setup.";
            staged.CancelStartup("Fixture cancellation.");
            if (staged.InitializationPending || !staged.InitializationFailed || staged.IsReady || staged.SaveSurfaceEdits() || !staged.StartupCancellationReady)
                return "FAIL: cancelled startup remained pending or permitted a save.";
            if (staged.GetLoadingStatus()["stage"].AsString() != "failed" || staged.LastError != "Fixture cancellation.")
                return "FAIL: startup cancellation lost its diagnostic.";
            return "PASS";
        }
        finally { staged.Free(); legacy.Free(); }
    }
}
