namespace RingworldTechDemo.Loading;

/// <summary>
/// A world child that splits heavy startup work into steps the loading bridge drives one at a time
/// (on a frame budget) after the world is attached, instead of doing it all in <c>_Ready</c>.
/// </summary>
public interface IRingDeferredReady
{
    /// <summary>True while at least one deferred step remains.</summary>
    bool DeferredReadyPending { get; }

    /// <summary>Runs ONE step. Returns true while more steps remain.</summary>
    bool AdvanceDeferredReady();
}
