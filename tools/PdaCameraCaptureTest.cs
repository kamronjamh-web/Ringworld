using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo.Tools;

/// <summary>Worldless, windowed viewport-capture bounds/timestamp/overlay restoration. No model, save or network access.</summary>
public partial class PdaCameraCaptureTest : Node
{
    private Task? _checks;
    private Control? _overlay;
    private Color _previousClearColor;
    public string Result { get; private set; } = "RUNNING";

    public override void _Ready()
    {
        _previousClearColor = RenderingServer.GetDefaultClearColor();
        RenderingServer.SetDefaultClearColor(new Color(.9f, .01f, .01f, 1f));
        _overlay = new ColorRect { Color = new Color(.01f, .01f, .9f, 1f) };
        AddChild(_overlay);
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _checks = RunChecks();
    }

    public override void _Process(double delta)
    {
        if (Result != "RUNNING" || _checks is not { IsCompleted: true }) return;
        try { _checks.GetAwaiter().GetResult(); Result = "PASS"; Console.WriteLine("PDA_CAMERA_CAPTURE=PASS"); }
        catch (Exception error) { Result = "FAIL " + error.Message; Console.Error.WriteLine("PDA_CAMERA_CAPTURE=" + Result); }
    }

    private async Task RunChecks()
    {
        if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("Requires windowed viewport rendering.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using (Image visible = GetViewport().GetTexture().GetImage())
        {
            Color visibleCenter = visible.GetPixel(visible.GetWidth() / 2, visible.GetHeight() / 2);
            if (visibleCenter.B <= visibleCenter.R + .3f) throw new InvalidOperationException("Fixture overlay did not render before capture.");
        }
        DateTimeOffset before = DateTimeOffset.UtcNow;
        PdaCapturedView captured = await PdaCameraCapture.CaptureAsync(this, _overlay!, CancellationToken.None);
        if (!_overlay!.Visible) throw new InvalidOperationException("Capture failed to restore overlay visibility.");
        if (captured.CapturedAtUtc < before || captured.CapturedAtUtc > DateTimeOffset.UtcNow) throw new InvalidOperationException("Capture timestamp is outside its request interval.");
        if (captured.PngBytes.Length == 0 || captured.PngBytes.Length > PdaCameraCapture.MaximumBytes) throw new InvalidOperationException("PNG length outside bounds.");
        using var decoded = new Image();
        if (decoded.LoadPngFromBuffer(captured.PngBytes) != Error.Ok || decoded.GetWidth() > 640 || decoded.GetHeight() > 360) throw new InvalidOperationException("PNG dimensions outside capture bounds.");
        Color center = decoded.GetPixel(decoded.GetWidth() / 2, decoded.GetHeight() / 2);
        if (center.R <= center.B + .3f) throw new InvalidOperationException("Captured image retained the visible blue overlay instead of the red background.");
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        double expectedAspect = viewportSize.X / viewportSize.Y;
        if (Math.Abs(decoded.GetWidth() / (double)decoded.GetHeight() - expectedAspect) > .02d) throw new InvalidOperationException("Capture changed viewport aspect ratio.");
        using var cancellation = new CancellationTokenSource();
        Task<PdaCapturedView> cancelled = PdaCameraCapture.CaptureAsync(this, _overlay, cancellation.Token);
        if (_overlay.Visible) throw new InvalidOperationException("Overlay did not hide during pending capture.");
        cancellation.Cancel();
        Task<PdaCapturedView> immediatelyRetried = PdaCameraCapture.CaptureAsync(this, _overlay, CancellationToken.None);
        try { await cancelled; throw new InvalidOperationException("Cancelled capture produced an image."); }
        catch (OperationCanceledException) { }
        PdaCapturedView retried = await immediatelyRetried;
        if (!_overlay.Visible) throw new InvalidOperationException("Cancellation did not restore overlay visibility.");
        using var retriedImage = new Image();
        if (retriedImage.LoadPngFromBuffer(retried.PngBytes) != Error.Ok) throw new InvalidOperationException("Immediate retry did not produce a PNG.");
        Color retriedCenter = retriedImage.GetPixel(retriedImage.GetWidth() / 2, retriedImage.GetHeight() / 2);
        if (retriedCenter.R <= retriedCenter.B + .3f) throw new InvalidOperationException("Cancelled capture restored the overlay into its immediate retry.");
    }

    public override void _ExitTree() => RenderingServer.SetDefaultClearColor(_previousClearColor);
}
