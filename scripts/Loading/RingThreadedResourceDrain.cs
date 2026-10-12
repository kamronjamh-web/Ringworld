using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Loading;

/// <summary>One matching get for each successful threaded request, on the main thread.</summary>
internal sealed class RingThreadedResourceClaims : IDisposable
{
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private bool _disposed;
    public int PendingCount => _pending.Count;

    public Error Request(string path)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RingThreadedResourceClaims));
        if (_pending.Contains(path)) throw new InvalidOperationException("Resource already requested by this owner: " + path);
        // Request even when another owner is loading this path: each request
        // acquires its own loader user_rc claim and therefore needs its own get.
        Error error = ResourceLoader.LoadThreadedRequest(path);
        if (error == Error.Ok) _pending.Add(path);
        return error;
    }

    public Resource? Take(string path)
    {
        if (!_pending.Contains(path)) throw new InvalidOperationException("Resource claim is not owned: " + path);
        var status = ResourceLoader.LoadThreadedGetStatus(path);
        if (status is not (ResourceLoader.ThreadLoadStatus.Loaded or ResourceLoader.ThreadLoadStatus.Failed))
            throw new InvalidOperationException("Resource request has not completed: " + path);
        try { return ResourceLoader.LoadThreadedGet(path); }
        finally { _pending.Remove(path); }
    }

    public bool DrainCompleted()
    {
        foreach (string path in new List<string>(_pending))
        {
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status is ResourceLoader.ThreadLoadStatus.Loaded or ResourceLoader.ThreadLoadStatus.Failed)
                _ = Take(path);
            else if (status == ResourceLoader.ThreadLoadStatus.InvalidResource)
                _pending.Remove(path); // No native token remains to consume.
        }
        return _pending.Count == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (DrainCompleted()) return;
        // Iterator/world lifetime can end before engine IO. Keep only claim
        // paths alive in an independent main-thread node until IO completes.
        if (Engine.GetMainLoop() is SceneTree tree && GodotObject.IsInstanceValid(tree.Root))
            tree.Root.CallDeferred(Node.MethodName.AddChild,
                new RingThreadedResourceDrain { Claims = this, ProcessMode = Node.ProcessModeEnum.Always });
    }
}

/// <summary>Nonblocking fallback for requests abandoned by an exiting owner.</summary>
public partial class RingThreadedResourceDrain : Node
{
    internal RingThreadedResourceClaims? Claims;
    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;
    public override void _Process(double delta)
    {
        if (Claims == null || Claims.DrainCompleted())
        {
            Claims = null;
            QueueFree();
        }
    }
}
