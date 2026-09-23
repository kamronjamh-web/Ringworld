using System;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Shared presentation and mouse-wheel safeguards for live tuning interfaces.</summary>
public static class TuningMenuUi
{
    private const float WheelPixelsPerNotch = 48f;

    public static VBoxContainer CreateSection(VBoxContainer parent, string title, string? note = null)
    {
        var section = new FoldableContainer
        {
            Name = MakeSectionName(title),
            Title = title,
            TooltipText = title,
            Folded = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TitleTextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        parent.AddChild(section);

        var padding = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        padding.AddThemeConstantOverride("margin_left", 8);
        padding.AddThemeConstantOverride("margin_right", 8);
        padding.AddThemeConstantOverride("margin_bottom", 8);
        section.AddChild(padding);
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8);
        padding.AddChild(body);
        if (!string.IsNullOrWhiteSpace(note))
            body.AddChild(new Label { Text = note, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        return body;
    }

    public static void ConfigureNumericScrolling(Control root)
    {
        root.MouseForcePassScrollEvents = false;
        ConfigureNumericScrollingRecursive(root);
    }

    private static void ConfigureNumericScrollingRecursive(Control root)
    {
        if (root is Slider slider) slider.Scrollable = false;
        foreach (Node child in root.GetChildren())
            if (child is Control control) ConfigureNumericScrollingRecursive(control);
    }

    /// <summary>Routes wheel input over numeric controls to their owning tab scroll view.</summary>
    public static bool HandleNumericWheel(Control? root, InputEvent @event)
    {
        if (root?.IsVisibleInTree() != true || @event is not InputEventMouseButton mouse || !mouse.Pressed)
            return false;
        float direction = mouse.ButtonIndex switch
        {
            MouseButton.WheelUp or MouseButton.WheelLeft => -1f,
            MouseButton.WheelDown or MouseButton.WheelRight => 1f,
            _ => 0f
        };
        if (direction == 0f) return false;

        Control? hovered = root.GetViewport().GuiGetHoveredControl();
        if (hovered is null || !IsDescendantOf(hovered, root) || !hovered.IsVisibleInTree()) return false;
        if (!HasNumericAncestor(hovered, root)) return false;

        ScrollContainer? scroll = FindScrollAncestor(hovered, root);
        if (scroll is null) return false;
        float factor = mouse.Factor > 0f && float.IsFinite(mouse.Factor) ? mouse.Factor : 1f;
        bool horizontal = mouse.ButtonIndex is MouseButton.WheelLeft or MouseButton.WheelRight;
        if (horizontal && scroll.HorizontalScrollMode != ScrollContainer.ScrollMode.Disabled)
        {
            float maximum = Mathf.Max(0f, (float)(scroll.GetHScrollBar().MaxValue - scroll.GetHScrollBar().Page));
            scroll.ScrollHorizontal = Mathf.RoundToInt(Mathf.Clamp(scroll.ScrollHorizontal + direction * WheelPixelsPerNotch * factor, 0f, maximum));
        }
        else if (!horizontal)
        {
            float maximum = Mathf.Max(0f, (float)(scroll.GetVScrollBar().MaxValue - scroll.GetVScrollBar().Page));
            scroll.ScrollVertical = Mathf.RoundToInt(Mathf.Clamp(scroll.ScrollVertical + direction * WheelPixelsPerNotch * factor, 0f, maximum));
        }
        root.GetViewport().SetInputAsHandled();
        return true;
    }

    private static bool HasNumericAncestor(Control control, Control root)
    {
        for (Node? node = control; node is not null; node = node.GetParent())
        {
            if (node is Slider or SpinBox) return true;
            if (node == root) break;
        }
        return false;
    }

    private static ScrollContainer? FindScrollAncestor(Control control, Control root)
    {
        for (Node? node = control; node is not null; node = node.GetParent())
        {
            if (node is ScrollContainer scroll) return scroll;
            if (node == root) break;
        }
        return null;
    }

    private static bool IsDescendantOf(Node node, Node ancestor)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current == ancestor) return true;
        return false;
    }

    private static string MakeSectionName(string title)
    {
        string name = "Section";
        foreach (char character in title)
            if (char.IsLetterOrDigit(character)) name += character;
        return name;
    }
}
