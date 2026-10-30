using Godot;
using RingworldTechDemo.Pda;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo;

/// <summary>
/// Runtime glue for the megastorm PDA lines (docs/megastorm-design.md 5.4): each frame queries <see cref="RingMegastorm"/> at the explorer, runs
/// <see cref="MegastormPdaTriggers"/> into a <see cref="PdaAnnouncer"/> with subtitles-only output, and unlocks the persisted database entry.
/// Untriggered it costs one bool check per frame. The announcer runs on the weather clock, so a paused sky pauses the PDA too.
/// </summary>
public partial class MegastormPdaDirector : Node
{
    public const string LinesPath = "res://resources/pda/megastorm_lines.json", DatabasePath = "res://resources/pda/megastorm_database.json";

    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");

    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private PdaSubtitleOverlay? _overlay;
    private PdaAnnouncer? _announcer;
    private MegastormPdaTriggers? _triggers;

    /// <summary>The database entry text, loaded on ready (null if the resource is missing).</summary>
    public PdaDatabaseEntry? DatabaseEntry { get; private set; }
    public bool EntryUnlocked => _megastorm?.ResonanceEntryUnlocked ?? false;

    public override void _Ready()
    {
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        if (_megastorm == null || !FileAccess.FileExists(LinesPath)) { SetProcess(false); return; }
        PdaLineTable table = PdaLineTable.Parse(FileAccess.GetFileAsString(LinesPath));
        if (FileAccess.FileExists(DatabasePath)) DatabaseEntry = PdaDatabaseEntry.Parse(FileAccess.GetFileAsString(DatabasePath));
        _overlay = new PdaSubtitleOverlay { Name = "PdaSubtitles" };
        AddChild(_overlay);
        RingMegastorm storm = _megastorm;
        _announcer = new PdaAnnouncer(() => storm.Now, null, new SubtitleOnlyVoiceOutput(line => _overlay.ShowLine(line)));
        _triggers = new MegastormPdaTriggers(table, _announcer, () => storm.Now, () => storm.ResonanceEntryUnlocked = true);
    }

    public override void _Process(double delta)
    {
        if (_megastorm == null || _triggers == null || _announcer == null) return;
        if (!_megastorm.Triggered) { _triggers.Update(default); return; }
        RingCanonicalPosition? p = _explorer?.CanonicalPosition;
        if (p == null) return;
        MegastormSample sample = _megastorm.QueryNow(p.Value);
        _triggers.Update(sample, MegastormPdaHazards.FromSample(sample, _megastorm.ObserverContext.Shelter));
        _announcer.Tick();
    }
}
