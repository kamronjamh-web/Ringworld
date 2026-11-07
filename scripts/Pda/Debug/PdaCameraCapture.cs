using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Pda;

public sealed record PdaCapturedView(byte[] PngBytes, DateTimeOffset CapturedAtUtc);

/// <summary>One opt-in viewport observation. Call on the game thread; the panel is hidden without changing its input ownership.</summary>
public static class PdaCameraCapture
{
    public const int MaximumWidth = 640, MaximumHeight = 360, MaximumBytes = 2 * 1024 * 1024;
    private static readonly ConditionalWeakTable<Control, SemaphoreSlim> CaptureGates = new();

    public static async Task<PdaCapturedView> CaptureAsync(Node owner, Control overlay, CancellationToken token)
    {
        SemaphoreSlim gate = CaptureGates.GetValue(overlay, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try { return await CaptureOnceAsync(owner, overlay, token); }
        finally { gate.Release(); }
    }

    private static async Task<PdaCapturedView> CaptureOnceAsync(Node owner, Control overlay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("Current-view capture requires a rendered viewport.");
        if (!owner.IsInsideTree()) throw new InvalidOperationException("Camera capture owner is outside the scene.");
        bool wasVisible = overlay.Visible;
        Control? previousFocus = owner.GetViewport().GuiGetFocusOwner();
        overlay.Visible = false;
        try
        {
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
            await owner.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            token.ThrowIfCancellationRequested();
            if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()) throw new OperationCanceledException("Capture scene exited.", token);
            DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
            using Image image = owner.GetViewport().GetTexture().GetImage();
            if (image == null || image.IsEmpty()) throw new InvalidOperationException("The current viewport has no image.");
            double scale = Math.Min(1d, Math.Min(MaximumWidth / (double)image.GetWidth(), MaximumHeight / (double)image.GetHeight()));
            int width = Math.Max(1, (int)Math.Round(image.GetWidth() * scale));
            int height = Math.Max(1, (int)Math.Round(image.GetHeight() * scale));
            image.Resize(width, height, Image.Interpolation.Lanczos);
            image.Convert(Image.Format.Rgba8);
            byte[] png = image.SavePngToBuffer();
            token.ThrowIfCancellationRequested();
            if (png.Length == 0 || png.Length > MaximumBytes) throw new InvalidOperationException("Captured PNG exceeds the image-input size limit.");
            return new PdaCapturedView(png, capturedAtUtc);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(overlay))
            {
                overlay.Visible = wasVisible;
                if (wasVisible && previousFocus != null && GodotObject.IsInstanceValid(previousFocus)
                    && previousFocus.IsVisibleInTree() && overlay.IsAncestorOf(previousFocus)) previousFocus.GrabFocus();
            }
        }
    }
}
