using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.ContainmentField;

public partial class RingContainmentField
{
    /// <summary>Loads the grave-key menu choices (user://gameplay_containment_field_defaults.tres) when the node enters the tree.</summary>
    [Export] public bool LoadSavedDefaults { get; set; } = true;

    /// <summary>Applies the saved menu choices, if any, to this node; with no saved file the built-in defaults stay.</summary>
    public void LoadSettingsFromDisk()
    {
        var defaults = new GameplayContainmentFieldDefaults();
        defaults.Load();
        if (defaults.HasSavedFile) defaults.Apply(this);
    }
}
