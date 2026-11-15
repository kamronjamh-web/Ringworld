using System;
using System.Runtime.InteropServices;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Autoload, inert unless RINGWORLD_TEST_NO_GRAB=1 on Windows. Keeps test runs from capturing the user's cursor: the game still
/// sets Input.MouseMode as usual, but the Win32 clip and capture it causes are undone, and focus is handed back to the window
/// that was active before the test window appeared.
/// </summary>
public partial class TestRunMouseRelease : Node
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, char[] buffer, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int DwmaCloaked = 14;

    private bool _active;
    private uint _pid;
    private IntPtr _hwnd;
    private System.Threading.Timer? _clipTimer;
    // Main window rect, refreshed in _Process and read by the timer thread (packed as four ints guarded by a lock-free copy).
    private volatile Rect[] _windowRect = new Rect[1];

    public override void _Ready()
    {
        _active = OS.GetName() == "Windows" && System.Environment.GetEnvironmentVariable("RINGWORLD_TEST_NO_GRAB") == "1";
        SetProcess(_active);
        if (!_active) return;
        ProcessMode = ProcessModeEnum.Always;
        _pid = GetCurrentProcessId();
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        _hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        MoveToTestScreen();
        RefreshWindowRect();
        RestoreForeground();
        // Clip is process-wide state, so a timer thread also clears ours while the main thread is stalled in a synchronous load.
        _clipTimer = new System.Threading.Timer(_ => ClearOwnClip(), null, 0, 50);
        GD.Print("TestRunMouseRelease: active");
    }

    public override void _ExitTree()
    {
        _clipTimer?.Dispose();
        _clipTimer = null;
    }

    public override void _Process(double delta)
    {
        RefreshWindowRect();
        var mode = Input.MouseMode;
        if (mode != Input.MouseModeEnum.Captured && mode != Input.MouseModeEnum.Confined && mode != Input.MouseModeEnum.ConfinedHidden) return;
        ClearOwnClip();
        IntPtr cap = GetCapture();
        if (cap != IntPtr.Zero && GetWindowThreadProcessId(cap, out uint pid) != 0 && pid == _pid) ReleaseCapture();
    }

    // Puts the test window on a non-primary screen so the user keeps the main display. RINGWORLD_TEST_SCREEN overrides:
    // "primary" leaves the window where Godot put it, a number picks that Godot screen index. The window keeps its size
    // (captures keep their resolution); it is centred on the screen, clamped to the screen's top-left corner.
    private void MoveToTestScreen()
    {
        int count = DisplayServer.GetScreenCount();
        string choice = System.Environment.GetEnvironmentVariable("RINGWORLD_TEST_SCREEN") ?? "";
        if (count < 2 || choice == "primary") return;
        int primary = DisplayServer.GetPrimaryScreen();
        int target = -1;
        if (int.TryParse(choice, out int requested) && requested >= 0 && requested < count) target = requested;
        else for (int i = 0; i < count; i++) if (i != primary) { target = i; break; }
        if (target < 0 || target == DisplayServer.WindowGetCurrentScreen()) return;
        if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed) return; // maximised/fullscreen runs size themselves to their screen
        Vector2I origin = DisplayServer.ScreenGetPosition(target);
        Vector2I area = DisplayServer.ScreenGetSize(target);
        Vector2I size = DisplayServer.WindowGetSizeWithDecorations();
        Vector2I pos = origin + new Vector2I(Math.Max(0, (area.X - size.X) / 2), Math.Max(0, (area.Y - size.Y) / 2));
        Vector2I before = DisplayServer.WindowGetSize();
        DisplayServer.WindowSetPosition(pos);
        if (DisplayServer.WindowGetSize() != before) DisplayServer.WindowSetSize(before); // a DPI change between screens must not resize the viewport
        GD.Print($"TestRunMouseRelease: window moved to screen {target} at {pos}, size {DisplayServer.WindowGetSize()}");
    }

    private void RefreshWindowRect()
    {
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out Rect r)) _windowRect = new[] { r };
    }

    // Clears the clip only when it is a partial clip lying inside our own window, never one set by another application.
    private void ClearOwnClip()
    {
        if (!GetClipCursor(out Rect c)) return;
        Rect w = _windowRect[0];
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        bool full = c.Left <= vx && c.Top <= vy && c.Right >= vx + vw && c.Bottom >= vy + vh;
        if (full) return;
        // Small tolerance: Windows may report the client rect or the window rect.
        const int slack = 2;
        bool inside = c.Left >= w.Left - slack && c.Top >= w.Top - slack && c.Right <= w.Right + slack && c.Bottom <= w.Bottom + slack;
        if (inside) ClipCursor(IntPtr.Zero);
    }

    // Hands the foreground back to the window that was active before ours: the next visible, unowned, non-tool, uncloaked top-level
    // window below ours in the z-order that belongs to another process.
    private void RestoreForeground()
    {
        if (_hwnd == IntPtr.Zero || GetForegroundWindow() != _hwnd) return;
        IntPtr h = GetWindow(_hwnd, GwHwndNext);
        for (int guard = 0; h != IntPtr.Zero && guard < 512; guard++, h = GetWindow(h, GwHwndNext))
        {
            if (!IsWindowVisible(h) || GetWindow(h, GwOwner) != IntPtr.Zero) continue;
            if ((GetWindowLong(h, GwlExStyle) & WsExToolWindow) != 0) continue;
            if (DwmGetWindowAttribute(h, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == _pid) continue;
            var cls = new char[64];
            int n = GetClassNameW(h, cls, cls.Length);
            SetForegroundWindow(h);
            GD.Print($"TestRunMouseRelease: focus returned to window (class {new string(cls, 0, n)}, title length {GetWindowTextLengthW(h)})");
            return;
        }
        GD.Print("TestRunMouseRelease: focus restore: none found");
    }
}
