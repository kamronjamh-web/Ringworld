using System;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>
/// Keeps UI windows out of the owner's 3D environment. Embedded popups otherwise
/// inherit canvas-background postprocessing, including the eyedropper screenshot.
/// </summary>
public sealed class TuningPopupIsolation : IDisposable
{
    // An intentional Window-based 3D preview may opt out before entering the tree.
    public const string PreserveWorldMeta = "tuning_preserve_3d_world";
    private readonly Node _owner;
    private readonly SceneTree _tree;
    private readonly World3D _uiWorld = new();
    private bool _disposed;

    public TuningPopupIsolation(Node owner)
    {
        _owner = owner;
        _tree = owner.GetTree();
        _tree.NodeAdded += OnNodeAdded;
        Seed(owner);
    }

    private void Seed(Node node)
    {
        if (node != _owner && IsPreviewBoundary(node)) return;
        OnNodeAdded(node);
        foreach (Node child in node.GetChildren(includeInternal: true)) Seed(child);
    }

    private static bool IsPreviewBoundary(Node node)
        => node is SubViewport or Node3D
            || node is Window and not Popup and not AcceptDialog
            || node.GetMeta(PreserveWorldMeta, false).AsBool();

    private bool IsOwnedUiWindow(Window window)
    {
        if (window is not Popup and not AcceptDialog || window.GetCamera3D() != null) return false;
        for (Node? node = window; node != null; node = node.GetParent())
        {
            if (IsPreviewBoundary(node)) return false;
            if (node == _owner) return true;
        }
        return false;
    }

    private void OnNodeAdded(Node node)
    {
        if (_disposed || node is not Window window || !IsOwnedUiWindow(window)) return;
        // Synchronous NodeAdded handling includes internal engine-created popup
        // and sampler windows before their first rendered frame. Disable3D alone
        // cannot prevent inherited BG_CANVAS postprocessing; the empty world does.
        window.World3D = _uiWorld;
        window.Disable3D = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (GodotObject.IsInstanceValid(_tree)) _tree.NodeAdded -= OnNodeAdded;
        // Existing UI windows retain their World3D resource; do not restore the
        // gameplay environment onto a popup during scene teardown.
    }
}
