using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tests;

public partial class TuningPopupIsolationTestDriver : RefCounted
{
    private TuningPopupIsolation? _policy;
    public void Attach(Node owner) { Stop(); _policy = new TuningPopupIsolation(owner); }
    public void Stop() { _policy?.Dispose(); _policy = null; }
}
