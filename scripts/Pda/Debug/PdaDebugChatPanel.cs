using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Pda;

/// <summary>Opt-in debug question box. Snapshots and all UI work stay on the main thread; inference runs in its own process.</summary>
public partial class PdaDebugChatPanel : CanvasLayer
{
    public const string InputOwnershipMeta = "ringworld_pda_debug_open";
    private const int MaxMessages = 8;
    private static readonly string[] ModelKeys = { "qwen3-0.6b", "qwen2.5-0.5b", "qwen2-0.5b" };

    [Export] public NodePath ContextFeedPath { get; set; } = new("../PdaContextFeed");
    private PdaContextFeed? _feed;
    private LocalPdaQaClient? _client;
    private LineEdit? _question;
    private Button? _ask, _cancel, _reload;
    private Label? _status;
    private RichTextLabel? _transcript;
    private TextEdit? _context;
    private OptionButton? _models, _memory;
    private readonly List<string> _messages = new();
    private Task<PdaQaReply>? _pending;
    private Task? _warmup;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _warmupCancellation;
    private Input.MouseModeEnum _previousMouseMode;
    private bool _previousMouseOwnership;
    private string? _previousQuestion;
    private string _workerMessage = "Local model warming in the background.";
    private ulong _requestStarted;
    private DateTimeOffset _snapshotCaptured;
    private long _snapshotVersion;
    private long _questionSerial, _warmupQuestionSerial;
    private bool _enabled;
    private string _requestedModelKey = ModelKeys[0];
    private PdaMemoryMode _requestedMemoryMode = PdaMemoryMode.SystemRam;
    private string _lastMetrics = "";

    /// <summary>Injection seam for deterministic UI lifecycle tests; normal play uses the owned local helper.</summary>
    public Func<string, string, CancellationToken, Task<PdaQaReply>>? AskOverride { get; set; }
    public bool IsBusy => _pending != null;
    public string TranscriptText => _transcript?.Text ?? "";
    public string ContextSuppliedText => _context?.Text ?? "";
    public string StatusText => _status?.Text ?? "";
    public string RequestedModelKey => _requestedModelKey;
    public PdaMemoryMode RequestedMemoryMode => _requestedMemoryMode;
    public string SelectedModelKey => ModelKeys[_models?.Selected ?? 0];
    public PdaMemoryMode SelectedMemoryMode => _memory?.Selected == 1 ? PdaMemoryMode.GpuVram : PdaMemoryMode.SystemRam;

    public static bool OwnsInput(Node node) => node.IsInsideTree()
        && node.GetTree().Root.GetMeta(InputOwnershipMeta, false).AsBool();

    public override void _Ready()
    {
        Visible = false;
        _enabled = OS.IsDebugBuild();
        if (!_enabled) { SetProcess(false); SetProcessInput(false); SetProcessUnhandledInput(false); return; }
        Layer = 96;
        ProcessMode = ProcessModeEnum.Always;
        _feed = GetNodeOrNull<PdaContextFeed>(ContextFeedPath);
        Build();
        StartWarmup();
    }

    public override void _Input(InputEvent @event)
    {
        if (!_enabled || @event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.F10 && !key.CtrlPressed && !key.AltPressed && !key.ShiftPressed && !key.MetaPressed)
        {
            SetOpen(!Visible);
            GetViewport().SetInputAsHandled();
        }
        else if (Visible && key.Keycode == Key.Escape)
        {
            SetOpen(false);
            GetViewport().SetInputAsHandled();
        }
        else if (Visible && (key.Keycode == Key.Quoteleft || key.Keycode >= Key.F1 && key.Keycode <= Key.F12))
        {
            // These control other debug overlays; ordinary keys must reach the LineEdit GUI.
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Visible) GetViewport().SetInputAsHandled();
    }

    public void SetOpen(bool open)
    {
        if (!_enabled || open == Visible) return;
        if (open)
        {
            _previousMouseMode = Input.MouseMode;
            _previousMouseOwnership = GetTree().Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool();
            GetTree().Root.SetMeta(InputOwnershipMeta, true);
            GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, true);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Visible = true;
            _question?.GrabFocus();
        }
        else
        {
            CancelQuestion();
            Visible = false;
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            GetTree().Root.SetMeta(InputOwnershipMeta, false);
            GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, _previousMouseOwnership);
            if (!GetTree().Paused) Input.MouseMode = _previousMouseMode;
        }
    }

    public override void _ExitTree()
    {
        if (Visible) SetOpen(false);
        CancelQuestion();
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        _warmupCancellation = null;
        ObserveDiscarded(_warmup);
        _warmup = null;
        _client?.Dispose();
        _client = null;
    }

    public override void _Process(double delta)
    {
        if (_warmup is { IsCompleted: true } warmed)
        {
            _warmup = null;
            _warmupCancellation?.Dispose();
            _warmupCancellation = null;
            bool updateStatus = !IsBusy && _questionSerial == _warmupQuestionSerial;
            try { warmed.GetAwaiter().GetResult(); if (updateStatus) _workerMessage = "Local model loaded and ready."; }
            catch (OperationCanceledException) { if (updateStatus) _workerMessage = "Model warmup cancelled."; }
            catch (Exception ex) { if (updateStatus) _workerMessage = ex.Message; AppendMessage("ERROR: Local model warmup failed: " + ex.Message); }
            RefreshStatus();
        }
        if (!Visible) return;
        // Another overlay may update its own shared mouse flag; this panel keeps its separate ownership.
        GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, true);
        if (_pending is { IsCompleted: true } completed)
        {
            _pending = null;
            _cancellation?.Dispose();
            _cancellation = null;
            try
            {
                PdaQaReply reply = completed.GetAwaiter().GetResult();
                _workerMessage = reply.Error == null ? $"{reply.Model} answered in {reply.ElapsedMilliseconds / 1000d:0.00}s." : reply.Error;
                _lastMetrics = FormatMetrics(reply);
                AppendMessage(reply.Error == null ? "PDA: " + reply.Answer : "ERROR: " + reply.Error);
            }
            catch (OperationCanceledException) { _workerMessage = "Question cancelled."; }
            catch (Exception ex) { _workerMessage = ex.Message; AppendMessage("ERROR: " + ex.Message); }
            UpdateButtons();
        }
        RefreshStatus();
    }

    public void SubmitQuestion(string question)
    {
        if (!Visible || IsBusy) return;
        question = question.Trim();
        if (question.Length == 0) return;
        if (question.Length > 512) { AppendMessage("ERROR: Questions are limited to 512 characters."); return; }
        _feed ??= GetNodeOrNull<PdaContextFeed>(ContextFeedPath);
        if (_feed == null) { AppendMessage("ERROR: Prepared context feed is unavailable in this scene."); return; }
        PdaQuestionContext prepared;
        try { prepared = _feed.PrepareQuestion(question, _previousQuestion); }
        catch (Exception ex) { AppendMessage("ERROR: Context capture failed: " + ex.Message); return; }
        _snapshotVersion = prepared.SnapshotVersion;
        _questionSerial++;
        _snapshotCaptured = _feed.CurrentSnapshot?.CapturedAtUtc ?? DateTimeOffset.UtcNow;
        if (_context != null) _context.Text = prepared.ContextText + "\n\n--- Selection diagnostics ---\n" + prepared.DebugText;
        AppendMessage("YOU: " + question);
        _previousQuestion = question;
        if (_question != null) _question.Text = "";
        _cancellation = new CancellationTokenSource();
        _requestStarted = Time.GetTicksMsec();
        _lastMetrics = "";
        _workerMessage = "Preparing local answer...";
        try
        {
            if (AskOverride != null) _pending = AskOverride(question, prepared.ContextText, _cancellation.Token);
            else
            {
                _client ??= CreateClient();
                _pending = _client.AskAsync(question, prepared.ContextText, _cancellation.Token);
            }
        }
        catch (Exception ex)
        {
            _cancellation.Dispose(); _cancellation = null;
            _workerMessage = ex.Message;
            AppendMessage("ERROR: " + ex.Message);
        }
        UpdateButtons();
        RefreshStatus();
    }

    public void CancelQuestion()
    {
        Task<PdaQaReply>? discarded = _pending;
        _pending = null;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        if (discarded != null)
        {
            // Observe a late fault without letting its result update a reopened or cleared panel.
            ObserveDiscarded(discarded);
            _workerMessage = "Question cancelled; local model remains loaded.";
        }
        UpdateButtons();
    }

    private static void ObserveDiscarded(Task? task)
    {
        if (task != null) _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private LocalPdaQaClient CreateClient() => new(ProjectSettings.GlobalizePath("res://"), _requestedModelKey, 4, _requestedMemoryMode);

    public void ReloadModel()
    {
        if (!_enabled) return;
        CancelQuestion();
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        _warmupCancellation = null;
        ObserveDiscarded(_warmup);
        _warmup = null;
        _client?.Dispose();
        _client = null;
        _requestedModelKey = SelectedModelKey;
        _requestedMemoryMode = SelectedMemoryMode;
        _lastMetrics = "";
        StartWarmup(force: true);
        RefreshStatus();
    }

    private void StartWarmup(bool force = false)
    {
        if (AskOverride != null) { _workerMessage = "Fixture inference provider ready."; return; }
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        ObserveDiscarded(_warmup);
        _warmup = null;
        _warmupCancellation = null;
        if (DisplayServer.GetName() == "headless" || (!force && OS.GetEnvironment("RINGWORLD_PDA_DISABLE_PRELOAD") == "1"))
        {
            _workerMessage = "Automatic model preload disabled for this debug run.";
            return;
        }
        _warmupCancellation = new CancellationTokenSource();
        try
        {
            _client ??= CreateClient();
            _warmupQuestionSerial = _questionSerial;
            _warmup = _client.WarmAsync(_warmupCancellation.Token);
            _workerMessage = "Local model warming in the background.";
        }
        catch (Exception ex) { _workerMessage = ex.Message; AppendMessage("ERROR: Local model warmup failed: " + ex.Message); }
    }

    public void ClearConversation()
    {
        CancelQuestion();
        _messages.Clear();
        _previousQuestion = null;
        _snapshotVersion = 0;
        _lastMetrics = "";
        if (_transcript != null) _transcript.Text = "";
        if (_context != null) _context.Text = "Ask a question to inspect the exact context supplied to the model.";
    }

    private void AppendMessage(string message)
    {
        _messages.Add(message.Length <= 2400 ? message : message[..2400] + "…");
        while (_messages.Count > MaxMessages) _messages.RemoveAt(0);
        if (_transcript != null) _transcript.Text = string.Join("\n\n", _messages);
    }

    private void UpdateButtons()
    {
        if (_ask != null) _ask.Disabled = IsBusy;
        if (_cancel != null) _cancel.Disabled = !IsBusy;
        if (_models != null) _models.Disabled = IsBusy;
        if (_memory != null) _memory.Disabled = IsBusy;
        if (_reload != null) _reload.Disabled = false;
    }

    private static string FormatMetrics(PdaQaReply reply)
    {
        if (reply.PromptTokens <= 0) return "";
        string prefill = double.IsFinite(reply.PrefillMilliseconds) && reply.PrefillMilliseconds > 0d
            ? $"{reply.PrefillMilliseconds:0} ms" : "n/a";
        string metrics = $"Prompt {reply.PromptTokens} tokens  •  first text {reply.FirstTokenMilliseconds:0} ms  •  prefill {prefill}  •  inference total {reply.ElapsedMilliseconds:0} ms";
        if (reply.QueueMilliseconds >= 1d) metrics += $"  •  queue {reply.QueueMilliseconds:0} ms";
        if (reply.ModelReadyMilliseconds >= 1d) metrics += $"  •  readiness {reply.ModelReadyMilliseconds:0} ms";
        return metrics;
    }

    private void RefreshStatus()
    {
        if (_status == null) return;
        string work = IsBusy ? $"{_client?.Status ?? "Answering"}  •  {(Time.GetTicksMsec() - _requestStarted) / 1000d:0.0}s"
            : _warmup != null ? _client?.Status ?? "Loading local model" : _workerMessage;
        PdaContextSnapshot? current = _feed?.CurrentSnapshot;
        string feed = current == null ? "Feed unavailable" : $"Prepared feed #{current.Version}, age {Math.Max(0d, (DateTimeOffset.UtcNow - current.CapturedAtUtc).TotalSeconds):0.0}s";
        string snapshot = _snapshotVersion == 0 ? "" : $"\nAnswer context #{_snapshotVersion}, age {Math.Max(0d, (DateTimeOffset.UtcNow - _snapshotCaptured).TotalSeconds):0.0}s; answers describe this snapshot.";
        PdaBackendInfo? backend = _client?.BackendInfo;
        string target = _requestedMemoryMode == PdaMemoryMode.GpuVram ? "VRAM / CUDA" : "System RAM / CPU";
        string actual = AskOverride != null ? "Fixture inference; no real model loaded."
            : backend == null ? $"Requested {_requestedModelKey} in {target}; backend not ready."
            : $"Loaded {_requestedModelKey}  •  {backend.MemoryMode} / {backend.Backend}  •  {backend.Device}  •  offloaded layers {backend.OffloadedLayers}/{backend.TotalLayers}";
        if (SelectedModelKey != _requestedModelKey || SelectedMemoryMode != _requestedMemoryMode)
            actual += "  •  Selection pending: press Reload model to apply.";
        _status.Text = work + "\n" + actual + (_lastMetrics.Length > 0 ? "\n" + _lastMetrics : "") + "\n" + feed + snapshot;
    }

    private void Build()
    {
        var root = new Control { Name = "PdaDebugRoot", MouseFilter = Control.MouseFilterEnum.Stop, Theme = RingUiStyle.CreateTheme() };
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { Name = "PdaDebugPanel" };
        root.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = 30; panel.OffsetTop = 35; panel.OffsetRight = -30; panel.OffsetBottom = -35;
        panel.AddThemeStyleboxOverride("panel", RingUiStyle.GlassPanel(.98f, 10));
        var margin = new MarginContainer();
        foreach (string edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 14);
        panel.AddChild(margin);
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 8); margin.AddChild(column);
        var header = new HBoxContainer(); column.AddChild(header);
        var title = new Label { Text = "PDA DEBUG — F10 / ESC CLOSE", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; title.AddThemeColorOverride("font_color", RingUiStyle.AccentBright); header.AddChild(title);
        var close = new Button { Text = "Close" }; close.Pressed += () => SetOpen(false); header.AddChild(close);
        var modelRow = new HBoxContainer(); column.AddChild(modelRow);
        modelRow.AddChild(new Label { Text = "Local model" });
        _models = new OptionButton { Name = "PdaModel" };
        foreach (string model in ModelKeys) _models.AddItem(model);
        _models.ItemSelected += _ => RefreshStatus();
        modelRow.AddChild(_models);
        _memory = new OptionButton { Name = "PdaMemoryMode" };
        _memory.AddItem("System RAM (CPU, 4 threads)");
        _memory.AddItem("VRAM (CUDA)");
        _memory.ItemSelected += _ => RefreshStatus();
        modelRow.AddChild(_memory);
        _reload = new Button { Name = "PdaReloadModel", Text = "Reload model", TooltipText = "Cancel the current answer and reload the selected model and memory backend." };
        _reload.Pressed += ReloadModel;
        modelRow.AddChild(_reload);
        var caveat = new Label { Name = "PdaModelCaveat", Text = "Experimental model answers — check the supplied context.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        caveat.AddThemeFontSizeOverride("font_size", 12);
        caveat.Modulate = RingUiStyle.Muted;
        column.AddChild(caveat);
        _status = new Label { Name = "PdaStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart }; _status.AddThemeFontSizeOverride("font_size", 12); column.AddChild(_status);
        var body = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = new[] { 0 } }; column.AddChild(body);
        var conversation = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) }; body.AddChild(conversation);
        conversation.AddChild(new Label { Text = "Conversation" });
        _transcript = new RichTextLabel { Name = "PdaTranscript", BbcodeEnabled = false, SelectionEnabled = true, ScrollFollowing = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        conversation.AddChild(_transcript);
        var inspector = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) }; body.AddChild(inspector);
        inspector.AddChild(new Label { Text = "Context supplied — facts, knowledge, recent changes" });
        _context = new TextEdit { Name = "PdaContextSupplied", Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Text = "Ask a question to inspect the exact context supplied to the model." };
        _context.AddThemeFontSizeOverride("font_size", 13);
        _context.AddThemeColorOverride("font_color", RingUiStyle.Ink);
        _context.AddThemeColorOverride("font_readonly_color", RingUiStyle.Ink);
        inspector.AddChild(_context);
        var row = new HBoxContainer(); column.AddChild(row);
        _question = new LineEdit { Name = "PdaQuestion", PlaceholderText = "Ask about the current game situation…", MaxLength = 512, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _question.TextSubmitted += SubmitQuestion; row.AddChild(_question);
        _ask = new Button { Name = "PdaAsk", Text = "Ask" }; _ask.Pressed += () => SubmitQuestion(_question.Text); row.AddChild(_ask);
        _cancel = new Button { Text = "Cancel", Disabled = true }; _cancel.Pressed += CancelQuestion; row.AddChild(_cancel);
        var clear = new Button { Text = "Clear" }; clear.Pressed += ClearConversation; row.AddChild(clear);
        var refresh = new Button { Text = "Refresh feed" }; refresh.Pressed += () => { _feed?.Capture(); RefreshStatus(); }; row.AddChild(refresh);
        RefreshStatus();
    }
}
